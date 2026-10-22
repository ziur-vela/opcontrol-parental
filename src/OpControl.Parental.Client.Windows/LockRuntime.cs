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
    private readonly ClientSettings _settings;
    private readonly ILogger<LockRuntime> _logger;
    private DeviceStateDto? _state;
    private RuntimeLockStateDto? _runtime;
    private string _policyFingerprint = "";
    private int _localActiveSeconds;
    private readonly Dictionary<string, int> _localAppSeconds = new(StringComparer.OrdinalIgnoreCase);

    public LockRuntime(DeviceStateStore store, StateStore writer, ClientSettings settings, ILogger<LockRuntime> logger)
    {
        _store = store;
        _writer = writer;
        _settings = settings;
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
            _localActiveSeconds = 0;
            _localAppSeconds.Clear();
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

    public RuntimeLockStateDto? ApplyLocalOverride(string option, int? minutes)
    {
        lock (_gate)
        {
            if (_state is null) return null;
            var now = DateTime.UtcNow;
            if (option == "unlock-until-next")
            {
                _state = _state with
                {
                    ManualLock = false,
                    BonusUntilUtc = null,
                    BonusMinutes = null,
                    UnlockUntilUtc = LockRules.NextLockCycleUtc(_state.Schedules, TimeZoneInfo.Local, now, _state.DailyLimit)
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
            var runtime = ComputeAndWriteLocked();
            _logger.LogInformation("Local override {Option} applied: unlockUntil={UnlockUntilUtc} locked={Locked} reason={Reason}",
                option, _state.UnlockUntilUtc, runtime?.IsLocked, runtime?.Reason);
            return runtime;
        }
    }

    public void AddLocalUsage(string identity, int seconds, bool alwaysAllowed = false)
    {
        if (seconds <= 0) return;
        lock (_gate)
        {
            if (alwaysAllowed)
                _localAppSeconds.TryAdd(identity, 0);
            else
                _localActiveSeconds += seconds;
            if (!string.IsNullOrWhiteSpace(identity))
                _localAppSeconds[identity] = _localAppSeconds.GetValueOrDefault(identity) + seconds;
        }
    }

    public bool IsAppBlocked(string identity, string? path, out string? reason)
    {
        lock (_gate)
        {
            reason = null;
            var state = AdjustedStateLocked();
            if (state is null) return false;
            return PolicyRules.IsAppBlocked(state, identity, path, TimeZoneInfo.Local, DateTime.UtcNow, out reason);
        }
    }

    private DeviceStateDto? AdjustedStateLocked()
    {
        if (_state is null) return null;
        var now = DateTime.UtcNow;
        var daily = _state.DailyLimit;
        if (daily is not null && now >= daily.DayEndUtc)
            daily = daily with { UsedTodaySeconds = 0, DayStartUtc = daily.DayEndUtc, DayEndUtc = daily.DayEndUtc.AddDays(1) };
        if (daily is not null)
            daily = daily with { UsedTodaySeconds = Math.Max(0, daily.UsedTodaySeconds) + _localActiveSeconds };

        var usage = (_state.AppUsage ?? []).ToList();
        foreach (var pair in _localAppSeconds)
        {
            var index = usage.FindIndex(u => u.Identity.Trim().Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                usage[index] = usage[index] with { UsedTodaySeconds = usage[index].UsedTodaySeconds + pair.Value };
            else
            {
                var rule = _state.AppRules.FirstOrDefault(r => PolicyRules.MatchesApp(r.AppIdentity, pair.Key, null));
                usage.Add(new AppDailyUsageDto(pair.Key, pair.Value, rule?.DailyLimitMinutes, rule?.AlwaysAllowed ?? false));
            }
        }

        return _state with { DailyLimit = daily, AppUsage = usage };
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
        var state = AdjustedStateLocked() ?? _state;
        var tz = TimeZoneInfo.Local;
        var evaluation = LockRules.Evaluate(state, tz, now);
        var next = LockRules.NextLockUtc(state.ManualLock, state.UnlockUntilUtc, state.BonusUntilUtc, state.Schedules, tz, now);
        string? unlockSchedule = null;
        if (evaluation.Reason == "override" && evaluation.EndsUtc is { } unlockEnd)
        {
            var active = LockRules.ActiveSchedule(state.Schedules, tz, now);
            if (active is not null && LockRules.CurrentScheduleEnd(active, tz, now) == unlockEnd)
                unlockSchedule = active.Name;
            else if (LockRules.NextScheduleStartWithName(state.Schedules, tz, now) is { } upcoming && upcoming.Utc == unlockEnd)
                unlockSchedule = upcoming.ScheduleName;
        }
        var runtime = new RuntimeLockStateDto(
            state.ChildId,
            state.ChildName,
            state.ParentDisplayName,
            evaluation.IsLocked,
            evaluation.Reason,
            evaluation.ScheduleName,
            evaluation.EndsUtc,
            next,
            state.BonusUntilUtc,
            state.BonusMinutes,
            now,
            LockRules.DailyLimitRemainingSeconds(state.DailyLimit, now),
            state.DailyLimit is { Enabled: true } ? state.DailyLimit.TodayMinutes : null,
            state.SpokenWarnings,
            _settings.Value.IdleThresholdSeconds,
            state.Language,
            unlockSchedule);
        var changed = _runtime is null
            || _runtime.IsLocked != runtime.IsLocked
            || _runtime.Reason != runtime.Reason
            || _runtime.ScheduleName != runtime.ScheduleName
            || _runtime.EndsUtc != runtime.EndsUtc
            || _runtime.NextLockUtc != runtime.NextLockUtc
            || _runtime.BonusUntilUtc != runtime.BonusUntilUtc
            || _runtime.BonusMinutes != runtime.BonusMinutes
            || _runtime.DailyLimitRemainingSeconds != runtime.DailyLimitRemainingSeconds
            || _runtime.DailyLimitMinutes != runtime.DailyLimitMinutes;
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
