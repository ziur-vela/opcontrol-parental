// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

/// <summary>
/// Builds the read-only summary the child window shows. It is scoped to the device's own
/// family and child and deliberately omits anything the child must not see (PIN, alerts,
/// parent emails, co-parent permissions, remote sessions, category domain lists).
/// </summary>
public sealed class ChildSummaryService
{
    private static readonly HashSet<string> LockedOptions = new(StringComparer.Ordinal)
    {
        "dashboard-lock", "pin-lock", "schedule-start", "daily-limit-reached"
    };
    private static readonly HashSet<string> UnlockedOptions = new(StringComparer.Ordinal)
    {
        "dashboard-unlock", "pin-unlock-until-next", "schedule-end"
    };
    private static readonly HashSet<string> BonusOptions = new(StringComparer.Ordinal)
    {
        "dashboard-bonus", "pin-bonus", "item-bonus-site", "item-bonus-app"
    };

    private readonly ParentalDbContext _db;

    public ChildSummaryService(ParentalDbContext db)
    {
        _db = db;
    }

    public async Task<ChildSummaryDto> BuildAsync(Device device, CancellationToken ct = default)
    {
        var child = await _db.Children.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == device.ChildId && c.FamilyId == device.FamilyId, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));

        var tz = TimeZoneResolver.Resolve(device.TimeZoneId);
        var now = DateTime.UtcNow;
        var (dayStart, dayEnd) = UsageService.LocalDayBounds(tz, now);
        var weekStart = LocalWeekStartUtc(tz, now);
        var weekFloor = now.AddDays(-7);

        var limit = await _db.ScreenTimeLimits.AsNoTracking()
            .FirstOrDefaultAsync(l => l.FamilyId == device.FamilyId && l.ChildId == device.ChildId, ct);

        var schedules = await _db.Schedules.AsNoTracking()
            .Where(s => s.FamilyId == device.FamilyId && s.ChildId == device.ChildId)
            .OrderBy(s => s.StartMinutes)
            .Select(s => new SummaryScheduleDto(s.Id, s.Name, s.DaysMask, s.StartMinutes, s.EndMinutes, s.IsEnabled))
            .ToListAsync(ct);
        var scheduleNames = schedules.ToDictionary(s => s.Id, s => s.Name);

        var siteRules = await _db.WebsiteRules.AsNoTracking()
            .Where(r => r.FamilyId == device.FamilyId && r.ChildId == device.ChildId)
            .OrderBy(r => r.Domain)
            .ToListAsync(ct);
        var appRules = await _db.AppRules.AsNoTracking()
            .Where(r => r.FamilyId == device.FamilyId && r.ChildId == device.ChildId)
            .OrderBy(r => r.DisplayName)
            .ToListAsync(ct);

        var categories = await (from toggle in _db.ChildCategories.AsNoTracking()
                                join category in _db.Categories.AsNoTracking() on toggle.CategoryKey equals category.Key
                                where toggle.FamilyId == device.FamilyId
                                      && toggle.ChildId == device.ChildId
                                      && toggle.Enabled
                                orderby category.Name
                                select new SummaryCategoryDto(category.Key, category.Name))
            .ToListAsync(ct);

        var intervals = await _db.UsageIntervals.AsNoTracking()
            .Where(u => u.FamilyId == device.FamilyId && u.ChildId == device.ChildId
                && u.Active && u.StartUtc >= weekFloor && u.StartUtc < dayEnd)
            .Select(u => new UsageRow(u.AppIdentity, u.AppDisplayName, u.StartUtc, u.DurationSeconds))
            .ToListAsync(ct);

        var alwaysAllowed = appRules.Where(r => r.AlwaysAllowed)
            .Select(r => r.AppIdentity.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var usedToday = intervals
            .Where(i => i.StartUtc >= dayStart && i.StartUtc < dayEnd && !alwaysAllowed.Contains(i.AppIdentity.Trim()))
            .Sum(i => i.DurationSeconds);
        var usedWeek = intervals
            .Where(i => i.StartUtc >= weekStart && i.StartUtc < dayEnd && !alwaysAllowed.Contains(i.AppIdentity.Trim()))
            .Sum(i => i.DurationSeconds);

        var todayUsage = GroupUsage(intervals.Where(i => i.StartUtc >= dayStart && i.StartUtc < dayEnd), appRules);
        var weekUsage = GroupUsage(intervals.Where(i => i.StartUtc >= weekStart && i.StartUtc < dayEnd), appRules);
        var todayByApp = todayUsage.ToDictionary(u => u.Identity.Trim(), u => u.Seconds, StringComparer.OrdinalIgnoreCase);

        var websites = siteRules.Select(r => new SummaryWebsiteDto(
            r.Domain,
            !r.IsBlocked,
            r.Mode == RuleMode.Always,
            r.Mode == RuleMode.Scheduled && r.ScheduleId is { } id ? scheduleNames.GetValueOrDefault(id) : null)).ToList();

        var apps = appRules.Select(r => new SummaryAppDto(
            r.AppIdentity,
            r.DisplayName,
            !r.IsBlocked,
            r.AlwaysAllowed,
            r.Mode == RuleMode.Scheduled && r.ScheduleId is { } id ? scheduleNames.GetValueOrDefault(id) : null,
            r.DailyLimitMinutes,
            todayByApp.GetValueOrDefault(r.AppIdentity.Trim(), 0))).ToList();

        var visits = await _db.VisitRecords.AsNoTracking()
            .Where(v => v.FamilyId == device.FamilyId && v.ChildId == device.ChildId && v.VisitedUtc >= weekFloor)
            .OrderByDescending(v => v.VisitedUtc)
            .Take(200)
            .Select(v => new SummaryVisitDto(v.Domain, v.VisitedUtc, v.Blocked))
            .ToListAsync(ct);

        var overrideEvents = await _db.OverrideEvents.AsNoTracking()
            .Where(e => e.FamilyId == device.FamilyId && e.ChildId == device.ChildId && e.CreatedUtc >= weekFloor)
            .OrderByDescending(e => e.CreatedUtc)
            .Take(120)
            .Select(e => new { e.Option, e.Minutes, e.CreatedUtc })
            .ToListAsync(ct);
        var history = overrideEvents
            .Select(e => new { Kind = HistoryKind(e.Option), e.Minutes, e.CreatedUtc })
            .Where(e => e.Kind is not null)
            .Take(40)
            .Select(e => new SummaryHistoryDto(e.Kind!, e.Minutes, e.CreatedUtc))
            .ToList();

        var recent = await _db.ChildMessages.AsNoTracking()
            .Where(m => m.FamilyId == device.FamilyId && m.ChildId == device.ChildId && m.DeletedUtc == null)
            .OrderByDescending(m => m.CreatedUtc)
            .Take(20)
            .ToListAsync(ct);
        recent.Reverse();

        var parentNames = await _db.Users.AsNoTracking()
            .Where(u => u.FamilyId == device.FamilyId)
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var recentDtos = recent.Select(m => ToMessageDto(m, child.Name, parentNames)).ToList();
        var unread = await _db.ChildMessages.AsNoTracking()
            .CountAsync(m => m.FamilyId == device.FamilyId && m.ChildId == device.ChildId
                && m.Direction == MessageDirection.ParentToChild && m.SeenUtc == null && m.DeletedUtc == null, ct);

        var localDay = TimeZoneInfo.ConvertTimeFromUtc(now, tz).DayOfWeek;
        var weekdays = Enumerable.Range(0, 7)
            .Select(day => new SummaryWeekdayDto(day, limit is null ? 0 : MinutesForDay(limit, (DayOfWeek)day)))
            .ToList();
        var todayMinutes = limit is { Enabled: true } ? MinutesForDay(limit, localDay) : 0;

        return new ChildSummaryDto(
            child.Id,
            child.Name,
            device.Name,
            now,
            limit?.Enabled ?? false,
            todayMinutes,
            usedToday,
            usedWeek,
            weekdays,
            schedules,
            categories,
            websites,
            apps,
            todayUsage,
            weekUsage,
            visits,
            history,
            recentDtos,
            unread);
    }

    public static DateTime LocalWeekStartUtc(TimeZoneInfo tz, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        var startLocal = DateTime.SpecifyKind(local.Date.AddDays(-daysSinceMonday), DateTimeKind.Unspecified);
        return UsageService.ToUtc(startLocal, tz);
    }

    private static int MinutesForDay(ScreenTimeLimit limit, DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => limit.MondayMinutes,
        DayOfWeek.Tuesday => limit.TuesdayMinutes,
        DayOfWeek.Wednesday => limit.WednesdayMinutes,
        DayOfWeek.Thursday => limit.ThursdayMinutes,
        DayOfWeek.Friday => limit.FridayMinutes,
        DayOfWeek.Saturday => limit.SaturdayMinutes,
        _ => limit.SundayMinutes
    };

    private sealed record UsageRow(string AppIdentity, string AppDisplayName, DateTime StartUtc, int DurationSeconds);

    private static List<SummaryAppUsageDto> GroupUsage(
        IEnumerable<UsageRow> intervals,
        IReadOnlyList<AppRule> rules)
    {
        var names = rules
            .GroupBy(r => r.AppIdentity.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);
        return intervals
            .Where(i => !string.IsNullOrWhiteSpace(i.AppIdentity))
            .Where(i => !AppUsageExclusions.IsExcluded(i.AppIdentity, i.AppDisplayName))
            .GroupBy(i => i.AppIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var identity = g.Key.Trim();
                var display = names.GetValueOrDefault(identity)
                    ?? g.Select(i => i.AppDisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                    ?? identity;
                return new SummaryAppUsageDto(identity, display, g.Sum(i => i.DurationSeconds));
            })
            .OrderByDescending(a => a.Seconds)
            .ToList();
    }

    private static string? HistoryKind(string option)
    {
        if (LockedOptions.Contains(option)) return "locked";
        if (UnlockedOptions.Contains(option)) return "unlocked";
        if (BonusOptions.Contains(option)) return "bonus";
        return null;
    }

    private static ChildMessageDto ToMessageDto(ChildMessage message, string childName,
        IReadOnlyDictionary<Guid, string> parentNames)
    {
        var status = message.Direction == MessageDirection.ChildToParent
            ? message.ParentReadUtc is not null ? MessageStatus.Seen
                : message.DeliveredUtc is not null ? MessageStatus.Delivered : MessageStatus.Sent
            : message.AcknowledgedUtc is not null ? MessageStatus.Acknowledged
                : message.SeenUtc is not null ? MessageStatus.Seen
                : message.DeliveredUtc is not null ? MessageStatus.Delivered
                : MessageStatus.Sent;
        var sender = message.Direction == MessageDirection.ParentToChild
            ? (message.SenderParentId is { } id && parentNames.TryGetValue(id, out var name) ? name : AppText.Get("Common.ParentFallback"))
            : childName;
        return new ChildMessageDto(
            message.Id, message.ChildId, message.DeviceId, message.Direction, message.SenderParentId,
            sender, message.Text, message.Preset, message.SpeakAloud, message.RequireAck,
            message.CreatedUtc, message.DeliveredUtc, message.SeenUtc, message.AcknowledgedUtc,
            message.ParentReadUtc, status, false);
    }
}
