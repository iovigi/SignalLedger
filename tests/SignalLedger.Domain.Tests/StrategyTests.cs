using SignalLedger.Domain.Backtesting;

namespace SignalLedger.Domain.Tests;

public sealed class StrategyTests
{
    [Fact]
    public void BuyAndHold_EntersAtFirstEvaluationOpenWithoutCloseLookahead()
    {
        var series = Series(10, 11, 12);
        var run = BacktestEngine.Run(series, new(2020, 1, 2), new(2020, 1, 3), 1_000, new BuyAndHoldStrategy(), new());
        var trade = Assert.Single(run.Ledger.Trades);
        Assert.Equal(new DateOnly(2020, 1, 2), trade.ExecutionDate);
        Assert.Equal("Initial benchmark allocation", trade.Reason);
    }

    [Fact]
    public void RisingSma_EntersAtNextOpenAfterFirstValidEvaluationSignal()
    {
        var series = Series(10, 11, 12, 13, 14);
        var run = BacktestEngine.Run(series, new(2020, 1, 3), new(2020, 1, 5), 1_000, new SmaStrategy(3), new());
        var trade = Assert.Single(run.Ledger.Trades);
        Assert.Equal(new DateOnly(2020, 1, 3), trade.SignalDate);
        Assert.Equal(new DateOnly(2020, 1, 4), trade.ExecutionDate);
    }

    [Fact]
    public void FallingSma_RemainsCash()
    {
        var run = BacktestEngine.Run(Series(14, 13, 12, 11), new(2020, 1, 3), new(2020, 1, 4), 1_000, new SmaStrategy(3), new());
        Assert.Empty(run.Ledger.Trades);
        Assert.Equal(1_000, run.Ledger.FinalEquity);
    }

    [Fact]
    public void BenchmarkAndStrategy_HaveIdenticalEvaluationTimestampsAndCostSettings()
    {
        var series = Series(10, 11, 12, 13, 14);
        var settings = new ExecutionSettings(0.2m, 5m);
        var benchmark = BacktestEngine.Run(series, new(2020, 1, 3), new(2020, 1, 5), 1_000, new BuyAndHoldStrategy(), settings);
        var strategy = BacktestEngine.Run(series, new(2020, 1, 3), new(2020, 1, 5), 1_000, new EmaStrategy(3), settings);
        Assert.Equal(benchmark.Ledger.Equity.Select(x => x.Date), strategy.Ledger.Equity.Select(x => x.Date));
        Assert.Equal(new DateOnly(2020, 1, 3), benchmark.Ledger.Equity[0].Date);
        Assert.Equal(new DateOnly(2020, 1, 3), strategy.Ledger.Equity[0].Date);
        Assert.All(benchmark.Ledger.Trades.Concat(strategy.Ledger.Trades), trade => Assert.True(trade.Commission > 0));
    }

    [Fact]
    public void EmaStrategy_UsesExplicitSmaSeed()
    {
        var run = BacktestEngine.Run(Series(10, 11, 12, 14), new(2020, 1, 3), new(2020, 1, 4), 1_000, new EmaStrategy(3), new());
        Assert.Equal(11d, run.Schedule.Indicator[2]);
        Assert.Equal(12.5d, run.Schedule.Indicator[3]);
    }

    private static PriceSeries Series(params decimal[] closes) => new(closes.Select((close, i) => new PriceBar(new DateOnly(2020, 1, 1).AddDays(i), close, close, close, close)));
}
