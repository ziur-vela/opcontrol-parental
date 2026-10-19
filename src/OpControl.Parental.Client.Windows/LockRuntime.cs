using System.Text.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class ClientSettings
{
    public ClientOptions Value { get; set; } = new();
}

public sealed class LockRuntime
{
    private readonly object _gate = new();
    private readonly DeviceStateStore _store;
    private readonly StateStore _writer;
    private readonly ILogger<LockRuntime> _logger;
    private DeviceStateDto? _state;
    private RuntimeLockStateDto? _runtime;
    private string _policyFingerprint = "";

    public LockRuntime(DeviceStateStore store, StateStore writer, ILogger<LockRuntime> logger)
    {
        _store = store;
        _writer = writer;
        _logger = logger;
    }

    public DeviceStateDto? DeviceState
    {
        get { lock (_gate) return _state; }
    }

    public RuntimeLockStateDto? Current
    {
        get { lock (_gate) return _runtime; }
    }

    public void Initialize(DeviceStateDto state)
    {
        lock (_gate)
        {
            _state = state;
            ComputeAndWriteLocked();
        }
    }

    public void ApplyServerState(DeviceStateDto state)
    {
        lock (_gate)
        {
            var fingerprint = PolicyFingerprint(state);
            var policyChanged = _policyFingerprint.Length > 0 && fingerprint != _policyFingerprint;
            _policyFingerprint = fingerprint;
            _state = state;
            _store.Save(state);
            ComputeAndWriteLocked();
            if (policyChanged) DnsCache.Flush(_logger);
        }
    }

    private static string PolicyFingerprint(DeviceStateDto state)
    {
        var parts = state.WebsiteRules.Select(r => $"s|{r.Domain}|{r.IsBlocked}|{r.Mode}|{r.ScheduleId}")
            .Concat(state.AppRules.Select(r => $"a|{r.AppIdentity}|{r.IsBlocked}|{r.Mode}|{r.ScheduleId}"))
            .Concat(state.ItemBonuses.Select(b => $"b|{b.RuleType}|{b.Identity}|{b.UntilUtc.Ticks}"))
            .Concat(state.Categories.Select(c => $"c|{c.Key}|{c.Enabled}|{c.Version}"))
            .OrderBy(s => s, StringComparer.Ordinal);
        return string.Join('\n', parts);
    }

    public void ApplyLocalOverride(string option, int? minutes)
    {
        lock (_gate)
        {
            if (_state is null) return;
            var now = DateTime.UtcNow;
            if (option == "unlock-until-next")
            {
                _state = _state with
                {
                    ManualLock = false,
                    BonusUntilUtc = null,
                    BonusMinutes = null,
                    UnlockUntilUtc = LockRules.NextLockCycleUtc(_state.Schedules, TimeZoneInfo.Local, now)
                };
            }
            else if (option == "bonus")
            {
                var bonusMinutes = Math.Clamp(minutes ?? 15, 1, 1440);
                _state = _state with
                {
                    ManualLock = true,
                    UnlockUntilUtc = null,
                    BonusUntilUtc = now.AddMinutes(bonusMinutes),
                    BonusMinutes = bonusMinutes
                };
            }
            _store.Save(_state);
            ComputeAndWriteLocked();
        }
    }

    public RuntimeLockStateDto? ComputeAndWrite()
    {
        lock (_gate)
        {
            return ComputeAndWriteLocked();
        }
    }

    private RuntimeLockStateDto? ComputeAndWriteLocked()
    {
        if (_state is null) return null;
        var now = DateTime.UtcNow;
        var evaluation = LockRules.Evaluate(_state, TimeZoneInfo.Local, now);
        var next = LockRules.NextLockUtc(_state.ManualLock, _state.UnlockUntilUtc, _state.BonusUntilUtc, _state.Schedules, TimeZoneInfo.Local, now);
        var runtime = new RuntimeLockStateDto(
            _state.ChildId,
            _state.ChildName,
            _state.ParentDisplayName,
            evaluation.IsLocked,
            evaluation.Reason,
            evaluation.ScheduleName,
            evaluation.EndsUtc,
            next,
            _state.BonusUntilUtc,
            _state.BonusMinutes,
            now);
        var changed = _runtime is null
            || _runtime.IsLocked != runtime.IsLocked
            || _runtime.Reason != runtime.Reason
            || _runtime.ScheduleName != runtime.ScheduleName
            || _runtime.EndsUtc != runtime.EndsUtc
            || _runtime.NextLockUtc != runtime.NextLockUtc
            || _runtime.BonusUntilUtc != runtime.BonusUntilUtc
            || _runtime.BonusMinutes != runtime.BonusMinutes;
        _runtime = runtime;
        _writer.Save(runtime);
        if (changed)
            _logger.LogInformation("Runtime lock state: locked={Locked} reason={Reason} next={Next}", runtime.IsLocked, runtime.Reason, runtime.NextLockUtc);
        return runtime;
    }
}

public sealed class RuntimeLoopService : BackgroundService
{
    private readonly LockRuntime _runtime;
    private readonly TaskManagerPolicy _policy;
    private readonly ClientSettings _settings;
    private readonly ILogger<RuntimeLoopService> _logger;

    public RuntimeLoopService(LockRuntime runtime, TaskManagerPolicy policy, ClientSettings settings, ILogger<RuntimeLoopService> logger)
    {
        _runtime = runtime;
        _policy = policy;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var runtime = _runtime.ComputeAndWrite();
                _policy.Sync(runtime?.IsLocked ?? false, _settings.Value.MonitoredAccount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Runtime loop failed");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _policy.Restore(_settings.Value.MonitoredAccount);
        await base.StopAsync(cancellationToken);
    }
}
