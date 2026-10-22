// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace OpControl.Parental.Localization;

public static class Loc
{
    public const string English = "en";
    public const string Spanish = "es-MX";

    private static readonly object Gate = new();
    private static string _language = English;
    private static Dictionary<string, string> _strings = Load(English);

    public static string Language => _language;

    public static void SetLanguage(string? language)
    {
        var normalized = Normalize(language);
        lock (Gate)
        {
            if (normalized == _language) return;
            _language = normalized;
            _strings = Load(normalized);
            var culture = CultureInfo.GetCultureInfo(normalized);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }

    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return English;
        return language.Trim().StartsWith("es", StringComparison.OrdinalIgnoreCase) ? Spanish : English;
    }

    public static string T(string key)
    {
        var map = _strings;
        return map.TryGetValue(key, out var value) ? value : key;
    }

    public static string T(string key, params object[] args)
    {
        var map = _strings;
        if (!map.TryGetValue(key, out var value)) return key;
        return args.Length == 0 ? value : string.Format(CultureInfo.GetCultureInfo(_language), value, args);
    }

    public static string NoticeDefault => T("Client.NoticeDefault");

    private static Dictionary<string, string> Load(string language)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream($"OpControl.Parental.Loc.{language}.json");
            if (stream is null) return [];
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
