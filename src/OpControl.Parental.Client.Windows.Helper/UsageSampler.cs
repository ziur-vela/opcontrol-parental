using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpControl.Parental.Client;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Helper;

public sealed class UsageSampler
{
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "brave", "chrome", "msedge", "firefox", "opera", "vivaldi", "librewolf", "waterfox"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(HelperPaths.JsonOptions) { WriteIndented = false };
    private readonly Dictionary<IntervalKey, int> _pending = new();
    private readonly Dictionary<SiteKey, int> _pendingSites = new();
    private readonly object _gate = new();
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private DateTime _lastFlushUtc = DateTime.UtcNow;

    public void Sample(RuntimeLockStateDto? state)
    {
        var now = DateTime.UtcNow;
        var elapsed = (int)Math.Clamp((now - _lastSampleUtc).TotalSeconds, 1, 20);
        if (now - _lastSampleUtc < TimeSpan.FromSeconds(5))
        {
            FlushCompleted(now);
            return;
        }
        _lastSampleUtc = now;

        if (state is null || state.IsLocked || IsSessionLocked())
        {
            FlushCompleted(now);
            return;
        }

        var threshold = Math.Clamp(state.IdleThresholdSeconds ?? 180, 30, 3600);
        if (IdleSeconds() >= threshold)
        {
            AddInterval(TruncateToMinute(now), "", "", elapsed, false);
            FlushCompleted(now);
            return;
        }

        var (identity, display, processName) = ForegroundApp();
        var bucket = TruncateToMinute(now);
        AddInterval(bucket, identity, display, elapsed, true);
        if (Browsers.Contains(processName) && TryReadLastVisit(now) is { } domain)
            AddSite(bucket, domain, elapsed);

        FlushCompleted(now);
    }

    public void FlushAll()
    {
        Flush(DateTime.MaxValue);
    }

    private void AddInterval(DateTime bucket, string identity, string display, int seconds, bool active)
    {
        var key = new IntervalKey(bucket, identity, display, active);
        lock (_gate) _pending[key] = _pending.GetValueOrDefault(key) + seconds;
    }

    private void AddSite(DateTime bucket, string domain, int seconds)
    {
        var key = new SiteKey(bucket, domain);
        lock (_gate) _pendingSites[key] = _pendingSites.GetValueOrDefault(key) + seconds;
    }

    private void FlushCompleted(DateTime nowUtc) => Flush(TruncateToMinute(nowUtc));

    private void Flush(DateTime beforeUtc)
    {
        List<IntervalKey> intervals;
        List<SiteKey> sites;
        lock (_gate)
        {
            intervals = _pending.Where(p => p.Key.Bucket < beforeUtc).Select(p => p.Key).ToList();
            foreach (var key in intervals)
            {
                var seconds = _pending[key];
                _pending.Remove(key);
                if (seconds > 0)
                    WriteLine(HelperPaths.UsageQueueFile, new UsageIntervalDto(Guid.NewGuid(), key.Identity, key.Display, key.Bucket, seconds, key.Active));
            }

            sites = _pendingSites.Where(p => p.Key.Bucket < beforeUtc).Select(p => p.Key).ToList();
            foreach (var key in sites)
            {
                var seconds = _pendingSites[key];
                _pendingSites.Remove(key);
                if (seconds > 0)
                    WriteLine(HelperPaths.WebsiteUsageQueueFile, new WebsiteUsageDto(Guid.NewGuid(), key.Domain, key.Bucket, seconds));
            }
        }
        _lastFlushUtc = DateTime.UtcNow;
    }

    private static void WriteLine<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(HelperPaths.UserDirectory);
            File.AppendAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
            if (new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                var lines = File.ReadAllLines(path);
                File.WriteAllLines(path, lines.Skip(lines.Length / 2));
            }
        }
        catch { }
    }

    private static string? TryReadLastVisit(DateTime nowUtc)
    {
        try
        {
            if (!File.Exists(HelperPaths.LastVisitFile)) return null;
            using var stream = new FileStream(HelperPaths.LastVisitFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("domain", out var domainElement)) return null;
            if (!document.RootElement.TryGetProperty("utc", out var utcElement) || !utcElement.TryGetDateTime(out var utc)) return null;
            if (nowUtc - utc > TimeSpan.FromMinutes(5)) return null;
            var domain = domainElement.GetString() ?? "";
            return domain.Length == 0 ? null : domain;
        }
        catch { return null; }
    }

    private static (string Identity, string Display, string Process) ForegroundApp()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return ("", "", "");
        _ = GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0) return ("", "", "");
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.SessionId != Process.GetCurrentProcess().SessionId) return ("", "", "");
            string? path = null;
            try { path = process.MainModule?.FileName; } catch { }
            var identity = AppIdentity.ForProcess(process, path);
            var display = process.ProcessName;
            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) display = description.Trim();
                }
            }
            catch { }
            return (identity, display, process.ProcessName);
        }
        catch { return ("", "", ""); }
    }

    private static int IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return (int)(idleMs / 1000);
    }

    private static bool IsSessionLocked()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
        if (desktop == IntPtr.Zero) return true;
        CloseDesktop(desktop);
        return false;
    }

    private static DateTime TruncateToMinute(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private readonly record struct IntervalKey(DateTime Bucket, string Identity, string Display, bool Active);
    private readonly record struct SiteKey(DateTime Bucket, string Domain);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);
}
