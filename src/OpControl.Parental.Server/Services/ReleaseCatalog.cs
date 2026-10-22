// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Server.Services;

public sealed record ReleaseFiles(string Version, string? SetupPath, string? MsiPath, string? Sha256, long SizeBytes, DateTime PublishedUtc)
{
    public ReleaseInfoDto ToDto(string? previous) => new(
        Version,
        Sha256 ?? "",
        SizeBytes,
        PublishedUtc,
        SetupPath is null ? "" : Path.GetFileName(SetupPath),
        MsiPath is null ? "" : Path.GetFileName(MsiPath),
        previous);
}

public sealed class ReleaseCatalog
{
    private static readonly Regex VersionPattern = new(@"(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?)", RegexOptions.Compiled);
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReleaseCatalog> _logger;
    private readonly object _gate = new();
    private List<ReleaseFiles> _releases = [];
    private DateTime _scannedUtc = DateTime.MinValue;
    private readonly Dictionary<string, (long Length, DateTime ModifiedUtc, string Sha256)> _hashCache = new(StringComparer.OrdinalIgnoreCase);

    public ReleaseCatalog(IConfiguration configuration, ILogger<ReleaseCatalog> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string Directory
    {
        get
        {
            var configured = _configuration["Releases:Path"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            return Path.Combine(AppContext.BaseDirectory, "releases");
        }
    }

    public IReadOnlyList<ReleaseFiles> All()
    {
        EnsureFresh();
        lock (_gate) return _releases;
    }

    public ReleaseFiles? Current()
    {
        EnsureFresh();
        lock (_gate) return _releases.FirstOrDefault(r => r.MsiPath is not null);
    }

    public ReleaseFiles? CurrentSetup()
    {
        EnsureFresh();
        lock (_gate) return _releases.FirstOrDefault(r => r.SetupPath is not null && File.Exists(r.SetupPath));
    }

    public ReleaseFiles? Previous()
    {
        EnsureFresh();
        lock (_gate) return _releases.Skip(1).FirstOrDefault(r => r.MsiPath is not null);
    }

    public ReleaseFiles? Get(string version)
    {
        EnsureFresh();
        lock (_gate) return _releases.FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));
    }

    public ReleaseInfoDto? CurrentInfo()
    {
        var current = Current();
        if (current is null) return null;
        return current.ToDto(Previous()?.Version);
    }

    public void Refresh()
    {
        lock (_gate) _scannedUtc = DateTime.MinValue;
        EnsureFresh();
    }

    private void EnsureFresh()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _scannedUtc < TimeSpan.FromSeconds(60)) return;
            _scannedUtc = DateTime.UtcNow;
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    _releases = [];
                    return;
                }

                var byVersion = new Dictionary<string, (string? Setup, string? Msi, DateTime Published)>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in System.IO.Directory.EnumerateFiles(Directory))
                {
                    var name = Path.GetFileName(path);
                    var extension = Path.GetExtension(path);
                    if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!name.StartsWith("OpControlParental", StringComparison.OrdinalIgnoreCase)) continue;
                    var match = VersionPattern.Match(name);
                    if (!match.Success) continue;
                    var version = match.Groups[1].Value;
                    var published = File.GetLastWriteTimeUtc(path);
                    var entry = byVersion.TryGetValue(version, out var existing) ? existing : (null, null, published);
                    if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase)) entry.Msi = path;
                    else if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) entry.Setup = path;
                    entry.Published = published > entry.Published ? published : entry.Published;
                    byVersion[version] = entry;
                }

                _releases = byVersion
                    .Select(pair =>
                    {
                        var path = pair.Value.Msi ?? pair.Value.Setup!;
                        var (sha, size) = HashFileCached(path);
                        return new ReleaseFiles(pair.Key, pair.Value.Setup, pair.Value.Msi, sha, size, pair.Value.Published);
                    })
                    .OrderByDescending(r => ParseVersion(r.Version))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not scan release directory {Directory}", Directory);
            }
        }
    }

    private (string Sha256, long Size) HashFileCached(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (_hashCache.TryGetValue(path, out var cached)
                && cached.Length == info.Length
                && cached.ModifiedUtc == info.LastWriteTimeUtc)
                return (cached.Sha256, cached.Length);

            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var size = stream.Length;
            _hashCache[path] = (size, info.LastWriteTimeUtc, hash);
            return (hash, size);
        }
        catch
        {
            return ("", 0);
        }
    }

    public static Version ParseVersion(string value)
    {
        var match = VersionPattern.Match(value);
        if (!match.Success) return new Version(0, 0);
        var text = match.Groups[1].Value.Split('-', '+')[0];
        return Version.TryParse(text, out var parsed) ? parsed : new Version(0, 0);
    }
}
