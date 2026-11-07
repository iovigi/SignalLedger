# Instructions for coding agents (Claude Code, Codex and others)

These rules apply to every session in this repository.

## Before changing anything

1. Read [`plans/SignalLedger_IMPLEMENTATION_PLAN.md`](plans/SignalLedger_IMPLEMENTATION_PLAN.md): the checklists and the append-only Progress Log are the source of truth for what is done.
2. Inspect the existing code before implementing a feature. Reuse existing services, entities, endpoints and components; never create a parallel implementation (e.g. a second provider client, sync service, calendar or job queue).
3. Work from the first relevant unchecked task. Do not reset items that are already checked unless you have evidence of a regression, and record that evidence.

## While working

4. Preserve existing functionality, API contracts and UX unless the task explicitly changes them.
5. Add or update automated tests for every behavioural change.
6. Run the relevant local verification and read the output:
   - `dotnet test SignalLedger.slnx -c Release`
   - in `src/signal-ledger-web`: `npm run lint`, `npm run build`, `npm run test:e2e`
   - `.\scripts\publish.ps1` for anything affecting hosting or publishing
7. Never claim that tests, builds or checks passed unless you executed them in this session and saw them pass. Report failures with their output.

## Recording progress

8. Change `[ ]` to `[x]` only after the acceptance criteria are verified, not when code is merely written.
9. Append a Progress Log row (date, task IDs, files, commands run and observed results, commit or blocker). Record blockers and incomplete work explicitly.

## Hard rules

10. **No deployment.** Do not deploy, run `scripts/deploy-iis.ps1`, connect to servers, change IIS sites/pools, touch production databases or trigger deployment pipelines unless a future user request explicitly authorizes that specific action.
11. **No synthetic data passed off as market data.** Never fabricate, interpolate or substitute prices or instruments. Synthetic data is allowed only in tests and the clearly labelled `SYNTH` demo series.
12. **Financial correctness first.** Keep next-session execution (no same-close fills), per-side costs, coherent raw OHLC, and the separation of price-return vs. total-return results. Change strategy maths only to fix a verified bug, with a test that proves it.
13. **Provider traffic stays on the server.** The React app must never call Yahoo or any provider, and the backend must never fetch user-supplied URLs.
14. **No unnecessary re-downloads.** Ordinary synchronization must request only missing completed sessions (`MarketDataSyncPlanner`); a current cache must produce zero provider requests. Full or range re-downloads happen only through the explicit refresh action.
15. **Tests never reach the network.** Use the stub handlers/providers in the test projects; never enable live Yahoo access in CI.
16. Respect provider terms: Yahoo's endpoint is unofficial; keep it disabled by default behind `AuthorizedUseConfirmed`, and never commit or publish downloaded prices.

Key references: [docs/MARKET_DATA.md](docs/MARKET_DATA.md), [docs/IIS_DEPLOYMENT.md](docs/IIS_DEPLOYMENT.md), [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md), [docs/BACKTEST_ASSUMPTIONS.md](docs/BACKTEST_ASSUMPTIONS.md), [docs/DATA_SOURCES.md](docs/DATA_SOURCES.md).
