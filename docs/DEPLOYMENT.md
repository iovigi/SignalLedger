# Deployment

SignalLedger ships as one self-hosted ASP.NET Core (Kestrel) process. The compiled React assets and the JSON API share the same origin, so no Node server, CORS setup or separate static host is needed in production. The SQLite database lives outside the release folder and `wwwroot`.

## Build the artifact

On a machine with the .NET 10 SDK and Node.js 22+:

```powershell
dotnet tool restore
.\scripts\publish.ps1
```

The script runs `npm ci`, frontend lint/build, all backend tests, `dotnet publish`, copies the Vite bundle into the published `wwwroot`, then starts the published DLL and checks that:

- `/api/health` returns JSON with `status: healthy`;
- `/api/does-not-exist` returns a JSON 404 (not the SPA HTML);
- the deep link `/methodology` returns `index.html`.

Output is `artifacts\publish`. Use `-SkipTestsForCi` only when the same CI job has already run the full test suite.

## Run

The target machine needs the .NET 10 ASP.NET Core Runtime only.

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:SignalLedger__DataDirectory = 'C:\ProgramData\SignalLedger\data'
dotnet .\SignalLedger.Api.dll --urls http://127.0.0.1:5080
```

Production defaults (`appsettings.Production.json`):

| Setting | Default | Meaning |
|---|---|---|
| `SignalLedger:DataDirectory` | `C:\ProgramData\SignalLedger\data` | SQLite location; keep it outside the release folder |
| `SignalLedger:PublicDemoMode` | `true` | Rejects CSV import and provider sync with 403 |
| `SignalLedger:SeedSyntheticDemo` | `true` | Seeds the deterministic 2,600-session `SYNTH` series |
| `SignalLedger:Yahoo:Enabled` / `AuthorizedUseConfirmed` | `false` | Provider sync stays off until both are set after a terms review |

Override any setting with environment variables (`SignalLedger__PublicDemoMode=false`, etc.). Never put the database, logs or credentials below `wwwroot`.

For HTTPS, either configure a Kestrel certificate (`Kestrel__Endpoints__Https__Url`, `Kestrel__Endpoints__Https__Certificate__Path`) or put any reverse proxy you already use in front of the loopback port. To keep the process running, register it with your platform's service manager (Windows service, systemd, etc.).

## Migrations, backup and rollback

The application applies EF Core migrations on startup. To migrate explicitly before switching traffic:

```powershell
dotnet .\SignalLedger.Api.dll --migrate-only
```

Before upgrading, stop the process and copy `signalledger.db` to a backup location. Deploy each release to its own folder so the previous one can be restarted unchanged. To roll back a schema change, stop the process, keep the current database aside, restore the matching backup and start the previous release. Never overwrite a live SQLite file while the process is running.

## Acceptance checks

After starting a release:

- `/api/health` returns JSON with `status: healthy`.
- Refreshing `/methodology` or another client route returns the SPA.
- `/api/does-not-exist` returns HTTP 404 with `application/problem+json`.
- In public-demo mode, CSV import and provider sync return 403 and a backtest on `SYNTH` succeeds.
- After a restart, the stored run is still available with the same fingerprint and final equity.
