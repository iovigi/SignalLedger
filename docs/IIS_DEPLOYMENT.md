# IIS deployment

SignalLedger is deployed as one ASP.NET Core site: compiled React assets and `/api` share the same origin. The SQLite database lives outside the release directory and `wwwroot`.

## Prerequisites

1. Windows Server 2022/2025 or supported Windows with IIS Web Server enabled.
2. Install the .NET 10 ASP.NET Core Hosting Bundle, then restart IIS (`iisreset`). Confirm `%SystemRoot%\System32\inetsrv\aspnetcore.dll` exists.
3. Install the .NET 10 SDK and Node.js 22+ only on the build machine. The server needs the Hosting Bundle, not Node.
4. Provision an HTTPS certificate in `Cert:\LocalMachine\My` and record its thumbprint.
5. Run deployment PowerShell as Administrator.

## Build the single-site artifact

From a clean checkout on the build machine:

```powershell
dotnet tool restore
.\scripts\publish-iis.ps1
```

The official script runs `npm ci`, frontend lint/build, all backend tests, `dotnet publish`, copies the Vite bundle into the published `wwwroot`, verifies `web.config` uses ASP.NET Core Module V2, and starts the published DLL long enough to validate `/api/health`. Output is `artifacts\publish`. `-SkipTestsForCi` is permitted only after the same CI job already ran the full suite.

Copy `artifacts\publish` to a secure staging directory on the IIS server. Do not copy a database or downloaded market data with it.

Validate staging paths without changing IIS (works without elevation): `./scripts/deploy-iis.ps1 -PublishDirectory C:\staging\SignalLedger -ValidateOnly`.

## Deploy

```powershell
.\scripts\deploy-iis.ps1 `
  -PublishDirectory C:\staging\SignalLedger `
  -SiteName SignalLedger `
  -AppPoolName SignalLedger `
  -SiteRoot C:\sites\SignalLedger `
  -DataDirectory C:\ProgramData\SignalLedger\data `
  -HttpPort 8080 `
  -HostName backtests.example.com `
  -HttpsPort 443 `
  -CertificateThumbprint THUMBPRINT_WITHOUT_SPACES
```

The deploy script refuses broad paths, creates a timestamped immutable release, backs up an existing SQLite database, grants the app-pool identity read/execute on the release and modify only on the data directory, runs EF migrations explicitly with `--migrate-only`, switches the IIS site, starts it, and validates health over localhost. The application pool is **No Managed Code** (`managedRuntimeVersion=""`) with ApplicationPoolIdentity.

The script writes the selected data directory into the release's production settings before activation. Never place the database, logs, secrets, or provider credentials below the site or `wwwroot`. Yahoo sync remains disabled in Production until both configuration gates are explicitly set after a terms/authorization review; public-demo mode also rejects sync and import in the application, while an IIS/network block remains useful defense in depth.

## Acceptance checks

Run from a client with the production hostname:

```powershell
Invoke-RestMethod https://backtests.example.com/api/health
Invoke-WebRequest https://backtests.example.com/methodology
```

Expected results:

- `/api/health` is JSON with `status: healthy`.
- `/methodology` and other deep links return the SPA HTML on refresh.
- `/api/does-not-exist` returns HTTP 404 with `application/problem+json`, never `index.html`.
- In a private deployment with `PublicDemoMode=false`, import a permitted synthetic CSV. In public-demo mode, confirm import and provider sync return 403, then run a backtest against the pre-seeded `SYNTH` series. Recycle the pool and confirm the run remains.
- Confirm `C:\ProgramData\SignalLedger\data\signalledger.db` persists when a new timestamped release is deployed.
- Confirm the pool identity can modify the data directory but cannot modify release files.

The repeatable acceptance command is:

```powershell
.\scripts\test-iis.ps1 -BaseUrl https://backtests.example.com -SiteName SignalLedger -AppPoolName SignalLedger
```

For a local certificate that is not trusted yet, add `-SkipCertificateCheck`. The script creates a deterministic synthetic backtest, recycles the pool, and requires the stored fingerprint and final equity to remain identical afterward.

IIS idle timeout may pause in-process jobs. Keep refresh and experiments user-initiated; set a suitable pool idle timeout or `0` for a dedicated deployment. Pending experiment jobs are persisted and recovered after process restart.

## Backup, rollback, and migration

The deploy script copies the existing database to `C:\sites\SignalLedger\backups\<timestamp>` before migration. Retain backups according to local policy and test restores. To roll back application code, point the site's root virtual directory to the previous folder under `releases` and recycle the pool. If a schema rollback is required, stop the site, preserve the current database separately, restore the matching pre-migration backup, then start the previous release. Never overwrite a live SQLite file while the worker process is running.

## Troubleshooting

- **500.30 (startup failure):** check Event Viewer → Windows Logs → Application and temporarily enable `stdoutLogEnabled="true"` in the published `web.config`; grant the pool identity modify access to a log directory outside `wwwroot`, reproduce once, then disable stdout logging.
- **502.5 / process failure:** verify the .NET 10 Hosting Bundle, x64 runtime, app pool No Managed Code, and that `dotnet SignalLedger.Api.dll --migrate-only` succeeds with the production data directory.
- **SQLite unable to open / readonly:** inspect ACLs with `icacls C:\ProgramData\SignalLedger\data`; the exact `IIS AppPool\SignalLedger` identity needs Modify.
- **Deep route 404:** ensure the published `wwwroot\index.html` exists and the request reaches ASP.NET Core; do not add a static IIS rewrite that catches `/api`.
- **API returns HTML:** confirm `web.config` was generated by `dotnet publish` and was not replaced with an SPA rewrite configuration.
