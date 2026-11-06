# Backtest assumptions

## Price series and indicators

The engine consumes an immutable sequence of unique, strictly increasing exchange sessions. It never invents bars for calendar gaps. OHLC values must be positive and internally consistent.

`SMA(N)` is the arithmetic mean of the latest `N` closing prices, measured in observations/trading sessions rather than calendar days. It is unavailable until `N` observations exist. `EMA(N)` is initialized with the first available `N`-session SMA and then uses `alpha = 2 / (N + 1)`; it is likewise unavailable before the seed.

An evaluation window may load earlier observations for indicator warm-up. Those observations cannot contribute returns, signals, or trades before the requested start. If fewer warm-up observations exist, the result carries an explicit shortened-warm-up flag.

Further execution, portfolio, cost, and metric conventions will be added alongside their verified engine milestones.

## Signal timing

The initial strategy position is cash. A close above its moving average requests an invested state; a close below requests cash; equality retains the prior desired state. A state change queues one order only. That order executes at the next available session open, never at the signal close, and must fill before that session's closing signal is evaluated. A final-session signal remains explicitly unfilled when no later bar exists. Warm-up observations never generate signals or orders.

## Portfolio and transaction costs

The portfolio is long-only, all-in/all-out, permits fractional units, and cannot borrow or short. Buy slippage raises the open reference price by `bps / 10,000`; sell slippage lowers it. Per-side commission is `abs(effective-price notional) × feePercent / 100`. Buy quantity is solved as `cash / (effectivePrice × (1 + feeRate))`, ensuring fees never make cash negative. Cash yield is 0% until a separately validated point-in-time rate series exists.

Each session is marked to its closing price. An open position remains open and is marked to market at the last close unless explicit end liquidation is enabled. Explicit liquidation is a separately identified last-close transaction; no liquidation is silently fabricated.

## Strategies and comparison baseline

Buy & Hold makes an explicit initial allocation at the first evaluation-session open; this is not based on that session's close. SMA and EMA start in cash and follow the close-signal/next-open rule above. All compared runs have the same requested evaluation dates, daily equity timestamps, initial capital, commission, slippage, and liquidation convention. A timing strategy may remain cash after the common baseline begins; its delayed entry does not shift or truncate the comparison window.

## Metrics

Total return is ending equity divided by initial capital minus one. CAGR uses actual elapsed calendar days divided by 365.2425 and is unavailable for a one-date series. Drawdown is the worst peak-to-subsequent-equity percentage. Calendar-year returns chain from the preceding year's ending equity (or initial capital for the first partial year). Annualized volatility is the sample standard deviation of daily portfolio returns times `sqrt(252)`. Sharpe uses a 0% risk-free rate and daily arithmetic mean divided by daily sample deviation times `sqrt(252)`; it is unavailable when data are insufficient or volatility is zero. Transactions count individual buys/sells; round trips count completed sells. Costs, invested sessions, and percent invested are reported separately.
