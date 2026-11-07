# IIS deployment (Windows Server)

SignalLedger can run in-process under IIS. The same published folder also runs as a standalone Kestrel process ([DEPLOYMENT.md](DEPLOYMENT.md)). This guide prepares an IIS host; nothing in the repository deploys automatically.

```text
IIS
 └─ Application pool "SignalLedger" (No Managed Code, AlwaysRunning, idle time-out 0)
     └─ ASP.NET Core Module V2, in-process, .NET 10
         ├─ REST API (/api/*) and /health
         ├─ React static files (wwwroot) with SPA fallback
         ├─ Yahoo provider (server-side only), market-data cache and job worker
         ├─ Backtesting engine
         └─ SQLite database (outside the site folder)
```

No Node.js process is used in production.

## Prerequisites

1. Windows Server 2019/2022/2025 with IIS (`Web-Server`, `Web-Mgmt-Console`, `Web-Scripting-Tools`).
2. **Application Initialization**: `Install-WindowsFeature Web-AppInit`.
3. **.NET 10 ASP.NET Core Hosting Bundle** (installs `AspNetCoreModuleV2` and the runtime). Run `iisreset` or restart IIS after installing it.
4. The `WebAdministration` PowerShell module (part of the IIS management scripts).

## Recommended layout

```text
C:\SignalLedger\
    app\        published release (replaced on each deployment)
    data\       signalledger.db, -wal, -shm (persistent; never inside app\ or wwwroot)
    logs\       optional stdout/diagnostic logs
```

Create the folders as part of your deployment; `deploy-iis.ps1` can create them and set permissions.

## Build the artifact

On a build machine (.NET 10 SDK, Node.js 22+):

```powershell
dotnet tool restore
.\scripts\publish.ps1
```

`artifacts\publish` then contains `SignalLedger.Api.dll`, `wwwroot\` (React build) and `web.config`. The script validates that `web.config` uses `AspNetCoreModuleV2`, `hostingModel="inprocess"`, `dotnet .\SignalLedger.Api.dll` and disabled stdout logging, that no database file is published, that `scripts\deploy-iis.ps1` parses, and it smoke-tests the published DLL (`/health`, `/api/health`, JSON 404 for unknown `/api` routes, SPA deep link).

## web.config

`src/SignalLedger.Api/web.config` is published unchanged:

- handler `aspNetCore` → `AspNetCoreModuleV2`, `processPath="dotnet"`, `arguments=".\SignalLedger.Api.dll"` (framework-dependent). For a self-contained publish change `processPath` to `.\SignalLedger.Api.exe` and remove `arguments`.
- `hostingModel="inprocess"`, `stdoutLogEnabled="false"` (enable temporarily for start-up troubleshooting; logs go to `.\logs\stdout*`).
- `ASPNETCORE_ENVIRONMENT=Production`, request size limit matching the 5 MB CSV upload, `X-Powered-By` removed, `nosniff`.

Application Initialization is **not** in `web.config`; the deployment script writes it to `applicationHost.config`, so the file stays valid on servers without the feature.

## Configure IIS with the script

`scripts/deploy-iis.ps1` is idempotent and supports `-WhatIf`. Run it elevated on the target server. Preview the changes first:

```powershell
.\scripts\deploy-iis.ps1 -SiteName SignalLedger -PhysicalPath C:\SignalLedger\app `
    -DataDirectory C:\SignalLedger\data -LogDirectory C:\SignalLedger\logs `
    -SourcePath .\artifacts\publish -HostName signalledger.example.org -Port 80 `
    -DisablePeriodicRecycle `
    -EnvironmentVariables @{ 'SignalLedger__PublicDemoMode' = 'false'; 'SignalLedger__ReadOnlyMode' = 'true' } `
    -WhatIf
```

Remove `-WhatIf` to apply. Use `-ApplicationAlias signalledger` to install under an existing site instead of creating one.

What it configures:

| Item | Value |
|---|---|
| Application pool | `SignalLedger` (configurable), .NET CLR version **No Managed Code**, Integrated pipeline |
| Start mode | `AlwaysRunning` |
| Idle time-out | `00:00:00` (disabled) |
| Auto start | pool `autoStart=true`, site `serverAutoStart=true` |
| Overlapped recycle | disabled (`disallowOverlappingRotation=true`, `maxProcesses=1`) so only one process owns the SQLite file and job queue |
| Periodic recycle | unchanged by default; `-DisablePeriodicRecycle` sets it to 0 |
| Identity | `ApplicationPoolIdentity` (or `NetworkService`/`LocalService`) |
| Preload | `preloadEnabled=true` on the site/application |
| Application Initialization | `doAppInitAfterRestart=true`, warm-up page `/health` |
| Environment | `ASPNETCORE_ENVIRONMENT`, `SignalLedger__DataDirectory`, plus `-EnvironmentVariables` (stored on the pool, not in the release folder) |
| Permissions | pool identity: read/execute on the app folder; **modify** on data and log folders |
| Release copy (optional) | `-SourcePath`: writes `app_offline.htm`, mirrors the release with robocopy (keeps `logs`), removes `app_offline.htm` |

The script refuses a data directory inside the application folder, does not install features or the Hosting Bundle, and does not start or stop anything other than what IIS does when settings change.

## Application settings

Set settings as application-pool environment variables (`-EnvironmentVariables`) or in `appsettings.Production.json`. The production defaults are deliberately locked down:

| Variable | Production default | Notes |
|---|---|---|
| `SignalLedger__DataDirectory` | `C:\ProgramData\SignalLedger\data` (script sets your `-DataDirectory`) | Absolute path outside the site. |
| `SignalLedger__PublicDemoMode` | `true` | Seeds `SYNTH`, read-only. Set `false` for a real installation. |
| `SignalLedger__ReadOnlyMode` | `false` | `true` for public sites: no imports, provider access or admin operations. |
| `SignalLedger__Yahoo__AuthorizedUseConfirmed` | `false` | Deployer attestation after reviewing Yahoo's terms. Without it Yahoo cannot be enabled. |
| `SignalLedger__Yahoo__Enabled` / `__AutoSyncEnabled` | `false` / `true` | Defaults; can be overridden from the UI if settings changes are allowed. |
| `SignalLedger__Administration__AllowHttpSettingsChanges` | `false` | Allow the UI to change provider settings. **No authentication exists**; enable only on trusted networks. |
| `SignalLedger__Administration__AllowHttpDownloadOperations` | `false` | Allow download/sync/repair/refresh/cancel over HTTP. Same caveat. |

See [MARKET_DATA.md](MARKET_DATA.md#security-model-no-authentication) for the security model.

## SQLite under IIS

- Use a local NTFS path (not a network share). The pool identity needs **Modify** on the directory because SQLite creates `-wal`/`-shm` and journal files next to the database.
- WAL mode is enabled at start-up; the API reads while the worker writes.
- Migrations run on start-up. To migrate explicitly before switching traffic: `dotnet C:\SignalLedger\app\SignalLedger.Api.dll --migrate-only` (run with the same environment variables).
- A web farm or several pools must **not** share one database. Job leases prevent duplicate job execution, but SQLite is intended for a single instance.

### Backup and restore

1. Stop the pool (`Stop-WebAppPool SignalLedger`) or place `app_offline.htm` in the app folder.
2. Copy `signalledger.db` **and** any `signalledger.db-wal`/`-shm` files together (or run `sqlite3 signalledger.db ".backup backup.db"` while running).
3. To restore, stop the pool, replace the files, start the pool.

Cached prices, settings and job records live only in this database; the release folder can be replaced freely.

## IIS lifecycle limitations

`AlwaysRunning` + preload + Application Initialization start the app when the pool starts and keep it running between requests. They do **not** guarantee uninterrupted execution: configuration changes, Hosting Bundle updates, pool recycles (periodic, memory limits, `web.config` edits), deployments and server restarts still stop the process. The job worker handles this by design: unfinished jobs stay in SQLite and resume after the next start, re-planning from the cache so only missing ranges are downloaded. No separate Windows Service is needed.

## Verification after deployment (manual)

- `GET /health` → `200 {"status":"healthy"}` (also used by Application Initialization; it never touches the provider).
- `GET /api/health` → JSON with version and environment.
- `GET /api/does-not-exist` → `404 application/problem+json`; a deep link such as `/methodology` → the SPA.
- `GET /api/settings/market-data` → expected effective flags and lock reasons.
- Recycle the pool and confirm `/health` responds without a first request delay (preload) and that `Pending` jobs resume.

## Troubleshooting

| Symptom | Check |
|---|---|
| 500.19 | Hosting Bundle missing or IIS not restarted after installing it. |
| 500.30 / 500.31 | Start-up failure: enable `stdoutLogEnabled` temporarily, check Event Viewer (`IIS AspNetCore Module V2`), verify the .NET 10 runtime. |
| `SQLite Error 8: attempt to write a readonly database` / `unable to open database file` | Pool identity lacks Modify on the data directory, or the path points into the release folder. |
| `database is locked` | Another process uses the file (second pool, overlapped recycle, backup tool). Keep `disallowOverlappingRotation=true`. |
| Jobs never run | `SignalLedger:Jobs:DisableWorker`, Yahoo disabled/not authorized, read-only mode. See `/api/market-data/status`. |
| App starts only on first request | Application Initialization feature missing, `preloadEnabled` false, or pool not `AlwaysRunning`. |
| Provider 429 | Yahoo rate limit: jobs reschedule automatically after `Retry-After`. Reduce manual syncs. |
