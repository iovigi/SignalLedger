# Contributing to SignalLedger

Thank you for helping make backtests more inspectable and reproducible.

## Workflow

1. Read the implementation plan and choose the first incomplete task whose dependencies are complete.
2. Create a focused branch and keep changes within that task.
3. Add or update automated tests, especially for timing, fees, numerical edge cases, and data validation.
4. Run the backend tests plus frontend lint/build commands documented in the README.
5. Mark a plan item complete only after its acceptance condition passes, then append truthful evidence to the progress log.

Never commit downloaded market data, secrets, personal financial data, generated databases, or build output. Test datasets must be synthetic or explicitly licensed. Strategy changes must preserve next-session execution and must include a human-readable assumptions note.

By participating, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
