using SignalLedger.Domain.Backtesting;

namespace SignalLedger.Domain.Tests;

public sealed class PortfolioLedgerTests
{
    [Fact]
    public void BuyAndSell_MatchHandCalculatedFeesAndSlippage()
    {
        var bars = Series(Bar(1, 100, 110), Bar(2, 120, 120));
        var orders = new[] { Order(1, 1, TradeSide.Buy, 100), Order(1, 2, TradeSide.Sell, 120) };
        var result = PortfolioLedger.Simulate(bars, orders, 1_000m, new(1m, 100m));
        var quantity = 1_000m / (101m * 1.01m);
        var expected = quantity * 118.8m * 0.99m;
        Assert.Equal(quantity, result.Trades[0].Units);
        Assert.Equal(expected, result.FinalEquity);
        Assert.Equal(0, result.Cash - expected);
        Assert.False(result.HasOpenPosition);
        Assert.Equal(2, result.Trades.Count);
    }

    [Fact]
    public void MarkToMarket_DoesNotFabricateEndLiquidation()
    {
        var result = PortfolioLedger.Simulate(Series(Bar(1, 100, 110)), [Order(1, 1, TradeSide.Buy, 100)], 1_000m, new());
        Assert.True(result.HasOpenPosition);
        Assert.False(result.LiquidatedAtEnd);
        Assert.Equal(1_100m, result.FinalEquity);
        Assert.Single(result.Trades);
    }

    [Fact]
    public void ExplicitLiquidation_UsesLastCloseAndAddsAuditableTrade()
    {
        var result = PortfolioLedger.Simulate(Series(Bar(1, 100, 110)), [Order(1, 1, TradeSide.Buy, 100)], 1_000m, new(LiquidateAtEnd: true));
        Assert.False(result.HasOpenPosition);
        Assert.True(result.LiquidatedAtEnd);
        Assert.Equal(1_100m, result.FinalEquity);
        Assert.Equal("Explicit end-of-window liquidation", result.Trades[^1].Reason);
    }

    [Fact]
    public void LargeFee_ScalesQuantityAndNeverMakesCashNegative()
    {
        var result = PortfolioLedger.Simulate(Series(Bar(1, 100, 100)), [Order(1, 1, TradeSide.Buy, 100)], 1_000m, new(50m));
        Assert.True(result.Cash >= 0);
        Assert.Equal(1_000m / 150m, result.Units);
        Assert.InRange(result.TotalCommission, 1_000m / 3m, 1_000m / 3m + 0.00000000000000000000000001m);
    }

    [Fact]
    public void DuplicateOrImpossibleOrders_AreRejected()
    {
        var bars = Series(Bar(1, 100, 100));
        Assert.Throws<ArgumentException>(() => PortfolioLedger.Simulate(bars, [Order(1, 1, TradeSide.Buy, 100), Order(1, 1, TradeSide.Sell, 100)], 1_000, new()));
        Assert.Throws<InvalidOperationException>(() => PortfolioLedger.Simulate(bars, [Order(1, 1, TradeSide.Sell, 100)], 1_000, new()));
    }

    private static PriceSeries Series(params PriceBar[] bars) => new(bars);
    private static PriceBar Bar(int day, decimal open, decimal close) => new(new DateOnly(2020, 1, day), open, Math.Max(open, close), Math.Min(open, close), close);
    private static ScheduledOrder Order(int signalDay, int executionDay, TradeSide side, decimal open) => new(new(2020, 1, signalDay), new(2020, 1, executionDay), side, open, "test");
}
