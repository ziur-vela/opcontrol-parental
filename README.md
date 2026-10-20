# OpControl Parental

Ground-up rebuild of the ArktinMonitor parental control system. Job 1 established the
foundation (identity, family/child/device model, enrollment, SignalR, dashboard, Windows service
and user-session helper). Job 2 added the lock overlay, schedules, PIN overrides, bonus time and
2FA. Job 3 adds website filtering through a local DNS resolver, visited-site history, curated
categories, app inventory/review, real-time app blocking, per-item bonus unlocks and the
Websites/Apps dashboard pages. Job 4 adds active screen-time and foreground-app tracking,
website time (approximate), daily device and per-app limits, always-allowed apps, reports,
the activity timeline and alerts with optional email delivery. Job 5A adds the WiX MSI and
interactive setup bootstrapper, the Add device install-link/QR flow, automatic client updates with
SHA-256 verification and rollback, helper authentication, usage sanity checks, unaccounted
helper-down time and additional tamper alerts. Job 5B adds co-parent roles and permissions,
multi-family tenancy with invite-only registration, server administrator tools, per-device time
zones, alert controls (mute/quiet hours/digest/retry), swappable branding and build-version
stamping. Job 7 adds parent-initiated remote screen and camera viewing over the existing
WebSocket relay, with a per-parent one-time camera supervision acknowledgement and the
`RemoteView` permission.

## Solution

| Project | Purpose |
|---|---|
| `src/OpControl.Parental.Server` | ASP.NET Core 10, Blazor Server dashboard, API, SignalR device hub, Identity, EF Core |
| `src/OpControl.Parental.Contracts` | Platform-neutral DTOs and hub method names |
| `src/OpControl.Parental.Client.Windows` | Windows service: enrollment, credential storage, SignalR, heartbeat, offline state cache |
| `src/OpControl.Parental.Client.Windows.Helper` | User-session helper: full-screen lock window driven by the cached state |
| `tests/OpControl.Parental.Tests` | Model, enrollment, credential and lock-evaluation tests |

Target framework: **.NET 10 (LTS), SDK 10.0.402, runtime 10.0.13**.

## Local build and test

```bash
dotnet restore
dotnet build
dotnet test
```

## Server deployment

Stack directory on the Docker host: `/opt/stacks/opcontrol-parental/`.

- `docker-compose.yml` and `docker-compose.override.yml` (host port `127.0.0.1:8096`)
- `.env` (database password, not committed)
- `data/postgres/` persistent PostgreSQL data (back up this directory)
- `data/keys/` ASP.NET Data Protection keys (back up with the database)
- `repo/` build context

Build and redeploy (the script stamps the version, commit and build date automatically):

```bash
./deploy/build-image.sh opcontrol-parental-web
cd /opt/stacks/opcontrol-parental
docker compose up -d
```

The image tag is `<version>-<YYYYMMDD>`. `GET /version` and `GET /health` return the same stamped
version string; the dashboard sidebar and Settings show it too. A build from an untagged commit
shows a `git describe` value such as `v0.6.0-3-g1a2b3c4`, so it is never mistaken for a release.

Fonts (IBM Plex Mono and Manrope) are self-hosted in `wwwroot/fonts` and embedded in the
Windows helper/setup; no Google Fonts runtime dependency. Licences are next to the fonts.

## Remote screen and camera

Parents with the **RemoteView** permission (owner by default) get a **Remote** page. Screen
view is silent on the child device; camera access is indicated by the device's hardware
camera light. Camera viewing requires a one-time camera supervision acknowledgement per
parent; acceptance is stored in `CameraConsents` with version, time, IP and user agent, and
a checkmark appears on the Remote page after acceptance. Sessions are parent-initiated,
last at most 10 minutes and can be stopped from the dashboard. No audio and no recording.

Server administrator and SMTP (no dashboard login needed):

```bash
docker exec parental-web dotnet OpControl.Parental.Server.dll --set-server-admin owner@example.com
# SMTP JSON on stdin; password is protected with the Data Protection keys
printf '%s' "$SMTP_JSON" | docker exec -i parental-web \
  dotnet OpControl.Parental.Server.dll --set-server-smtp-stdin
```

Only the first run creates a family (`/setup`); afterwards new families join by invitation from
the server administrator and there is no public sign-up.

Health: `GET /health`. Public URL: `https://parental.opcontrol.net` (Cloudflare Tunnel).

## Windows build host

Windows client builds run on a dedicated Windows build host, never on a monitored child device.
Everything is kept under `C:\BuildTools\opcontrol-parental`:

```powershell
# one-time setup (already done)
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile C:\BuildTools\opcontrol-parental\dotnet-install.ps1
& C:\BuildTools\opcontrol-parental\dotnet-install.ps1 -Version 10.0.402 -InstallDir C:\BuildTools\opcontrol-parental\dotnet -NoPath
& C:\BuildTools\opcontrol-parental\dotnet\dotnet.exe tool install wix --tool-path C:\BuildTools\opcontrol-parental\tools --version 5.0.2
& C:\BuildTools\opcontrol-parental\tools\wix.exe extension add -g WixToolset.Util.wixext/5.0.2
```

Build the client, helper, MSI and setup bootstrapper in one step:

```powershell
powershell -ExecutionPolicy Bypass -File C:\BuildTools\opcontrol-parental\repo\installer\build-windows.ps1 -Version 0.7.0
```

Artifacts land in `artifacts\OpControlParental-<version>.msi` and
`artifacts\OpControlParental-Setup-<version>.exe`. Do **not** build on a monitored child device.

## Windows client

Publish (Linux payload publish only; WiX still needs Windows):

```bash
dotnet publish src/OpControl.Parental.Client.Windows -c Release -r win-x64 -o out/client
dotnet publish src/OpControl.Parental.Client.Windows.Helper -c Release -r win-x64 -o out/helper
```

### Installer, silent install and auto-update

The Windows installer is a WiX MSI plus a small WinForms setup bootstrapper. Build commands:

```bash
# 1. Publish the Windows payloads on any OS with the .NET 10 SDK
dotnet publish src/OpControl.Parental.Client.Windows -c Release -r win-x64 -o artifacts/publish-0.5.0/client -p:Version=0.5.0
dotnet publish src/OpControl.Parental.Client.Windows.Helper -c Release -r win-x64 -o artifacts/publish-0.5.0/helper -p:Version=0.5.0

# 2. Build the MSI on Windows (WiX 5 is Windows-only)
dotnet tool install wix --tool-path C:\ocp-build\tools --version 5.0.2
powershell -ExecutionPolicy Bypass -File installer/build-msi-windows.ps1 `
  -Version 0.5.0 -PublishRoot artifacts/publish-0.5.0 `
  -OutFile artifacts/OpControlParental-0.5.0.msi -InstallerDir installer

# 3. Embed the MSI in the setup bootstrapper
dotnet publish src/OpControl.Parental.Setup -c Release -r win-x64 -o artifacts/setup-0.5.0 `
  -p:Version=0.5.0 -p:MsiPath="$PWD/artifacts/OpControlParental-0.5.0.msi"
cp artifacts/setup-0.5.0/OpControl.Parental.Setup.exe artifacts/OpControlParental-Setup-0.5.0.exe
```

`installer/build-installer.sh` publishes the payloads and documents this sequence, but WiX itself
must run on Windows. The setup bootstrapper is interactive; the MSI supports silent install:

```bat
msiexec /i OpControlParental.msi /qn SERVER=https://parental.opcontrol.net CODE=XXXXXXXXXX ACCOUNT=<child-windows-account>
OpControlParental-Setup-0.5.0.exe /S SERVER=https://parental.opcontrol.net CODE=XXXXXXXXXX ACCOUNT=<child-windows-account>
```

The pre-configured download names the setup `OpControlParental-<CODE>.exe`; the bootstrapper reads
the one-time code from its own file name and pre-fills the dialog. The MSI writes `SERVER`, `CODE`
and `ACCOUNT` to `HKLM\Software\OpControl\Parental` on first install; the service consumes and
deletes those values on first start. Upgrades keep `C:\ProgramData\OpControlParental` (device
credential, cached state), so no re-enrollment is required.

The server serves auto-update files from `data/releases` (mounted at
`/var/lib/opcontrol-parental/releases`). Place `OpControlParental-<version>.msi` and
`OpControlParental-Setup-<version>.exe` there. The highest version with an MSI is current;
the server publishes its SHA-256. A device checks daily and whenever the dashboard requests an
update, downloads the MSI, verifies SHA-256, runs the update runner, and reports healthy. If the
new service does not write the health marker within five minutes, the runner uninstalls it with
`SUPPRESSDATA=1` and installs the previous version (keeping the credential). The dashboard shows
the client version per device and an Update now button.

Signing is optional. Set a certificate and run:

```powershell
powershell -ExecutionPolicy Bypass -File installer/sign.ps1 -CertificateThumbprint <thumbprint>
```

This signs and verifies the MSI and setup EXE with SHA-256 and a timestamp.

### Install links (Add device)

Parents create a single-use install link in Add device. The dashboard shows the link, QR code,
one-time code and copy/email buttons. The link is valid for 24 hours by default
(`FamilySettings.InstallLinkMinutes`, configurable in Settings), can be revoked, and flips to
"Connected" when the device enrolls. The public `/i/<token>` page detects phones (Android coming
soon) and offers the pre-configured setup on Windows. A manual code entry fallback remains in the
setup dialog and in the Downloads page instructions.

### Manual scripted install (legacy)

`deploy/install-x1.ps1` and `deploy/uninstall-x1.ps1` remain for reference, but new devices should
use the MSI/setup.

The service stores the device credential in `C:\ProgramData\OpControlParental\private\device.json`
with an ACL that grants access to SYSTEM and Administrators only. The user-session helper reads
only `state.json` (Users read) and never sees the credential.

## Migration

Generate a migration:

```bash
dotnet ef migrations add <Name> --project src/OpControl.Parental.Server --startup-project src/OpControl.Parental.Server --output-dir Data/Migrations
```

The server applies pending migrations on startup.
