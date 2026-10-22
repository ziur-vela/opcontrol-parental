// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Text;

namespace OpControl.Parental.Contracts;

/// <summary>
/// Cleans text that comes from a monitored device before it is stored.
/// Registry values, AppX metadata and file versions can contain NUL bytes or
/// other control characters, and PostgreSQL rejects those in UTF-8 text
/// columns. Unpaired surrogates are dropped for the same reason.
/// </summary>
public static class DeviceText
{
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0) return "";

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        for (var i = 0; i < value.Length && builder.Length < maxLength; i++)
        {
            var ch = value[i];
            if (ch == '\0') continue;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(ch);
                    builder.Append(value[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(ch)) continue;
            if (char.IsControl(ch) && ch is not ('\t' or '\n' or '\r')) continue;
            builder.Append(ch);
        }

        var result = builder.ToString().Trim();
        if (result.Length > maxLength) result = result[..maxLength];
        if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result = result[..^1];
        return result;
    }

    public static string? CleanOptional(string? value, int maxLength)
    {
        var clean = Clean(value, maxLength);
        return clean.Length == 0 ? null : clean;
    }

    public static bool HasControlCharacters(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\0' || (char.IsControl(ch) && ch is not ('\t' or '\n' or '\r'))) return true;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { i++; continue; }
                return true;
            }
            if (char.IsLowSurrogate(ch)) return true;
        }
        return false;
    }
}

/// <summary>Small decisions shared by the parent dashboard pages and their tests.</summary>
public static class RuleAvailability
{
    public static bool CanUnblock(Guid ruleId) => ruleId != Guid.Empty;
    public static bool CanUnblock(Guid? ruleId) => ruleId is { } id && id != Guid.Empty;
}
