// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace OpControl.Parental.Client;

public sealed class NetworkDnsConfigurator
{
    private sealed class DnsState
    {
        public Dictionary<string, InterfaceState> Interfaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class InterfaceState
    {
        public string Name { get; set; } = "";
        public List<string> IPv4 { get; set; } = [];
        public List<string> IPv6 { get; set; } = [];
        public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private readonly ILogger<NetworkDnsConfigurator> _logger;
    private readonly object _gate = new();
    private DnsState? _state;

    public NetworkDnsConfigurator(ILogger<NetworkDnsConfigurator> logger) => _logger = logger;

    public bool IsApplied { get; private set; }

    public IReadOnlyList<IPAddress> UpstreamServers
    {
        get
        {
            lock (_gate)
            {
                var servers = _state?.Interfaces.Values
                    .SelectMany(i => i.IPv4.Concat(i.IPv6))
                    .Select(IPAddress.Parse)
                    .Where(a => !IPAddress.IsLoopback(a))
                    .Distinct()
                    .ToList() ?? [];
                if (servers.Count == 0)
                    servers.Add(IPAddress.Parse("1.1.1.1"));
                return servers;
            }
        }
    }

    private bool _ipv4 = true;
    private bool _ipv6 = true;

    public void Apply(bool ipv4 = true, bool ipv6 = true)
    {
        if (!OperatingSystem.IsWindows()) return;
        _ipv4 = ipv4;
        _ipv6 = ipv6;
        lock (_gate)
        {
            _state ??= LoadState();
            var changed = false;
            foreach (var nic in EligibleInterfaces())
            {
                if (!_state.Interfaces.TryGetValue(nic.Name, out var existing)
                    || (existing.IPv4.Count == 0 && existing.IPv6.Count == 0))
                {
                    var captured = Capture(nic);
                    if (captured.IPv4.Count == 0 && captured.IPv6.Count == 0)
                    {
                        Netsh($"interface ipv4 set dnsservers name=\"{nic.Name}\" source=dhcp");
                        Netsh($"interface ipv6 set dnsservers name=\"{nic.Name}\" source=dhcp");
                        for (var wait = 0; wait < 10 && captured.IPv4.Count == 0 && captured.IPv6.Count == 0; wait++)
                        {
                            Thread.Sleep(1000);
                            var fresh = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == nic.Name);
                            if (fresh is not null) captured = Capture(fresh);
                        }
                    }
                    if (captured.IPv4.Count == 0 && captured.IPv6.Count == 0)
                        captured.IPv4.Add("1.1.1.1");
                    _state.Interfaces[nic.Name] = captured;
                    changed = true;
                }
                SetLoopback(nic.Name, _ipv4, _ipv6);
            }

            if (changed)
                SaveState(_state);
            IsApplied = true;
        }
    }

    public bool EnsureApplied()
    {
        if (!OperatingSystem.IsWindows() || !IsApplied) return false;
        var changed = false;
        lock (_gate)
        {
            foreach (var nic in EligibleInterfaces())
            {
                if (!HasLoopback(nic))
                {
                    _logger.LogInformation("Re-applying local DNS filter to {Interface}", nic.Name);
                    SetLoopback(nic.Name, _ipv4, _ipv6);
                    changed = true;
                }
            }
        }
        return changed;
    }

    public void Restore()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_gate)
        {
            var state = _state ?? LoadState();
            foreach (var (name, original) in state.Interfaces)
            {
                if (original.IPv4.Count > 0)
                {
                    Netsh($"interface ipv4 set dnsservers name=\"{name}\" static {original.IPv4[0]} primary");
                    for (var i = 1; i < original.IPv4.Count; i++)
                        Netsh($"interface ipv4 add dnsservers name=\"{name}\" address={original.IPv4[i]} index={i + 1} validate=no");
                }
                else
                {
                    Netsh($"interface ipv4 set dnsservers name=\"{name}\" source=dhcp");
                }

                if (original.IPv6.Count > 0)
                {
                    Netsh($"interface ipv6 set dnsservers name=\"{name}\" static {original.IPv6[0]} primary");
                    for (var i = 1; i < original.IPv6.Count; i++)
                        Netsh($"interface ipv6 add dnsservers name=\"{name}\" address={original.IPv6[i]} index={i + 1} validate=no");
                }
                else
                {
                    Netsh($"interface ipv6 set dnsservers name=\"{name}\" source=dhcp");
                }
            }

            _state = state;
            IsApplied = false;
        }
    }

    private static IEnumerable<NetworkInterface> EligibleInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Where(n => !n.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase)
                && !n.Description.Contains("tailscale", StringComparison.OrdinalIgnoreCase)
                && !n.Name.Contains("wintun", StringComparison.OrdinalIgnoreCase)
                && !n.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase)
                && !n.Name.Contains("Filter Driver", StringComparison.OrdinalIgnoreCase)
                && !n.Name.Contains("LightWeight Filter", StringComparison.OrdinalIgnoreCase)
                && !n.Name.Contains("Packet Scheduler", StringComparison.OrdinalIgnoreCase));

    private static InterfaceState Capture(NetworkInterface nic)
    {
        var properties = nic.GetIPProperties();
        return new InterfaceState
        {
            Name = nic.Name,
            IPv4 = properties.DnsAddresses
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString()).Distinct().ToList(),
            IPv6 = properties.DnsAddresses
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString()).Distinct().ToList()
        };
    }

    private bool HasLoopback(NetworkInterface nic)
    {
        var ipv4 = QueryDnsServers(nic.Name, "ipv4");
        var ipv6 = QueryDnsServers(nic.Name, "ipv6");
        if (ipv4 is not null || ipv6 is not null)
        {
            var ipv4Ok = !_ipv4 || ipv4 is null || ipv4.Count == 0 || ipv4.Contains("127.0.0.1");
            var ipv6Ok = !_ipv6 || ipv6 is null || ipv6.Count == 0 || ipv6.Contains("::1");
            return ipv4Ok && ipv6Ok;
        }

        var addresses = nic.GetIPProperties().DnsAddresses;
        return addresses.Any(a => IPAddress.IsLoopback(a));
    }

    private static List<string>? QueryDnsServers(string interfaceName, string family)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"interface {family} show dnsservers name=\"{interfaceName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0) return null;
            var servers = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var text = line.Trim();
                var colon = text.IndexOf(':');
                if (colon < 0) continue;
                var value = text[(colon + 1)..].Trim();
                if (value.Length == 0 || value.StartsWith("Register", StringComparison.OrdinalIgnoreCase)) continue;
                if (text.Contains("DNS Servers", StringComparison.OrdinalIgnoreCase) || text.Contains("Statically Configured", StringComparison.OrdinalIgnoreCase))
                    servers.AddRange(value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries));
            }
            return servers;
        }
        catch { return null; }
    }

    private void SetLoopback(string name, bool ipv4 = true, bool ipv6 = true)
    {
        if (ipv4) Netsh($"interface ipv4 set dnsservers name=\"{name}\" static 127.0.0.1 primary");
        if (ipv6) Netsh($"interface ipv6 set dnsservers name=\"{name}\" static ::1 primary");
    }

    private void Netsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return;
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(); } catch { }
                _logger.LogWarning("netsh timed out: {Arguments}", arguments);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "netsh failed: {Arguments}", arguments);
        }
    }

    private DnsState LoadState()
    {
        try
        {
            if (File.Exists(ClientPaths.DnsStateFile))
                return JsonSerializer.Deserialize<DnsState>(File.ReadAllText(ClientPaths.DnsStateFile), JsonOptions) ?? new DnsState();
        }
        catch { }
        return new DnsState();
    }

    private void SaveState(DnsState state)
    {
        try
        {
            Directory.CreateDirectory(ClientPaths.PrivateDirectory);
            SecureFile.SystemOnlyDirectory(ClientPaths.PrivateDirectory);
            var temp = ClientPaths.DnsStateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            SecureFile.SystemOnly(temp);
            File.Move(temp, ClientPaths.DnsStateFile, overwrite: true);
            SecureFile.SystemOnly(ClientPaths.DnsStateFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist DNS state");
        }
    }
}
