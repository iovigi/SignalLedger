# Third-party notices

SignalLedger application source is MIT licensed. Market data is not included and is not covered by that license.

| Component | Use | License |
|---|---|---|
| ASP.NET Core / Entity Framework Core / SQLite provider | API, hosting, persistence | MIT |
| React and React DOM | Web UI | MIT |
| Recharts | Charts | MIT |
| Vite and `@vitejs/plugin-react` | Frontend build | MIT |
| Tailwind CSS and `@tailwindcss/vite` | Styling/build | MIT |
| TypeScript | Compiler | Apache-2.0 |
| Playwright / Axe integration | Browser/accessibility tests | Apache-2.0 / MPL-2.0 |
| Oxlint | Linting | MIT |

Transitive dependencies are locked in `package-lock.json` and NuGet assets. Before redistributing binaries, generate a current inventory from the lock files and retain all required notices. No dependency license grants permission to download, bundle, or redistribute third-party market prices.
