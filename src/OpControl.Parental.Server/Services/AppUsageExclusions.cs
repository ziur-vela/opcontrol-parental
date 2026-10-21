namespace OpControl.Parental.Server.Services;

/// <summary>
/// System shell processes, Windows UI hosts and our own programs are not apps the child
/// uses. They must never appear in "most used" / app usage lists on the child window or
/// the parent dashboard. File Explorer is the one exception: time spent browsing files is
/// real app usage, so the Windows Explorer shell is shown as "File Explorer".
/// </summary>
public static class AppUsageExclusions
{
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost.exe",
        "backgroundtaskhost.exe",
        "csrss.exe",
        "ctfmon.exe",
        "dwm.exe",
        "dllhost.exe",
        "lockapp.exe",
        "logonui.exe",
        "peopleexperiencehost.exe",
        "runtimebroker.exe",
        "searchapp.exe",
        "searchhost.exe",
        "searchui.exe",
        "securityhealthsystray.exe",
        "shellexperiencehost.exe",
        "shellhost.exe",
        "sihost.exe",
        "smartscreen.exe",
        "startmenuexperiencehost.exe",
        "svchost.exe",
        "systemsettings.exe",
        "taskhostw.exe",
        "textinputhost.exe",
        "wininit.exe",
        "winlogon.exe",
        "wwahost.exe"
    };

    private static readonly HashSet<string> OwnProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "opcontrol.parental.client.exe",
        "opcontrol.parental.helper.exe",
        "opcontrol.parental.setup.exe"
    };

    private static readonly string[] StorePackagePrefixes =
    {
        "microsoft.549981c3f5f10",              // Cortana
        "microsoft.lockapp",
        "microsoft.windows.client.webshell",    // Widgets / web shell
        "microsoft.windows.search",
        "microsoft.windows.shellexperiencehost",
        "microsoft.windows.startmenuexperiencehost",
        "microsoft.windowsshell",
        "microsoftwindows.client.webshell"
    };

    private static readonly HashSet<string> SystemDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application Frame Host",
        "Client Server Runtime Process",
        "Desktop Window Manager",
        "Lock App",
        "Microsoft Text Input Application",
        "Search",
        "Shell Infrastructure Host",
        "Start",
        "Text Input Application",
        "Windows Explorer",
        "Windows Input Experience",
        "Windows Logon Application",
        "Windows Shell Experience Host",
        "Windows Start-Up Application"
    };

    private static readonly string[] ExplorerFileDisplays =
    {
        "file explorer",
        "explorador de archivos"
    };

    public static bool IsExcluded(string? identity, string? displayName)
    {
        var executable = ExecutableName(identity);
        var display = (displayName ?? "").Trim();

        if (executable.Length > 0 && OwnProcesses.Contains(executable)) return true;
        if (display.Contains("opcontrol", StringComparison.OrdinalIgnoreCase)) return true;

        if (string.Equals(executable, "explorer.exe", StringComparison.OrdinalIgnoreCase))
            return !IsFileExplorerDisplay(display);

        if (SystemProcesses.Contains(executable)) return true;

        var package = StorePackageName(identity);
        if (package.Length > 0 && StorePackagePrefixes.Any(prefix =>
                package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return true;

        return SystemDisplayNames.Contains(display);
    }

    public static bool IsFileExplorerDisplay(string? displayName)
    {
        var display = (displayName ?? "").Trim();
        return ExplorerFileDisplays.Any(name => display.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExecutableName(string? identity)
    {
        var value = (identity ?? "").Trim();
        if (value.Length == 0) return "";
        if (value.StartsWith("store:", StringComparison.OrdinalIgnoreCase)) return "";

        var pipe = value.IndexOf('|');
        if (pipe > 0) value = value[..pipe];
        if (value.StartsWith("win32:", StringComparison.OrdinalIgnoreCase)) value = value[6..];
        else if (value.StartsWith("path:", StringComparison.OrdinalIgnoreCase)) value = value[5..];

        var slash = value.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) value = value[(slash + 1)..];
        return value.Trim();
    }

    private static string StorePackageName(string? identity)
    {
        var value = (identity ?? "").Trim();
        return value.StartsWith("store:", StringComparison.OrdinalIgnoreCase) ? value[6..].Trim() : "";
    }
}
