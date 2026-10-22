// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Reflection;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Server.Services;

public sealed class BuildInfo
{
    public BuildInfo()
    {
        var assembly = typeof(BuildInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;
        var metadata = plus >= 0 ? informational[(plus + 1)..] : "";
        var commit = metadata.Split('.')[0];
        if (string.IsNullOrWhiteSpace(commit)) commit = "unknown";
        var buildDate = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "BuildDate", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(buildDate))
            buildDate = File.GetLastWriteTimeUtc(assembly.Location).ToString("yyyy-MM-dd");

        Version = version;
        Commit = commit.Length > 12 ? commit[..12] : commit;
        BuildDate = buildDate;
        ShortCommit = Commit.Length > 7 ? Commit[..7] : Commit;
        var label = version.StartsWith('v') ? version : $"v{version}";
        DisplayVersion = $"{label} · build {BuildDate} · {ShortCommit}";
    }

    public string Version { get; }
    public string Commit { get; }
    public string ShortCommit { get; }
    public string BuildDate { get; }
    public string DisplayVersion { get; }

    public VersionInfoDto ToDto() => new(Version, DisplayVersion, Commit, BuildDate);
}

public sealed class BrandOptions
{
    public string ProductName { get; set; } = "Parental OpControl";
    public string AccentColor { get; set; } = "#4f7cff";
    public string LogoLight { get; set; } = "/brand/lockup-color.svg";
    public string LogoDark { get; set; } = "/brand/lockup-ondark.svg";
}
