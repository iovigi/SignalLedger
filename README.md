# SignalLedger

**Backtests you can inspect, not just admire.** SignalLedger is an open-source market backtesting lab for comparing buy-and-hold with transparent, rule-based strategies. It is designed for reproducibility: signals are generated at a session close and execute no earlier than the next session open, costs remain visible, and every transaction is auditable.

> [!WARNING]
> Backtests are hypothetical, omit real-world effects unless explicitly configured, and do not predict future performance. SignalLedger is educational software, not financial advice.

## Current status

The repository is under active development. The .NET 10 solution and React/TypeScript application shell build successfully; the implementation checklist lives in [`plans/SignalLedger_IMPLEMENTATION_PLAN.md`](plans/SignalLedger_IMPLEMENTATION_PLAN.md).

## Prerequisites

- .NET SDK 10.0.401 or a compatible 10.0 feature-band patch
- Node.js 22 or later and npm 10 or later
- PowerShell 7 recommended on Windows

## Run locally

```powershell
dotnet restore SignalLedger.slnx
dotnet build SignalLedger.slnx
dotnet run --project src/SignalLedger.Api
```

In another terminal:

```powershell
cd src/signal-ledger-web
npm install
npm run dev
```

The frontend development server prints its local URL. The API launch profile uses the URL defined in `src/SignalLedger.Api/Properties/launchSettings.json`.

## Verify changes

```powershell
dotnet test SignalLedger.slnx -c Release
cd src/signal-ledger-web
npm run lint
npm run build
```

## Market data policy

SignalLedger is CSV-first and must not bundle or redistribute downloaded Yahoo price history. Yahoo's chart endpoint is unofficial and its use may be restricted; the optional adapter will remain disabled unless a deployer confirms authorization. Index series such as `^NDX` are non-tradable theoretical proxies, and daily OHLC values are not Bid/Ask quotes. Repository fixtures must be synthetic or explicitly licensed.

See [docs/DATA_SOURCES.md](docs/DATA_SOURCES.md) for supported symbols, CSV requirements, tradability labels, adjusted-price constraints, and the provider authorization gate.

## Production target

The target is one ASP.NET Core site hosted on Windows/IIS, serving both `/api` and the compiled React SPA from the same origin. The SQLite data directory must remain outside the published site and `wwwroot`.

## Contributing and license

Read [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and [docs/DISCLAIMER.md](docs/DISCLAIMER.md) before contributing. Application source is released under the [MIT License](LICENSE); that license grants no rights to third-party market data.
