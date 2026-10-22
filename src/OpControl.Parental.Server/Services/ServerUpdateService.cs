// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed record ServerUpdateInfo(
    string CurrentVersion, string? LatestVersion, string? CurrentDisplay, string? LatestDisplay,
    string? ReleaseUrl, string? NotesEn, string? NotesEs, DateTime? PublishedUtc, DateTime? LastCheckedUtc,
    bool GitHubCheckEnabled, bool NotifyNewVersion, bool UpdateAvailable);

public sealed class ServerUpdateService
{
    public const string Repository = "ziur-vela/parental-opcontrol";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private readonly ParentalDbContext _db;
    private readonly BuildInfo _build;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;

    public ServerUpdateService(ParentalDbContext db, BuildInfo build, IHttpClientFactory httpFactory, IConfiguration configuration)
    {
        _db = db;
        _build = build;
        _httpFactory = httpFactory;
        _configuration = configuration;
    }

    public async Task<ServerSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await _db.ServerSettings.FirstOrDefaultAsync(ct);
        if (settings is not null) return settings;
        settings = new ServerSettings();
        _db.ServerSettings.Add(settings);
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<bool> IsUpdaterAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = UpdaterHttp();
            using var response = await http.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<ServerUpdateInfo> GetInfoAsync(bool refresh, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (refresh || (settings.LastUpdateCheckUtc is null
            || DateTime.UtcNow - settings.LastUpdateCheckUtc > CheckInterval))
        {
            if (refresh || settings.GitHubCheckEnabled) await CheckAsync(settings, ct);
            else
            {
                settings.LastUpdateCheckUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }
        return ToInfo(settings);
    }

    public async Task<ServerUpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        return await CheckAsync(settings, ct);
    }

    private async Task<ServerUpdateInfo> CheckAsync(ServerSettings settings, CancellationToken ct)
    {
        settings.LastUpdateCheckUtc = DateTime.UtcNow;

        try
        {
            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ParentalOpControl", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var token = _configuration["Updates:GitHubToken"];
            if (!string.IsNullOrWhiteSpace(token))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", ct);
            if (!response.IsSuccessStatusCode)
            {
                await _db.SaveChangesAsync(ct);
                return ToInfo(settings);
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            ReleaseCandidate? best = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
                var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (!tag.StartsWith('v')) continue;
                if (!Version.TryParse(tag.TrimStart('v'), out var parsed)) continue;
                if (!Version.TryParse(_build.Version.TrimStart('v'), out var current)) continue;
                if (parsed <= current) continue;
                if (best is not null && parsed <= best.Version) continue;
                best = new ReleaseCandidate(parsed, tag.TrimStart('v'),
                    release.TryGetProperty("html_url", out var u) ? u.GetString() : null,
                    release.TryGetProperty("body", out var b) ? b.GetString() : null,
                    release.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var dt) ? dt : null,
                    ManifestUrl(release));
            }

            if (best is null)
            {
                settings.LatestVersion = null;
                settings.LatestReleaseUrl = null;
                settings.LatestManifestUrl = null;
                settings.LatestNotesEn = null;
                settings.LatestNotesEs = null;
                settings.LatestPublishedUtc = null;
            }
            else
            {
                settings.LatestVersion = best.Version.ToString();
                settings.LatestReleaseUrl = best.Url;
                settings.LatestManifestUrl = best.ManifestUrl;
                settings.LatestPublishedUtc = best.PublishedUtc;
                settings.LatestNotesEn = best.Notes;
                settings.LatestNotesEs = best.Notes;
                if (!string.IsNullOrWhiteSpace(best.ManifestUrl))
                {
                    var notes = await TryReadManifestNotesAsync(http, best.ManifestUrl, ct);
                    if (notes is not null)
                    {
                        settings.LatestNotesEn = string.IsNullOrWhiteSpace(notes.Value.En) ? best.Notes : notes.Value.En;
                        settings.LatestNotesEs = string.IsNullOrWhiteSpace(notes.Value.Es) ? best.Notes : notes.Value.Es;
                    }
                }
            }
        }
        catch
        {
            // Keep the previous cached values when GitHub is unreachable.
        }

        await _db.SaveChangesAsync(ct);
        return ToInfo(settings);
    }

    private static string? ManifestUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.Equals(name, "release-manifest.json", StringComparison.OrdinalIgnoreCase))
                return asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null;
        }
        return null;
    }

    private static async Task<ManifestNotes?> TryReadManifestNotesAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("notes", out var notes)) return null;
            var en = notes.TryGetProperty("en", out var e) ? e.GetString() : null;
            var es = notes.TryGetProperty("es", out var s) ? s.GetString() : null;
            return new ManifestNotes(en, es);
        }
        catch { return null; }
    }

    private ServerUpdateInfo ToInfo(ServerSettings settings)
    {
        var current = _build.Version.TrimStart('v');
        var latest = settings.LatestVersion;
        var available = !string.IsNullOrWhiteSpace(latest)
            && Version.TryParse(latest, out var l) && Version.TryParse(current, out var c) && l > c;
        return new ServerUpdateInfo(current, latest, _build.DisplayVersion,
            string.IsNullOrWhiteSpace(latest) ? null : $"v{latest}",
            settings.LatestReleaseUrl, settings.LatestNotesEn, settings.LatestNotesEs,
            settings.LatestPublishedUtc, settings.LastUpdateCheckUtc,
            settings.GitHubCheckEnabled, settings.NotifyNewVersion, available);
    }

    public async Task SaveSettingsAsync(bool githubCheckEnabled, bool notifyNewVersion, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        settings.GitHubCheckEnabled = githubCheckEnabled;
        settings.NotifyNewVersion = notifyNewVersion;
        settings.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(bool Ok, string? Error)> ApplyAsync(string version, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (!string.Equals(settings.LatestVersion, version, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(settings.LatestManifestUrl))
            return (false, "That version is not the current stable release. Check again first.");
        try
        {
            using var http = UpdaterHttp();
            var payload = JsonSerializer.Serialize(new { version, manifestUrl = settings.LatestManifestUrl });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("/update", content, ct);
            if (response.IsSuccessStatusCode) return (true, null);
            var body = await response.Content.ReadAsStringAsync(ct);
            return (false, string.IsNullOrWhiteSpace(body) ? $"Updater returned {(int)response.StatusCode}." : body);
        }
        catch (Exception ex)
        {
            return (false, $"The updater sidecar is not reachable: {ex.Message}");
        }
    }

    public async Task<JsonDocument?> UpdaterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = UpdaterHttp();
            using var response = await http.GetAsync("/status", ct);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> RestoreAsync(string backup, CancellationToken ct = default)
    {
        try
        {
            using var http = UpdaterHttp();
            var payload = JsonSerializer.Serialize(new { backup, confirm = true });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("/restore", content, ct);
            if (response.IsSuccessStatusCode) return (true, null);
            return (false, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private HttpClient UpdaterHttp()
    {
        var client = _httpFactory.CreateClient();
        client.BaseAddress = new Uri(_configuration["Updater:BaseUrl"] ?? "http://updater:8098");
        client.Timeout = TimeSpan.FromSeconds(20);
        var secret = _configuration["Updater:Secret"] ?? _configuration["UPDATER_SECRET"] ?? "";
        if (!string.IsNullOrWhiteSpace(secret))
            client.DefaultRequestHeaders.Add("X-Updater-Secret", secret);
        return client;
    }

    private sealed record ReleaseCandidate(Version Version, string Tag, string? Url, string? Notes,
        DateTime? PublishedUtc, string? ManifestUrl);
    private readonly record struct ManifestNotes(string? En, string? Es);
}
