using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class AppInventoryService : BackgroundService
{
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "svchost.exe", "dwm.exe", "explorer.exe", "fontdrvhost.exe", "sihost.exe",
        "taskhostw.exe", "runtimebroker.exe", "ctfmon.exe", "conhost.exe", "msiexec.exe",
        "opcontrol.parental.client.exe", "opcontrol.parental.helper.exe", "opcontrol.parental.client.windows.helper.exe"
    };

    private readonly ClientSettings _settings;
    private readonly DataSyncService _sync;
    private readonly ILogger<AppInventoryService> _logger;
    private string _lastHash = "";
    private bool _firstRun = true;

    public AppInventoryService(ClientSettings settings, DataSyncService sync, ILogger<AppInventoryService> logger)
    {
        _settings = settings;
        _sync = sync;
        _logger = logger;
        try
        {
            if (File.Exists(ClientPaths.InventoryHashFile))
            {
                _lastHash = File.ReadAllText(ClientPaths.InventoryHashFile).Trim();
                _firstRun = false;
            }
        }
        catch { }
    }

    public static bool IsProtected(string processName) => ProtectedProcesses.Contains(processName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "App inventory scan failed");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task ScanAsync(CancellationToken ct)
    {
        var apps = Collect();
        var hash = ComputeHash(apps);
        if (!_firstRun && hash == _lastHash) return;
        _lastHash = hash;
        _firstRun = false;
        try { File.WriteAllText(ClientPaths.InventoryHashFile, hash); } catch { }
        _logger.LogInformation("App inventory changed: {Count} apps", apps.Count);
        _sync.QueueInventory(new AppInventoryRequest(apps, DateTime.UtcNow));
        await Task.CompletedTask;
    }

    public List<AppInventoryItemDto> Collect()
    {
        var apps = new Dictionary<string, AppInventoryItemDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in CollectStoreApps()) apps[app.Identity] = app;
        foreach (var app in CollectWin32Apps()) apps[app.Identity] = app;
        foreach (var app in CollectShortcutApps()) apps[app.Identity] = app;
        return apps.Values.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IEnumerable<AppInventoryItemDto> CollectStoreApps()
    {
        var results = new List<AppInventoryItemDto>();
        try
        {
            var sid = MonitoredSid();
            if (sid is null) return results;
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(sid))
            {
                try
                {
                    if (package.IsFramework || package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System)
                        continue;
                    var id = package.Id;
                    var identity = $"store:{id.FamilyName.ToLowerInvariant()}";
                    results.Add(new AppInventoryItemDto(
                        identity,
                        Safe(() => package.DisplayName) ?? id.Name,
                        Safe(() => package.PublisherDisplayName),
                        Safe(() => package.InstalledPath),
                        "store",
                        id.Version.ToString()));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Store app inventory failed");
        }
        return results;
    }

    private IEnumerable<AppInventoryItemDto> CollectWin32Apps()
    {
        var results = new List<AppInventoryItemDto>();
        var roots = new List<RegistryKey?>
        {
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
        };
        var sid = MonitoredSid();
        if (sid is not null)
        {
            try { roots.Add(Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Windows\CurrentVersion\Uninstall")); }
            catch { }
        }

        foreach (var root in roots)
        {
            if (root is null) continue;
            using (root)
            {
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var key = root.OpenSubKey(name);
                        if (key is null) continue;
                        if (Convert.ToInt32(key.GetValue("SystemComponent") ?? 0) == 1) continue;
                        var display = key.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(display)) continue;
                        var publisher = key.GetValue("Publisher") as string;
                        var version = key.GetValue("DisplayVersion") as string;
                        var path = ExtractExecutable(key.GetValue("DisplayIcon") as string);
                        path ??= ExtractExecutable(key.GetValue("UninstallString") as string);
                        var identity = path is not null ? AppIdentity.FromWin32Path(path) : "";
                        if (identity.Length == 0)
                            identity = $"win32:{Slug(display)}|{AppIdentity.NormalizePublisher(publisher)}";
                        results.Add(new AppInventoryItemDto(identity, display.Trim(), publisher, path, "win32", version));
                    }
                    catch { }
                }
            }
        }
        return results;
    }

    private IEnumerable<AppInventoryItemDto> CollectShortcutApps()
    {
        var results = new List<AppInventoryItemDto>();
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        var profile = MonitoredProfilePath();
        if (profile is not null)
            folders.Add(Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu"));
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        object? shell = null;
        try { shell = shellType is null ? null : Activator.CreateInstance(shellType); } catch { }
        if (shell is null) return results;

        try
        {
            foreach (var folder in folders.Where(Directory.Exists))
            {
                var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 5 };
                foreach (var shortcut in Directory.EnumerateFiles(folder, "*.lnk", options).Take(500))
                {
                    try
                    {
                        var shortcutObject = shellType!.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcut]);
                        var target = shortcutObject?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcutObject, null) as string;
                        if (string.IsNullOrWhiteSpace(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(target)) continue;
                        var identity = AppIdentity.FromWin32Path(target);
                        if (identity.Length == 0) continue;
                        var info = FileVersionInfo.GetVersionInfo(target);
                        var display = string.IsNullOrWhiteSpace(info.FileDescription) ? Path.GetFileNameWithoutExtension(target) : info.FileDescription;
                        results.Add(new AppInventoryItemDto(identity, display, info.CompanyName, target, "win32", info.FileVersion));
                    }
                    catch { }
                }
            }
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
        return results;
    }

    private string? MonitoredProfilePath()
    {
        try
        {
            var sid = MonitoredSid();
            if (sid is null) return null;
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            return key?.GetValue("ProfileImagePath") as string;
        }
        catch { return null; }
    }

    private string? MonitoredSid()
    {
        try
        {
            var account = _settings.Value.MonitoredAccount;
            var name = account.Contains('\\') ? account : $"{Environment.MachineName}\\{account}";
            return (new NTAccount(name).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch { return null; }
    }

    private static string? ExtractExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1) text = text[1..end];
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma > 2 && int.TryParse(text[(comma + 1)..].Trim(), out _)) text = text[..comma];
            var space = text.IndexOf(' ');
            if (space > 2) text = text[..space];
        }
        text = text.Trim().Trim('"');
        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text : null;
    }

    private static string Safe(Func<string?> getter)
    {
        try { return getter() ?? ""; } catch { return ""; }
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') builder.Append(ch);
        }
        return builder.Length == 0 ? "app" : builder.ToString();
    }

    private static string ComputeHash(IEnumerable<AppInventoryItemDto> apps)
    {
        var text = string.Join('\n', apps.Select(a => $"{a.Identity}|{a.Version}|{a.DisplayName}").OrderBy(s => s, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
