using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace OpControl.Parental.Client;

public static class DomainUtil
{
    private static readonly Lazy<PublicSuffix> Suffixes = new(PublicSuffix.Load);
    private static readonly HashSet<string> IgnoreSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "msftconnecttest.com", "msftncsi.com", "windowsupdate.com", "update.microsoft.com",
        "delivery.mp.microsoft.com", "displaycatalog.mp.microsoft.com", "storeedgefd.dsx.mp.microsoft.com",
        "settings-win.data.microsoft.com", "v10.events.data.microsoft.com", "v20.events.data.microsoft.com",
        "watson.telemetry.microsoft.com", "time.windows.com", "time.nist.gov", "ntp.org",
        "gstatic.com", "googleapis.com", "googletagmanager.com", "google-analytics.com",
        "googlesyndication.com", "googleadservices.com", "doubleclick.net", "2mdn.net",
        "facebook.net", "fbcdn.net", "cdninstagram.com",
        "cdn.jsdelivr.net", "jsdelivr.net", "unpkg.com", "cdnjs.cloudflare.com",
        "cloudfront.net", "amazonaws.com", "azureedge.net", "trafficmanager.net",
        "edgekey.net", "edgesuite.net", "akamaihd.net", "akamaiedge.net", "fastly.net",
        "scorecardresearch.com", "adnxs.com", "criteo.com", "taboola.com", "outbrain.com",
        "sentry.io", "bugsnag.com", "segment.io", "mixpanel.com", "hotjar.com",
        "nr-data.net", "newrelic.com", "optimizely.com", "adsafeprotected.com", "moatads.com",
        "advertising.com", "yieldmo.com", "rubiconproject.com", "pubmatic.com", "openx.net",
        "casalemedia.com", "smartadserver.com", "serving-sys.com", "adsrvr.org", "bidswitch.net",
        "app-measurement.com", "firebaseinstallations.googleapis.com", "crashlytics.com",
        "digicert.com", "sectigo.com", "letsencrypt.org", "ocsp.digicert.com",
        "dns.msftncsi.com", "connectivity-check.ubuntu.com", "connectivitycheck.gstatic.com",
        "ts.net", "tailscale.com", "beta.tailscale.net"
    };

    public static string? GetRegistrableDomain(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (!IsLoggableHost(host)) return null;
        return Suffixes.Value.GetRegistrableDomain(host) ?? host;
    }

    public static bool IsLoggableHost(string host)
    {
        if (host.Length is < 4 or > 253) return false;
        if (host.StartsWith('_') || host.Contains('_')) return false;
        if (host is "localhost" or "wpad" or "localhost.localdomain") return false;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".arpa", StringComparison.OrdinalIgnoreCase)) return false;
        if (!host.Contains('.')) return false;
        if (IPAddress.TryParse(host, out var address) && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            return false;
        return true;
    }

    public static bool IsIgnored(string registrableDomain)
    {
        var value = registrableDomain.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var suffix in IgnoreSuffixes)
        {
            if (value.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool SetContainsDomainOrParent(HashSet<string> set, string domain)
    {
        var value = domain.Trim().TrimEnd('.').ToLowerInvariant();
        while (value.Length > 0)
        {
            if (set.Contains(value)) return true;
            var dot = value.IndexOf('.');
            if (dot < 0) break;
            value = value[(dot + 1)..];
        }
        return false;
    }

    private sealed class PublicSuffix
    {
        private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _wildcard = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _exception = new(StringComparer.OrdinalIgnoreCase);

        public static PublicSuffix Load()
        {
            var result = new PublicSuffix();
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("public_suffix_list.dat");
                if (stream is null) return result;
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("//")) continue;
                    if (line.StartsWith('!'))
                        result._exception.Add(line[1..]);
                    else if (line.StartsWith("*."))
                        result._wildcard.Add(line[2..]);
                    else
                        result._exact.Add(line);
                }
            }
            catch { }
            return result;
        }

        public string? GetRegistrableDomain(string host)
        {
            var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length < 2) return host;

            var suffixLabels = 1;
            for (var i = 0; i < labels.Length; i++)
            {
                var candidate = string.Join('.', labels[i..]);
                if (_exception.Contains(candidate))
                {
                    suffixLabels = labels.Length - i - 1;
                    break;
                }
                if (_exact.Contains(candidate))
                {
                    suffixLabels = labels.Length - i;
                    break;
                }
                if (i + 1 < labels.Length)
                {
                    var parent = string.Join('.', labels[(i + 1)..]);
                    if (_wildcard.Contains(parent))
                    {
                        suffixLabels = labels.Length - i;
                        break;
                    }
                }
            }

            var registrableLabels = suffixLabels + 1;
            if (labels.Length <= suffixLabels) return null;
            return string.Join('.', labels[(labels.Length - registrableLabels)..]);
        }
    }
}

public static class DnsCache
{
    public static void Flush(ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ipconfig.exe",
                Arguments = "/flushdns",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })?.WaitForExit(10000);
            logger?.LogInformation("DNS cache flushed after rule change");
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "DNS cache flush failed");
        }
    }
}
