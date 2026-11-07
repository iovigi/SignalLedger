# SignalLedger

**Backtests you can inspect, not just admire.** SignalLedger is an open-source lab for comparing Buy & Hold with SMA/EMA market-timing strategies using next-session execution, visible costs, reproducible data fingerprints, and an auditable trade ledger.

![SignalLedger dashboard](docs/SCREENSHOTS/dashboard.png)

> [!WARNING]
> Results are hypothetical, use price returns rather than total returns, and do not predict future performance. SignalLedger is educational software, not financial advice.

## What is included

- CSV-first daily OHLC import with validation, audit history, and SQLite persistence.
- Deterministic Buy & Hold, SMA, and EMA simulations with next-open fills, commission, slippage, drawdown, CAGR, Sharpe, and trade journals.
- Strategy Explorer with bounded parameter grids, rolling windows, training/holdout separation, cancellation, CSV export, and drilldowns.
- Responsive React dashboard, browser accessibility/regression tests, and a synthetic public-demo dataset.
- One-folder, self-hosted ASP.NET Core publish; no Node process or paid service is needed in production.

## Quick start

Prerequisites: .NET SDK 10.0.401 (or compatible patch), Node.js 22+, npm 10+, and PowerShell 7 recommended.

```powershell
dotnet restore SignalLedger.slnx
dotnet test SignalLedger.slnx -c Release
cd src/signal-ledger-web
npm ci
npm run dev
```

In another terminal, run `dotnet run --project src/SignalLedger.Api`. Use `samples/synthetic-prices.csv` for a small offline import. Production public-demo mode automatically creates a deterministic 2,600-session `SYNTH` series and disables CSV import and provider sync.

## Verify and publish

```powershell
dotnet test SignalLedger.slnx -c Release
cd src/signal-ledger-web
npm run lint
npm run build
npm run test:e2e
cd ../..
.\scripts\publish.ps1
```

The publish script produces and smoke-tests `artifacts/publish`, containing the API and SPA assets, and checks health, the JSON `/api` 404 and SPA deep links. Run it with `dotnet SignalLedger.Api.dll`. See [deployment](docs/DEPLOYMENT.md) for configuration, HTTPS, backup, migration and rollback.

## Data and methodology

SignalLedger does not bundle or redistribute downloaded market data. Yahoo's unofficial chart endpoint is isolated and disabled unless a deployer explicitly confirms authorized use. `^NDX`, `^IXIC`, and `^GSPC` are non-tradable price-index proxies; their OHLC values are not Bid/Ask. QQQ raw-price tests exclude distributions and are not total-return results.

Read [data sources](docs/DATA_SOURCES.md), [backtest assumptions](docs/BACKTEST_ASSUMPTIONS.md), and the [disclaimer](docs/DISCLAIMER.md) before interpreting results. The implementation checklist and verification record are in [the plan](plans/SignalLedger_IMPLEMENTATION_PLAN.md).

## Contributing and license

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Application source is MIT licensed; this grants no rights to third-party market data. Dependency licenses are summarized in [third-party notices](docs/THIRD_PARTY_NOTICES.md).
