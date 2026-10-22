// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Server.Services;

/// <summary>Renders the stored block reason in the current language.</summary>
public static class BlockReasonText
{
    public static string Describe(string? reason, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(reason)) return AppText.Get("BlockReason.Unknown", language);
        if (reason == "rule:always") return AppText.Get("BlockReason.RuleAlways", language);
        if (reason == "rule:schedule") return AppText.Get("BlockReason.RuleScheduleGeneric", language);
        if (reason.StartsWith("rule:schedule:", StringComparison.Ordinal))
            return AppText.Get("BlockReason.RuleSchedule", language, reason["rule:schedule:".Length..]);
        if (reason.StartsWith("category:", StringComparison.Ordinal))
        {
            var key = reason["category:".Length..];
            return AppText.Get("BlockReason.Category", language, CategoryText.Name(key, key, language));
        }
        if (reason == "doh") return AppText.Get("BlockReason.Doh", language);
        return AppText.Get("BlockReason.Unknown", language);
    }

    public static string Short(string? reason, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(reason)) return AppText.Get("BlockReason.UnknownShort", language);
        if (reason == "rule:always") return AppText.Get("BlockReason.RuleAlwaysShort", language);
        if (reason.StartsWith("rule:schedule", StringComparison.Ordinal)) return AppText.Get("BlockReason.RuleScheduleShort", language);
        if (reason.StartsWith("category:", StringComparison.Ordinal))
        {
            var key = reason["category:".Length..];
            return AppText.Get("BlockReason.CategoryShort", language, CategoryText.Name(key, key, language));
        }
        return AppText.Get("BlockReason.UnknownShort", language);
    }
}
