// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Server.Services;

public sealed class BakedReleaseService
{
    private const string SourceDirectory = "/app/release-assets";
    private readonly IConfiguration _configuration;
    private readonly BuildInfo _build;
    private readonly ILogger<BakedReleaseService> _logger;

    public BakedReleaseService(IConfiguration configuration, BuildInfo build, ILogger<BakedReleaseService> logger)
    {
        _configuration = configuration;
        _build = build;
        _logger = logger;
    }

    public void EnsureCopied()
    {
        try
        {
            if (!Directory.Exists(SourceDirectory))
            {
                _logger.LogInformation("No baked client release assets found; using the mounted releases folder as is.");
                return;
            }
            var destination = _configuration["Releases:Path"];
            if (string.IsNullOrWhiteSpace(destination))
            {
                _logger.LogWarning("Releases:Path is not configured; baked client assets were not copied.");
                return;
            }
            Directory.CreateDirectory(destination);
            foreach (var source in Directory.EnumerateFiles(SourceDirectory))
            {
                var name = Path.GetFileName(source);
                if (!name.StartsWith("OpControlParental", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(destination, name);
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length) continue;
                File.Copy(source, target, overwrite: true);
                _logger.LogInformation("Published baked client release {File} for server version {Version}", name, _build.Version);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish the baked client release assets.");
        }
    }
}

public sealed class SetupTokenService
{
    public SetupTokenService() => Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public string Token { get; }

    public bool IsValid(string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(candidate.Trim().ToLowerInvariant()),
            System.Text.Encoding.UTF8.GetBytes(Token));
}
