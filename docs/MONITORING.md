# Diagnostics / monitoring endpoint

A read-only, API-key-protected endpoint that returns everything needed to diagnose a running instance (including production) without server access: why Yahoo is (in)active, cache coverage per source, sync plans, job history, background-worker heartbeats, configuration with the provider that supplied each value, database facts and the most recent application logs.

It never contacts a provider, never queues work and never changes state.

## Switching it on and off

| Setting (`SignalLedger:Monitoring:*`) | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Feature flag. While `false` every `/api/monitoring*` route answers exactly like an unknown API route (JSON 404). |
| `ApiKey` | set in `appsettings.json` | Required key, at least 32 characters. A missing or shorter key keeps the endpoint closed (404) even when `Enabled` is `true`. |
| `LogCapacity` | `5000` | Size of the in-memory log ring buffer (100–100,000; read at startup). |

`Enabled` and `ApiKey` are read on every request. Editing `appsettings.json` / `appsettings.Production.json` on the server switches the endpoint on or off **without a restart** (ASP.NET Core reloads JSON configuration on change). Environment variables (`SignalLedger__Monitoring__Enabled=true`, `SignalLedger__Monitoring__ApiKey=...`) also work, but under IIS they need an application-pool recycle.

> **The repository is public.** The key committed in `appsettings.json` is therefore known to anyone who reads the source. Treat it as a placeholder: on a server you care about, override it with your own random key (environment variable or the server's `appsettings.Production.json`) and keep `Enabled=false` when you are not actively diagnosing.

## Authentication

Send the key in the `X-Monitoring-Key` header (or `Authorization: Bearer <key>`). The comparison is constant-time; failures return `401` and are logged with the caller's IP. All monitoring routes share a per-IP limit of 60 requests per minute and respond with `Cache-Control: no-store`. Query-string keys are deliberately not accepted, so the key never ends up in IIS/proxy access logs.

## Routes

| Route | Returns |
|---|---|
| `GET /api/monitoring?logLevel=Debug&logTake=200` | Full snapshot (see below). `logTake=0` omits log entries; max 5000. |
| `GET /api/monitoring/logs?minLevel=&category=&contains=&afterSequence=&sinceUtc=&take=` | Captured log entries. `category`/`contains` are case-insensitive substring filters. With `afterSequence` the oldest new entries are returned first (tailing); otherwise the newest `take` (default 500, max 5000). |
| `GET /api/monitoring/settings?all=false` | All settings: `marketData.effective` (what is actually in force, with `valueSources` = database/configuration), `marketData.storedOverrides` (values saved from the Data Manager in SQLite), `configured` (each section as the app binds it: general flags, Yahoo, Administration, Jobs, Monitoring — key shown only as length/usable), and `configuration.values` (raw keys under `SignalLedger`, `Logging`, `AllowedHosts`, `urls`, `Kestrel`, `ConnectionStrings` with the provider that won). `all=true` lists every configuration key, environment variables included. Secret-looking keys are redacted. |
| `GET /api/monitoring/jobs?status=Failed&symbol=QQQ&take=100` | Raw persisted market-data job rows (including lease owner, retry count, `NotBeforeUtc`, error). |

### Snapshot contents

- `findings` — ordered Error → Warning → Info, each with a stable `code`, e.g. `yahoo.blocked.read-only`, `yahoo.blocked.not-authorized`, `yahoo.disabled`, `yahoo.no-cached-data`, `yahoo.initial-download-blocked`, `yahoo.download-not-started`, `sync.last-error`, `jobs.failed`, `jobs.stuck-pending`, `jobs.retry-scheduled`, `worker.disabled|stalled|faulted|not-started`, `database.pending-migrations`, `logs.errors`.
- `application` — version, environment, IIS vs Kestrel (`APP_POOL_ID`), machine, process id/start/uptime, runtime, memory, GC, threads, content root.
- `configuration` — every value under `SignalLedger`, `Logging`, `AllowedHosts`, `urls` with the configuration provider that won (JSON file, environment variables, command line…). Keys that look like secrets are redacted.
- `marketData` — effective settings and their value sources, operator overrides stored in SQLite, and per instrument: cached bars per source (count, first/last date, unavailable sessions), Yahoo sync state and the current incremental plan (computed from SQLite only).
- `jobs` — market-data job counts and the 50 most recent jobs; experiment, backtest and import summaries.
- `workers` — heartbeats of `MarketDataJobWorker`, `MarketDataAutoSync`, `ExperimentWorker` (state, last activity, next run, last error).
- `database` — SQLite path and size (plus WAL), applied/pending migrations, row counts.
- `logs` — counts per level and recent entries.

## Log capture

Logs are always captured into a bounded in-memory ring buffer (independent of the flag, so history exists the moment you switch the endpoint on). Levels for the buffer are configured separately under `Logging:Monitoring:LogLevel` (defaults: `SignalLedger` at Debug, `System.Net.Http.HttpClient` at Information, framework noise at Warning) and reload without a restart. The buffer does not survive a process restart or app-pool recycle; persistent job history is in SQLite and appears in `jobs`.

## Example

```powershell
$h = @{ 'X-Monitoring-Key' = '<key>' }
Invoke-RestMethod https://<host>/api/monitoring -Headers $h | ConvertTo-Json -Depth 8
Invoke-RestMethod 'https://<host>/api/monitoring/logs?minLevel=Warning&take=200' -Headers $h
```

```bash
curl -s -H "X-Monitoring-Key: <key>" "https://<host>/api/monitoring/logs?category=Providers&afterSequence=1234"
```
