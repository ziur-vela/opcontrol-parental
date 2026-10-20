using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Client;

/// <summary>
/// Relays screen/camera JPEG frames from the helper to the server while a parent
/// has an active remote-view session. The helper owns capture and the camera eye.
/// </summary>
public sealed class RemoteRelayService
{
    private const string PipeName = "OpControlParental.Remote";
    private readonly ClientSettings _settings;
    private readonly CredentialStore _credentials;
    private readonly ILogger<RemoteRelayService> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    public RemoteRelayService(ClientSettings settings, CredentialStore credentials, ILogger<RemoteRelayService> logger)
    {
        _settings = settings;
        _credentials = credentials;
        _logger = logger;
    }

    public void Start(RemoteSessionRequest request)
    {
        if (_active.ContainsKey(request.SessionId)) return;
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        if (!_active.TryAdd(request.SessionId, cts))
        {
            cts.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await RunAsync(request, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Remote session {SessionId} failed", request.SessionId); }
            finally
            {
                if (_active.TryRemove(request.SessionId, out var active)) active.Dispose();
                _logger.LogInformation("Remote session {SessionId} stopped", request.SessionId);
            }
        });
    }

    public void Stop(Guid sessionId)
    {
        if (_active.TryRemove(sessionId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    private async Task RunAsync(RemoteSessionRequest request, CancellationToken ct)
    {
        var credential = _credentials.Load();
        if (credential is null || !credential.IsUsable)
        {
            _logger.LogWarning("Remote session requested but the device has no credential");
            return;
        }

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + credential.Credential);
        var server = _settings.Value.ServerUrl.TrimEnd('/');
        var wsUri = new Uri(server.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            + "/ws/device-remote/" + request.SessionId);
        await ws.ConnectAsync(wsUri, ct);

        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(3000, ct);
        }
        catch
        {
            await SendStatusAsync(ws, Loc.T("Client.Pipe.NoService"), ct);
            await CloseAsync(ws, "helper-unavailable", ct);
            return;
        }

        using var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { kind = request.Kind == RemoteSessionKind.Camera ? "camera" : "screen", showNotice = request.ShowNotice, noticeText = request.NoticeText }));

        var header = new byte[5];
        var buffer = new byte[1024 * 1024];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open && pipe.IsConnected)
        {
            await ReadExactAsync(pipe, header, ct);
            var kind = header[0];
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if (length <= 0 || length > buffer.Length)
            {
                await SendStatusAsync(ws, Loc.T("Client.Pipe.InvalidResponse"), ct);
                break;
            }
            var payload = length == buffer.Length ? buffer : new byte[length];
            await ReadExactAsync(pipe, payload.AsMemory(0, length), ct);
            if (kind == 2)
            {
                await SendStatusAsync(ws, Encoding.UTF8.GetString(payload, 0, length), ct);
                if (Encoding.UTF8.GetString(payload, 0, length).StartsWith("No camera", StringComparison.OrdinalIgnoreCase)) break;
                continue;
            }
            await ws.SendAsync(payload.AsMemory(0, length), WebSocketMessageType.Binary, true, ct);
        }

        await CloseAsync(ws, "relay-ended", ct);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> target, CancellationToken ct)
    {
        var read = 0;
        while (read < target.Length)
        {
            var n = await stream.ReadAsync(target[read..], ct);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static Task SendStatusAsync(ClientWebSocket ws, string message, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "status", message })),
            WebSocketMessageType.Text, true, ct);

    private static async Task CloseAsync(ClientWebSocket ws, string reason, CancellationToken ct)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        catch { }
    }
}
