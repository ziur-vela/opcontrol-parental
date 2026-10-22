// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Contracts;

/// <summary>
/// Infrastructure, update and remote-support traffic that must never appear as a
/// child visit or alert and must never be blocked by a category. One list used by
/// the server and the Windows client.
/// </summary>
public static class BackgroundDomains
{
    private static readonly string[] Domains =
    [
        // Our own service and the RMM/remote-support stack.
        "opcontrol.net",
        "tacticalrmm.io",
        "rmm.opcontrol.net",
        "api-rmm.opcontrol.net",
        "mesh-rmm.opcontrol.net",
        "tacticalrmm.com",
        "dwservice.net",
        "dwagent.net",
        "meshcentral.com",
        "meshcentral.opcontrol.net",
        "tailscale.com",
        "tailscale.io",
        "ts.net",
        "beta.tailscale.net",
        "login.tailscale.com",
        "controlplane.tailscale.com",
        // Public IP discovery.
        "icanhazip.com",
        "ipify.org",
        "api.ipify.org",
        "ipinfo.io",
        "ifconfig.me",
        "ifconfig.co",
        "whatismyipaddress.com",
        "myip.com",
        "checkip.amazonaws.com",
        "ident.me",
        // Windows Update, telemetry and connectivity checks.
        "microsoft.com",
        "windowsupdate.com",
        "update.microsoft.com",
        "delivery.mp.microsoft.com",
        "displaycatalog.mp.microsoft.com",
        "storeedgefd.dsx.mp.microsoft.com",
        "settings-win.data.microsoft.com",
        "v10.events.data.microsoft.com",
        "v20.events.data.microsoft.com",
        "watson.telemetry.microsoft.com",
        "msftconnecttest.com",
        "msftncsi.com",
        "time.windows.com",
        "time.nist.gov"
    ];

    public static bool IsBackground(string? domain)
    {
        var value = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        while (value.Length > 0)
        {
            if (Domains.Any(d => value.Equals(d, StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
                return true;
            var dot = value.IndexOf('.');
            if (dot < 0) break;
            value = value[(dot + 1)..];
        }
        return false;
    }

    public static IReadOnlyList<string> All => Domains;
}
