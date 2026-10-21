using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Client;

public sealed class PinPipeServer : BackgroundService
{
    public const string PipeName = "OpControlParental";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly LockRuntime _runtime;
    private readonly LockoutStore _lockout;
    private readonly OverrideSync _overrides;
    private readonly ClientSettings _settings;
    private readonly DataSyncService _sync;
    private readonly MessageSyncService _messages;
    private readonly ILogger<PinPipeServer> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _tickets = new();

    public PinPipeServer(LockRuntime runtime, LockoutStore lockout, OverrideSync overrides, ClientSettings settings, DataSyncService sync, MessageSyncService messages, ILogger<PinPipeServer> logger)
    {
        _runtime = runtime;
        _lockout = lockout;
        _overrides = overrides;
        _settings = settings;
        _sync = sync;
        _messages = messages;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                _ = HandleAsync(pipe, stoppingToken);
                pipe = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PIN pipe accept failed");
                pipe?.Dispose();
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        try
        {
            var account = _settings.Value.MonitoredAccount;
            var name = account.Contains('\\') ? account : $"{Environment.MachineName}\\{account}";
            var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        }
        catch { }
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 5, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var requestType = "unknown";
        try
        {
            var clientPid = WindowsSession.GetPipeClientProcessId(pipe.SafePipeHandle);
            var clientSession = WindowsSession.GetProcessSessionId(clientPid);
            var monitoredSession = WindowsSession.GetMonitoredSessionId(_settings.Value.MonitoredAccount);
            var imagePath = WindowsSession.GetProcessImagePath(clientPid);
            var expectedPath = string.IsNullOrWhiteSpace(_settings.Value.HelperPath)
                ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Helper", "OpControl.Parental.Helper.exe"))
                : _settings.Value.HelperPath!;
            var sessionOk = clientSession > 0 && monitoredSession > 0 && clientSession == monitoredSession;
            var imageOk = !string.IsNullOrWhiteSpace(imagePath)
                && string.Equals(Path.GetFileName(imagePath), "OpControl.Parental.Helper.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFullPath(imagePath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
            if (!sessionOk || !imageOk)
            {
                _sync.QueueTamper(new TamperEventDto("helper-auth-failed",
                    $"Rejected pipe client pid {clientPid} session {clientSession} image {imagePath ?? "unknown"}.", DateTime.UtcNow));
                _logger.LogWarning("Rejected helper pipe client pid={Pid} session={Session} image={Image}", clientPid, clientSession, imagePath);
                await WriteAsync(pipe, new PinPipeResponse(false, Loc.T("Client.Pipe.NotAllowed")), ct);
                return;
            }

            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            for (var i = 0; i < 10; i++)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) return;
                var request = JsonSerializer.Deserialize<PinPipeRequest>(line, JsonOptions);
                requestType = request?.Type ?? "unknown";
                var response = request is null
                    ? new PinPipeResponse(false, Loc.T("Client.Pipe.InvalidRequest"))
                    : await DispatchAsync(request, ct);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PIN pipe request failed type={Type}", requestType);
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private async Task<PinPipeResponse> DispatchAsync(PinPipeRequest request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case "verify-pin":
                return VerifyPin(request.Pin ?? "");
            case "override":
                return await ApplyOverrideAsync(request, ct);
            case "state":
                return new PinPipeResponse(true, State: _runtime.Current);
            case "messages-thread":
                return new PinPipeResponse(true, Messages: await _messages.GetThreadAsync(ct));
            case "message-send":
                _logger.LogInformation("message-send received length={Length}", request.Text?.Length ?? 0);
                if (string.IsNullOrWhiteSpace(request.Text))
                {
                    _logger.LogInformation("message-send rejected: empty");
                    return new PinPipeResponse(false, Loc.T("Client.Messages.Placeholder"));
                }
                var result = await _messages.SendAsync(request.Text, ct);
                _logger.LogInformation("message-send result ok={Ok} queued={Queued} error={Error}",
                    result.Ok, result.Queued, result.Error ?? "");
                return new PinPipeResponse(result.Ok, result.Error, Message: result.Message, Queued: result.Queued);
            case "message-seen":
                if (request.MessageId is not { } seenId) return new PinPipeResponse(false, Loc.T("Client.Pipe.InvalidRequest"));
                await _messages.MarkSeenAsync(seenId, ct);
                return new PinPipeResponse(true);
            case "message-ack":
                if (request.MessageId is not { } ackId) return new PinPipeResponse(false, Loc.T("Client.Pipe.InvalidRequest"));
                await _messages.MarkAcknowledgedAsync(ackId, ct);
                return new PinPipeResponse(true);
            case "open-kid":
                try
                {
                    Directory.CreateDirectory(ClientPaths.UserDirectory);
                    var temp = ClientPaths.UiCommandFile + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(new
                    {
                        tab = request.Tab ?? "home",
                        theme = request.Theme,
                        language = request.Language,
                        messagesOnly = request.MessagesOnly ?? false,
                        utc = DateTime.UtcNow
                    }, JsonOptions));
                    File.Move(temp, ClientPaths.UiCommandFile, overwrite: true);
                    return new PinPipeResponse(true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not queue the My OpControl window command");
                    return new PinPipeResponse(false, ex.Message);
                }
            default:
                return new PinPipeResponse(false, Loc.T("Client.Pipe.UnknownRequest"));
        }
    }

    private PinPipeResponse VerifyPin(string pin)
    {
        var check = _lockout.Check();
        if (check.LockedOut)
            return new PinPipeResponse(false, Loc.T("Client.Pipe.TooMany"), true, check.RemainingSeconds, 0);

        var hash = _runtime.DeviceState?.OverridePinHash;
        if (string.IsNullOrWhiteSpace(hash))
            return new PinPipeResponse(false, Loc.T("Client.Pipe.NoPin"));

        if (!PinHasher.Verify(hash, pin))
        {
            var failure = _lockout.RegisterFailure();
            return failure.LockedOut
                ? new PinPipeResponse(false, Loc.T("Client.Pipe.TooMany"), true, failure.RemainingSeconds, 0)
                : new PinPipeResponse(false, Loc.T("Client.Pipe.Incorrect"), false, null, failure.AttemptsRemaining);
        }

        _lockout.Reset();
        var ticket = Guid.NewGuid().ToString("N");
        _tickets[ticket] = DateTime.UtcNow.AddMinutes(2);
        foreach (var expired in _tickets.Where(t => t.Value < DateTime.UtcNow).Take(20).ToList())
            _tickets.TryRemove(expired.Key, out _);
        return new PinPipeResponse(true, Ticket: ticket);
    }

    private async Task<PinPipeResponse> ApplyOverrideAsync(PinPipeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Ticket) || !_tickets.TryRemove(request.Ticket, out var expires) || expires < DateTime.UtcNow)
            return new PinPipeResponse(false, Loc.T("Client.Pipe.Expired"));

        if (request.Option is not ("unlock-until-next" or "bonus"))
            return new PinPipeResponse(false, Loc.T("Client.Pipe.UnknownOption"));

        await _overrides.ApplyAsync(request.Option, request.Minutes, _settings.Value, ct);
        return new PinPipeResponse(true, State: _runtime.Current);
    }

    private static async Task WriteAsync(NamedPipeServerStream pipe, PinPipeResponse response, CancellationToken ct)
    {
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
    }
}
