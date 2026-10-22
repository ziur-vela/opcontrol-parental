// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Hubs;

namespace OpControl.Parental.Server.Services;

public enum LockAction
{
    Lock,
    Unlock,
    Bonus
}

public sealed class LockService
{
    private readonly ParentalDbContext _db;
    private readonly IHubContext<ParentalHub> _hub;
    private readonly DashboardEventBus _events;
    private readonly RuleService _rules;
    private readonly UsageService _usage;
    private readonly AlertService _alerts;
    private readonly ILogger<LockService> _logger;

    public LockService(ParentalDbContext db, IHubContext<ParentalHub> hub, DashboardEventBus events, RuleService rules, UsageService usage, AlertService alerts, ILogger<LockService> logger)
    {
        _db = db;
        _hub = hub;
        _events = events;
        _rules = rules;
        _usage = usage;
        _alerts = alerts;
        _logger = logger;
    }

    public async Task<LockState> EnsureLockStateAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        var state = await _db.LockStates.FirstOrDefaultAsync(s => s.ChildId == childId && s.FamilyId == familyId, ct);
        if (state is not null)
            return state;

        state = new LockState { FamilyId = familyId, ChildId = childId, ManualLock = false, UpdatedUtc = DateTime.UtcNow };
        _db.LockStates.Add(state);
        await _db.SaveChangesAsync(ct);
        return state;
    }

    public async Task<LockStateDto> GetStateAsync(Guid familyId, Guid childId, string? timeZoneId = null, CancellationToken ct = default)
    {
        var child = await _db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.Id == childId && c.FamilyId == familyId, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));
        var state = await EnsureLockStateAsync(familyId, childId, ct);
        var schedules = await LoadScheduleDtosAsync(familyId, childId, ct);
        var zone = ResolveTimeZone(timeZoneId ?? await FirstDeviceTimeZoneAsync(familyId, childId, ct));
        var now = DateTime.UtcNow;
        var daily = await _usage.BuildDailyLimitAsync(familyId, childId, zone, now, ct);
        return BuildDto(state, schedules, zone, now, daily);
    }

    public async Task<DeviceStateDto> GetDeviceStateAsync(Guid familyId, Guid childId, CancellationToken ct = default) =>
        await GetDeviceStateAsync(familyId, childId, null, ct);

    public async Task<DeviceStateDto> GetDeviceStateForDeviceAsync(Device device, CancellationToken ct = default) =>
        await GetDeviceStateAsync(device.FamilyId, device.ChildId, device.ClientLanguage, ct);

    public async Task<DeviceStateDto> GetDeviceStateAsync(Guid familyId, Guid childId, string? deviceLanguage, CancellationToken ct = default)
    {
        var child = await _db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.Id == childId && c.FamilyId == familyId, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));
        var state = await EnsureLockStateAsync(familyId, childId, ct);
        var schedules = await LoadScheduleDtosAsync(familyId, childId, ct);
        var parentName = await ParentDisplayNameAsync(familyId, ct);
        var policy = await _rules.GetDevicePolicyAsync(familyId, childId, ct);
        var tz = ResolveTimeZone(await FirstDeviceTimeZoneAsync(familyId, childId, ct));
        var now = DateTime.UtcNow;
        var dailyLimit = await _usage.BuildDailyLimitAsync(familyId, childId, tz, now, ct);
        var appUsage = await _usage.AppUsageTodayAsync(familyId, childId, tz, now, ct);
        var limitSettings = await _usage.EnsureLimitAsync(familyId, childId, ct);
        var language = AppCulture.Normalize(deviceLanguage ?? await FamilyLanguageAsync(familyId, ct));
        return new DeviceStateDto(
            childId, child.Name, parentName, state.ManualLock, state.UnlockUntilUtc, state.BonusUntilUtc,
            state.BonusMinutes, state.OverridePinHash, schedules, policy.Sites, policy.Apps, policy.Bonuses,
            policy.Categories, now, appUsage, dailyLimit, limitSettings.SpokenWarnings, language);
    }

    public async Task<LockStateDto> ApplyAsync(Guid familyId, Guid childId, Guid? parentId, LockAction action, int? minutes = null, CancellationToken ct = default)
    {
        var childExists = await _db.Children.AnyAsync(c => c.Id == childId && c.FamilyId == familyId, ct);
        if (!childExists)
            throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));

        var state = await EnsureLockStateAsync(familyId, childId, ct);
        var now = DateTime.UtcNow;
        var schedules = await LoadScheduleDtosAsync(familyId, childId, ct);
        var zone = ResolveTimeZone(await FirstDeviceTimeZoneAsync(familyId, childId, ct));
        var daily = await _usage.BuildDailyLimitAsync(familyId, childId, zone, now, ct);
        DateTime? unlockUntil = null;

        switch (action)
        {
            case LockAction.Lock:
                state.ManualLock = true;
                state.UnlockUntilUtc = null;
                state.BonusUntilUtc = null;
                state.BonusMinutes = null;
                _db.OverrideEvents.Add(new OverrideEvent
                {
                    FamilyId = familyId,
                    ChildId = childId,
                    CreatedByParentId = parentId,
                    Option = "dashboard-lock",
                    CreatedUtc = now
                });
                break;
            case LockAction.Unlock:
                state.ManualLock = false;
                state.BonusUntilUtc = null;
                state.BonusMinutes = null;
                unlockUntil = LockRules.NextLockCycleUtc(schedules, zone, now, daily);
                state.UnlockUntilUtc = unlockUntil;
                _db.OverrideEvents.Add(new OverrideEvent
                {
                    FamilyId = familyId,
                    ChildId = childId,
                    CreatedByParentId = parentId,
                    Option = "dashboard-unlock",
                    Detail = $"until {unlockUntil:o}",
                    CreatedUtc = now
                });
                break;
            case LockAction.Bonus:
                var bonusMinutes = Math.Clamp(minutes ?? 15, 1, 1440);
                state.ManualLock = true;
                state.UnlockUntilUtc = null;
                state.BonusUntilUtc = now.AddMinutes(bonusMinutes);
                state.BonusMinutes = bonusMinutes;
                _db.OverrideEvents.Add(new OverrideEvent
                {
                    FamilyId = familyId,
                    ChildId = childId,
                    CreatedByParentId = parentId,
                    Option = "dashboard-bonus",
                    Minutes = bonusMinutes,
                    CreatedUtc = now
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        state.UpdatedUtc = now;
        await _db.SaveChangesAsync(ct);

        var dto = BuildDto(state, schedules, zone, now, daily);
        _events.PublishLockState(dto);
        await PublishDeviceStateAsync(familyId, childId, ct);
        _logger.LogInformation("Lock action {Action} applied to child {ChildId} (unlockUntil={UnlockUntilUtc} locked={Locked} reason={Reason})",
            action, childId, dto.UnlockUntilUtc, dto.IsLocked, dto.Reason);
        return dto;
    }

    public async Task<LockStateDto> ApplyDeviceOverrideAsync(Device device, DeviceOverrideRequest request, CancellationToken ct = default)
    {
        var state = await EnsureLockStateAsync(device.FamilyId, device.ChildId, ct);
        var now = DateTime.UtcNow;
        var applied = request.AppliedUtc is { } requested
            && requested <= now.AddMinutes(1)
            && requested > now.AddHours(-24)
                ? (requested.Kind == DateTimeKind.Utc ? requested : requested.ToUniversalTime())
                : now;
        var schedules = await LoadScheduleDtosAsync(device.FamilyId, device.ChildId, ct);
        var zone = ResolveTimeZone(device.TimeZoneId);
        var daily = await _usage.BuildDailyLimitAsync(device.FamilyId, device.ChildId, zone, now, ct);
        DateTime? unlockUntil = null;
        var option = request.Option switch
        {
            "unlock-until-next" => "pin-unlock-until-next",
            "bonus" => "pin-bonus",
            _ => null
        } ?? throw new InvalidOperationException(AppText.Get("Error.UnknownOverride"));

        if (request.Option == "unlock-until-next")
        {
            state.ManualLock = false;
            state.BonusUntilUtc = null;
            state.BonusMinutes = null;
            unlockUntil = LockRules.NextLockCycleUtc(schedules, zone, applied, daily);
            state.UnlockUntilUtc = unlockUntil;
        }
        else
        {
            var bonusMinutes = Math.Clamp(request.Minutes ?? 15, 1, 1440);
            state.ManualLock = true;
            state.UnlockUntilUtc = null;
            state.BonusUntilUtc = applied.AddMinutes(bonusMinutes);
            state.BonusMinutes = bonusMinutes;
        }

        state.UpdatedUtc = now;
        _db.OverrideEvents.Add(new OverrideEvent
        {
            FamilyId = device.FamilyId,
            ChildId = device.ChildId,
            DeviceId = device.Id,
            Option = option,
            Minutes = request.Option == "bonus" ? Math.Clamp(request.Minutes ?? 15, 1, 1440) : null,
            CreatedUtc = now,
            Detail = request.Option == "unlock-until-next"
                ? $"until {unlockUntil:o}" + (applied == now ? "" : $" applied {applied:o}")
                : applied == now ? null : $"applied {applied:o}"
        });
        await _db.SaveChangesAsync(ct);

        await _alerts.AddAsync(device.FamilyId, device.ChildId, device.Id, "pin-override",
            option == "pin-bonus"
                ? new AlertContent("Alert.PinOverride.BonusTitle", "Alert.PinOverride.BonusDetail",
                    device.Name, (state.BonusMinutes ?? request.Minutes ?? 15).ToString())
                : new AlertContent("Alert.PinOverride.UnlockTitle", "Alert.PinOverride.UnlockDetail", device.Name),
            TimeSpan.FromMinutes(1), ct);

        var dto = BuildDto(state, schedules, zone, now, daily);
        _events.PublishLockState(dto);
        await PublishDeviceStateAsync(device.FamilyId, device.ChildId, ct);
        _logger.LogInformation("Device PIN override {Option} applied to child {ChildId} (unlockUntil={UnlockUntilUtc} locked={Locked} reason={Reason})",
            option, device.ChildId, dto.UnlockUntilUtc, dto.IsLocked, dto.Reason);
        return dto;
    }

    public async Task SetPinAsync(Guid familyId, Guid childId, string pin, CancellationToken ct = default)
    {
        await EnsureChildAsync(familyId, childId, ct);
        if (pin.Length is < 4 or > 12 || !pin.All(char.IsDigit))
            throw new InvalidOperationException(AppText.Get("Error.PinFormat"));
        var state = await EnsureLockStateAsync(familyId, childId, ct);
        state.OverridePinHash = PinHasher.Hash(pin);
        state.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await PublishDeviceStateAsync(familyId, childId, ct);
    }

    public async Task ClearPinAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        await EnsureChildAsync(familyId, childId, ct);
        var state = await EnsureLockStateAsync(familyId, childId, ct);
        state.OverridePinHash = null;
        state.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await PublishDeviceStateAsync(familyId, childId, ct);
    }

    public async Task<bool> HasPinAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        await EnsureChildAsync(familyId, childId, ct);
        var state = await EnsureLockStateAsync(familyId, childId, ct);
        return !string.IsNullOrWhiteSpace(state.OverridePinHash);
    }

    public async Task<List<OverrideEventDto>> GetHistoryAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        return await _db.OverrideEvents.AsNoTracking()
            .Where(e => e.FamilyId == familyId && e.ChildId == childId)
            .OrderByDescending(e => e.CreatedUtc)
            .Take(50)
            .Select(e => new OverrideEventDto(e.Id, e.Option, e.Minutes, e.CreatedUtc, e.Detail))
            .ToListAsync(ct);
    }

    public async Task PublishDeviceStateAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        var devices = await _db.Devices.AsNoTracking()
            .Where(d => d.FamilyId == familyId && d.ChildId == childId && !d.IsRevoked)
            .ToListAsync(ct);
        if (devices.Count == 0)
        {
            var state = await GetDeviceStateAsync(familyId, childId, ct);
            await _hub.Clients.Group(DeviceGroups.Child(childId)).SendAsync(DeviceHubMethods.DeviceStateChanged, state);
            return;
        }
        foreach (var device in devices)
        {
            var state = await GetDeviceStateForDeviceAsync(device, ct);
            await _hub.Clients.Group(DeviceGroups.Device(device.Id)).SendAsync(DeviceHubMethods.DeviceStateChanged, state);
        }
    }

    public async Task<string> FamilyLanguageAsync(Guid familyId, CancellationToken ct = default) =>
        AppCulture.Normalize(await _db.Families.AsNoTracking().Where(f => f.Id == familyId).Select(f => f.DefaultLanguage).FirstOrDefaultAsync(ct));

    public async Task<LockStateDto> PublishCurrentStateAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        var dto = await GetStateAsync(familyId, childId, null, ct);
        _events.PublishLockState(dto);
        await PublishDeviceStateAsync(familyId, childId, ct);
        return dto;
    }

    public async Task EnsureChildAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        if (!await _db.Children.AnyAsync(c => c.Id == childId && c.FamilyId == familyId, ct))
            throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));
    }

    private async Task<List<ScheduleDto>> LoadScheduleDtosAsync(Guid familyId, Guid childId, CancellationToken ct)
    {
        return await _db.Schedules.AsNoTracking()
            .Where(s => s.FamilyId == familyId && s.ChildId == childId)
            .OrderBy(s => s.StartMinutes)
            .Select(s => new ScheduleDto(s.Id, s.Name, s.DaysMask, s.StartMinutes, s.EndMinutes, s.IsEnabled))
            .ToListAsync(ct);
    }

    private async Task<string?> FirstDeviceTimeZoneAsync(Guid familyId, Guid childId, CancellationToken ct)
    {
        return await _db.Devices.AsNoTracking()
            .Where(d => d.FamilyId == familyId && d.ChildId == childId)
            .Select(d => d.TimeZoneId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<string?> ParentDisplayNameAsync(Guid familyId, CancellationToken ct)
    {
        return await _db.Users.AsNoTracking()
            .Where(u => u.FamilyId == familyId)
            .OrderBy(u => u.RoleInFamily == FamilyRole.Owner ? 0 : 1)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct);
    }

    private static LockStateDto BuildDto(LockState state, IReadOnlyList<ScheduleDto> schedules, TimeZoneInfo tz, DateTime now, DailyLimitDto? daily = null)
    {
        var evaluation = LockRules.Evaluate(state.ManualLock, state.UnlockUntilUtc, state.BonusUntilUtc, schedules, tz, now, daily);
        return new LockStateDto(
            state.ChildId,
            evaluation.IsLocked,
            state.ManualLock,
            state.UnlockUntilUtc,
            state.BonusUntilUtc,
            state.BonusMinutes,
            evaluation.EndsUtc,
            now,
            evaluation.Reason,
            evaluation.ScheduleName);
    }

    private static TimeZoneInfo ResolveTimeZone(string? id) => TimeZoneResolver.Resolve(id);
}
