// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class CategoryRefreshService : BackgroundService
{
    private static readonly (string Key, string Name, string Url)[] Sources =
    [
        ("adult", "Adult content", "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn-only/hosts"),
        ("gambling", "Gambling", "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/gambling-only/hosts"),
        ("social", "Social media", "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/social/hosts"),
        ("fakenews", "Fake news", "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/fakenews-only/hosts")
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CategoryRefreshService> _logger;

    public CategoryRefreshService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory, ILogger<CategoryRefreshService> logger)
    {
        _scopes = scopes;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Category refresh failed");
            }

            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
        var client = _httpFactory.CreateClient("categories");
        client.Timeout = TimeSpan.FromMinutes(2);

        foreach (var (key, name, url) in Sources)
        {
            List<string> domains;
            try
            {
                var text = await client.GetStringAsync(url, ct);
                domains = ParseHosts(text);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning("Could not fetch {Category} list: {Message}", key, ex.Message);
                continue;
            }

            if (domains.Count < 10)
            {
                _logger.LogWarning("Category {Category} list was unexpectedly small ({Count}); keeping previous version", key, domains.Count);
                continue;
            }

            var category = await db.Categories.FirstOrDefaultAsync(c => c.Key == key, ct);
            if (category is null)
            {
                category = new Category { Key = key, Name = name, Version = 0, SourceUrl = url };
                db.Categories.Add(category);
                await db.SaveChangesAsync(ct);
            }

            var existing = await db.CategoryDomains.Where(d => d.CategoryKey == key).ToListAsync(ct);
            var current = existing.Where(d => d.RemovedVersion is null).Select(d => d.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var target = domains.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newVersion = category.Version + 1;
            var added = 0;
            var removed = 0;

            foreach (var domain in target)
            {
                if (current.Contains(domain)) continue;
                db.CategoryDomains.Add(new CategoryDomain
                {
                    CategoryKey = key,
                    Domain = domain,
                    AddedVersion = newVersion
                });
                added++;
            }

            foreach (var row in existing.Where(d => d.RemovedVersion is null && !target.Contains(d.Domain)))
            {
                row.RemovedVersion = newVersion;
                removed++;
            }

            category.Name = name;
            category.Version = newVersion;
            category.FetchedUtc = DateTime.UtcNow;
            category.SourceUrl = url;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Category {Category} v{Version}: {Active} domains (+{Added} / -{Removed})", key, newVersion, target.Count, added, removed);
        }
    }

    private static List<string> ParseHosts(string text)
    {
        var domains = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment].Trim();
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var host = parts[1].Trim().TrimEnd('.').ToLowerInvariant();
            if (host.Length is < 3 or > 253 || !host.Contains('.')) continue;
            if (host.Contains('/') || host.Contains(':')) continue;
            domains.Add(host);
        }
        return domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
