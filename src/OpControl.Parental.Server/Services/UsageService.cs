using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class UsageService
{
    private readonly ParentalDbContext _db;

    public UsageService(ParentalDbContext db) => _db = db;

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

    public async Task<ScreenTimeLimit> EnsureLimitAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        var limit = await _db.ScreenTimeLimits.FirstOrDefaultAsync(l => l.ChildId == childId && l.FamilyId == familyId, ct);
        if (limit is not null) return limit;
        limit = new ScreenTimeLimit { FamilyId = familyId, ChildId = childId };
        _db.ScreenTimeLimits.Add(limit);
        await _db.SaveChangesAsync(ct);
        return limit;
    }

    public async Task<ScreenTimeLimit> SetLimitAsync(Guid familyId, Guid childId, Guid? parentId, bool enabled, bool spokenWarnings, int monday, int tuesday, int wednesday, int thursday, int friday, int saturday, int sunday, CancellationToken ct = default)
    {
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
        var latest = now.AddMinutes(10);

        var intervals = (request.Intervals ?? [])
            .Where(i => i.Id != Guid.Empty && i.DurationSeconds > 0)
            .Where(i => i.StartUtc >= earliest && i.StartUtc <= latest)
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .Take(2000)
            .ToList();
        if (intervals.Count > 0)
        {
            var ids = intervals.Select(i => i.Id).ToList();
            var existing = await _db.UsageIntervals.AsNoTracking()
                .Where(u => u.DeviceId == device.Id && ids.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);
            var known = existing.ToHashSet();
            foreach (var item in intervals.Where(i => !known.Contains(i.Id)))
            {
                _db.UsageIntervals.Add(new UsageInterval
                {
                    Id = item.Id,
                    FamilyId = device.FamilyId,
                    ChildId = device.ChildId,
                    DeviceId = device.Id,
                    AppIdentity = Clean(item.AppIdentity, 255),
                    AppDisplayName = Clean(item.AppDisplayName, 255),
                    StartUtc = item.StartUtc.Kind == DateTimeKind.Utc ? item.StartUtc : item.StartUtc.ToUniversalTime(),
                    DurationSeconds = Math.Clamp(item.DurationSeconds, 1, 900),
                    Active = item.Active,
                    CreatedUtc = now
                });
            }
        }

        var sites = (request.Sites ?? [])
            .Where(s => s.Id != Guid.Empty && s.DurationSeconds > 0 && !string.IsNullOrWhiteSpace(s.Domain))
            .Where(s => s.StartUtc >= earliest && s.StartUtc <= latest)
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .Take(2000)
            .ToList();
        if (sites.Count > 0)
        {
            var ids = sites.Select(s => s.Id).ToList();
            var existing = await _db.WebsiteUsages.AsNoTracking()
                .Where(u => u.DeviceId == device.Id && ids.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);
            var known = existing.ToHashSet();
            foreach (var item in sites.Where(s => !known.Contains(s.Id)))
            {
                _db.WebsiteUsages.Add(new WebsiteUsage
                {
                    Id = item.Id,
                    FamilyId = device.FamilyId,
                    ChildId = device.ChildId,
                    DeviceId = device.Id,
                    Domain = Clean(item.Domain, 255).ToLowerInvariant(),
                    StartUtc = item.StartUtc.Kind == DateTimeKind.Utc ? item.StartUtc : item.StartUtc.ToUniversalTime(),
                    DurationSeconds = Math.Clamp(item.DurationSeconds, 1, 900),
                    CreatedUtc = now
                });
            }
        }

        await _db.SaveChangesAsync(ct);
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
