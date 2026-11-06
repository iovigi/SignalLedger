namespace SignalLedger.Domain.Backtesting;

public sealed record ExecutionSettings(decimal CommissionPercentPerSide = 0, decimal SlippageBpsPerSide = 0, bool LiquidateAtEnd = false);

public sealed record LedgerTrade(
    DateOnly SignalDate, DateOnly ExecutionDate, TradeSide Side, decimal ReferencePrice,
    decimal SlippagePerUnit, decimal EffectivePrice, decimal Units, decimal Notional,
    decimal Commission, decimal CashAfter, decimal EquityAfter, string Reason);

public sealed record EquityPoint(DateOnly Date, decimal Cash, decimal Units, decimal ClosingPrice, decimal Equity, bool Invested);

public sealed record LedgerResult(
    decimal InitialCapital, decimal FinalEquity, decimal Cash, decimal Units,
    decimal TotalCommission, decimal EstimatedSlippageCost,
    IReadOnlyList<LedgerTrade> Trades, IReadOnlyList<EquityPoint> Equity,
    bool HasOpenPosition, bool LiquidatedAtEnd);

public static class PortfolioLedger
{
    public static LedgerResult Simulate(PriceSeries evaluationBars, IReadOnlyList<ScheduledOrder> orders, decimal initialCapital, ExecutionSettings settings)
    {
        if (initialCapital <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapital));
        if (settings.CommissionPercentPerSide is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(settings));
        if (settings.SlippageBpsPerSide is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(settings));
        var orderLookup = orders.GroupBy(x => x.ExecutionDate).ToDictionary(x => x.Key, x => x.ToArray());
        if (orderLookup.Any(x => x.Value.Length > 1)) throw new ArgumentException("Only one order may execute per session.", nameof(orders));
        var cash = initialCapital; decimal units = 0; decimal commissionTotal = 0; decimal slippageTotal = 0;
        var trades = new List<LedgerTrade>(); var equity = new List<EquityPoint>();
        var feeRate = settings.CommissionPercentPerSide / 100m; var slippageRate = settings.SlippageBpsPerSide / 10_000m;
        foreach (var bar in evaluationBars)
        {
            if (orderLookup.TryGetValue(bar.Date, out var sessionOrders))
            {
                var order = sessionOrders[0];
                if (order.ReferenceOpen != bar.Open) throw new ArgumentException($"Order reference open does not match the series on {bar.Date:yyyy-MM-dd}.");
                Execute(order.SignalDate, bar.Date, order.Side, bar.Open, bar.Open, order.Reason, ref cash, ref units, feeRate, slippageRate, trades, ref commissionTotal, ref slippageTotal);
            }
            equity.Add(new(bar.Date, cash, units, bar.Close, cash + units * bar.Close, units > 0));
        }

        var liquidated = false;
        if (settings.LiquidateAtEnd && units > 0 && evaluationBars.Count > 0)
        {
            var last = evaluationBars[^1];
            Execute(last.Date, last.Date, TradeSide.Sell, last.Close, last.Close, "Explicit end-of-window liquidation", ref cash, ref units, feeRate, slippageRate, trades, ref commissionTotal, ref slippageTotal);
            equity[^1] = new(last.Date, cash, units, last.Close, cash, false);
            liquidated = true;
        }
        var finalEquity = equity.Count == 0 ? initialCapital : equity[^1].Equity;
        return new(initialCapital, finalEquity, cash, units, commissionTotal, slippageTotal, trades, equity, units > 0, liquidated);
    }

    private static void Execute(DateOnly signalDate, DateOnly executionDate, TradeSide side, decimal referencePrice, decimal markPrice, string reason,
        ref decimal cash, ref decimal units, decimal feeRate, decimal slippageRate, List<LedgerTrade> trades, ref decimal commissionTotal, ref decimal slippageTotal)
    {
        if (side == TradeSide.Buy)
        {
            if (units != 0) throw new InvalidOperationException("Cannot buy while already invested.");
            var effective = referencePrice * (1 + slippageRate);
            var quantity = cash / (effective * (1 + feeRate));
            var notional = quantity * effective; var commission = notional * feeRate; var spent = notional + commission;
            cash -= spent;
            if (cash < 0 && cash > -0.00000001m) cash = 0;
            if (cash < 0) throw new InvalidOperationException("Buy calculation produced negative cash.");
            units = quantity; commissionTotal += commission; slippageTotal += quantity * (effective - referencePrice);
            trades.Add(new(signalDate, executionDate, side, referencePrice, effective - referencePrice, effective, quantity, notional, commission, cash, cash + units * markPrice, reason));
        }
        else
        {
            if (units <= 0) throw new InvalidOperationException("Cannot sell without an open position.");
            var effective = referencePrice * (1 - slippageRate); var quantity = units; var notional = quantity * effective; var commission = notional * feeRate;
            cash += notional - commission; units = 0; commissionTotal += commission; slippageTotal += quantity * (referencePrice - effective);
            trades.Add(new(signalDate, executionDate, side, referencePrice, referencePrice - effective, effective, quantity, notional, commission, cash, cash, reason));
        }
    }
}
