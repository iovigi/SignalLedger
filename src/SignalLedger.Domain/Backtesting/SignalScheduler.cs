namespace SignalLedger.Domain.Backtesting;

public enum PositionState { Cash, Invested }
public enum TradeSide { Buy, Sell }

public sealed record StrategySignal(DateOnly SignalDate, PositionState DesiredState, string Reason);
public sealed record ScheduledOrder(DateOnly SignalDate, DateOnly ExecutionDate, TradeSide Side, decimal ReferenceOpen, string Reason);
public sealed record ScheduleResult(IReadOnlyList<StrategySignal> Signals, IReadOnlyList<ScheduledOrder> Orders, PositionState FinalState, StrategySignal? UnfilledSignal);

public static class SignalScheduler
{
    public static ScheduleResult ScheduleCloseVsIndicator(
        EvaluationSeries evaluation,
        IReadOnlyList<double?> indicator,
        PositionState initialState = PositionState.Cash)
    {
        if (indicator.Count != evaluation.Bars.Count) throw new ArgumentException("Indicator length must match the loaded price series.", nameof(indicator));
        var state = initialState;
        StrategySignal? pending = null;
        var signals = new List<StrategySignal>();
        var orders = new List<ScheduledOrder>();
        for (var i = evaluation.EvaluationStartIndex; i < evaluation.Bars.Count; i++)
        {
            var bar = evaluation.Bars[i];
            if (bar.Date < evaluation.From || bar.Date > evaluation.To) continue;
            if (pending is not null)
            {
                var side = pending.DesiredState == PositionState.Invested ? TradeSide.Buy : TradeSide.Sell;
                orders.Add(new(pending.SignalDate, bar.Date, side, bar.Open, pending.Reason));
                state = pending.DesiredState;
                pending = null;
            }

            var value = indicator[i];
            if (value is null) continue;
            var desired = (double)bar.Close > value.Value ? PositionState.Invested : (double)bar.Close < value.Value ? PositionState.Cash : state;
            if (desired == state) continue;
            pending = new(bar.Date, desired, desired == PositionState.Invested ? "Close above indicator" : "Close below indicator");
            signals.Add(pending);
        }
        return new(signals, orders, state, pending);
    }
}
