# Market data: provider, cache and synchronization

SignalLedger downloads historical daily prices **once**, keeps them in SQLite, and afterwards fetches only the trading sessions that are missing. Backtests read only the local cache, so they work with the provider disabled and without internet access. Licensing and redistribution rules are in [DATA_SOURCES.md](DATA_SOURCES.md).

## Architecture

```text
React (Data Manager)           ASP.NET Core process
  status / settings / jobs ──► REST API ──► MarketDataJobService ──► MarketDataJobs (SQLite)
                                                   ▲                         │ lease
                         MarketDataAutoSyncService ┘        MarketDataJobWorker (BackgroundService)
                         (checks SQLite only)                        │
                                                       MarketDataSyncService ──plan──► MarketDataSyncPlanner
                                                                     │                 (pure, no network)
                                                       IMarketDataProvider ──► YahooChartAdapter ──► Yahoo
                                                                     │          (gate checked first)
                                                       DailyBars / MarketDataSyncStates / UnavailableSessions
BacktestService ◄──────────────────────────────────── DailyBars (never calls the provider)
```

| Component | File | Responsibility |
|---|---|---|
| `IMarketDataProvider` | `Infrastructure/Providers/YahooChartAdapter.cs` | Provider abstraction (`ProviderId`, supported symbols, inclusive date-range fetch). A second provider can be added without touching sync or backtests. |
| `YahooChartAdapter` | same | Typed `HttpClient`; access gate, allowlist, range bounds, per-attempt timeout, bounded exponential retry, HTTP 429 `Retry-After`, response validation. |
| `MarketDataSettingsService` | `Providers/MarketDataSettings.cs` | Resolves effective settings and the provider gate (see [Feature flags](#feature-flags)). |
| `MarketDataSyncPlanner` | `Providers/MarketDataSyncPlanner.cs` | Pure function: cached dates + calendar → minimal list of request ranges. |
| `MarketDataSyncService` | `Providers/MarketDataSyncService.cs` | Executes a plan, validates and upserts each range in its own transaction, maintains sync metadata. |
| `MarketDataJobWorker` | `Providers/MarketDataJobs.cs` | Persistent job queue execution, leasing, progress, cancellation, retry and restart recovery. |
| `MarketDataAutoSyncService` | same | Periodic cache check that queues jobs only for instruments with missing sessions. |
| `UsEquityTradingCalendar` | `Reporting/UsEquityTradingCalendar.cs` | NYSE sessions, holidays, exceptional closures, exchange time zone. |

All Yahoo requests originate from the server. The React app never calls Yahoo and never supplies a URL: the backend builds the request from an allowlisted symbol and validated dates.

## Supported instruments

`^NDX`, `^IXIC`, `^GSPC` (non-tradable price indices) and `QQQ` (ETF). The provider never substitutes another symbol; a response whose `meta.symbol` differs from the request is rejected. Coverage is whatever the provider actually has: QQQ starts on 1999-03-10, so a 30-year request stores QQQ from that date and records that earlier dates were requested and are pre-inception.

## Storage

| Table | Key | Contents |
|---|---|---|
| `DailyBars` | unique (`Symbol`, `SessionDate`, `Source`) | OHLC, adjusted close, volume, currency, `Source` = provider id (`YahooChart`, `UserCsv`, `Synthetic`). Values from different sources are never mixed in a backtest. |
| `MarketDataSyncStates` | (`Symbol`, `Source`) | Earliest/latest cached date, earliest/latest **requested** date, last successful sync, last attempt, last error. |
| `UnavailableSessions` | unique (`Symbol`, `Source`, `SessionDate`) | Expected sessions the provider was asked for and did not return (e.g. an unscheduled closure). Ordinary syncs skip them; *Repair* retries them. |
| `MarketDataJobs` | `Id` | Persistent job queue (type, symbol, range, status, progress, counts, retries, lease, error). |
| `ApplicationSettings` | `Key` | Operator overrides made from the UI. |
| `ImportRuns` | `Id` | Audit row per sync/import run. |

Yahoo serializes prices as single-precision floats (909.92 arrives as `909.9199829101562`); the adapter decodes such values to the intended decimal.

The database lives in `SignalLedger:DataDirectory` (never under `wwwroot` or the release folder), uses WAL journaling and a 30 s busy timeout, and is migrated on startup (`--migrate-only` migrates and exits).

## Incremental synchronization

Before any request the planner reads only SQLite and computes, for one instrument:

1. **Latest completed session** – the most recent NYSE session whose daily bar has settled (18:00 America/New_York on the session date; daylight-saving aware). Today's partial bar is never stored.
2. **Initial** – if nothing was ever requested, the full window `[latest − HistoricalYears, latest]`, split into ≤ 10-year requests.
3. **Backfill** – if `HistoricalYears` was increased, only the range older than the earliest date already requested.
4. **Gaps** – expected sessions between the earliest and latest cached bar that are missing (and not marked unavailable).
5. **Tail** – expected sessions after the latest cached bar up to the latest completed session.
6. **Reconciliation** (optional) – the last *N* cached sessions, when `RecentSessionReconciliationDays` > 0.

Missing sessions are grouped into minimal contiguous ranges of consecutive trading sessions. A single missing day produces a one-day request; weekends, holidays and closures are never requested. **If nothing is missing the plan is empty and no HTTP request is made.**

Example: `^NDX` cached through Friday 2026-11-06; on Monday 2026-11-09 after 18:00 New York time the plan is one request for 2026-11-09. Before 18:00 the plan is empty.

### Writing data

Each range is written in its own transaction: bars are filtered to the requested range and to completed sessions, validated (positive prices, `Low ≤ min(Open, Close)`, `High ≥ max(Open, Close)`, matching symbol and source), de-duplicated by date and inserted. Existing bars are **not** overwritten during ordinary sync; only *Reconciliation* and *Refresh* ranges may update existing values. A failed or malformed response rolls back that range only. Earlier ranges stay committed and no valid cached bar is removed or replaced with empty data. Sync state records the last error.

Expected sessions that a successful response did not contain, and that are more than 7 days old, are recorded in `UnavailableSessions` so they are not requested again on every sync. Dates before the first returned bar of an initial/backfill request are treated as pre-inception and covered by `EarliestRequestedDate` instead.

### Operations

| Operation | Endpoint | Behaviour |
|---|---|---|
| Download historical data | `POST /api/market-data/download` | Incremental plan; on an empty cache this is the initial download. |
| Sync missing data | `POST /api/market-data/sync` | Same plan; makes zero requests when current. |
| Repair gaps | `POST /api/market-data/repair` | Gaps only, including sessions previously marked unavailable. |
| Force refresh | `POST /api/market-data/refresh` `{symbol, from, to}` | Re-downloads and overwrites exactly that range. Never automatic. |

Body for download/sync/repair: `{}` (all provider instruments), `{"symbol":"QQQ"}` or `{"symbols":["QQQ","^NDX"]}`. `?symbol=QQQ` is also accepted.

## Trading calendar

`UsEquityTradingCalendar` implements NYSE full-day closures: weekends; New Year's Day (Sunday → Monday; a Saturday holiday is **not** observed on the prior Friday); MLK Day (from 1998); Presidents' Day; Good Friday; Memorial Day; Juneteenth (from 2022); Independence Day; Labor Day; Thanksgiving; Christmas (Saturday → Friday, Sunday → Monday); and exceptional closures (2001-09-11..14, 2004-06-11, 2007-01-02, 2012-10-29/30, 2018-12-05, 2025-01-09). Early-close days still produce a daily bar. A future unscheduled closure is handled automatically by the unavailable-session mechanism; add it to `ExceptionalClosures` when known.

Verified against live Yahoo data on 2026-11-07: 30 years of `^GSPC`, `^IXIC`, `^NDX` (7,548 sessions each) and QQQ since 1999 (6,960) produced **zero** missing sessions.

## Feature flags

Configuration (`appsettings*.json` or environment variables such as `SignalLedger__Yahoo__Enabled=true`):

```json
{
  "SignalLedger": {
    "ReadOnlyMode": false,
    "Yahoo": {
      "Enabled": false,
      "AuthorizedUseConfirmed": false,
      "AutoSyncEnabled": true,
      "SyncIntervalMinutes": 60,
      "HistoricalYears": 30,
      "RecentSessionReconciliationDays": 0,
      "MaximumYears": 40,
      "RequestTimeoutSeconds": 30,
      "MaxAttempts": 3,
      "RetryBaseDelayMilliseconds": 1000,
      "MaxRetryAfterSeconds": 60
    },
    "Administration": {
      "AllowHttpSettingsChanges": false,
      "AllowHttpDownloadOperations": false
    },
    "Jobs": { "MaxConcurrency": 1, "PollIntervalSeconds": 5, "LeaseSeconds": 120, "MaxRetries": 3 }
  }
}
```

(The section names follow the project's existing `SignalLedger:` root; `SignalLedger:Yahoo` corresponds to the `MarketData:YahooFinance` example in the original specification.)

Precedence, resolved by `MarketDataSettingsService` on every request:

1. **Server restrictions win.** `ReadOnlyMode` (or `PublicDemoMode`) disables provider access and all administrative HTTP operations. `Yahoo:AuthorizedUseConfirmed=false` means Yahoo cannot be enabled by any means, including the UI.
2. **Operator overrides** saved from the UI (`ApplicationSettings` table) override configuration for `Enabled`, `AutoSyncEnabled`, `SyncIntervalMinutes`, `HistoricalYears` and `RecentSessionReconciliationDays`.
3. **Configuration/environment** supply the defaults.

Changes take effect immediately (no restart). Disabling Yahoo cancels pending and running Yahoo jobs; cached prices, CSV import and backtests are unaffected. `GET /api/settings/market-data` shows the effective values, why something is disabled, and whether each value came from `configuration` or `database`.

When Yahoo is disabled the adapter throws before creating a request, so no HTTP traffic can occur.

### Security model (no authentication)

SignalLedger has no accounts. Therefore:

- `AllowHttpSettingsChanges` and `AllowHttpDownloadOperations` are **off by default** and enforced in the backend. The React controls only mirror the server's answer.
- Enable them only on a trusted network (localhost, VPN, or behind a reverse proxy that restricts these paths). Anyone who can reach the site can otherwise start downloads that consume the deployer's provider quota and may breach provider terms.
- For public installations use `ReadOnlyMode=true`: everything stays viewable, nothing administrative is possible.
- Provider/settings endpoints are rate limited to 10 requests per minute per TCP peer address. Forwarded headers (`X-Forwarded-For`) are not trusted because no forwarded-headers middleware is configured; behind a proxy all clients share one budget. CORS is not used and is not an authorization mechanism.
- The backend never fetches user-supplied URLs; symbols are allowlisted and date ranges bounded.

## Background jobs

Jobs are rows in `MarketDataJobs` (`Pending → Running → Completed | Failed | Cancelled`) executed by `MarketDataJobWorker` inside the ASP.NET Core process. The browser can be closed at any time.

- **Duplicate prevention:** while a download/sync/repair job is active for an instrument, further requests return that job (`duplicatesOf` in the response). An identical active refresh range is also reused.
- **Leasing:** a job is claimed with an atomic conditional `UPDATE` that sets a lease (`LeaseOwner`, `LeaseExpiresUtc`), renewed every third of `LeaseSeconds`. Two processes sharing one SQLite file (e.g. an IIS overlapped recycle) cannot run the same job; only one job per instrument runs at a time.
- **Progress:** completed/total request ranges, records added/updated and provider requests are persisted after every range.
- **Cancellation:** `POST /api/market-data/jobs/{id}/cancel` cancels a pending job immediately and a running job at its next await (the in-flight HTTP request is cancelled).
- **Retries:** transient provider failures (timeouts, 408/429/5xx) are retried inside the adapter (bounded exponential backoff, `Retry-After` up to `MaxRetryAfterSeconds`). If they persist, the job returns to `Pending` with a `NotBeforeUtc` delay (the provider's `Retry-After`, otherwise 2, 4, 8 minutes), up to `Jobs:MaxRetries`. Non-transient failures fail the job without touching cached data.
- **Restart recovery:** on startup, `Running` jobs whose lease has expired are returned to `Pending`. Because every job re-plans from SQLite, a resumed download requests only ranges that are still missing; a refresh job resumes from its persisted `ResumeFrom` date.
- **Shutdown:** on graceful shutdown running jobs are cancelled and left `Pending` for the next start.

`SignalLedger:Jobs:DisableWorker=true` disables the worker and automatic sync (used by tests and maintenance windows).

### Automatic synchronization

When Yahoo and `AutoSyncEnabled` are both effective, `MarketDataAutoSyncService` wakes every `SyncIntervalMinutes`, plans each instrument from SQLite and queues a `Sync` job only where sessions are missing. **Checking the cache every hour does not mean downloading every hour**: a current cache produces no job and no request. Instruments that have never been downloaded are left for an explicit *Download historical data* action.

## Failure handling summary

| Situation | Result |
|---|---|
| Yahoo disabled / not authorized | 403 from the API; adapter refuses before any request; running jobs cancelled. |
| HTTP 429 | Honour `Retry-After` (≤ 60 s inline, otherwise job rescheduled after it). |
| Timeout / 5xx | Bounded retries, then job retry with backoff. |
| Malformed JSON, wrong symbol, invalid OHLC, array length mismatch | Range rejected and rolled back; job fails; cache intact; `LastError` recorded. |
| Provider has no data for a session | Recorded as unavailable after 7 days; skipped by sync, retried by repair. |
| Process killed mid-download | Job recovered at next start; only missing ranges requested. |
| No internet | Backtests and cached data unaffected; status shows the last error and latest cached date. |

## Backtesting

`BacktestService` and the Strategy Explorer read `DailyBars` for one symbol **and one source** only. They never contact a provider. If the evaluation window has fewer than two cached bars the request is rejected with the cached range in the message, e.g. *"Cached ^NDX/YahooChart data covers 2024-01-02 to 2024-12-31"*; missing data is never filled or substituted. The engine still uses coherent raw OHLC with next-open execution; `AdjustedClose` is stored but not used for fills, so index runs remain price-return simulations and no total-return claim is made.
