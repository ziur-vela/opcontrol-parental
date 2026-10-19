using Microsoft.Win32;

namespace OpControl.Parental.Client;

public static class BrowserDohPolicy
{
    public static void Apply(ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        SetString(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", "off");
        SetString(@"SOFTWARE\Policies\BraveSoftware\Brave", "DnsOverHttpsMode", "off");
        SetString(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", "off");
        SetDword(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled", 0);
        SetDword(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Locked", 1);
        SetDword(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "EnableAutoDoh", 0);
        logger?.LogInformation("Browser DNS-over-HTTPS policies applied");
    }

    private static void SetString(string path, string name, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            key?.SetValue(name, value, RegistryValueKind.String);
        }
        catch { }
    }

    private static void SetDword(string path, string name, int value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch { }
    }
}
