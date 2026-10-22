# Parental OpControl

[English](README.md) · [Español](README.es.md)

Self-hosted parental control software for families. It has a web dashboard for parents, a Windows client for the child's computer and a guided installer. It is a ground-up rebuild of the ArktinMonitor parental control system, available in English and Spanish.

![Family dashboard](assets/screenshots/dashboard.png)

## Features

### Lock and unlock
- Lock or unlock a device instantly from the dashboard
- Full-screen lock overlay on the child's computer that keeps working offline
- Lock schedules per child, including overnight windows such as 21:00 to 07:00
- Bonus time (+15 min, +30 min or a custom amount) with a countdown banner on the child's screen
- Parent override PIN on the lock screen, with a menu to unlock until the next lock or give bonus time

### Screen time
- Active screen time per child across all of the child's devices, with idle time excluded
- Daily device limit and per-app limits that end in the lock screen
- Day, week and month usage charts, most used apps and an activity timeline

### Websites and apps
- Website filtering through a local DNS resolver on the device, with curated categories (adult, gambling)
- Visited sites history with inline block and unblock
- Installed app inventory, a review list for new apps and real-time app blocking
- Always-allowed apps and sites, plus bonus unlocks for a single blocked site or app

### Remote viewing
- Parent-initiated screen and camera viewing, up to 10 minutes per session, no audio and no recording
- Optional on-screen notice on the child's device, configurable per device
- One-time camera supervision acknowledgement per parent, plus a separate RemoteView permission

### Alerts and email
- Alerts for tampering (service stopped, helper killed, DNS changes, clock changes, uninstall attempts) and limits
- Email alerts and a daily digest, with mute per alert type and quiet hours
- Bilingual HTML emails (invitations, alerts, digest) with a plain-text version

### Families and parents
- Multiple families on one server, with invite-only registration and no public sign-up
- Co-parents with per-parent permissions; every action records which parent made it
- Two-factor authentication (TOTP) for parent accounts
- Server administrator tools and a build version shown in the dashboard

### Windows client
- MSI installer and setup wizard; parents add a device with a one-time install link or QR code
- Automatic updates with SHA-256 verification and automatic rollback
- SYSTEM service plus a user-session helper, with tamper detection and offline enforcement from a local cache

### Interface
- English and Spanish (es-MX), selected per parent, per family and per device
- Dark and light themes, built for both desktop and phone screens

## Screenshots

| Screen time | Lock schedules |
|---|---|
| ![Screen time](assets/screenshots/screen-time.png) | ![Lock schedules](assets/screenshots/schedules.png) |

![Remote screen viewing](assets/screenshots/remote.png)

Screenshots use demo names.

## Solution

| Project | Purpose |
|---|---|
| `src/OpControl.Parental.Server` | ASP.NET Core 10, Blazor Server dashboard, API, SignalR device hub, Identity, EF Core |
| `src/OpControl.Parental.Contracts` | Platform-neutral DTOs and hub method names |
| `src/OpControl.Parental.Client.Windows` | Windows service: enrollment, credential storage, SignalR, heartbeat, offline state cache |
| `src/OpControl.Parental.Client.Windows.Helper` | User-session helper: full-screen lock window driven by the cached state |
| `src/OpControl.Parental.Setup` | Setup bootstrapper (interactive install wizard that embeds the MSI) |
| `src/OpControl.Parental.Localization` | Shared English and Spanish strings for the Windows client and setup |
| `tests/OpControl.Parental.Tests` | Model, enrollment, credential and lock-evaluation tests |

Target framework: **.NET 10 (LTS), SDK 10.0.402, runtime 10.0.13**.

## Local build and test

```bash
dotnet restore
dotnet build
dotnet test
```

## Install on your own server

You need a Linux machine with Docker Engine and the Docker Compose plugin, a
domain name and HTTPS (a reverse proxy or tunnel in front of the web port).

1. Download `docker-compose.yml`, `.env.example` and `LICENSE` from the latest
   release: <https://github.com/ziur-vela/parental-opcontrol/releases>.
2. Put them in a folder such as `/opt/stacks/opcontrol-parental`.
3. Copy `.env.example` to `.env` and fill in:
   - `POSTGRES_PASSWORD`: a long random password.
   - `UPDATER_SECRET`: run `openssl rand -hex 32`.
   - `STACK_DIR`: the absolute path of this folder.
   - `PUBLIC_BASE_URL`: the address families will use, for example
     `https://parental.example.com`.
4. Start it: `docker compose up -d`.
5. Read the first-start setup token in the log:
   `docker compose logs web | grep 'FIRST-RUN SETUP'`.
6. Open `https://your-address/setup`, enter the token, create the first server
   administrator, scan the QR code with an authenticator app, and optionally add
   SMTP settings.
7. Add a child device from **Add device** and install the Windows client.

The setup page disappears after the first administrator is created. Later
families join by invitation from Server admin. The web container runs without
the Docker socket; only the updater sidecar has it.

## Updating

Server admin → **Updates** checks GitHub for stable releases every six hours.
When a new version is available, click **Update now** (with a two-factor code if
your account uses 2FA). The updater:

1. backs up the database with `pg_dump` (keeps the last five backups),
2. pulls the signed image by digest,
3. recreates the web container,
4. waits up to three minutes for `/health` to report the new version,
5. rolls back to the previous image if the health check fails.

Child devices update from their own server through the existing client update
flow. Pre-release tags are never offered on the stable channel. Installations
that must stay offline can turn the GitHub check off on the same page.

Our own homelab deployment notes, staging stack and build host steps are in
[`docs/HOMELAB-DEPLOY.md`](docs/HOMELAB-DEPLOY.md).

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

There is no default Windows account. Interactive setup lists non-administrator accounts first
and requires the parent to pick one; silent installs must pass `ACCOUNT=`. The pre-configured
download names the setup `OpControlParental-<CODE>.exe`; the bootstrapper reads
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

`deploy/install-legacy.ps1` and `deploy/uninstall-legacy.ps1` remain for reference, but new devices should
use the MSI/setup. The legacy installer now requires `-MonitoredAccount`; there is no default account.

The service stores the device credential in `C:\ProgramData\OpControlParental\private\device.json`
with an ACL that grants access to SYSTEM and Administrators only. The user-session helper reads
only `state.json` (Users read) and never sees the credential.

## License

Parental OpControl is free for personal and other non-commercial use under the
[PolyForm Noncommercial License 1.0.0](LICENSE). You may use, change and share
it for non-commercial purposes, and you must keep the Required Notice:

`Required Notice: Copyright (c) 2026 Ziur / OpControl IT Solutions (https://opcontrol.net)`

Commercial use (selling it, offering it as a paid service, or using it inside a
business) needs written permission from OpControl IT Solutions. Contact
`hendry.ruiz@opcontrol.net`.

The project may move to an open-source license (AGPL-3.0) if it gets
contributors. By sending a contribution you agree that it may be distributed
under this license and under any future license chosen by the copyright holder,
including AGPL-3.0. See [CONTRIBUTING.md](CONTRIBUTING.md).

Third-party components, fonts and data sources are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Migration

Generate a migration:

```bash
dotnet ef migrations add <Name> --project src/OpControl.Parental.Server --startup-project src/OpControl.Parental.Server --output-dir Data/Migrations
```

The server applies pending migrations on startup.
