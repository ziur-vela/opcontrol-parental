using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using OpControl.Parental.Contracts;

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
    private readonly ILogger<PinPipeServer> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _tickets = new();

    public PinPipeServer(LockRuntime runtime, LockoutStore lockout, OverrideSync overrides, ClientSettings settings, DataSyncService sync, ILogger<PinPipeServer> logger)
    {
        _runtime = runtime;
        _lockout = lockout;
        _overrides = overrides;
        _settings = settings;
        _sync = sync;
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
                await WriteAsync(pipe, new PinPipeResponse(false, "Not allowed from this process."), ct);
                return;
            }

            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            for (var i = 0; i < 10; i++)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) return;
                var request = JsonSerializer.Deserialize<PinPipeRequest>(line, JsonOptions);
                var response = request is null
                    ? new PinPipeResponse(false, "Invalid request.")
                    : await DispatchAsync(request, ct);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PIN pipe request failed");
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
            default:
                return new PinPipeResponse(false, "Unknown request.");
        }
    }

    private PinPipeResponse VerifyPin(string pin)
    {
        var check = _lockout.Check();
        if (check.LockedOut)
            return new PinPipeResponse(false, "Too many attempts.", true, check.RemainingSeconds, 0);

        var hash = _runtime.DeviceState?.OverridePinHash;
        if (string.IsNullOrWhiteSpace(hash))
            return new PinPipeResponse(false, "No override PIN is set.");

        if (!PinHasher.Verify(hash, pin))
        {
            var failure = _lockout.RegisterFailure();
            return failure.LockedOut
                ? new PinPipeResponse(false, "Too many attempts.", true, failure.RemainingSeconds, 0)
                : new PinPipeResponse(false, "Incorrect PIN.", false, null, failure.AttemptsRemaining);
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
            return new PinPipeResponse(false, "PIN verification expired.");

        if (request.Option is not ("unlock-until-next" or "bonus"))
            return new PinPipeResponse(false, "Unknown override option.");

        await _overrides.ApplyAsync(request.Option, request.Minutes, _settings.Value, ct);
        return new PinPipeResponse(true, State: _runtime.Current);
    }

    private static async Task WriteAsync(NamedPipeServerStream pipe, PinPipeResponse response, CancellationToken ct)
    {
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
    }
}
