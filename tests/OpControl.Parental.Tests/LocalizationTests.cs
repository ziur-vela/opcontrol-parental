using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Resources;
using OpControl.Parental.Server.Services;
using Xunit;

namespace OpControl.Parental.Tests;

public class LocalizationTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpControl.Parental.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, string> ReadResx(string name)
    {
        var path = Path.Combine(Root, "src", "OpControl.Parental.Server", "Resources", name);
        var document = XDocument.Load(path);
        return document.Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")?.Value ?? "");
    }

    [Fact]
    public void Resource_files_have_the_same_keys_and_no_empty_values()
    {
        var english = ReadResx("AppStrings.resx");
        var spanish = ReadResx("AppStrings.es-MX.resx");
        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.OrderBy(k => k), spanish.Keys.OrderBy(k => k));
        Assert.All(english, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
        Assert.All(spanish, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
        Assert.Equal(AppStringsMarker, typeof(AppStrings).FullName);
    }

    private const string AppStringsMarker = "OpControl.Parental.Server.Resources.AppStrings";

    [Fact]
    public void Client_resource_files_match()
    {
        using var english = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(Root, "src", "OpControl.Parental.Localization", "en.json")));
        using var spanish = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(Root, "src", "OpControl.Parental.Localization", "es-MX.json")));
        var en = english.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray();
        var es = spanish.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray();
        Assert.Equal(en, es);
        Assert.DoesNotContain(english.RootElement.EnumerateObject(), p => string.IsNullOrWhiteSpace(p.Value.GetString()));
    }

    [Theory]
    [InlineData("es-MX", "es-MX")]
    [InlineData("es", "es-MX")]
    [InlineData("es-MX,es;q=0.9,en;q=0.8", "es-MX")]
    [InlineData("en-US", "en")]
    [InlineData(null, "en")]
    [InlineData("fr-FR", "en")]
    public void Culture_resolution_normalizes_supported_languages(string? input, string expected) =>
        Assert.Equal(expected, AppCulture.Normalize(input));

    [Fact]
    public void Spanish_accept_language_is_detected() =>
        Assert.Equal("es-MX", AppCulture.FromAcceptLanguage("es-MX,es;q=0.9,en;q=0.8"));

    [Fact]
    public void Durations_and_numbers_follow_the_culture()
    {
        using (CultureScope.Use("en"))
        {
            Assert.Equal("1 h 25 min", Formats.Duration(5100));
            Assert.Equal("3 min", Formats.Duration(180));
            Assert.Equal("1,234", Formats.Number(1234));
        }
        using (CultureScope.Use("es-MX"))
        {
            Assert.Equal("1 h 25 min", Formats.Duration(5100));
            Assert.Equal("3 min", Formats.Duration(180));
            Assert.Equal("1,234", Formats.Number(1234));
        }
    }

    [Fact]
    public void Alert_text_renders_in_the_readers_language()
    {
        var alert = new Alert
        {
            Type = "new-app",
            Title = "New app installed: Example",
            Detail = "Example (app:1) was installed on Device.",
            TitleKey = "Alert.NewApp.Title",
            DetailKey = "Alert.NewApp.Detail",
            ArgsJson = AlertText.SerializeArgs(["Example", "app:1", "Device"])
        };
        Assert.Equal("New app installed: Example", AlertText.Render(alert, "en").Title);
        Assert.Contains("Example", AlertText.Render(alert, "es-MX").Title);
        Assert.StartsWith("Se instaló", AlertText.Render(alert, "es-MX").Detail);
        var legacy = new Alert { Title = "Old alert", Detail = "Old detail" };
        Assert.Equal(("Old alert", "Old detail"), AlertText.Render(legacy, "es-MX"));
    }

    [Fact]
    public void Every_spanish_key_differs_or_is_intentionally_identical()
    {
        var english = ReadResx("AppStrings.resx");
        var spanish = ReadResx("AppStrings.es-MX.resx");
        var identical = english.Where(p => p.Value == spanish[p.Key]).Select(p => p.Key).ToList();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "App.Name", "Language.English", "Language.Spanish", "Layout.Spanish",
            "Settings.NoticeDefault", "Downloads.Sha", "Common.Minutes", "Common.HourShort",
            "Common.MinuteShort", "Placeholder.InstallCode", "Setup.Title",
            "Alert.Update.Detail", "Install.ForChild2", "Alert.Tampering.DeviceKind", "Alert.Tampering.DeviceKindMore", "Apps.FilterWin32", "Common.No", "Home.Bonus15", "Home.Bonus30", "Layout.English"
        };
        Assert.All(identical, key => Assert.Contains(key, allowed));
    }

    [Fact]
    public void Razor_and_razor_pages_do_not_contain_literal_ui_text()
    {
        var allowedText = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Parental OpControl", "OpControl", "EN", "ES", "Windows", "Linux", "Android",
            "Microsoft Store", "Win32", "Branding", "TOTP QR code", "SHA-256 (MSI):"
        };
        var latinWords = new Regex("[A-Za-z]{3,}", RegexOptions.Compiled);
        var failures = new List<string>();
        var files = Directory.EnumerateFiles(Path.Combine(Root, "src", "OpControl.Parental.Server"), "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "src", "OpControl.Parental.Server"), "*.cshtml", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(Root, file);
            var text = File.ReadAllText(file);
            text = Regex.Replace(text, "@code\\s*\\{.*?\\n\\}", "", RegexOptions.Singleline);
            text = Regex.Replace(text, "@\\{.*?\\n\\}", "", RegexOptions.Singleline);
            text = Regex.Replace(text, "<script.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<style.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var lines = text.Split('\n').Where(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith("@") && !trimmed.StartsWith("}") && !trimmed.StartsWith("{")
                    && !trimmed.StartsWith("else") && !trimmed.StartsWith("case ");
            });
            var cleaned = string.Join('\n', lines);
            foreach (Match match in Regex.Matches(cleaned, ">([^<>@]+)<"))
            {
                var value = match.Groups[1].Value.Trim();
                if (value.Length == 0 || value.Contains('@') || !latinWords.IsMatch(value)) continue;
                if (Regex.IsMatch(value, @"^[\W\d]+$")) continue;
                if (value.Contains('(') || value.Contains(')') || value.Contains(';') || value.Contains("=>")
                    || value.Contains(']') || value.Contains('}') || value.Contains('{')) continue;
                if (allowedText.Any(a => value.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;
                failures.Add($"{relative}: literal text \"{value}\"");
            }
            foreach (Match match in Regex.Matches(cleaned, "(placeholder|title|alt|aria-label)=\"([^\"]*[A-Za-z][^\"]*)\""))
            {
                var value = match.Groups[2].Value;
                if (value.StartsWith('@') || value.Contains("example.com") || value.Contains("youtube.com")
                    || value.Contains("Program Files") || value.Contains("22:00") || value.Contains("07:00")) continue;
                if (allowedText.Any(a => value.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;
                failures.Add($"{relative}: literal attribute {match.Groups[1].Value}=\"{value}\"");
            }
        }

        Assert.True(failures.Count == 0, "Hardcoded user-facing strings found:\n" + string.Join('\n', failures));
    }

    [Fact]
    public void Services_do_not_throw_literal_english_messages()
    {
        var failures = new List<string>();
        var files = Directory.EnumerateFiles(Path.Combine(Root, "src", "OpControl.Parental.Server"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));
        var pattern = new Regex("(?:throw new \\w+Exception\\(|MessageBox\\.Show\\(|(?:Error|Message|_notice)\\s*=\\s*)\"([^\"\\n]{3,})\"", RegexOptions.Compiled);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(Root, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in pattern.Matches(lines[i]))
                {
                    var value = match.Groups[1].Value;
                    if (value == "muted") continue;
                    if (value.StartsWith("Error.") || value.StartsWith("Team.") || value.StartsWith("Home.")
                        || value.StartsWith("Pin.") || value.StartsWith("ServerAdmin.") || value.StartsWith("TwoFactor.")
                        || value.StartsWith("Login.") || value.StartsWith("Update.") || value.StartsWith("Alert.")) continue;
                    failures.Add($"{relative}:{i + 1}: \"{value}\"");
                }
            }
        }
        Assert.True(failures.Count == 0, "Hardcoded exception/UI messages found:\n" + string.Join('\n', failures));
    }
}
