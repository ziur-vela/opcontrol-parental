// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Net.Http.Headers;
using System.Text.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class CategoryCache
{
    private sealed class CacheEntry
    {
        public int Version { get; set; }
        public HashSet<string> Domains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CacheFile
    {
        public Dictionary<string, CacheEntry> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private static readonly JsonSerializerOptions CompactJson = new(ClientOptions.JsonOptions) { WriteIndented = false };
    private readonly object _gate = new();
    private CacheFile _cache = new();
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialStore _credentials;
    private readonly EnrollmentClient _enrollment;
    private readonly ClientSettings _settings;
    private readonly ILogger<CategoryCache> _logger;
    private readonly SemaphoreSlim _updateGate = new(1, 1);

    public CategoryCache(IHttpClientFactory httpFactory, CredentialStore credentials, EnrollmentClient enrollment, ClientSettings settings, ILogger<CategoryCache> logger)
    {
        _httpFactory = httpFactory;
        _credentials = credentials;
        _enrollment = enrollment;
        _settings = settings;
        _logger = logger;
        Load();
    }

    public bool IsInEnabledCategory(string domain, IReadOnlyList<CategoryToggleDto> categories)
    {
        lock (_gate)
        {
            foreach (var category in categories.Where(c => c.Enabled))
            {
                if (!_cache.Categories.TryGetValue(category.Key, out var entry)) continue;
                if (DomainUtil.SetContainsDomainOrParent(entry.Domains, domain)) return true;
            }
        }
        return false;
    }

    public async Task UpdateAsync(IReadOnlyList<CategoryToggleDto> categories, CancellationToken ct)
    {
        var enabled = categories.Where(c => c.Enabled).ToList();
        if (enabled.Count == 0) return;
        await _updateGate.WaitAsync(ct);
        try
        {
            var credential = _credentials.Load();
            if (credential is null || !credential.IsUsable) return;
            var client = _httpFactory.CreateClient("device");
            var server = _settings.Value.ServerUrl.TrimEnd('/');
            var changed = false;

            foreach (var category in enabled)
            {
                int since;
                lock (_gate)
                {
                    since = _cache.Categories.TryGetValue(category.Key, out var existing) ? existing.Version : 0;
                }
                if (since == category.Version && since > 0) continue;

                var delta = await GetAsync(client, $"{server}/api/device/categories/{Uri.EscapeDataString(category.Key)}?since={since}", credential, ct);
                if (delta is null) continue;
                lock (_gate)
                {
                    if (!_cache.Categories.TryGetValue(category.Key, out var entry) || delta.Reset)
                    {
                        entry = new CacheEntry();
                        _cache.Categories[category.Key] = entry;
                    }
                    foreach (var domain in delta.Removed) entry.Domains.Remove(domain);
                    foreach (var domain in delta.Added) entry.Domains.Add(domain);
                    entry.Version = delta.Version;
                    changed = true;
                }
                _logger.LogInformation("Category {Category} cached at v{Version} ({Count} domains, +{Added}/-{Removed})",
                    category.Key, delta.Version, delta.Added.Count + delta.Removed.Count, delta.Added.Count, delta.Removed.Count);
            }

            lock (_gate)
            {
                var active = enabled.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var key in _cache.Categories.Keys.Where(k => !active.Contains(k)).ToList())
                    _cache.Categories.Remove(key);
                if (changed) Save();
            }
            if (changed) DnsCache.Flush(_logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Category refresh failed");
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private async Task<CategoryDeltaDto?> GetAsync(HttpClient client, string url, DeviceCredential credential, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
            using var response = await client.SendAsync(message, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                var refreshed = await _enrollment.RefreshAsync(_settings.Value.ServerUrl, credential.Credential, ct);
                if (refreshed is null) return null;
                credential.Credential = refreshed.Credential;
                credential.ExpiresUtc = refreshed.CredentialExpiresUtc;
                credential.UpdatedUtc = DateTime.UtcNow;
                _credentials.Save(credential);
                continue;
            }
            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<CategoryDeltaDto>(await response.Content.ReadAsStringAsync(ct), JsonOptions);
        }
        return null;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(ClientPaths.CategoryCacheFile)) return;
            _cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(ClientPaths.CategoryCacheFile), JsonOptions) ?? new CacheFile();
        }
        catch { _cache = new CacheFile(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(ClientPaths.PrivateDirectory);
            SecureFile.SystemOnlyDirectory(ClientPaths.PrivateDirectory);
            var temp = ClientPaths.CategoryCacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache, CompactJson));
            SecureFile.SystemOnly(temp);
            File.Move(temp, ClientPaths.CategoryCacheFile, overwrite: true);
            SecureFile.SystemOnly(ClientPaths.CategoryCacheFile);
        }
        catch { }
    }
}
