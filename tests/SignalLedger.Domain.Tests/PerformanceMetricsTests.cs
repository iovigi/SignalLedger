using SignalLedger.Domain.Backtesting;

namespace SignalLedger.Domain.Tests;

public sealed class PerformanceMetricsTests
{
    [Fact]
    public void GoldenVector_ComputesReturnCagrDrawdownAndAnnualReturns()
    {
        var points = new[]
        {
            Point(new(2020, 1, 1), 100), Point(new(2020, 6, 1), 120),
            Point(new(2020, 12, 31), 90), Point(new(2021, 1, 1), 108), Point(new(2022, 1, 1), 121)
        };
        var ledger = Ledger(100, 121, points);
        var result = PerformanceMetrics.Calculate(ledger);
        var years = (points[^1].Date.DayNumber - points[0].Date.DayNumber) / 365.2425;
        Assert.Equal(0.21, result.TotalReturn, 12);
        Assert.Equal(Math.Pow(1.21, 1 / years) - 1, result.Cagr!.Value, 12);
        Assert.Equal(-0.25, result.MaxDrawdown, 12);
        Assert.Equal(3, result.AnnualReturns.Count);
        Assert.Equal(2020, result.WorstCalendarYear?.Year);
    }

    [Fact]
    public void CostsTradesRoundTripsAndInvestedTimeRemainDistinct()
    {
        var trades = new[]
        {
            Trade(TradeSide.Buy, 1m), Trade(TradeSide.Sell, 2m), Trade(TradeSide.Buy, 3m)
        };
        var points = new[] { Point(new(2020, 1, 1), 100, true), Point(new(2020, 1, 2), 101), Point(new(2020, 1, 3), 102, true) };
        var ledger = new LedgerResult(100, 102, 0, 1, 6, 4, trades, points, true, false);
        var result = PerformanceMetrics.Calculate(ledger);
        Assert.Equal(3, result.Transactions);
        Assert.Equal(1, result.RoundTrips);
        Assert.Equal(6, result.TotalCommission);
        Assert.Equal(4, result.EstimatedSlippageCost);
        Assert.Equal(2d / 3d, result.PercentTimeInvested, 12);
    }

    [Fact]
    public void TinyOrFlatSeries_ReturnsWarningsInsteadOfNanOrInfinity()
    {
        var result = PerformanceMetrics.Calculate(Ledger(100, 100, [Point(new(2020, 1, 1), 100)]));
        Assert.Null(result.Cagr);
        Assert.Null(result.SharpeRatio);
        Assert.All(result.Warnings, warning => Assert.False(string.IsNullOrWhiteSpace(warning)));
    }

    private static LedgerResult Ledger(decimal initial, decimal final, IReadOnlyList<EquityPoint> points) => new(initial, final, final, 0, 0, 0, [], points, false, false);
    private static EquityPoint Point(DateOnly date, decimal equity, bool invested = false) => new(date, equity, invested ? 1 : 0, equity, equity, invested);
    private static LedgerTrade Trade(TradeSide side, decimal commission) => new(new(2020, 1, 1), new(2020, 1, 2), side, 100, 0, 100, 1, 100, commission, 0, 100, "test");
}
