using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class RuleService
{
    private static readonly Regex DomainPattern = new(
        @"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ParentalDbContext _db;

    public RuleService(ParentalDbContext db) => _db = db;

    public static string NormalizeDomain(string input)
    {
        var value = (input ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0) throw new InvalidOperationException("Enter a domain.");
        if (value.StartsWith("http://", StringComparison.Ordinal)) value = value[7..];
        if (value.StartsWith("https://", StringComparison.Ordinal)) value = value[8..];
        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];
        var at = value.IndexOf('@');
        if (at >= 0) value = value[(at + 1)..];
        var colon = value.IndexOf(':');
        if (colon >= 0) value = value[..colon];
        value = value.Trim('.');
        if (value.StartsWith("www.", StringComparison.Ordinal)) value = value[4..];
        if (!DomainPattern.IsMatch(value))
            throw new InvalidOperationException("That does not look like a valid domain.");
        return value;
    }

    public static string NormalizeAppIdentity(string identity)
    {
        var value = (identity ?? "").Trim();
        if (value.Length is < 2 or > 255) throw new InvalidOperationException("Choose an app.");
        return value;
    }

    public async Task<(IReadOnlyList<WebsiteRuleDto> Sites, IReadOnlyList<AppRuleDto> Apps, IReadOnlyList<ItemBonusDto> Bonuses, IReadOnlyList<CategoryToggleDto> Categories)> GetDevicePolicyAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var sites = await _db.WebsiteRules.AsNoTracking()
            .Where(r => r.FamilyId == familyId && r.ChildId == childId)
            .OrderBy(r => r.Domain)
            .Select(r => new WebsiteRuleDto(r.Id, r.Domain, r.IsBlocked, r.Mode, r.ScheduleId, r.UpdatedUtc))
            .ToListAsync(ct);

        var apps = await _db.AppRules.AsNoTracking()
            .Where(r => r.FamilyId == familyId && r.ChildId == childId)
            .OrderBy(r => r.DisplayName)
            .Select(r => new AppRuleDto(r.Id, r.AppIdentity, r.DisplayName, r.Publisher, r.IsBlocked, r.Mode, r.ScheduleId, r.UpdatedUtc))
            .ToListAsync(ct);

        var bonuses = await _db.ItemBonuses.AsNoTracking()
            .Where(b => b.FamilyId == familyId && b.ChildId == childId && b.UntilUtc > now)
            .OrderBy(b => b.UntilUtc)
            .Select(b => new ItemBonusDto(b.RuleType, b.Identity, b.UntilUtc, b.Minutes))
            .ToListAsync(ct);

        var categories = await _db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var toggles = await _db.ChildCategories.AsNoTracking()
            .Where(c => c.FamilyId == familyId && c.ChildId == childId)
            .ToDictionaryAsync(c => c.CategoryKey, c => c.Enabled, ct);
        var categoryDtos = categories
            .Select(c => new CategoryToggleDto(c.Key, c.Name, toggles.GetValueOrDefault(c.Key), c.Version))
            .ToList();

        return (sites, apps, bonuses, categoryDtos);
    }

    public async Task<WebsiteRule> UpsertWebsiteRuleAsync(Guid familyId, Guid childId, Guid? parentId, string domain, bool blocked, RuleMode mode, Guid? scheduleId, CancellationToken ct = default)
    {
        domain = NormalizeDomain(domain);
        if (!blocked && mode != RuleMode.Always)
            throw new InvalidOperationException("Always-allow rules cannot be tied to a schedule.");
        if (mode == RuleMode.Scheduled && scheduleId is null)
            throw new InvalidOperationException("Pick a schedule for a scheduled rule.");
        if (scheduleId is { } sid)
        {
            var ok = await _db.Schedules.AnyAsync(s => s.Id == sid && s.FamilyId == familyId && s.ChildId == childId, ct);
            if (!ok) throw new InvalidOperationException("Schedule not found for this child.");
        }

        var rule = await _db.WebsiteRules.FirstOrDefaultAsync(r => r.FamilyId == familyId && r.ChildId == childId && r.Domain == domain, ct);
        if (rule is null)
        {
            rule = new WebsiteRule
            {
                FamilyId = familyId,
                ChildId = childId,
                Domain = domain,
                IsBlocked = blocked,
                Mode = mode,
                ScheduleId = scheduleId
            };
            _db.WebsiteRules.Add(rule);
        }
        else
        {
            rule.IsBlocked = blocked;
            rule.Mode = mode;
            rule.ScheduleId = mode == RuleMode.Scheduled ? scheduleId : null;
            rule.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        AddHistory(familyId, childId, "site", rule.Id, rule.Domain, rule.Domain,
            blocked ? RuleAction.Blocked : RuleAction.Allowed, mode, rule.ScheduleId, parentId);
        await _db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task RemoveWebsiteRuleAsync(Guid familyId, Guid childId, Guid? parentId, Guid ruleId, CancellationToken ct = default)
    {
        var rule = await _db.WebsiteRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.FamilyId == familyId && r.ChildId == childId, ct);
        if (rule is null) return;
        _db.WebsiteRules.Remove(rule);
        AddHistory(familyId, childId, "site", rule.Id, rule.Domain, rule.Domain, RuleAction.Removed, rule.Mode, rule.ScheduleId, parentId);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<AppRule> UpsertAppRuleAsync(Guid familyId, Guid childId, Guid? parentId, string identity, string displayName, string? publisher, bool blocked, RuleMode mode, Guid? scheduleId, CancellationToken ct = default)
    {
        identity = NormalizeAppIdentity(identity);
        displayName = string.IsNullOrWhiteSpace(displayName) ? identity : displayName.Trim();
        if (!blocked && mode != RuleMode.Always)
            throw new InvalidOperationException("Always-allow rules cannot be tied to a schedule.");
        if (mode == RuleMode.Scheduled && scheduleId is null)
            throw new InvalidOperationException("Pick a schedule for a scheduled rule.");
        if (scheduleId is { } sid)
        {
            var ok = await _db.Schedules.AnyAsync(s => s.Id == sid && s.FamilyId == familyId && s.ChildId == childId, ct);
            if (!ok) throw new InvalidOperationException("Schedule not found for this child.");
        }

        var rule = await _db.AppRules.FirstOrDefaultAsync(r => r.FamilyId == familyId && r.ChildId == childId && r.AppIdentity == identity, ct);
        if (rule is null)
        {
            rule = new AppRule
            {
                FamilyId = familyId,
                ChildId = childId,
                AppIdentity = identity,
                DisplayName = displayName,
                Publisher = publisher,
                IsBlocked = blocked,
                Mode = mode,
                ScheduleId = scheduleId
            };
            _db.AppRules.Add(rule);
        }
        else
        {
            rule.DisplayName = displayName;
            rule.Publisher = publisher ?? rule.Publisher;
            rule.IsBlocked = blocked;
            rule.Mode = mode;
            rule.ScheduleId = mode == RuleMode.Scheduled ? scheduleId : null;
            rule.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        AddHistory(familyId, childId, "app", rule.Id, rule.AppIdentity, rule.DisplayName,
            blocked ? RuleAction.Blocked : RuleAction.Allowed, mode, rule.ScheduleId, parentId);
        await _db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task RemoveAppRuleAsync(Guid familyId, Guid childId, Guid? parentId, Guid ruleId, CancellationToken ct = default)
    {
        var rule = await _db.AppRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.FamilyId == familyId && r.ChildId == childId, ct);
        if (rule is null) return;
        _db.AppRules.Remove(rule);
        AddHistory(familyId, childId, "app", rule.Id, rule.AppIdentity, rule.DisplayName, RuleAction.Removed, rule.Mode, rule.ScheduleId, parentId);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddItemBonusAsync(Guid familyId, Guid childId, Guid? parentId, string ruleType, string identity, int minutes, DateTime? appliedUtc = null, CancellationToken ct = default)
    {
        if (ruleType is not ("site" or "app"))
            throw new InvalidOperationException("Unknown rule type.");
        identity = ruleType == "site" ? NormalizeDomain(identity) : NormalizeAppIdentity(identity);
        minutes = Math.Clamp(minutes, 1, 1440);
        var now = DateTime.UtcNow;
        var anchor = appliedUtc is { } applied && applied <= now && applied > now.AddHours(-24) ? applied : now;
        var until = anchor.AddMinutes(minutes);

        var existing = await _db.ItemBonuses
            .Where(b => b.FamilyId == familyId && b.ChildId == childId && b.RuleType == ruleType && b.Identity == identity && b.UntilUtc > now)
            .ToListAsync(ct);
        if (existing.Count > 0)
        {
            foreach (var bonus in existing)
            {
                bonus.Minutes = minutes;
                bonus.UntilUtc = until > bonus.UntilUtc ? until : bonus.UntilUtc;
            }
        }
        else
        {
            _db.ItemBonuses.Add(new ItemBonus
            {
                FamilyId = familyId,
                ChildId = childId,
                RuleType = ruleType,
                Identity = identity,
                Minutes = minutes,
                UntilUtc = until,
                CreatedByParentId = parentId,
                CreatedUtc = now
            });
        }

        _db.OverrideEvents.Add(new OverrideEvent
        {
            FamilyId = familyId,
            ChildId = childId,
            Option = $"item-bonus-{ruleType}",
            Detail = identity,
            Minutes = minutes,
            CreatedByParentId = parentId,
            CreatedUtc = now
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task ToggleCategoryAsync(Guid familyId, Guid childId, string key, bool enabled, CancellationToken ct = default)
    {
        var category = await _db.Categories.FirstOrDefaultAsync(c => c.Key == key, ct)
            ?? throw new InvalidOperationException("Category not found.");
        var toggle = await _db.ChildCategories.FirstOrDefaultAsync(c => c.FamilyId == familyId && c.ChildId == childId && c.CategoryKey == key, ct);
        if (toggle is null)
        {
            toggle = new ChildCategory { FamilyId = familyId, ChildId = childId, CategoryKey = key, Enabled = enabled };
            _db.ChildCategories.Add(toggle);
        }
        else
        {
            toggle.Enabled = enabled;
            toggle.UpdatedUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<RuleHistoryDto>> GetHistoryAsync(Guid familyId, Guid childId, int take = 100, CancellationToken ct = default)
    {
        var rows = await _db.RuleHistory.AsNoTracking()
            .Where(h => h.FamilyId == familyId && h.ChildId == childId)
            .OrderByDescending(h => h.ChangedUtc)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(h => new RuleHistoryDto(h.Id, h.RuleType, h.Identity, h.DisplayName, h.Action.ToString(), h.Mode.ToString(), h.ChangedUtc)).ToList();
    }

    public async Task<CategoryDeltaDto> GetCategoryDeltaAsync(string key, int sinceVersion, Guid familyId, Guid childId, CancellationToken ct = default)
    {
        var category = await _db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct)
            ?? throw new InvalidOperationException("Category not found.");
        var enabled = await _db.ChildCategories.AsNoTracking()
            .AnyAsync(c => c.FamilyId == familyId && c.ChildId == childId && c.CategoryKey == key && c.Enabled, ct);
        if (!enabled)
            return new CategoryDeltaDto(category.Key, category.Name, category.Version, sinceVersion, [], [], false);

        var reset = sinceVersion <= 0 || sinceVersion > category.Version;
        var domains = await _db.CategoryDomains.AsNoTracking()
            .Where(d => d.CategoryKey == key)
            .ToListAsync(ct);
        var added = reset
            ? domains.Where(d => d.RemovedVersion is null).Select(d => d.Domain).ToList()
            : domains.Where(d => d.AddedVersion > sinceVersion && d.RemovedVersion is null).Select(d => d.Domain).ToList();
        var removed = reset
            ? new List<string>()
            : domains.Where(d => d.RemovedVersion is { } rv && rv > sinceVersion).Select(d => d.Domain).ToList();
        return new CategoryDeltaDto(category.Key, category.Name, category.Version, sinceVersion, added, removed, reset);
    }

    public async Task SyncVisitsAsync(Device device, VisitBatchRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = request.Visits
            .Where(v => !string.IsNullOrWhiteSpace(v.Domain) && v.Domain.Length <= 255)
            .GroupBy(v => new { v.Domain, v.Blocked, Minute = new DateTime(v.VisitedUtc.Ticks - v.VisitedUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc) })
            .Select(g => g.First())
            .Take(2000)
            .Select(v => new VisitRecord
            {
                FamilyId = device.FamilyId,
                ChildId = device.ChildId,
                DeviceId = device.Id,
                Domain = v.Domain.Trim().ToLowerInvariant(),
                Blocked = v.Blocked,
                VisitedUtc = v.VisitedUtc.Kind == DateTimeKind.Utc ? v.VisitedUtc : v.VisitedUtc.ToUniversalTime()
            })
            .ToList();
        if (rows.Count == 0) return;
        _db.VisitRecords.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SyncAppInventoryAsync(Device device, AppInventoryRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var incoming = request.Apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Identity) && a.Identity.Length <= 255)
            .GroupBy(a => a.Identity)
            .Select(g => g.First())
            .Take(2000)
            .ToList();
        var existing = await _db.InstalledApps
            .Where(a => a.DeviceId == device.Id)
            .ToDictionaryAsync(a => a.Identity, ct);
        var firstSync = existing.Count == 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in incoming)
        {
            seen.Add(app.Identity);
            if (existing.TryGetValue(app.Identity, out var row))
            {
                row.DisplayName = string.IsNullOrWhiteSpace(app.DisplayName) ? row.DisplayName : app.DisplayName.Trim();
                row.Publisher = app.Publisher;
                row.Path = app.Path;
                row.Source = app.Source;
                row.Version = app.Version;
                row.LastSeenUtc = now;
                row.RemovedUtc = null;
            }
            else
            {
                _db.InstalledApps.Add(new InstalledApp
                {
                    FamilyId = device.FamilyId,
                    ChildId = device.ChildId,
                    DeviceId = device.Id,
                    Identity = app.Identity,
                    DisplayName = string.IsNullOrWhiteSpace(app.DisplayName) ? app.Identity : app.DisplayName.Trim(),
                    Publisher = app.Publisher,
                    Path = app.Path,
                    Source = string.IsNullOrWhiteSpace(app.Source) ? "win32" : app.Source,
                    Version = app.Version,
                    IsNew = !firstSync,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                });
            }
        }

        foreach (var (identity, row) in existing)
        {
            if (!seen.Contains(identity) && row.RemovedUtc is null)
                row.RemovedUtc = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task SyncBlockEventsAsync(Device device, BlockEventRequest request, CancellationToken ct = default)
    {
        var rows = request.Events
            .Where(e => e.RuleType is "site" or "app" && !string.IsNullOrWhiteSpace(e.Identity) && e.Identity.Length <= 255)
            .Take(500)
            .Select(e => new BlockEvent
            {
                FamilyId = device.FamilyId,
                ChildId = device.ChildId,
                DeviceId = device.Id,
                RuleType = e.RuleType,
                Identity = e.Identity.Trim().ToLowerInvariant(),
                DisplayName = string.IsNullOrWhiteSpace(e.DisplayName) ? e.Identity : e.DisplayName.Trim(),
                OccurredUtc = e.OccurredUtc.Kind == DateTimeKind.Utc ? e.OccurredUtc : e.OccurredUtc.ToUniversalTime()
            })
            .ToList();
        if (rows.Count == 0) return;
        _db.BlockEvents.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    private void AddHistory(Guid familyId, Guid childId, string ruleType, Guid ruleId, string identity, string displayName, RuleAction action, RuleMode mode, Guid? scheduleId, Guid? parentId)
    {
        _db.RuleHistory.Add(new RuleHistory
        {
            FamilyId = familyId,
            ChildId = childId,
            RuleType = ruleType,
            RuleId = ruleId,
            Identity = identity,
            DisplayName = displayName,
            Action = action,
            Mode = mode,
            ScheduleId = scheduleId,
            ChangedByParentId = parentId
        });
    }
}
