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
    private readonly ILogger<LockService> _logger;

    public LockService(ParentalDbContext db, IHubContext<ParentalHub> hub, DashboardEventBus events, ILogger<LockService> logger)
    {
        _db = db;
        _hub = hub;
        _events = events;
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
        var childExists = await _db.Children.AnyAsync(c => c.Id == childId && c.FamilyId == familyId, ct);
        if (!childExists)
            throw new InvalidOperationException("Child not found in this family.");

        var state = await EnsureLockStateAsync(familyId, childId, ct);
        var schedules = await _db.Schedules
            .AsNoTracking()
            .Where(s => s.FamilyId == familyId && s.ChildId == childId && s.IsEnabled)
            .ToListAsync(ct);

        var tz = ResolveTimeZone(timeZoneId);
        return BuildDto(state, schedules, tz, DateTime.UtcNow);
    }

    public async Task<LockStateDto> ApplyAsync(Guid familyId, Guid childId, Guid? parentId, LockAction action, int? minutes = null, CancellationToken ct = default)
    {
        var childExists = await _db.Children.AnyAsync(c => c.Id == childId && c.FamilyId == familyId, ct);
        if (!childExists)
            throw new InvalidOperationException("Child not found in this family.");

        var state = await EnsureLockStateAsync(familyId, childId, ct);
        var now = DateTime.UtcNow;
        var schedules = await _db.Schedules
            .Where(s => s.FamilyId == familyId && s.ChildId == childId && s.IsEnabled)
            .ToListAsync(ct);
        var tz = ResolveTimeZone(await _db.Devices
            .Where(d => d.FamilyId == familyId && d.ChildId == childId)
            .Select(d => d.TimeZoneId)
            .FirstOrDefaultAsync(ct));

        switch (action)
        {
            case LockAction.Lock:
                state.ManualLock = true;
                state.UnlockUntilUtc = null;
                state.BonusUntilUtc = null;
                break;
            case LockAction.Unlock:
                state.ManualLock = false;
                state.BonusUntilUtc = null;
                state.UnlockUntilUtc = LockEvaluator.CurrentScheduleEnd(schedules, tz, now);
                _db.OverrideEvents.Add(new OverrideEvent
                {
                    FamilyId = familyId,
                    ChildId = childId,
                    CreatedByParentId = parentId,
                    Option = "dashboard-unlock",
                    CreatedUtc = now
                });
                break;
            case LockAction.Bonus:
                var bonusMinutes = Math.Clamp(minutes ?? 15, 1, 1440);
                state.ManualLock = true;
                state.UnlockUntilUtc = null;
                state.BonusUntilUtc = now.AddMinutes(bonusMinutes);
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

        var dto = BuildDto(state, schedules, tz, now);
        _events.PublishLockState(dto);
        await _hub.Clients.Group(DeviceGroups.Child(childId)).SendAsync(DeviceHubMethods.LockStateChanged, dto);
        _logger.LogInformation("Lock action {Action} applied to child {ChildId} (locked={Locked})", action, childId, dto.IsLocked);
        return dto;
    }

    public async Task<LockStateDto> PublishCurrentStateAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        var dto = await GetStateAsync(familyId, childId, null, ct);
        _events.PublishLockState(dto);
        await _hub.Clients.Group(DeviceGroups.Child(childId)).SendAsync(DeviceHubMethods.LockStateChanged, dto);
        return dto;
    }

    private static LockStateDto BuildDto(LockState state, List<Schedule> schedules, TimeZoneInfo tz, DateTime now)
    {
        var evaluation = LockEvaluator.Evaluate(state, schedules, tz, now);
        return new LockStateDto(state.ChildId, evaluation.IsLocked, state.ManualLock, state.UnlockUntilUtc, state.BonusUntilUtc, now, evaluation.Reason);
    }

    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}
