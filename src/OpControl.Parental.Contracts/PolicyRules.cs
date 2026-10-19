namespace OpControl.Parental.Contracts;

public static class PolicyRules
{
    public static bool IsWebsiteBlocked(
        DeviceStateDto state,
        string domain,
        TimeZoneInfo tz,
        DateTime nowUtc,
        Func<string, bool>? isInEnabledCategory = null)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length == 0) return false;

        if (HasItemBonus(state, "site", domain, nowUtc))
            return false;

        var siteRules = state.WebsiteRules;
        if (siteRules.Any(r => !r.IsBlocked && r.Mode == RuleMode.Always && Matches(r.Domain, domain)))
            return false;

        foreach (var rule in siteRules.Where(r => r.IsBlocked))
        {
            if (!Matches(rule.Domain, domain)) continue;
            if (rule.Mode == RuleMode.Always || ScheduleActive(state.Schedules, rule.ScheduleId, tz, nowUtc))
                return true;
        }

        if (isInEnabledCategory is not null && isInEnabledCategory(domain))
            return true;

        return false;
    }

    public static bool IsAppBlocked(DeviceStateDto state, string identity, string? path, TimeZoneInfo tz, DateTime nowUtc)
        => IsAppBlocked(state, identity, path, tz, nowUtc, out _);

    public static bool IsAppBlocked(DeviceStateDto state, string identity, string? path, TimeZoneInfo tz, DateTime nowUtc, out string? reason)
    {
        reason = null;
        identity = identity.Trim().ToLowerInvariant();
        if (identity.Length == 0) return false;

        if (IsAppAlwaysAllowed(state, identity, path))
            return false;

        if (HasItemBonus(state, "app", identity, nowUtc))
            return false;

        if (IsAppLimitExceeded(state, identity))
        {
            reason = "daily-limit";
            return true;
        }

        foreach (var rule in state.AppRules.Where(r => r.IsBlocked))
        {
            if (!MatchesApp(rule.AppIdentity, identity, path)) continue;
            if (rule.Mode == RuleMode.Always || ScheduleActive(state.Schedules, rule.ScheduleId, tz, nowUtc))
            {
                reason = "rule";
                return true;
            }
        }

        return false;
    }

    public static bool IsAppAlwaysAllowed(DeviceStateDto state, string identity, string? path)
    {
        identity = identity.Trim().ToLowerInvariant();
        if (identity.Length == 0) return false;
        return state.AppRules.Any(r => r.AlwaysAllowed && MatchesApp(r.AppIdentity, identity, path));
    }

    public static bool IsAppLimitExceeded(DeviceStateDto state, string identity)
    {
        identity = identity.Trim().ToLowerInvariant();
        var usage = state.AppUsage?.FirstOrDefault(u => u.Identity.Trim().Equals(identity, StringComparison.OrdinalIgnoreCase));
        return usage is { LimitMinutes: > 0 } && usage.UsedTodaySeconds >= usage.LimitMinutes * 60;
    }

    public static bool MatchesApp(string ruleIdentity, string identity, string? path)
    {
        ruleIdentity = ruleIdentity.Trim().ToLowerInvariant();
        if (ruleIdentity.StartsWith("path:", StringComparison.Ordinal))
            return path is not null && path.Trim().ToLowerInvariant().Equals(ruleIdentity[5..], StringComparison.OrdinalIgnoreCase);
        return identity.Trim().Equals(ruleIdentity, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ScheduleActive(IReadOnlyList<ScheduleDto> schedules, Guid? scheduleId, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (scheduleId is not { } id) return false;
        var schedule = schedules.FirstOrDefault(s => s.Id == id && s.IsEnabled);
        if (schedule is null) return false;
        var active = LockRules.ActiveSchedule([schedule], tz, nowUtc);
        return active is not null;
    }

    public static bool Matches(string ruleDomain, string domain)
    {
        ruleDomain = ruleDomain.Trim().TrimEnd('.').ToLowerInvariant();
        return domain.Equals(ruleDomain, StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith("." + ruleDomain, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesIdentity(string ruleIdentity, string identity)
    {
        ruleIdentity = ruleIdentity.Trim().ToLowerInvariant();
        identity = identity.Trim().ToLowerInvariant();
        return identity.Equals(ruleIdentity, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasItemBonus(DeviceStateDto state, string type, string identity, DateTime nowUtc) =>
        state.ItemBonuses.Any(b => b.RuleType.Equals(type, StringComparison.OrdinalIgnoreCase)
            && b.UntilUtc > nowUtc
            && (type == "site" ? Matches(b.Identity, identity) : MatchesIdentity(b.Identity, identity)));
}
