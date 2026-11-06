# SignalLedger — Open-Source Market Backtesting Lab

> **Status:** Planning · **Version:** 1.0 · **Updated:** 2026-11-07  
> **License:** MIT (application source only; not a license for market data)  
> **Hosting target:** Windows + IIS · **Authentication:** None  
> **Primary language:** English for repository, API, UI, tests and documentation.

## 0. Mission

Build an attractive, reproducible portfolio-grade web application that imports historical market data, caches it locally and compares **Buy & Hold** with rule-based strategies such as **30-trading-day SMA market timing**, including transaction costs, execution delays, out-of-market interest and risk metrics. Users can change symbols, dates, parameters and costs; explore strategy robustness; and audit every trade. The application must run locally and be deployable as **one ASP.NET Core website on IIS**. No accounts, login, subscription, paid dependencies or broker connectivity.

**Working name:** `SignalLedger` (verify GitHub repository/name availability before publishing). Suggested repo: `signal-ledger`. Suggested tagline: *Backtests you can inspect, not just admire.*

### Non-negotiable definitions

- **Yahoo is not a historical Bid/Ask feed.** Use daily OHLC; label `^NDX` as a *non-tradable price index / theoretical proxy*.
- Yahoo's chart endpoint is **unofficial/undocumented**. Automated collection may be restricted by Yahoo's terms. **Before enabling automated ingestion, review current provider terms and obtain permission or switch to a licensed/authorized provider.** Keep the Yahoo adapter optional and isolated; implement CSV import so the application works without it. Do not commit, bundle, publish or redistribute downloaded Yahoo prices in the repository, releases or demo.
- A NASDAQ-100 index backtest is **not equivalent to tradeable returns**. Add QQQ (available from 1999) as a tradeable ETF *proxy* with adjusted-price/dividend handling clearly documented; do not claim exact executable fills for index values or a fee-free dividend-inclusive backtest.
- The social-media claim about 1926–2026 uses CRSP / broad US market total return, **not NASDAQ-100**. We are testing a related hypothesis, not reproducing its numeric results.
- **No look-ahead bias:** a signal based on today's close may execute **no earlier than the next trading session's open**. Keep initial-position logic explicit.
- **No authentication** means every visitor can invoke every exposed operation. A public demo must have rate limiting, bounds on CPU/data downloads, and preferably admin/import endpoints disabled publicly or restricted at IIS/network layer (without adding application auth).

## 1. Product scope

### MVP

1. Data Manager: choose symbol (`^NDX`, `^IXIC`, `^GSPC`, `QQQ`), import CSV or refresh via an approved Yahoo adapter, inspect coverage and data quality, export owned/user-supplied data where permitted.
2. Persistent normalized daily price cache, incremental sync, audit metadata, clear errors, offline backtesting.
3. Backtesting: Buy & Hold, SMA cross-price rule, optional EMA; cash position when out of market; configurable dates, initial capital, per-side transaction fees and slippage.
4. Backtesting results: equity curves, drawdown, price+indicator with markers, annual returns, comparison table, auditable transaction journal.
5. Strategy Explorer: SMA lengths × transaction costs, rolling start dates, basic training/test split; all comparisons use identical assumptions.
6. Single-site IIS deployment with React assets served by ASP.NET Core and `/api` hosted on same origin.
7. Tests, CI, sample **synthetic** dataset, screenshots, README, reproducibility and portfolio polish.

### Deferred (not MVP)

Futures continuous contracts/rollovers, intraday ticks or Bid/Ask, live trading, automated orders, portfolio rebalancing, short selling, leverage, tax calculations, multi-user workspaces, optimized strategies with machine learning, dividends from unverified datasets, cloud jobs, high-frequency backtesting.

## 2. Technical architecture

- **Backend:** .NET 10, ASP.NET Core Web API, C#, nullable enabled, `Microsoft.NET.Sdk.Web`.
- **Frontend:** React + TypeScript + Vite, Tailwind CSS, lightweight accessible components.
- **Charts:** TradingView Lightweight Charts for candles/indicators and Recharts (or one lightweight chart library) for equity/drawdown; no paid charting dependency.
- **Storage:** SQLite + EF Core migrations. SQLite database **outside `wwwroot` and outside publicly served deployment files**.
- **Jobs:** bounded background sync/experiment queue in-process for MVP, status persisted; cancellation, hard execution limits, restart-safe behavior. Do not depend on an always-running IIS process for scheduled jobs; manual on-demand refresh is sufficient for MVP.
- **Deployment:** Vite output copied to API `wwwroot` at publish time. ASP.NET Core serves SPA assets, fallback to `/index.html` for non-API client routes, and JSON under `/api`.
- **Time and precision:** exchange trading calendar (`America/New_York`), date-only bars; decimal for currency/fees, double for indicators/return statistics; explicitly define rounding rules.
- **Observability:** structured logs, health endpoint, clear provider responses and correlation IDs; logs must not contain secrets.

### Suggested repository structure

```text
signal-ledger/
  README.md
  LICENSE
  CONTRIBUTING.md
  CODE_OF_CONDUCT.md
  .github/workflows/ci.yml
  docs/
    IMPLEMENTATION_PLAN.md     # THIS DOCUMENT; single source of truth for progress
    DATA_SOURCES.md
    BACKTEST_ASSUMPTIONS.md
    IIS_DEPLOYMENT.md
    ARCHITECTURE.md
    SCREENSHOTS/
  src/
    SignalLedger.Api/          # ASP.NET Core entry point, controllers/endpoints, static SPA
    SignalLedger.Domain/       # models, strategies, returns, portfolio simulation
    SignalLedger.Infrastructure/ # EF Core, price data adapters, CSV, background jobs
    signal-ledger-web/         # React/Vite/TypeScript
  tests/
    SignalLedger.Domain.Tests/
    SignalLedger.Api.IntegrationTests/
    SignalLedger.Infrastructure.Tests/
    SignalLedger.E2E/         # Playwright
  samples/
    synthetic-prices.csv
  scripts/
    publish-iis.ps1
    deploy-iis.ps1
```

### Data flow

```text
CSV import / approved Yahoo Chart adapter
            |
       Validate & normalize
            |
   SQLite DailyBars + ImportRuns
            |
   Immutable in-memory price snapshot
            |
      Strategy signals (t close)
            |
      Execution (t+1 open)
            |
       Portfolio simulation
            |
 Equity + trades + metrics + warnings
            |
    JSON API -> React charts
```

## 3. Data contracts and storage

**DailyBar**: `Symbol`, `SessionDate`, `Open`, `High`, `Low`, `Close`, `AdjustedClose?`, `Volume?`, `Currency`, `Source`, `UpdatedAtUtc`, `IsFinal`. Unique `(Symbol, SessionDate, Source)`. Reject non-positive OHLC, `High < max(Open,Close,Low)`, `Low > min(Open,Close,High)`, duplicate/conflicting bars; retain data-quality warnings rather than silently fabricating missing dates.

**Instrument**: `Symbol`, `DisplayName`, `AssetClass` (`Index`, `ETF`), `Currency`, `ExchangeTimeZone`, `PriceSeriesMeaning` (`Unadjusted`, `Adjusted`, `TotalReturn`), source availability and first/last dates.

**ImportRun**: id, source, symbol, requested/actual ranges, records added/updated/rejected, started/completed, warning/error, provider metadata; store no credentials.

**CashRate**: date, annualized rate, convention, source and `AvailableAt` (to prevent forward-looking rates). Interest accrual should be based on a documented, consistent day-count rule and availability lag. MVP defaults to **0% cash yield** until the historical series has been validated; optional T-bill mode comes after correct yield conversion.

**BacktestRun**: id, version of engine, source + data fingerprint/hash, strategy name/parameters, dates, execution settings, results, status, timestamp. **Do not store enormous chart JSON blobs in a single DB column**; normalized summary plus compact time series or compute on demand is preferred. Results must be reproducible with the same data snapshot and engine version.

**Trade**: signal date, execution date, side, fill reference, configured slippage, effective price, units, notional, cost, cash/equity after execution, reason. Persist or return trades without ambiguity about count: `transactions` = single buys/sells; `roundTrips` = completed entry/exit pairs.

Use EF Core migration-based schema creation, WAL mode where appropriate, DB busy timeout, transactional upserts, and a single bounded DB writer for sync tasks. Keep index/cached data separate from code and publish directory.

## 4. Strategy semantics — exact implementation rules

### Shared settings

- `symbol`, `from`, `to`, `initialCapital`, `executionMode=NextOpen`, `commissionPercentPerSide`, `slippageBpsPerSide`, `cashYieldMode=None|HistoricalTBill`, `signalPriceMode=RawClose`.
- UI defaults: 30 years when available, `$1,000`, SMA `30`, fee `0%` with visible preset `0.2%`, slippage `0 bps` (state that 0 is optimistic), cash yield `0%` until a verified historical series is loaded.
- Filter requested evaluation window while loading *warm-up bars before it* for MA. **Warm-up bars cannot create trades before evaluation start.** If no warm-up, show shortened usable window.
- No fractional position constraint in simulation unless explicitly configured; long-only, all-in/all-out, no leverage, no shorts. Cash cannot become negative from fees. Use consistent same-day ordering: begin with opening positions, fill pending orders at open, accrue/mark as defined, generate closing signal.
- The last available bar values mark portfolio equity. **Do not fabricate liquidation** at the last close unless `liquidateAtEnd=true`; show open-position mark-to-market vs realized liquidation separately.
- Missing sessions are not weekends automatically. Respect trading calendar; reject/flag missing bars within expected market sessions.

### Buy & Hold

- Enter at the first executable open **within the evaluation window**; assess closing-price equity each trading session; no exit unless explicit final liquidation.
- Same fee/slippage model as other strategies. Display invested time and benchmark return from the first common executable day; make all comparison start dates identical.

### SMA(close,N)

- `SMA_N(t) = sum(Close[t-N+1..t])/N` using N **trading sessions**, not calendar days or monthly points.
- After session `t` close: if `Close[t] > SMA_N(t)`, desired state = invested; if `<`, desired state = cash; if `=`, retain prior desired state.
- If desired state differs from current/pending state, queue exactly one action at **next available session's open**. Do not signal and execute on the same close. Avoid duplicate consecutive actions and overlapping orders.
- Initial state = cash until first valid signal; make this visible. Strategy's evaluation starts on a common start date with Buy & Hold and warm-up observations are excluded from performance.
- EMA is optional but must explicitly define initialization and alpha `2/(N+1)`.

### Transaction model

- Per-side commission = `abs(tradeNotional) * (feePct / 100)`.
- Slippage = `referenceOpen * slippageBps / 10000`, added for buys and subtracted for sells. For an index this is **hypothetical execution**.
- Buy quantity must be solved net of fees so total cash spent <= cash available. Sell proceeds net of fees; keep all formulas and rounding documented and tested.
- Historical cash return, when implemented, accrues only while holding cash; no retroactive knowledge of future T-bill rates.
- Never present `Open` and `Close` as historical Bid/Ask.

### Adjusted price warning

An ETF's adjusted close is not automatically compatible with raw open fills. Do **not** mix adjusted close signals/total-return valuation with raw open execution in a way that introduces splits/dividend discontinuities. MVP uses raw OHLC, labels benchmark **price return excluding distributions**, and separately documents any fully validated total-return mode added later. If adjusting, apply a coherent adjustment factor to the full OHLC series and document assumptions; dividend reinvestment is separate.

## 5. Analytics definitions

For both strategy and benchmark provide: final equity, net total return, CAGR with actual elapsed years, max peak-to-trough drawdown, annual returns, transaction count, round trips, total commission, estimated slippage cost, trading days in market, percent time invested, worst calendar year, volatility and Sharpe (show risk-free-rate methodology and insufficient-data warnings). Equity charts must use **common timestamps** and a shared comparison baseline. Performance percentages must remain distinct from absolute-dollar differences; fees included in the balance.

Also expose `underperformance / outperformance vs B&H` under the exact same source, time span, fees and execution conventions. Always show a visible **price-index proxy / non-tradable** warning for `^NDX` and other indices.

## 6. Web UX

**Navigation:** Dashboard, Data Manager, Strategy Explorer, Runs/Trade Journal, About/Methodology.

**Dashboard**
- Symbol chooser; date range with presets 5y/10y/20y/30y and custom dates; initial capital.
- Strategy select (Buy & Hold, SMA, EMA); MA period slider + numeric input; fee/slippage and cash yield toggles; execution mode fixed Next Open for MVP.
- Run button with progress/error state; cards for final equity, CAGR, max drawdown, trades and invested time; B&H side-by-side.
- Equity and drawdown overlay, price/MA+buy/sell markers, annual returns chart, strategy assumptions and data warnings.

**Data Manager**
- Per-symbol source, first/last day, missing sessions, updated timestamp, import/sync status; CSV upload; provider licensing notice.
- `Sync` visible only when adapter is enabled and provider usage authorized by deployer; otherwise instruct use of CSV. Import validation preview and downloadable error list.

**Strategy Explorer**
- SMA lengths `[5,10,20,30,50,100,150,200]` × fees `[0,0.05,0.1,0.2,0.5]%` by default; heatmap of net outperformance, clickable cell opens its full run.
- Multiple start dates/rolling windows; fixed holdout window; clearly label in-sample/out-of-sample and avoid best-parameter claims based exclusively on training data.

**Portfolio presentation**
- Professional dark/light responsive theme, typography and legible legends, keyboard-accessible forms, loading/empty/error states.
- About page describing signal timing, risks, price vs total returns, source restrictions, and not-financial-advice disclaimer.
- Provide sample synthetic demo so a public portfolio deployment works without copyrighted data or external API access.

## 7. Suggested REST endpoints

| Verb | Route | Notes |
|---|---|---|
| GET | `/api/health` | Version, health, DB availability (no secrets) |
| GET | `/api/instruments` | Instruments & coverage |
| GET | `/api/market-data/{symbol}` | Paged or downsampled time series; bounded dates |
| POST | `/api/imports/csv` | CSV upload; strict max size, type validation, safe names |
| POST | `/api/market-data/sync` | Optional approved provider; job id; disabled publicly by default |
| GET | `/api/jobs/{id}` | Progress/errors/cancellation status |
| POST | `/api/backtests` | Validated `BacktestRequest`; bounded lookback/points |
| GET | `/api/backtests/{id}` | Run result + data fingerprint |
| GET | `/api/backtests/{id}/trades` | Pageable transaction journal |
| POST | `/api/experiments` | Bounded matrix, asynchronous job |
| GET | `/api/experiments/{id}` | Heatmap and per-run stats |
| GET | `/api/exports/{id}` | Export simulation results (not unauthorized market-data redistribution) |

Use consistent RFC 7807 Problem Details errors. Strictly validate symbol from configured allowlist, range, capital, MA period, fees and maximum experiment count. Add timeout/cancellation on all network IO and heavy jobs. Explicit API versioning can be deferred.

### Example request

```json
{
  "symbol": "^NDX",
  "startDate": "1996-01-01",
  "endDate": "2026-10-31",
  "initialCapital": 1000,
  "strategy": { "type": "SMA", "period": 30 },
  "execution": {
    "mode": "NextOpen",
    "commissionPercentPerSide": 0.2,
    "slippageBpsPerSide": 0,
    "cashYieldMode": "None"
  }
}
```

## 8. IIS deployment contract (required, not optional)

**Topology:** Build React into ASP.NET Core `wwwroot`; deploy **one published ASP.NET Core site** to IIS, e.g. `C:\sites\SignalLedger\current`. No Node server, Docker or reverse proxy is necessary in production. Site root serves SPA and `/api/*` from same hostname. Avoid client-side CORS complexity. Database under e.g. `C:\ProgramData\SignalLedger\data\signalledger.db`, never under `wwwroot`.

**Prerequisites:** Windows + IIS feature, .NET 10 ASP.NET Core Hosting Bundle (includes ASP.NET Core Module), HTTPS certificate and binding, dedicated Application Pool with **No Managed Code**, appropriate service identity. Microsoft IIS guidance: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0

**Build/publish contract:**

```powershell
# development/build machine
cd src/signal-ledger-web
npm ci
npm run build
# Build pipeline copies dist/* into src/SignalLedger.Api/wwwroot/
cd ../..
dotnet restore
dotnet test -c Release
dotnet publish src/SignalLedger.Api/SignalLedger.Api.csproj -c Release -o .\artifacts\publish
```

The script `scripts/publish-iis.ps1` must perform frontend install/build, copy `dist` to API webroot, run tests (unless an explicitly named CI-only skip), publish and verify that output has `web.config`, `index.html`, JS/CSS assets, API DLL and health endpoint contract. Make this publish script the documented official path; **do not assume `dotnet publish` alone builds the frontend unless explicitly wired into MSBuild**.

The IIS `web.config` should be generated/managed by ASP.NET Core publish; do not replace it with a static SPA rewrite config that breaks the ASP.NET Core Module. SPA fallback should be implemented in ASP.NET Core after `/api` routing and static-file middleware, not via redirecting API 404s to HTML.

**Configuration:** `appsettings.Production.json` + environment variables for data directory, enabled providers, public demo mode, max job concurrency, request size limits and logging. No secrets in git. Use IIS pool identity ACLs: read/execute for published directory; modify for data/log folder only. Run EF migrations carefully at startup or as an explicit deploy step (prefer explicit deploy step on production). Back up DB before migration/redeploy, and support restore. Avoid overwriting SQLite DB during deployment.

**Operational acceptance tests:** HTTPS loads UI; deep link refresh works; `/api/health` returns JSON; `/api/nonexistent` returns JSON 404 (not SPA HTML); CSV import from permitted interface; backtest yields same reproducible values before/after recycle; stored DB remains after publish; logs writable but not public; unauthorized provider sync inaccessible on public demo; process recovers from IIS recycle; App Pool idle timeout behavior documented. Provide `docs/IIS_DEPLOYMENT.md` with IIS Manager screenshots or precise CLI instructions and troubleshooting for 500.30/502.5.

## 9. Open-source and repository quality

- MIT `LICENSE`, `README.md` with clear value proposition, screenshots and non-misleading performance examples, quick start, Windows/IIS publishing, methodology disclaimer, data licensing notice.
- `CONTRIBUTING.md`, issue templates, pull-request template, `CODE_OF_CONDUCT.md`, `.editorconfig`, `.gitignore`, Dependabot/Renovate optional.
- GitHub Actions CI: Windows runner for .NET build/tests + frontend lint/typecheck/tests; Linux-compatible tests optional; integration/e2e in headless browser when stable.
- Keep test fixtures **synthetic or specifically licensed**, never downloaded Yahoo archives. Credit provider dependencies; check license compatibility of packages/fonts/assets.
- No vendor credentials, customer data, personal financial data or real trade execution.
- Demo deployment must not imply any strategy guarantees profits. Show assumptions and reproducibility directly in the UI.

## 10. Collaboration rules for Claude Code / Codex (MUST FOLLOW)

This document is the **implementation backlog and progress tracker**. Agents must treat its checkbox tasks as the source of truth.

1. At the start of each work session, read this entire file, `README.md`, project structure and git status; identify the **first incomplete task whose dependencies are complete**.
2. Work in small, logical commits/PRs. Do not change architecture without updating this document and explaining why.
3. **Only change `[ ]` to `[x]` after code, automated tests, and the task's acceptance conditions are satisfied.** Never mark a task complete just because a file was created or code was drafted.
4. For each checked task, append an entry to the **Progress Log** below with date, task ID, changed files, test/build commands and observed results, limitations and commit hash if available.
5. If blocked, leave `[ ]`, record a `BLOCKED` entry with the reason, and move to a truly independent task. Do not fabricate test results, integrations, deployed sites or datasets.
6. Run relevant targeted tests before each checkmark and the full CI/test suite before completing a milestone. If tests cannot be executed, **do not check the task** and report the dependency.
7. Do not overwrite unrelated user changes. Check git diff/status before and after work. Avoid broad refactors outside the assigned task.
8. All strategies need unit tests for edge cases and a human-readable formula/assumptions note. Reject look-ahead behavior even if it improves outcomes.
9. Prioritize an independently runnable vertical slice, not dozens of stubs. No fake integrations presented as real implementations.
10. Always leave next-session handoff: completed IDs, open blockers, exact next task, commands to reproduce and notable decisions.

### Copy/paste agent instruction

> Implement SignalLedger according to `docs/IMPLEMENTATION_PLAN.md`. First inspect the repo and existing code, then select the next unchecked task whose dependencies are satisfied. Implement and test it fully. Update the checkbox and append a truthful Progress Log entry only when the acceptance criterion passes. Run appropriate builds/tests and preserve IIS deployment compatibility. Do not invent test results or market data. Continue through tasks in dependency order; report blockers rather than skipping validation. Do not mark a task as complete if you did not verify it.

## 11. Implementation checklist

### M0 — Repository and bootstrapping

- [x] **M0.1** Create Git repo and solution with `Api`, `Domain`, `Infrastructure`, `Domain.Tests`, `Api.IntegrationTests`, React/Vite app. **Done when:** `dotnet build` and `npm run build` pass.
- [x] **M0.2** Add MIT license, README, `.gitignore`, `.editorconfig`, contribution and disclaimer docs. **Done when:** a new developer can clone and run without undocumented prerequisites.
- [ ] **M0.3** Add CI for restore, backend tests, frontend typecheck/lint/build. **Done when:** CI goes green on main branch.
- [x] **M0.4** Wire one-process SPA + API with `/api/health`, fallback routing and environment-aware config. **Done when:** development UI and API run; fallback does not catch `/api` 404.

### M1 — Persistent data and import

- [x] **M1.1** EF Core schema + migration for instruments, DailyBars, ImportRuns and BacktestRuns. **Done when:** DB persists across app restarts; duplicate key handling covered by tests.
- [x] **M1.2** Implement CSV import, validation, field mapping and import audit status. **Done when:** synthetic 30-year-like input imports; malformed prices/dates produce actionable errors, no silent fixes.
- [x] **M1.3** Implement data coverage/quality report and data-manager API. **Done when:** coverage dates, missing bars and source are accurately reported.
- [x] **M1.4** Implement optional Yahoo Chart adapter behind configuration and licensing gate, with bounded dates, retries with backoff, cancellation, incremental upsert and provider error handling. **Done when:** fixture/contract tests pass; any live fetch is enabled **only if authorized**, never required for build or CI.
- [x] **M1.5** Document provider restrictions, supported symbols, whether the series is tradable, and CSV-first fallback. **Done when:** reader cannot mistake OHLC for Bid/Ask or index values for executable prices.

### M2 — Deterministic backtest core

- [x] **M2.1** Implement typed immutable price series, indicators (SMA/EMA) and warm-up rules. **Done when:** golden-vector tests pass for N=1, N>history, missing dates and equality.
- [x] **M2.2** Implement signal-to-next-open scheduler, desired-state transitions and explicit initial-state policy. **Done when:** tests prove no same-close execution, duplicate orders or forward data access.
- [x] **M2.3** Implement long-only cash/units portfolio ledger, per-side fees, slippage, mark-to-market and end-of-window semantics. **Done when:** hand-calculated tiny datasets match ledger exactly and cash never becomes negative.
- [x] **M2.4** Implement Buy & Hold, SMA and EMA strategies behind interface. **Done when:** benchmark and strategy use consistent first execution day and model assumptions.
- [x] **M2.5** Compute CAGR, drawdown, costs, trades, invested time, annual returns and Sharpe with documented conventions. **Done when:** golden-vector metrics and edge-case tests pass.
- [ ] **M2.6** Expose validated backtest API, data fingerprints, deterministic results and history storage. **Done when:** same data+params yield identical outcomes after restart.
- [ ] **M2.7** Add optional historical cash/T-bill adapter with correct rate semantics and point-in-time availability. **Done when:** interest accrual and missing-rate scenarios validated; otherwise keep UI mode disabled and default 0%.

### M3 — Web dashboard

- [ ] **M3.1** Design responsive dashboard with symbol, dates, capital, strategy and execution cost form. **Done when:** accessible keyboard controls and input validation work on desktop/mobile.
- [ ] **M3.2** Render comparison metrics, normalized equity/drawdown and prices with SMA signals. **Done when:** chart points and table values agree with API fixtures.
- [ ] **M3.3** Implement pageable trade journal and detailed assumptions/warnings. **Done when:** each trade shows signal/fill dates, costs and auditable balance.
- [ ] **M3.4** Implement Data Manager with file upload, cache status, refresh and import-error UI. **Done when:** user can complete first run from a synthetic CSV fully in browser.
- [ ] **M3.5** Add empty/loading/error/accessibility polish, About/Methodology page and screenshots. **Done when:** E2E happy path and representative failure path pass.

### M4 — Strategy Explorer

- [ ] **M4.1** Implement bounded grid of SMA periods × per-side fees, job progress and cancel behavior. **Done when:** grid values equal corresponding independent backtests.
- [ ] **M4.2** Implement rolling windows and holdout split for out-of-sample validation. **Done when:** no test-window values leak into parameter selection.
- [ ] **M4.3** Add interactive heatmap, drilldowns, comparison exports and warnings about overfitting. **Done when:** arbitrary selected cell opens reproducible results.

### M5 — IIS and release quality

- [ ] **M5.1** Implement `publish-iis.ps1` to build UI, copy static files, test, publish, and validate output. **Done when:** Windows build produces one deployable IIS folder containing `web.config` and SPA assets.
- [ ] **M5.2** Implement `deploy-iis.ps1` and `docs/IIS_DEPLOYMENT.md`: IIS pool, hosting bundle, HTTPS, ACLs, DB location and backup/migration/rollback. **Done when:** documented clean-server deployment works.
- [ ] **M5.3** Verify IIS smoke tests: deep routes, `/api` 404, persistent cache, recycle, 500.x troubleshooting, disabled public sync. **Done when:** test evidence recorded from actual IIS host (not assumed).
- [ ] **M5.4** Add CI regression suite, security limits, synthetic public demo seed, license review and polished README/screenshots. **Done when:** fresh clone and public demo do not need paid services or unauthorized data.
- [ ] **M5.5** Tag `v1.0.0`, publish release notes and GitHub demo link (if publicly hosted). **Done when:** repo contains working instructions and reproducible demo evidence.

## 12. Test case matrix (minimum)

| Case | Expected behavior |
|---|---|
| Rising prices, SMA always below close | Enter only at first **next open** after first valid signal, then hold |
| Falling prices, close below SMA | Remain cash; no fictitious loss from unheld units |
| Exact equality close = SMA | Keep prior target state |
| Fast alternating closes | Correct trade count, transaction costs per side, no duplicate pending orders |
| Large fee or slippage | No negative cash; either smaller valid buy or clear rejection |
| Last-day entry signal | No trade without next session data |
| Missing bar/session | Warn or reject by policy; no invented bar |
| Warm-up crossing start date | Indicator may use prior bars; performance/trades begin within interval |
| Tiny data set (N too large) | Insufficient-data warning, not NaN/Infinity |
| Same initial value/data/parameters | Bitwise or tolerance-defined deterministic metrics |
| CSV re-import | Idempotent, metadata correct, no duplicate dates |
| Adjusted ETF vs raw index | Clearly different labeling; no undocumented adjusted-close/raw-open mix |
| IIS SPA deep link | HTML page loads; unknown `/api` remains JSON 404 |
| IIS restart/publish | SQLite and run records survive |

## 13. Definition of Done for a milestone

A milestone is done only when **all** its task checkboxes are marked, tests actually run, documentation is updated, and a reviewer can follow the steps from a clean checkout. No checked item may rely on hardcoded example profits, silently missing dividends, fabricated market data, or unverified IIS setup. The README must clearly state that backtested results are historical/hypothetical and do not predict future performance.

## 14. Progress Log (append-only; never fabricate)

| Date (UTC) | Task ID | Implementation / files | Verification commands & observed result | Commit / blocker |
|---|---|---|---|---|
| 2026-11-07 | PLAN | Implementation plan created; **no implementation tasks completed** | Documentation only; no build/tests run | Initial planning |
| 2026-11-07 | M0.1 | Added `SignalLedger.slnx`, .NET 10 API/Domain/Infrastructure projects, domain and API test projects, shared build settings, SDK pin, and React/Vite/TypeScript application shell | `dotnet build SignalLedger.slnx -c Release --no-restore` passed (0 warnings/errors); `dotnet test SignalLedger.slnx -c Release --no-build` passed (2/2); `npm run lint` passed; `npm run build` passed (Vite production bundle generated) | Working tree; not committed |
| 2026-11-07 | M0.2 | Added README onboarding/commands, `.editorconfig`, contribution guide, code of conduct, and financial/data disclaimer; retained MIT license and repository `.gitignore` | Required-file check passed; documented `dotnet build SignalLedger.slnx -c Release --no-restore` passed with 0 warnings/errors; `git diff --check` passed | Working tree; not committed |
| 2026-11-07 | M0.4 | Replaced template weather endpoint with JSON health contract, Problem Details API 404, static files and SPA deep-link fallback; added real in-process integration tests | `dotnet test SignalLedger.slnx -c Release --no-restore` passed: Domain 1/1, API integration 4/4 including health, deep link and API 404 | Working tree; not committed |
| 2026-11-07 | M1.1 | Added typed instrument/bar/import/backtest models, EF Core SQLite context and generated `InitialCreate` migration; added Infrastructure test project with unique-key and restart persistence coverage | `dotnet tool run dotnet-ef migrations add InitialCreate ...` succeeded; `dotnet test SignalLedger.slnx -c Release --no-restore` passed: Domain 1/1, API 4/4, Infrastructure 3/3 | Foundation commit `c415cb1`; M1.1 working tree not yet committed |
| 2026-11-07 | M1.2 | Added bounded CSV parser/import service with explicit header mapping, invariant date/number parsing, OHLC/volume validation, transactional idempotent upsert, per-row actionable errors and persisted import audit | `dotnet test SignalLedger.slnx -c Release --no-restore` passed: Domain 1/1, API 4/4, Infrastructure 7/7; includes 7,560-row synthetic import, malformed rows, custom mapping and re-import tests; `git diff --check` passed | M1.1 commit `9c16386`; M1.2 not yet committed |
| 2026-11-07 | M1.3 | Wired migrated SQLite storage into the API, seeded the supported instrument catalog, added source-specific coverage/quality reporting, tradability warnings, US equity-session calendar (including exceptional closures), and Data Manager endpoints | `dotnet test SignalLedger.slnx -c Release --no-restore` passed: Domain 1/1, API 6/6, Infrastructure 11/11; `git diff --check` passed | M1.2 commit `ad9cf37`; M1.3 not yet committed |
| 2026-11-07 | M1.4 | Added disabled-by-default Yahoo Chart adapter with dual authorization gate, allowlist/range/timeout bounds, retry/backoff, cancellation, fixture parsing, provider errors, incremental transactional sync and audit; no live requests in tests | `dotnet test SignalLedger.slnx -c Release --no-restore` passed: Domain 1/1, API 7/7, Infrastructure 17/17; contract tests cover disabled/unauthorized modes, fixture parsing, retry bounds, allowlist/range and incremental sync | M1.3 commit `7363d4a`; M1.4 not yet committed |
| 2026-11-07 | M1.5 | Added `docs/DATA_SOURCES.md` covering CSV-first use, supported symbols, index/ETF tradability, OHLC vs Bid/Ask, Yahoo restrictions/gate, redistribution and adjusted-price constraints | Documentation reviewed against provider/runtime defaults; `git diff --check` passed | Included with M1.4 working tree |
| 2026-11-07 | M2.1–M2.5 | Added immutable ordered price series, SMA/EMA with explicit seeding and warm-up, next-open signal scheduler, long-only fee/slippage ledger, Buy & Hold/SMA/EMA strategy interface, common-window engine, and documented performance analytics | `dotnet test SignalLedger.slnx -c Release --no-restore` passed: Domain 27/27, API 7/7, Infrastructure 17/17; golden tests cover N=1/N>history/gaps/equality, no-look-ahead/duplicates/last signal, hand-calculated costs, no negative cash, end liquidation, shared timestamps and metrics edge cases; `git diff --check` passed | M1 commit `0b6df3a`; M2 core not yet committed |

## 15. References

- Microsoft: [Publish ASP.NET Core to IIS](https://learn.microsoft.com/en-us/aspnet/core/tutorials/publish-to-iis)
- Microsoft: [Host ASP.NET Core on Windows with IIS](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0)
- Yahoo Finance: https://finance.yahoo.com/ (verify terms before automated usage)
- Yahoo unofficial chart sample: `https://query1.finance.yahoo.com/v8/finance/chart/%5ENDX?range=30y&interval=1d` (**not a supported public API contract**)
- Kenneth R. French Data Library: https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/data_library.html
- FRED: https://fred.stlouisfed.org/ (historical rate datasets and documented API)

---

**Next agent task:** Implement `M2.6` validated deterministic backtest API, fingerprinting and restart-stable history. `M0.3` awaits a green GitHub Actions run on main.
