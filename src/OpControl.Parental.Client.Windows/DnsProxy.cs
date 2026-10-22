// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class DnsProxy
{
    private static readonly HashSet<string> DohDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "dns.google", "dns.google.com", "cloudflare-dns.com", "mozilla.cloudflare-dns.com",
        "chrome.cloudflare-dns.com", "dns.quad9.net", "doh.opendns.com", "dns.nextdns.io",
        "dns.adguard.com", "dns.adguard-dns.com", "family.adguard-dns.com", "doh.pub",
        "dns.alidns.com", "doh.360.cn", "dot.pub", "dns.tencent.com", "dns.baidu.com"
    };

    private readonly LockRuntime _runtime;
    private readonly CategoryCache _categories;
    private readonly NetworkDnsConfigurator _network;
    private readonly DataSyncService _sync;
    private readonly ILogger<DnsProxy> _logger;
    private UdpClient? _udp4;
    private UdpClient? _udp6;
    private TcpListener? _tcp4;
    private TcpListener? _tcp6;
    private CancellationTokenSource? _cts;
    private int _loggedForwardFailure;
    private int _loggedFirstQuery;
    private int _loggedSendFailure;
    private int _loggedFirstForward;

    public DnsProxy(LockRuntime runtime, CategoryCache categories, NetworkDnsConfigurator network, DataSyncService sync, ILogger<DnsProxy> logger)
    {
        _runtime = runtime;
        _categories = categories;
        _network = network;
        _sync = sync;
        _logger = logger;
    }

    public bool Ipv4Bound { get; private set; }
    public bool Ipv6Bound { get; private set; }
    public bool Ready => Ipv4Bound || Ipv6Bound;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        try
        {
            _udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53));
            Ipv4Bound = true;
            _ = Task.Run(() => UdpLoopAsync(_udp4, _cts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not bind DNS resolver on 127.0.0.1:53");
        }

        try
        {
            _udp6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 53));
            Ipv6Bound = true;
            _ = Task.Run(() => UdpLoopAsync(_udp6, _cts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not bind DNS resolver on [::1]:53");
        }

        try
        {
            _tcp4 = new TcpListener(IPAddress.Loopback, 53);
            _tcp4.Start();
            _ = Task.Run(() => TcpLoopAsync(_tcp4, _cts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not bind DNS TCP on 127.0.0.1:53: {Message}", ex.Message);
        }

        try
        {
            _tcp6 = new TcpListener(IPAddress.IPv6Loopback, 53);
            _tcp6.Start();
            _ = Task.Run(() => TcpLoopAsync(_tcp6, _cts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not bind DNS TCP on [::1]:53: {Message}", ex.Message);
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _udp4?.Dispose();
        _udp6?.Dispose();
        try { _tcp4?.Stop(); } catch { }
        try { _tcp6?.Stop(); } catch { }
        _cts?.Dispose();
        _cts = null;
        Ipv4Bound = false;
        Ipv6Bound = false;
    }

    private async Task UdpLoopAsync(UdpClient listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await listener.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNS UDP receive failed");
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var response = await HandleAsync(received.Buffer, ct);
                    if (response is not null)
                    {
                        try
                        {
                            await listener.SendAsync(response, response.Length, received.RemoteEndPoint);
                        }
                        catch (Exception ex)
                        {
                            if (Interlocked.Exchange(ref _loggedSendFailure, 1) == 0)
                                _logger.LogWarning(ex, "DNS UDP send failed to {Endpoint}", received.RemoteEndPoint);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DNS UDP query failed");
                }
            }, ct);
        }
    }

    private async Task TcpLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNS TCP accept failed");
                continue;
            }

            _ = Task.Run(() => HandleTcpClientAsync(client, ct), ct);
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                await using var stream = client.GetStream();
                var lengthBuffer = new byte[2];
                if (!await ReadExactAsync(stream, lengthBuffer, ct)) return;
                var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
                if (length is 0 or > 4096) return;
                var query = new byte[length];
                if (!await ReadExactAsync(stream, query, ct)) return;

                byte[]? response;
                if (TryGetBlocked(query, out var blockResponse, out _))
                    response = blockResponse;
                else
                    response = await ForwardTcpAsync(query, ct);
                if (response is null) return;

                var header = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)response.Length);
                await stream.WriteAsync(header, ct);
                await stream.WriteAsync(response, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNS TCP query failed");
            }
        }
    }

    private async Task<byte[]?> HandleAsync(byte[] query, CancellationToken ct)
    {
        if (TryGetBlocked(query, out var blocked, out var question))
        {
            if (question is not null && DomainUtil.GetRegistrableDomain(question.Name) is { } registrable)
                _sync.RecordVisit(registrable, true);
            return blocked;
        }

        if (DnsPacket.TryParseQuestion(query, out var q))
        {
            if (Interlocked.Exchange(ref _loggedFirstQuery, 1) == 0)
                _logger.LogInformation("DNS resolver handled first query for {Name} type {Type}", q.Name, q.Type);
            if (DomainUtil.GetRegistrableDomain(q.Name) is { } domain && DnsPacket.IsAddressType(q.Type))
                _sync.RecordVisit(domain, false);
        }

        var forwarded = await ForwardUdpAsync(query, ct);
        if (forwarded is null && Interlocked.Exchange(ref _loggedForwardFailure, 1) == 0)
            _logger.LogWarning("DNS forwarding returned no response");
        return forwarded;
    }

    private bool TryGetBlocked(byte[] query, out byte[]? response, out DnsQuestion? question)
    {
        response = null;
        question = null;
        if (!DnsPacket.TryParseQuestion(query, out var parsed)) return false;
        question = parsed;
        if (!IsBlocked(parsed.Name)) return false;
        response = DnsPacket.BuildBlockedResponse(query, parsed);
        return true;
    }

    private bool IsBlocked(string name)
    {
        name = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0) return false;
        if (DomainUtil.SetContainsDomainOrParent(DohDomains, name)) return true;

        var state = _runtime.DeviceState;
        if (state is null) return false;
        try
        {
            return PolicyRules.IsWebsiteBlocked(state, name, TimeZoneInfo.Local, DateTime.UtcNow,
                domain => _categories.IsInEnabledCategory(domain, state.Categories));
        }
        catch
        {
            return false;
        }
    }

    private async Task<byte[]?> ForwardUdpAsync(byte[] query, CancellationToken ct)
    {
        var name = DnsPacket.TryParseQuestion(query, out var parsed) ? parsed.Name : "";
        var upstreams = UpstreamsFor(name).ToList();
        if (Interlocked.Exchange(ref _loggedFirstForward, 0) == 0)
        {
            Interlocked.Exchange(ref _loggedFirstForward, 1);
            _logger.LogInformation("DNS upstreams: {Servers}", string.Join(", ", upstreams));
        }
        foreach (var server in upstreams)
        {
            try
            {
                using var client = new UdpClient(server.AddressFamily);
                client.Client.SendTimeout = 3000;
                client.Client.ReceiveTimeout = 3000;
                await client.SendAsync(query, query.Length, new IPEndPoint(server, 53));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var result = await client.ReceiveAsync(timeout.Token);
                if (result.Buffer.Length >= 12)
                {
                    _logger.LogDebug("DNS upstream {Server} answered {Name} ({Length} bytes)", server, name, result.Buffer.Length);
                    return result.Buffer;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DNS upstream {Server} failed for {Name}", server, name);
            }
        }

        return null;
    }

    private async Task<byte[]?> ForwardTcpAsync(byte[] query, CancellationToken ct)
    {
        var name = DnsPacket.TryParseQuestion(query, out var parsed) ? parsed.Name : "";
        foreach (var server in UpstreamsFor(name))
        {
            try
            {
                using var client = new TcpClient(server.AddressFamily);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                await client.ConnectAsync(server, 53, timeout.Token);
                await using var stream = client.GetStream();
                var header = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)query.Length);
                await stream.WriteAsync(header, timeout.Token);
                await stream.WriteAsync(query, timeout.Token);
                var lengthBuffer = new byte[2];
                if (!await ReadExactAsync(stream, lengthBuffer, timeout.Token)) continue;
                var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
                if (length is 0 or > 8192) continue;
                var response = new byte[length];
                if (!await ReadExactAsync(stream, response, timeout.Token)) continue;
                return response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            catch { }
        }
        return null;
    }

    private IEnumerable<IPAddress> UpstreamsFor(string name)
    {
        var value = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".tailscale.com", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".beta.tailscale.net", StringComparison.OrdinalIgnoreCase)
            || !value.Contains('.'))
            return [IPAddress.Parse("100.100.100.100")];
        return _network.UpstreamServers;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }
}
