# OpControl Parental

Ground-up rebuild of the ArktinMonitor parental control system. Job 1 established the
foundation (identity, family/child/device model, enrollment, SignalR, dashboard, Windows service
and user-session helper). Job 2 added the lock overlay, schedules, PIN overrides, bonus time and
2FA. Job 3 adds website filtering through a local DNS resolver, visited-site history, curated
categories, app inventory/review, real-time app blocking, per-item bonus unlocks and the
Websites/Apps dashboard pages.

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

Stack directory on `opcontrol-dockers`: `/opt/stacks/opcontrol-parental/`.

- `docker-compose.yml` and `docker-compose.override.yml` (host port `127.0.0.1:8096`)
- `.env` (database password, not committed)
- `data/postgres/` persistent PostgreSQL data (back up this directory)
- `data/keys/` ASP.NET Data Protection keys (back up with the database)
- `repo/` build context

Build and redeploy:

```bash
cd /opt/stacks/opcontrol-parental/repo
docker build -t opcontrol-parental-web:0.1.0-20261018 .
cd /opt/stacks/opcontrol-parental
docker compose up -d
```

Health: `GET /health`. Public URL: `https://parental.opcontrol.net` (Cloudflare Tunnel).

## Windows client

Publish:

```bash
dotnet publish src/OpControl.Parental.Client.Windows -c Release -r win-x64 -o out/client
dotnet publish src/OpControl.Parental.Client.Windows.Helper -c Release -r win-x64 -o out/helper
```

Install on a test device with `deploy/install-x1.ps1` (elevated PowerShell). Remove with
`deploy/uninstall-x1.ps1`.

The service stores the device credential in `C:\ProgramData\OpControlParental\private\device.json`
with an ACL that grants access to SYSTEM and Administrators only. The user-session helper reads
only `state.json` (Users read) and never sees the credential.

## Migration

Generate a migration:

```bash
dotnet ef migrations add <Name> --project src/OpControl.Parental.Server --startup-project src/OpControl.Parental.Server --output-dir Data/Migrations
```

The server applies pending migrations on startup.
