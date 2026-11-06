namespace SignalLedger.Domain.Backtesting;

public sealed record StrategySchedule(
    string Name, IReadOnlyList<double?> Indicator, IReadOnlyList<StrategySignal> Signals,
    IReadOnlyList<ScheduledOrder> Orders, StrategySignal? UnfilledSignal);

public interface IBacktestStrategy
{
    string Name { get; }
    int WarmupSessions { get; }
    StrategySchedule CreateSchedule(EvaluationSeries evaluation);
}

public sealed class BuyAndHoldStrategy : IBacktestStrategy
{
    public string Name => "Buy & Hold";
    public int WarmupSessions => 0;
    public StrategySchedule CreateSchedule(EvaluationSeries evaluation)
    {
        var indicator = new double?[evaluation.Bars.Count];
        var first = evaluation.EvaluationBars.FirstOrDefault();
        if (first.Bar == default) return new(Name, indicator, [], [], null);
        var signal = new StrategySignal(first.Bar.Date, PositionState.Invested, "Initial benchmark allocation");
        var order = new ScheduledOrder(first.Bar.Date, first.Bar.Date, TradeSide.Buy, first.Bar.Open, signal.Reason);
        return new(Name, indicator, [signal], [order], null);
    }
}

public sealed class SmaStrategy(int period) : IBacktestStrategy
{
    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));
    public string Name => $"SMA({Period})";
    public int WarmupSessions => Period - 1;
    public StrategySchedule CreateSchedule(EvaluationSeries evaluation)
    {
        var indicator = Indicators.SimpleMovingAverage(evaluation.Bars, Period);
        var schedule = SignalScheduler.ScheduleCloseVsIndicator(evaluation, indicator);
        return new(Name, indicator, schedule.Signals, schedule.Orders, schedule.UnfilledSignal);
    }
}

public sealed class EmaStrategy(int period) : IBacktestStrategy
{
    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));
    public string Name => $"EMA({Period})";
    public int WarmupSessions => Period - 1;
    public StrategySchedule CreateSchedule(EvaluationSeries evaluation)
    {
        var indicator = Indicators.ExponentialMovingAverage(evaluation.Bars, Period);
        var schedule = SignalScheduler.ScheduleCloseVsIndicator(evaluation, indicator);
        return new(Name, indicator, schedule.Signals, schedule.Orders, schedule.UnfilledSignal);
    }
}

public sealed record StrategyRun(string StrategyName, EvaluationSeries Evaluation, StrategySchedule Schedule, LedgerResult Ledger);

public static class BacktestEngine
{
    public static StrategyRun Run(PriceSeries completeSeries, DateOnly from, DateOnly to, decimal initialCapital, IBacktestStrategy strategy, ExecutionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        var evaluation = completeSeries.ForEvaluation(from, to, strategy.WarmupSessions);
        var schedule = strategy.CreateSchedule(evaluation);
        var evaluationBars = new PriceSeries(evaluation.EvaluationBars.Select(x => x.Bar));
        var ledger = PortfolioLedger.Simulate(evaluationBars, schedule.Orders, initialCapital, settings);
        return new(strategy.Name, evaluation, schedule, ledger);
    }
}
