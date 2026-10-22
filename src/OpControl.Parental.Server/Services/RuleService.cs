// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

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
    private readonly AlertService _alerts;

    public RuleService(ParentalDbContext db, AlertService alerts)
    {
        _db = db;
        _alerts = alerts;
    }

    public async Task EnsureChildAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        if (!await _db.Children.AnyAsync(c => c.Id == childId && c.FamilyId == familyId, ct))
            throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));
    }

    public static string NormalizeDomain(string input)
    {
        var value = (input ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0) throw new InvalidOperationException(AppText.Get("Error.EnterDomain"));
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
            throw new InvalidOperationException(AppText.Get("Error.InvalidDomain"));
        return value;
    }

    public static string NormalizeAppIdentity(string identity)
    {
        var value = (identity ?? "").Trim();
        if (value.Length is < 2 or > 255) throw new InvalidOperationException(AppText.Get("Error.ChooseApp"));
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
            .Select(r => new AppRuleDto(r.Id, r.AppIdentity, r.DisplayName, r.Publisher, r.IsBlocked, r.Mode, r.ScheduleId, r.UpdatedUtc, r.DailyLimitMinutes, r.AlwaysAllowed))
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
            throw new InvalidOperationException(AppText.Get("Error.AllowNoSchedule"));
        if (mode == RuleMode.Scheduled && scheduleId is null)
            throw new InvalidOperationException(AppText.Get("Error.PickSchedule"));
        if (scheduleId is { } sid)
        {
            var ok = await _db.Schedules.AnyAsync(s => s.Id == sid && s.FamilyId == familyId && s.ChildId == childId, ct);
            if (!ok) throw new InvalidOperationException(AppText.Get("Error.ScheduleNotFound"));
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
            throw new InvalidOperationException(AppText.Get("Error.AllowNoSchedule"));
        if (mode == RuleMode.Scheduled && scheduleId is null)
            throw new InvalidOperationException(AppText.Get("Error.PickSchedule"));
        if (scheduleId is { } sid)
        {
            var ok = await _db.Schedules.AnyAsync(s => s.Id == sid && s.FamilyId == familyId && s.ChildId == childId, ct);
            if (!ok) throw new InvalidOperationException(AppText.Get("Error.ScheduleNotFound"));
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
                ScheduleId = scheduleId,
                AlwaysAllowed = !blocked
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
            rule.AlwaysAllowed = !blocked;
            if (blocked) rule.DailyLimitMinutes = null;
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
            throw new InvalidOperationException(AppText.Get("Error.UnknownRuleType"));
        await EnsureChildAsync(familyId, childId, ct);
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

    public async Task ToggleCategoryAsync(Guid familyId, Guid childId, Guid? parentId, string key, bool enabled, CancellationToken ct = default)
    {
        await EnsureChildAsync(familyId, childId, ct);
        var category = await _db.Categories.FirstOrDefaultAsync(c => c.Key == key, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.CategoryNotFound"));
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
        _db.OverrideEvents.Add(new OverrideEvent
        {
            FamilyId = familyId,
            ChildId = childId,
            CreatedByParentId = parentId,
            Option = "category-toggled",
            Detail = $"{category.Key}|{(enabled ? "1" : "0")}",
            CreatedUtc = DateTime.UtcNow
        });
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
            ?? throw new InvalidOperationException(AppText.Get("Error.CategoryNotFound"));
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
        var incoming = request.Visits
            .Where(v => !string.IsNullOrWhiteSpace(v.Domain) && v.Domain.Length <= 255)
            .Select(v => new
            {
                Domain = v.Domain.Trim().TrimEnd('.').ToLowerInvariant(),
                VisitedUtc = v.VisitedUtc.Kind == DateTimeKind.Utc ? v.VisitedUtc : v.VisitedUtc.ToUniversalTime(),
                v.Blocked,
                v.BlockReason,
                Lookups = Math.Clamp(v.Lookups, 1, 10_000)
            })
            .Where(v => !BackgroundDomains.IsBackground(v.Domain))
            .Take(5000)
            .ToList();
        if (incoming.Count == 0) return;

        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in incoming.GroupBy(v => v.Domain, StringComparer.OrdinalIgnoreCase))
        {
            var domain = group.Key;
            var last = await _db.VisitRecords
                .Where(v => v.DeviceId == device.Id && v.Domain == domain)
                .OrderByDescending(v => v.VisitedUtc)
                .FirstOrDefaultAsync(ct);
            foreach (var item in group.OrderBy(v => v.VisitedUtc))
            {
                var reason = item.Blocked ? item.BlockReason : null;
                if (item.Blocked && string.IsNullOrWhiteSpace(reason))
                    reason = await ResolveBlockReasonAsync(device, domain, ct);
                if (last is not null && item.VisitedUtc >= last.VisitedUtc
                    && item.VisitedUtc - last.VisitedUtc < TimeSpan.FromMinutes(5))
                {
                    last.Lookups += item.Lookups;
                    if (item.Blocked)
                    {
                        last.Blocked = true;
                        last.BlockedLookups += item.Lookups;
                        if (!string.IsNullOrWhiteSpace(reason)) last.BlockReason = reason;
                    }
                }
                else
                {
                    last = new VisitRecord
                    {
                        FamilyId = device.FamilyId,
                        ChildId = device.ChildId,
                        DeviceId = device.Id,
                        Domain = domain,
                        Blocked = item.Blocked,
                        BlockReason = reason,
                        Lookups = item.Lookups,
                        BlockedLookups = item.Blocked ? item.Lookups : 0,
                        VisitedUtc = item.VisitedUtc
                    };
                    _db.VisitRecords.Add(last);
                }
                touched.Add(domain);
            }
        }
        await _db.SaveChangesAsync(ct);

        var settings = await _db.FamilySettings.AsNoTracking().FirstOrDefaultAsync(s => s.FamilyId == device.FamilyId, ct);
        var hours = Math.Clamp(settings?.RepeatedBlockAlertHours ?? 6, 1, 24);
        var since = now.AddHours(-hours);
        foreach (var domain in touched)
        {
            var recent = await _db.VisitRecords.AsNoTracking()
                .Where(v => v.FamilyId == device.FamilyId && v.ChildId == device.ChildId
                    && v.DeviceId == device.Id && v.Domain == domain && v.Blocked && v.VisitedUtc >= since)
                .Select(v => new { v.Lookups, v.BlockedLookups, v.BlockReason })
                .ToListAsync(ct);
            var attempts = recent.Sum(v => Math.Max(1, v.BlockedLookups > 0 ? v.BlockedLookups : v.Lookups));
            if (attempts < 5) continue;
            var already = (await _db.Alerts.AsNoTracking()
                .Where(a => a.FamilyId == device.FamilyId && a.ChildId == device.ChildId && a.DeviceId == device.Id
                    && a.Type == "repeated-blocked-site" && a.CreatedUtc >= since)
                .Select(a => a.ArgsJson)
                .ToListAsync(ct))
                .Any(args => args is not null && args.Contains(domain, StringComparison.OrdinalIgnoreCase));
            if (already) continue;
            var reason = recent.Select(v => v.BlockReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? "unknown";
            await _alerts.AddAsync(device.FamilyId, device.ChildId, device.Id, "repeated-blocked-site",
                new AlertContent("Alert.RepeatedBlocked.Title", "Alert.RepeatedBlocked.DetailReason",
                    domain, attempts.ToString(), device.Name, reason),
                null, ct);
        }
    }

    private async Task<string> ResolveBlockReasonAsync(Device device, string domain, CancellationToken ct)
    {
        var parents = Parents(domain).ToList();
        var rules = await _db.WebsiteRules.AsNoTracking()
            .Where(r => r.FamilyId == device.FamilyId && r.ChildId == device.ChildId && parents.Contains(r.Domain))
            .ToListAsync(ct);
        if (rules.Any(r => !r.IsBlocked)) return "unknown";
        foreach (var rule in rules.Where(r => r.IsBlocked))
        {
            if (rule.Mode == RuleMode.Always) return "rule:always";
            if (rule.ScheduleId is { } id)
            {
                var name = await _db.Schedules.AsNoTracking().Where(s => s.Id == id).Select(s => s.Name).FirstOrDefaultAsync(ct);
                return string.IsNullOrWhiteSpace(name) ? "rule:schedule" : "rule:schedule:" + name;
            }
        }
        var enabled = await _db.ChildCategories.AsNoTracking()
            .Where(c => c.FamilyId == device.FamilyId && c.ChildId == device.ChildId && c.Enabled)
            .Select(c => c.CategoryKey)
            .ToListAsync(ct);
        foreach (var key in enabled)
        {
            if (await _db.CategoryDomains.AsNoTracking().AnyAsync(d => d.CategoryKey == key
                && d.RemovedVersion == null && parents.Contains(d.Domain), ct))
                return "category:" + key;
        }
        return "unknown";
    }

    private static IEnumerable<string> Parents(string domain)
    {
        var value = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        while (value.Length > 0)
        {
            yield return value;
            var dot = value.IndexOf('.');
            if (dot < 0) break;
            value = value[(dot + 1)..];
        }
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
        var newApps = new List<(string Identity, string DisplayName)>();

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
                var display = string.IsNullOrWhiteSpace(app.DisplayName) ? app.Identity : app.DisplayName.Trim();
                _db.InstalledApps.Add(new InstalledApp
                {
                    FamilyId = device.FamilyId,
                    ChildId = device.ChildId,
                    DeviceId = device.Id,
                    Identity = app.Identity,
                    DisplayName = display,
                    Publisher = app.Publisher,
                    Path = app.Path,
                    Source = string.IsNullOrWhiteSpace(app.Source) ? "win32" : app.Source,
                    Version = app.Version,
                    IsNew = !firstSync,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                });
                if (!firstSync) newApps.Add((app.Identity, display));
            }
        }

        foreach (var (identity, row) in existing)
        {
            if (!seen.Contains(identity) && row.RemovedUtc is null)
                row.RemovedUtc = now;
        }

        await _db.SaveChangesAsync(ct);

        foreach (var app in newApps)
        {
            await _alerts.AddAsync(device.FamilyId, device.ChildId, device.Id, "new-app",
                new AlertContent("Alert.NewApp.Title", "Alert.NewApp.Detail",
                    app.DisplayName, app.Identity, device.Name),
                TimeSpan.FromDays(1), ct);
        }
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

    public async Task SetAppLimitAsync(Guid familyId, Guid childId, Guid? parentId, string identity, string displayName, string? publisher, int? minutes, bool alwaysAllowed, CancellationToken ct = default)
    {
        await EnsureChildAsync(familyId, childId, ct);
        identity = NormalizeAppIdentity(identity);
        displayName = string.IsNullOrWhiteSpace(displayName) ? identity : displayName.Trim();
        var rule = await _db.AppRules.FirstOrDefaultAsync(r => r.FamilyId == familyId && r.ChildId == childId && r.AppIdentity == identity, ct);

        if (minutes is null && !alwaysAllowed)
        {
            if (rule is null) return;
            rule.DailyLimitMinutes = null;
            rule.AlwaysAllowed = false;
            rule.UpdatedUtc = DateTime.UtcNow;
            if (!rule.IsBlocked && rule.Mode == RuleMode.Always)
                _db.AppRules.Remove(rule);
            await _db.SaveChangesAsync(ct);
            _db.OverrideEvents.Add(new OverrideEvent
            {
                FamilyId = familyId,
                ChildId = childId,
                CreatedByParentId = parentId,
                Option = "app-limit-removed",
                Detail = rule.DisplayName,
                CreatedUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
            if (!rule.IsBlocked)
            {
                AddHistory(familyId, childId, "app", rule.Id, rule.AppIdentity, rule.DisplayName, RuleAction.Removed, RuleMode.Always, null, parentId);
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        if (rule is null)
        {
            rule = new AppRule
            {
                FamilyId = familyId,
                ChildId = childId,
                AppIdentity = identity,
                DisplayName = displayName,
                Publisher = publisher,
                IsBlocked = false,
                Mode = RuleMode.Always
            };
            _db.AppRules.Add(rule);
        }

        rule.DisplayName = displayName;
        rule.Publisher = publisher ?? rule.Publisher;
        if (alwaysAllowed)
        {
            rule.IsBlocked = false;
            rule.Mode = RuleMode.Always;
            rule.ScheduleId = null;
            rule.AlwaysAllowed = true;
            rule.DailyLimitMinutes = null;
        }
        else
        {
            rule.IsBlocked = false;
            rule.Mode = RuleMode.Always;
            rule.ScheduleId = null;
            rule.AlwaysAllowed = false;
            rule.DailyLimitMinutes = Math.Clamp(minutes!.Value, 1, 1440);
        }
        rule.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _db.OverrideEvents.Add(new OverrideEvent
        {
            FamilyId = familyId,
            ChildId = childId,
            CreatedByParentId = parentId,
            Option = alwaysAllowed ? "app-always-allowed" : "app-limit-set",
            Detail = rule.DisplayName + (minutes is { } value ? $" · {value} min" : ""),
            Minutes = minutes,
            CreatedUtc = DateTime.UtcNow
        });
        AddHistory(familyId, childId, "app", rule.Id, rule.AppIdentity, rule.DisplayName, RuleAction.Allowed, rule.Mode, rule.ScheduleId, parentId);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SyncTamperEventsAsync(Device device, TamperEventRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var events = (request.Events ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.Kind))
            .Take(100)
            .ToList();
        foreach (var item in events)
        {
            _db.ClientTamperEvents.Add(new ClientTamperEvent
            {
                FamilyId = device.FamilyId,
                ChildId = device.ChildId,
                DeviceId = device.Id,
                Kind = item.Kind.Length > 40 ? item.Kind[..40] : item.Kind,
                Detail = item.Detail is { Length: > 500 } ? item.Detail[..500] : item.Detail,
                OccurredUtc = item.OccurredUtc.Kind == DateTimeKind.Utc ? item.OccurredUtc : item.OccurredUtc.ToUniversalTime(),
                CreatedUtc = now
            });
        }
        if (events.Count > 0) await _db.SaveChangesAsync(ct);

        foreach (var item in events)
        {
            await _alerts.AddAsync(device.FamilyId, device.ChildId, device.Id, "tampering",
                string.IsNullOrWhiteSpace(item.Detail)
                    ? new AlertContent("Alert.Tampering.Title", "Alert.Tampering.Detail", item.Kind, device.Name)
                    : new AlertContent("Alert.Tampering.Title", "Alert.Tampering.DetailMore", item.Kind, device.Name, item.Detail),
                TimeSpan.FromHours(1), ct);
        }
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
