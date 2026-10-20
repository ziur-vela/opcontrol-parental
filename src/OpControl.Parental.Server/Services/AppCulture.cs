using System.Globalization;

namespace OpControl.Parental.Server.Services;

public static class AppCulture
{
    public const string English = "en";
    public const string Spanish = "es-MX";
    public const string CookieName = "ocp.lang";

    public static readonly (string Code, string NameKey)[] Supported =
    [
        (English, "Language.English"),
        (Spanish, "Language.Spanish")
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return English;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return Spanish;
        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return English;
        return English;
    }

    public static bool IsSupported(string? value) =>
        string.Equals(value, English, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Spanish, StringComparison.OrdinalIgnoreCase);

    public static CultureInfo Culture(string? value) => CultureInfo.GetCultureInfo(Normalize(value));

    public static string FromAcceptLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return English;
        var first = header.Split(',')[0].Split(';')[0].Trim();
        return Normalize(first);
    }

    public static string FromBrowserOrCookie(string? cookie, string? acceptLanguage) =>
        IsSupported(cookie) ? Normalize(cookie) : FromAcceptLanguage(acceptLanguage);
}

public static class Formats
{
    public static string Duration(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalHours >= 1)
            return string.Format(CultureInfo.CurrentCulture, "{0} h {1:00} min", (int)span.TotalHours, span.Minutes);
        return string.Format(CultureInfo.CurrentCulture, "{0} min", span.Minutes);
    }

    public static string DurationShort(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? string.Format(CultureInfo.CurrentCulture, "{0} h {1} min", (int)span.TotalHours, span.Minutes)
            : Math.Max(1, span.Minutes) + " min";
    }

    public static string DateTimeLocal(DateTime value) => value.ToString("d MMM yyyy, t", CultureInfo.CurrentCulture);

    public static string CompactTime(DateTime value) =>
        value.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H') ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);

    public static string CompactDateTime(DateTime value) =>
        value.ToString("d MMM", CultureInfo.CurrentCulture) + ", " + CompactTime(value);
    public static string DateLocal(DateTime value) => value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    public static string TimeLocal(DateTime value) => value.ToString("t", CultureInfo.CurrentCulture);
    public static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previousCulture;
    private readonly CultureInfo _previousUiCulture;

    private CultureScope(string language)
    {
        _previousCulture = CultureInfo.CurrentCulture;
        _previousUiCulture = CultureInfo.CurrentUICulture;
        var culture = AppCulture.Culture(language);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static CultureScope Use(string? language) => new(language ?? AppCulture.English);

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
    }
}
