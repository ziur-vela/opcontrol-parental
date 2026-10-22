// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed record SiteReviewResult(
    string Domain,
    string? Title,
    string? Description,
    string? FaviconUrl,
    string? FetchError,
    IReadOnlyList<string> Categories,
    bool IsAdult,
    string? BlockReason,
    string? KnownPurposeKey,
    int VisitCount,
    int LookupCount,
    DateTime? FirstSeenUtc,
    DateTime? LastSeenUtc);

/// <summary>
/// Builds the "what is this website?" panel: a bounded server-side page fetch,
/// category-membership lookup, block reason and seven-day visit totals.
/// Never executes JavaScript, never sends cookies and never follows more than
/// three HTTPS redirects.
/// </summary>
public sealed class SiteReviewService
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxFaviconBytes = 64 * 1024;
    private const int MaxRedirects = 3;
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private static readonly Regex TitleRegex = new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DescriptionRegex = new(@"<meta\s+[^>]*name\s*=\s*[""']description[""'][^>]*content\s*=\s*[""'](.*?)[""']", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DescriptionReverseRegex = new(@"<meta\s+[^>]*content\s*=\s*[""'](.*?)[""'][^>]*name\s*=\s*[""']description[""']", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex IconRegex = new(@"<link\s+[^>]*rel\s*=\s*[""'][^""']*icon[^""']*[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HrefRegex = new(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    private readonly ParentalDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SiteReviewService> _logger;
    private readonly Func<Uri, CancellationToken, Task<bool>>? _addressCheck;

    public SiteReviewService(ParentalDbContext db, IHttpClientFactory httpFactory, IMemoryCache cache, ILogger<SiteReviewService> logger,
        Func<Uri, CancellationToken, Task<bool>>? addressCheck = null)
    {
        _db = db;
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
        _addressCheck = addressCheck;
    }

    public static bool IsValidDomain(string? domain)
    {
        var value = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (value.Length is < 4 or > 253) return false;
        if (value.Contains('/') || value.Contains(':') || value.Contains(' ')) return false;
        if (!value.Contains('.')) return false;
        if (IPAddress.TryParse(value, out _)) return false;
        return value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-');
    }

    public async Task<SiteReviewResult> GetAsync(Guid familyId, Guid childId, string domain, CancellationToken ct = default)
    {
        var value = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (!IsValidDomain(value))
            return Empty(value, "Website.Review.Invalid");

        var parents = Parents(value).ToList();
        var categories = await _db.CategoryDomains.AsNoTracking()
            .Where(d => d.RemovedVersion == null && parents.Contains(d.Domain))
            .Select(d => d.CategoryKey)
            .Distinct()
            .ToListAsync(ct);
        var isAdult = categories.Contains("adult");

        var enabledCategories = await _db.ChildCategories.AsNoTracking()
            .Where(c => c.FamilyId == familyId && c.ChildId == childId && c.Enabled)
            .Select(c => c.CategoryKey)
            .ToListAsync(ct);
        var rules = await _db.WebsiteRules.AsNoTracking()
            .Where(r => r.FamilyId == familyId && r.ChildId == childId && parents.Contains(r.Domain))
            .ToListAsync(ct);
        var blockReason = await BlockReasonAsync(familyId, childId, value, rules, enabledCategories, ct);

        var now = DateTime.UtcNow;
        var since = now.AddDays(-7);
        var visits = await _db.VisitRecords.AsNoTracking()
            .Where(v => v.FamilyId == familyId && v.ChildId == childId && parents.Contains(v.Domain) && v.VisitedUtc >= since)
            .Select(v => new { v.Domain, v.VisitedUtc, v.Lookups })
            .ToListAsync(ct);
        var all = await _db.VisitRecords.AsNoTracking()
            .Where(v => v.FamilyId == familyId && v.ChildId == childId && parents.Contains(v.Domain))
            .GroupBy(v => v.Domain)
            .Select(g => new { First = g.Min(v => v.VisitedUtc), Last = g.Max(v => v.VisitedUtc) })
            .OrderBy(x => x.First)
            .FirstOrDefaultAsync(ct);
        var visitCount = visits.Count;
        var lookupCount = visits.Sum(v => Math.Max(1, v.Lookups));

        PageInfo page;
        if (isAdult)
            page = new PageInfo(null, null, null, null);
        else
            page = await FetchPageAsync(value, ct);

        return new SiteReviewResult(value, page.Title, page.Description, page.FaviconUrl,
            page.ErrorKey, categories, isAdult, blockReason, KnownSites.KeyFor(value),
            visitCount, lookupCount, all?.First, all?.Last);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetFaviconAsync(string domain, CancellationToken ct = default)
    {
        var value = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (!IsValidDomain(value) || BackgroundDomains.IsBackground(value)) return null;
        var parents = Parents(value).ToList();
        if (await _db.CategoryDomains.AsNoTracking().AnyAsync(d => d.CategoryKey == "adult"
            && d.RemovedVersion == null && parents.Contains(d.Domain), ct))
            return null;
        var key = "site-favicon:" + value;
        if (_cache.TryGetValue(key, out (byte[] Bytes, string ContentType)? cached)) return cached;
        var page = await FetchPageAsync(value, ct);
        if (string.IsNullOrWhiteSpace(page.FaviconUrl) || !Uri.TryCreate(page.FaviconUrl, UriKind.Absolute, out var icon)
            || icon.Scheme != Uri.UriSchemeHttps)
            return null;
        try
        {
            var client = _httpFactory.CreateClient("sitereview");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PageTimeout);
            using var response = await client.GetAsync(icon, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var type = response.Content.Headers.ContentType?.MediaType ?? "image/x-icon";
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            var bytes = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), MaxFaviconBytes, timeout.Token);
            if (bytes.Length == 0) return null;
            var result = (bytes, type);
            _cache.Set(key, result, CacheLifetime);
            return result;
        }
        catch { return null; }
    }

    private async Task<string?> BlockReasonAsync(Guid familyId, Guid childId, string domain,
        List<WebsiteRule> rules, List<string> enabledCategories, CancellationToken ct)
    {
        var allowed = rules.FirstOrDefault(r => !r.IsBlocked);
        if (allowed is not null) return null;
        foreach (var rule in rules.Where(r => r.IsBlocked))
        {
            if (rule.Mode == RuleMode.Always) return "rule:always";
            if (rule.ScheduleId is { } scheduleId)
            {
                var name = await _db.Schedules.AsNoTracking()
                    .Where(s => s.Id == scheduleId && s.FamilyId == familyId && s.ChildId == childId)
                    .Select(s => s.Name)
                    .FirstOrDefaultAsync(ct);
                if (!string.IsNullOrWhiteSpace(name)) return "rule:schedule:" + name;
                return "rule:schedule";
            }
        }
        foreach (var category in enabledCategories)
        {
            var parents = Parents(domain).ToList();
            if (await _db.CategoryDomains.AsNoTracking().AnyAsync(d => d.CategoryKey == category
                && d.RemovedVersion == null && parents.Contains(d.Domain), ct))
                return "category:" + category;
        }
        return null;
    }

    private async Task<PageInfo> FetchPageAsync(string domain, CancellationToken ct)
    {
        var key = "site-review:" + domain;
        if (_cache.TryGetValue(key, out PageInfo? cached) && cached is not null) return cached;
        var result = await FetchPageCoreAsync(domain, ct);
        _cache.Set(key, result, result.ErrorKey is null ? CacheLifetime : TimeSpan.FromMinutes(5));
        return result;
    }

    private async Task<PageInfo> FetchPageCoreAsync(string domain, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("sitereview");
        var url = new Uri("https://" + domain + "/");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PageTimeout);
            for (var redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                var publicAddress = _addressCheck is null
                    ? await IsPublicHttpsAsync(url, timeout.Token)
                    : await _addressCheck(url, timeout.Token);
                if (!publicAddress) return new PageInfo(null, null, null, "Website.Review.Unreachable");
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (url.Scheme != Uri.UriSchemeHttps) return new PageInfo(null, null, null, "Website.Review.Unreachable");
                    continue;
                }
                if (!response.IsSuccessStatusCode) return new PageInfo(null, null, null, "Website.Review.Unreachable");
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Length > 0 && !type.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return new PageInfo(null, null, null, "Website.Review.NotHtml");
                if (response.Content.Headers.ContentLength > MaxBytes)
                    return new PageInfo(null, null, null, "Website.Review.TooLarge");
                var bytes = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), MaxBytes, timeout.Token);
                var html = Encoding.UTF8.GetString(bytes);
                return new PageInfo(ParseTitle(html), ParseDescription(html), ParseFavicon(url, html), null);
            }
            return new PageInfo(null, null, null, "Website.Review.Redirects");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PageInfo(null, null, null, "Website.Review.Timeout");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Website review fetch failed for {Domain}", domain);
            return new PageInfo(null, null, null, "Website.Review.Unreachable");
        }
    }

    private static async Task<bool> IsPublicHttpsAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host)) return false;
        if (uri.Host is "localhost" || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            if (addresses.Length == 0) return false;
            return addresses.All(address => !IsPrivate(address));
        }
        catch { return false; }
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 0
                || bytes[0] >= 224;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast;
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (memory.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - memory.Length)), ct);
            if (read <= 0) break;
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static string? ParseTitle(string html)
    {
        var match = TitleRegex.Match(html);
        return match.Success ? Clean(match.Groups[1].Value, 200) : null;
    }

    private static string? ParseDescription(string html)
    {
        var match = DescriptionRegex.Match(html);
        if (!match.Success) match = DescriptionReverseRegex.Match(html);
        return match.Success ? Clean(match.Groups[1].Value, 400) : null;
    }

    private static string? ParseFavicon(Uri page, string html)
    {
        foreach (Match tag in IconRegex.Matches(html))
        {
            var href = HrefRegex.Match(tag.Value);
            if (!href.Success) continue;
            if (Uri.TryCreate(page, WebUtility.HtmlDecode(href.Groups[1].Value), out var icon) && icon.Scheme == Uri.UriSchemeHttps)
                return icon.ToString();
        }
        return new Uri(page, "/favicon.ico").ToString();
    }

    private static string? Clean(string value, int max)
    {
        var text = WebUtility.HtmlDecode(TagRegex.Replace(value, " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text.Length > max ? text[..max] : text;
    }

    private static IEnumerable<string> Parents(string domain)
    {
        var value = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        while (value.Length > 0)
        {
            yield return value;
            var dot = value.IndexOf('.');
            if (dot < 0) break;
            value = value[(dot + 1)..];
        }
    }

    private static SiteReviewResult Empty(string domain, string errorKey) => new(domain, null, null, null, errorKey,
        [], false, null, null, 0, 0, null, null);

    private sealed record PageInfo(string? Title, string? Description, string? FaviconUrl, string? ErrorKey);
}
