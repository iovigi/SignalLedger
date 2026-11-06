# SignalLedger v1.0.0

SignalLedger 1.0 is an inspectable, reproducible market-backtesting lab for Windows/IIS. It compares Buy & Hold with SMA and EMA timing rules using next-session execution and an auditable cost ledger.

## Highlights

- Strict CSV-first daily OHLC ingestion, SQLite persistence, data-quality reporting, and an optional authorization-gated provider adapter.
- Deterministic Buy & Hold/SMA/EMA engine with no same-close execution, coherent fee/slippage accounting, reproducible fingerprints, persisted results, and pageable trades.
- CAGR, drawdown, volatility, Sharpe, annual returns, invested time, and explicit non-tradable index warnings.
- Responsive React dashboard and Strategy Explorer with bounded grids, rolling windows, training/holdout separation, cancellation, export, and full-run drilldowns.
- Synthetic public demo requiring no paid service or unauthorized market data; public imports/provider sync are disabled and expensive writes are rate-limited.
- One-folder ASP.NET Core/IIS artifact with versioned deployment, explicit migrations, database backup, ACL setup, HTTPS binding support, rollback guidance, and automated acceptance checks.

## Reproducibility evidence

- Backend: 27 domain, 17 infrastructure, and 16 API integration tests.
- Browser: four Playwright workflows covering CSV-to-backtest, failure state, Strategy Explorer export/drilldown, accessibility, and mobile layout.
- Windows GitHub Actions builds and tests the backend/frontend, runs Playwright, publishes the IIS artifact, and verifies its health contract.

## Important limitations

The application uses raw daily OHLC price returns and does not claim dividend-inclusive total returns. Index fills are theoretical. Results are hypothetical and are not financial advice. No third-party market data is bundled. The optional Yahoo adapter is unofficial, disabled by default, and must not be enabled without a current authorization/terms review.

See `README.md`, `docs/BACKTEST_ASSUMPTIONS.md`, `docs/DATA_SOURCES.md`, and `docs/IIS_DEPLOYMENT.md` before use.
