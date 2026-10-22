// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.Win32;

namespace OpControl.Parental.Client;

public static class MsiConfig
{
    private const string KeyPath = @"Software\OpControl\Parental";

    public static void Apply(ClientOptions options)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key is null) return;
            var server = key.GetValue("Server") as string;
            var code = key.GetValue("Code") as string;
            var account = key.GetValue("Account") as string;
            var changed = false;
            if (!string.IsNullOrWhiteSpace(server) && !string.Equals(options.ServerUrl.TrimEnd('/'), server.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                options.ServerUrl = server.TrimEnd('/');
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(code))
            {
                options.EnrollmentCode = code.Trim();
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(account))
            {
                options.OsAccountName = account.Trim();
                options.MonitoredAccount = account.Trim();
                changed = true;
            }
            if (changed)
            {
                options.Save();
                try { SecureFile.SystemOnly(ClientPaths.ConfigFile); } catch { }
            }

            foreach (var name in new[] { "Server", "Code", "Account" })
            {
                try { key.DeleteValue(name, throwOnMissingValue: false); } catch { }
            }
        }
        catch
        {
            // The service can run without MSI-provided values; the installer UI is the normal path.
        }
    }
}
