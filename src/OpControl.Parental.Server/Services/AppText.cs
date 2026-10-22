// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpControl.Parental.Server.Resources;

namespace OpControl.Parental.Server.Services;

public static class AppText
{
    private static readonly ResourceManager Resources =
        new("OpControl.Parental.Server.Resources.AppStrings", typeof(AppStrings).Assembly);

    public static string Get(string key, params object[] args) =>
        Get(key, CultureInfo.CurrentCulture.Name, args);

    public static string Get(string key, string? language, params object[] args)
    {
        var culture = AppCulture.Culture(language ?? CultureInfo.CurrentCulture.Name);
        var value = Resources.GetString(key, culture) ?? key;
        return args.Length == 0 ? value : string.Format(culture, value, args);
    }

    public static string GetArgs(string key, string? language, IReadOnlyList<string> args) =>
        Get(key, language, args.Cast<object>().ToArray());
}

public sealed record AlertContent(string TitleKey, string DetailKey, params string[] Args)
{
    public static implicit operator AlertContent(string titleKey) => new(titleKey, titleKey);
}

public static class AlertText
{
    public static (string Title, string Detail) Render(Data.Alert alert, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(alert.TitleKey))
            return RenderLegacy(alert, language);
        var args = ParseArgs(alert.ArgsJson);
        var title = AppText.GetArgs(alert.TitleKey, language, args);
        var detail = string.IsNullOrWhiteSpace(alert.DetailKey) ? "" : AppText.GetArgs(alert.DetailKey, language, args);
        return (title, detail);
    }

    private static (string Title, string Detail) RenderLegacy(Data.Alert alert, string? language)
    {
        var detail = alert.Detail ?? "";
        switch (alert.Type)
        {
            case "new-app":
            {
                var match = Regex.Match(detail, @"^(.+) \((.+)\) was installed on (.+)\.$");
                if (match.Success)
                    return (AppText.Get("Alert.NewApp.LegacyTitle", language),
                        AppText.Get("Alert.NewApp.Detail", language, match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value));
                break;
            }
            case "repeated-blocked-site":
            {
                var match = Regex.Match(detail, @"^(\d+) blocked attempts to (\S+) in the last 10 minutes on (.+)\.$");
                if (match.Success)
                    return (AppText.Get("Alert.RepeatedBlocked.LegacyTitle", language),
                        AppText.Get("Alert.RepeatedBlocked.Detail", language, match.Groups[2].Value, match.Groups[1].Value, match.Groups[3].Value));
                break;
            }
            case "device-offline":
            {
                var match = Regex.Match(detail, @"^(.+) has not contacted the server since (.+)\.$");
                if (match.Success)
                    return (AppText.Get("Alert.DeviceOffline.LegacyTitle", language),
                        AppText.Get("Alert.DeviceOffline.Detail", language, match.Groups[1].Value, match.Groups[2].Value));
                break;
            }
            case "tampering":
            {
                var match = Regex.Match(detail, @"^(\S+) was reported on (.+?)(?:: (.+))?\.?$");
                if (match.Success)
                {
                    var kind = TamperKindName(match.Groups[1].Value, language);
                    var device = match.Groups[2].Value;
                    var known = match.Groups[1].Value is "service-stopped" or "dns-settings-changed" or "helper-killed"
                        or "helper-auth-failed" or "clock-changed" or "uninstall-attempted" or "usage-sanity";
                    return (AppText.Get("Alert.Tampering.Title", language),
                        string.IsNullOrWhiteSpace(match.Groups[3].Value) || known
                            ? AppText.Get("Alert.Tampering.DeviceKind", language, device, kind)
                            : AppText.Get("Alert.Tampering.DeviceKindMore", language, device, kind, match.Groups[3].Value));
                }
                break;
            }
            case "daily-limit":
            {
                var match = Regex.Match(detail, @"^The (\d+) minute limit for (\S+) has been reached\.$");
                if (match.Success)
                    return (AppText.Get("Alert.DailyLimit.Title", language),
                        AppText.Get("Alert.DailyLimit.Detail", language, match.Groups[1].Value, match.Groups[2].Value));
                break;
            }
            case "app-limit":
            {
                var match = Regex.Match(detail, @"^(.+) reached its (\d+) minute daily limit\.$");
                if (match.Success)
                    return (AppText.Get("Alert.AppLimit.LegacyTitle", language),
                        AppText.Get("Alert.AppLimit.Detail", language, match.Groups[1].Value, match.Groups[2].Value));
                break;
            }
            case "pin-override":
            {
                var match = Regex.Match(detail, @"^(pin-unlock-until-next|pin-bonus) was used on (.+?)(?: for (\d+) minutes)?\.$");
                if (match.Success && match.Groups[1].Value == "pin-bonus")
                    return (AppText.Get("Alert.PinOverride.BonusTitle", language),
                        AppText.Get("Alert.PinOverride.BonusDetail", language, match.Groups[2].Value, match.Groups[3].Value));
                if (match.Success)
                    return (AppText.Get("Alert.PinOverride.UnlockTitle", language),
                        AppText.Get("Alert.PinOverride.UnlockDetail", language, match.Groups[2].Value));
                break;
            }
        }

        var title = alert.Type switch
        {
            "new-app" => AppText.Get("Alert.NewApp.LegacyTitle", language),
            "repeated-blocked-site" => AppText.Get("Alert.RepeatedBlocked.LegacyTitle", language),
            "pin-override" => AppText.Get("Alert.PinOverride.UnlockTitle", language),
            "daily-limit" => AppText.Get("Alert.DailyLimit.Title", language),
            "app-limit" => AppText.Get("Alert.AppLimit.LegacyTitle", language),
            "device-offline" => AppText.Get("Alert.DeviceOffline.LegacyTitle", language),
            "tampering" => AppText.Get("Alert.Tampering.Title", language),
            "update-rolledback" => AppText.Get("Alert.UpdateRolledBack.Title", language),
            "update-failed" => AppText.Get("Alert.UpdateFailed.Title", language),
            _ => alert.Title
        };
        return (title, detail);
    }

    private static string TamperKindName(string kind, string? language) => kind switch
    {
        "service-stopped" => AppText.Get("Activity.Tamper.ServiceStopped", language),
        "helper-killed" => AppText.Get("Activity.Tamper.HelperKilled", language),
        "helper-auth-failed" => AppText.Get("Activity.Tamper.HelperAuth", language),
        "dns-settings-changed" => AppText.Get("Activity.Tamper.Dns", language),
        "clock-changed" => AppText.Get("Activity.Tamper.Clock", language),
        "uninstall-attempted" => AppText.Get("Activity.Tamper.Uninstall", language),
        "usage-sanity" => AppText.Get("Activity.Tamper.UsageSanity", language),
        _ => kind
    };

    public static string[] ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }

    public static string SerializeArgs(IReadOnlyList<string> args) =>
        JsonSerializer.Serialize(args);
}
