using SignalLedger.Domain.Backtesting;

namespace SignalLedger.Domain.Tests;

public sealed class SignalSchedulerTests
{
    [Fact]
    public void SignalAtClose_ExecutesOnlyAtNextAvailableOpen()
    {
        var evaluation = Window(Bar(2, 10, 11), Bar(5, 12, 13));
        var result = SignalScheduler.ScheduleCloseVsIndicator(evaluation, [9, 9]);
        var order = Assert.Single(result.Orders);
        Assert.Equal(new DateOnly(2020, 1, 2), order.SignalDate);
        Assert.Equal(new DateOnly(2020, 1, 5), order.ExecutionDate);
        Assert.Equal(12m, order.ReferenceOpen);
    }

    [Fact]
    public void PersistentDesiredState_DoesNotCreateDuplicateOrders()
    {
        var evaluation = Window(Bar(1, 10, 11), Bar(2, 12, 13), Bar(3, 14, 15), Bar(4, 16, 17));
        var result = SignalScheduler.ScheduleCloseVsIndicator(evaluation, [9, 9, 9, 9]);
        Assert.Single(result.Signals);
        Assert.Single(result.Orders);
    }

    [Fact]
    public void Equality_RetainsPriorDesiredState()
    {
        var evaluation = Window(Bar(1, 10, 11), Bar(2, 12, 12), Bar(3, 13, 14));
        var result = SignalScheduler.ScheduleCloseVsIndicator(evaluation, [9, 12, 20]);
        Assert.Equal(2, result.Signals.Count);
        Assert.Equal(TradeSide.Buy, result.Orders[0].Side);
        Assert.Equal(PositionState.Cash, result.UnfilledSignal?.DesiredState);
    }

    [Fact]
    public void LastDaySignal_RemainsUnfilledWithoutFabricatedFutureSession()
    {
        var evaluation = Window(Bar(1, 10, 11));
        var result = SignalScheduler.ScheduleCloseVsIndicator(evaluation, [9]);
        Assert.Empty(result.Orders);
        Assert.NotNull(result.UnfilledSignal);
    }

    [Fact]
    public void WarmupObservations_CannotGeneratePreEvaluationSignals()
    {
        var series = new PriceSeries([Bar(1, 10, 11), Bar(2, 12, 13), Bar(3, 14, 15)]);
        var evaluation = series.ForEvaluation(new(2020, 1, 3), new(2020, 1, 3), 2);
        var result = SignalScheduler.ScheduleCloseVsIndicator(evaluation, [1, 1, 1]);
        Assert.Single(result.Signals);
        Assert.Equal(new DateOnly(2020, 1, 3), result.Signals[0].SignalDate);
    }

    [Fact]
    public void AlternatingSignals_DoNotOverlapPendingOrders()
    {
        var evaluation = Window(Bar(1, 10, 11), Bar(2, 10, 9), Bar(3, 10, 11), Bar(4, 10, 9));
        var result = SignalScheduler.ScheduleCloseVsIndicator(evaluation, [10, 10, 10, 10]);
        Assert.Equal(4, result.Signals.Count);
        Assert.Equal(3, result.Orders.Count);
        Assert.All(result.Orders, order => Assert.True(order.ExecutionDate > order.SignalDate));
    }

    private static EvaluationSeries Window(params PriceBar[] bars)
    {
        var series = new PriceSeries(bars);
        return series.ForEvaluation(bars[0].Date, bars[^1].Date, 0);
    }
    private static PriceBar Bar(int day, decimal open, decimal close) => new(new DateOnly(2020, 1, day), open, Math.Max(open, close), Math.Min(open, close), close);
}
