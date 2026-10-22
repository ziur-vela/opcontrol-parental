// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Contracts;

public static class TimeZoneResolver
{
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        if (OperatingSystem.IsWindows())
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); } catch { }
            }
        }
        else
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); } catch { }
            }
        }
        return TimeZoneInfo.Utc;
    }
}
