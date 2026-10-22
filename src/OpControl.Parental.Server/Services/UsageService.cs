// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class UsageService
{
    private readonly ParentalDbContext _db;
    private readonly AlertService _alerts;

    public UsageService(ParentalDbContext db, AlertService alerts)
    {
        _db = db;
        _alerts = alerts;
    }

    public static (DateTime StartUtc, DateTime EndUtc) LocalDayBounds(TimeZoneInfo tz, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var startLocal = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        var endLocal = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
        return (ToUtc(startLocal, tz), ToUtc(endLocal, tz));
    }

    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }
        catch (ArgumentException)
        {
            return TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), tz);
        }
    }

    public static int MinutesForDay(ScreenTimeLimit limit, DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => limit.MondayMinutes,
        DayOfWeek.Tuesday => limit.TuesdayMinutes,
        DayOfWeek.Wednesday => limit.WednesdayMinutes,
        DayOfWeek.Thursday => limit.ThursdayMinutes,
        DayOfWeek.Friday => limit.FridayMinutes,
        DayOfWeek.Saturday => limit.SaturdayMinutes,
        _ => limit.SundayMinutes
    };

    public async Task EnsureChildAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        if (!await _db.Children.AnyAsync(c => c.Id == childId && c.FamilyId == familyId, ct))
            throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));
    }

    public async Task<ScreenTimeLimit> EnsureLimitAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        await EnsureChildAsync(familyId, childId, ct);
        var limit = await _db.ScreenTimeLimits.FirstOrDefaultAsync(l => l.ChildId == childId && l.FamilyId == familyId, ct);
        if (limit is not null) return limit;
        limit = new ScreenTimeLimit { FamilyId = familyId, ChildId = childId };
        _db.ScreenTimeLimits.Add(limit);
        await _db.SaveChangesAsync(ct);
        return limit;
    }

    public async Task<ScreenTimeLimit> SetLimitAsync(Guid familyId, Guid childId, Guid? parentId, bool enabled, bool spokenWarnings, int monday, int tuesday, int wednesday, int thursday, int friday, int saturday, int sunday, CancellationToken ct = default)
    {
        await EnsureChildAsync(familyId, childId, ct);
        var limit = await EnsureLimitAsync(familyId, childId, ct);
        limit.Enabled = enabled;
        limit.SpokenWarnings = spokenWarnings;
        limit.MondayMinutes = Clamp(monday);
        limit.TuesdayMinutes = Clamp(tuesday);
        limit.WednesdayMinutes = Clamp(wednesday);
        limit.ThursdayMinutes = Clamp(thursday);
        limit.FridayMinutes = Clamp(friday);
        limit.SaturdayMinutes = Clamp(saturday);
        limit.SundayMinutes = Clamp(sunday);
        limit.UpdatedByParentId = parentId;
        limit.UpdatedUtc = DateTime.UtcNow;
        _db.OverrideEvents.Add(new OverrideEvent
        {
            FamilyId = familyId,
            ChildId = childId,
            CreatedByParentId = parentId,
            Option = enabled ? "screen-time-limit-set" : "screen-time-limit-disabled",
            Detail = enabled ? $"{monday}/{tuesday}/{wednesday}/{thursday}/{friday}/{saturday}/{sunday} minutes" : null,
            CreatedUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return limit;
    }

    public async Task<int> UsedTodaySecondsAsync(Guid familyId, Guid childId, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct = default)
    {
        var (start, end) = LocalDayBounds(tz, nowUtc);
        var alwaysAllowed = await AlwaysAllowedIdentitiesAsync(familyId, childId, ct);
        var intervals = await _db.UsageIntervals.AsNoTracking()
            .Where(u => u.FamilyId == familyId && u.ChildId == childId && u.Active && u.StartUtc >= start && u.StartUtc < end)
            .Select(u => new { u.AppIdentity, u.DurationSeconds })
            .ToListAsync(ct);
        return intervals
            .Where(i => !alwaysAllowed.Contains(i.AppIdentity))
            .Sum(i => i.DurationSeconds);
    }

    public async Task<List<AppDailyUsageDto>> AppUsageTodayAsync(Guid familyId, Guid childId, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct = default)
    {
        var (start, end) = LocalDayBounds(tz, nowUtc);
        var rules = await _db.AppRules.AsNoTracking()
            .Where(r => r.FamilyId == familyId && r.ChildId == childId)
            .ToListAsync(ct);
        var intervals = await _db.UsageIntervals.AsNoTracking()
            .Where(u => u.FamilyId == familyId && u.ChildId == childId && u.Active && u.StartUtc >= start && u.StartUtc < end)
            .Select(u => new { u.AppIdentity, u.DurationSeconds })
            .ToListAsync(ct);

        var result = new List<AppDailyUsageDto>();
        foreach (var group in intervals.GroupBy(i => i.AppIdentity, StringComparer.OrdinalIgnoreCase))
        {
            var identity = group.Key;
            var rule = rules.FirstOrDefault(r => string.Equals(r.AppIdentity.Trim(), identity.Trim(), StringComparison.OrdinalIgnoreCase));
            var alwaysAllowed = rule?.AlwaysAllowed ?? false;
            result.Add(new AppDailyUsageDto(identity, group.Sum(i => i.DurationSeconds), rule?.DailyLimitMinutes, alwaysAllowed));
        }

        return result;
    }

    public async Task<DailyLimitDto> BuildDailyLimitAsync(Guid familyId, Guid childId, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct = default)
    {
        var limit = await EnsureLimitAsync(familyId, childId, ct);
        var (start, end) = LocalDayBounds(tz, nowUtc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var minutes = MinutesForDay(limit, local.DayOfWeek);
        var used = await UsedTodaySecondsAsync(familyId, childId, tz, nowUtc, ct);
        return new DailyLimitDto(limit.Enabled, minutes, used, start, end);
    }

    public async Task SyncUsageAsync(Device device, UsageBatchRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var earliest = now.AddDays(-30);
        var latest = now.AddMinutes(1);
        var rejected = 0;
        var truncated = 0;

        var intervals = (request.Intervals ?? [])
            .Where(i => i.Id != Guid.Empty && i.DurationSeconds > 0)
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .Take(2000)
            .ToList();
        var accepted = new List<UsageIntervalDto>();
        foreach (var item in intervals)
        {
            var start = item.StartUtc.Kind == DateTimeKind.Utc ? item.StartUtc : item.StartUtc.ToUniversalTime();
            if (start < earliest || start > latest)
            {
                rejected++;
                continue;
            }
            var wallClockCap = Math.Max(1, (int)Math.Ceiling((now - start).TotalSeconds) + 60);
            var duration = Math.Clamp(item.DurationSeconds, 1, 900);
            if (duration > wallClockCap)
            {
                duration = Math.Min(duration, wallClockCap);
                truncated++;
            }
            accepted.Add(item with { StartUtc = start, DurationSeconds = duration });
        }

        if (accepted.Count > 0)
        {
            var ids = accepted.Select(i => i.Id).ToList();
            var existing = await _db.UsageIntervals.AsNoTracking()
                .Where(u => u.DeviceId == device.Id && ids.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);
            var known = existing.ToHashSet();

            var dayTotals = new Dictionary<DateTime, int>();
            foreach (var item in accepted.Where(i => !known.Contains(i.Id) && i.Active))
            {
                var day = item.StartUtc.Date;
                dayTotals[day] = dayTotals.GetValueOrDefault(day) + item.DurationSeconds;
            }
            var overDays = dayTotals.Where(p => p.Value > 86400).Select(p => p.Key).ToHashSet();
            if (overDays.Count > 0)
            {
                var dayStart = overDays.Min();
                var dayEnd = overDays.Max().AddDays(1);
                var stored = await _db.UsageIntervals.AsNoTracking()
                    .Where(u => u.DeviceId == device.Id && u.Active && u.StartUtc >= dayStart && u.StartUtc < dayEnd)
                    .Select(u => new { u.StartUtc, u.DurationSeconds })
                    .ToListAsync(ct);
                foreach (var group in stored.GroupBy(u => u.StartUtc.Date))
                    dayTotals[group.Key] = dayTotals.GetValueOrDefault(group.Key) + group.Sum(u => u.DurationSeconds);
            }

            foreach (var item in accepted.Where(i => !known.Contains(i.Id)))
            {
                var duration = item.DurationSeconds;
                if (item.Active && overDays.Contains(item.StartUtc.Date))
                {
                    var used = dayTotals.GetValueOrDefault(item.StartUtc.Date);
                    var remaining = Math.Max(0, 86400 - (used - duration));
                    if (remaining <= 0)
                    {
                        rejected++;
                        continue;
                    }
                    if (duration > remaining)
                    {
                        duration = remaining;
                        truncated++;
                    }
                }

                _db.UsageIntervals.Add(new UsageInterval
                {
                    Id = item.Id,
                    FamilyId = device.FamilyId,
                    ChildId = device.ChildId,
                    DeviceId = device.Id,
                    AppIdentity = Clean(item.AppIdentity, 255),
                    AppDisplayName = Clean(item.AppDisplayName, 255),
                    StartUtc = item.StartUtc,
                    DurationSeconds = duration,
                    Active = item.Active,
                    CreatedUtc = now
                });
            }
        }

        var sites = (request.Sites ?? [])
            .Where(s => s.Id != Guid.Empty && s.DurationSeconds > 0 && !string.IsNullOrWhiteSpace(s.Domain))
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .Take(2000)
            .ToList();
        var acceptedSites = new List<WebsiteUsageDto>();
        foreach (var item in sites)
        {
            var start = item.StartUtc.Kind == DateTimeKind.Utc ? item.StartUtc : item.StartUtc.ToUniversalTime();
            if (start < earliest || start > latest)
            {
                rejected++;
                continue;
            }
            var wallClockCap = Math.Max(1, (int)Math.Ceiling((now - start).TotalSeconds) + 60);
            var duration = Math.Clamp(item.DurationSeconds, 1, 900);
            if (duration > wallClockCap)
            {
                duration = Math.Min(duration, wallClockCap);
                truncated++;
            }
            acceptedSites.Add(item with { StartUtc = start, DurationSeconds = duration });
        }
        if (acceptedSites.Count > 0)
        {
            var ids = acceptedSites.Select(s => s.Id).ToList();
            var existing = await _db.WebsiteUsages.AsNoTracking()
                .Where(u => u.DeviceId == device.Id && ids.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);
            var known = existing.ToHashSet();
            foreach (var item in acceptedSites.Where(s => !known.Contains(s.Id)))
            {
                _db.WebsiteUsages.Add(new WebsiteUsage
                {
                    Id = item.Id,
                    FamilyId = device.FamilyId,
                    ChildId = device.ChildId,
                    DeviceId = device.Id,
                    Domain = Clean(item.Domain, 255).ToLowerInvariant(),
                    StartUtc = item.StartUtc,
                    DurationSeconds = item.DurationSeconds,
                    CreatedUtc = now
                });
            }
        }

        if (rejected > 0 || truncated > 0)
        {
            var detail = $"{rejected} rejected, {truncated} truncated";
            _db.ClientTamperEvents.Add(new ClientTamperEvent
            {
                FamilyId = device.FamilyId,
                ChildId = device.ChildId,
                DeviceId = device.Id,
                Kind = "usage-sanity",
                Detail = detail,
                OccurredUtc = now
            });
            await _db.SaveChangesAsync(ct);
            await _alerts.AddAsync(device.FamilyId, device.ChildId, device.Id, "tampering",
                new AlertContent("Alert.UsageSanity.Title", "Alert.UsageSanity.Detail", device.Name, detail),
                TimeSpan.FromHours(1), ct);
            return;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task SyncUnaccountedAsync(Device device, UnaccountedBatchRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var earliest = now.AddDays(-30);
        var incoming = (request.Intervals ?? [])
            .Where(i => i.Id != Guid.Empty && i.DurationSeconds > 0)
            .Where(i => i.StartUtc >= earliest && i.StartUtc <= now.AddMinutes(5))
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .Take(2000)
            .ToList();
        if (incoming.Count == 0) return;
        var ids = incoming.Select(i => i.Id).ToList();
        var known = (await _db.UnaccountedIntervals.AsNoTracking()
            .Where(u => u.DeviceId == device.Id && ids.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(ct)).ToHashSet();
        foreach (var item in incoming.Where(i => !known.Contains(i.Id)))
        {
            _db.UnaccountedIntervals.Add(new UnaccountedInterval
            {
                Id = item.Id,
                FamilyId = device.FamilyId,
                ChildId = device.ChildId,
                DeviceId = device.Id,
                StartUtc = item.StartUtc.Kind == DateTimeKind.Utc ? item.StartUtc : item.StartUtc.ToUniversalTime(),
                DurationSeconds = Math.Clamp(item.DurationSeconds, 1, 3600),
                Reason = Clean(item.Reason, 80) is { Length: > 0 } reason ? reason : "helper-down",
                CreatedUtc = now
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> UnaccountedSecondsAsync(Guid familyId, Guid childId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        return await _db.UnaccountedIntervals.AsNoTracking()
            .Where(u => u.FamilyId == familyId && u.ChildId == childId && u.StartUtc >= startUtc && u.StartUtc < endUtc)
            .SumAsync(u => u.DurationSeconds, ct);
    }

    private async Task<HashSet<string>> AlwaysAllowedIdentitiesAsync(Guid familyId, Guid childId, CancellationToken ct)
    {
        var rules = await _db.AppRules.AsNoTracking()
            .Where(r => r.FamilyId == familyId && r.ChildId == childId && r.AlwaysAllowed)
            .Select(r => r.AppIdentity)
            .ToListAsync(ct);
        return rules.Select(r => r.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int Clamp(int minutes) => Math.Clamp(minutes, 0, 1440);
    private static string Clean(string? value, int max) => (value ?? "").Trim() is { Length: > 0 } v ? v[..Math.Min(v.Length, max)] : "";
}
