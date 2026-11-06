using SignalLedger.Domain.Backtesting;

namespace SignalLedger.Domain.Tests;

public sealed class PriceSeriesAndIndicatorTests
{
    [Fact]
    public void Sma_GoldenVectorAndPeriodOneAreExact()
    {
        var series = Series(1, 2, 3, 4, 5);
        Assert.Equal(new double?[] { null, null, 2, 3, 4 }, Indicators.SimpleMovingAverage(series, 3));
        Assert.Equal(new double?[] { 1, 2, 3, 4, 5 }, Indicators.SimpleMovingAverage(series, 1));
    }

    [Fact]
    public void Ema_UsesSmaSeedAndDocumentedAlpha()
    {
        var result = Indicators.ExponentialMovingAverage(Series(1, 2, 3, 6), 3);
        Assert.Equal(new double?[] { null, null, 2, 4 }, result);
    }

    [Fact]
    public void PeriodLongerThanHistory_ReturnsOnlyMissingValues()
    {
        Assert.All(Indicators.SimpleMovingAverage(Series(1, 2), 3), value => Assert.Null(value));
        Assert.All(Indicators.ExponentialMovingAverage(Series(1, 2), 3), value => Assert.Null(value));
    }

    [Fact]
    public void Warmup_LoadsPriorTradingObservationsButNeverChangesEvaluationStart()
    {
        var series = Series(1, 2, 3, 4, 5);
        var window = series.ForEvaluation(new DateOnly(2020, 1, 4), new DateOnly(2020, 1, 5), 2);
        Assert.Equal(2, window.EvaluationStartIndex);
        Assert.Equal(new DateOnly(2020, 1, 2), window.Bars[0].Date);
        Assert.Equal(new[] { new DateOnly(2020, 1, 4), new DateOnly(2020, 1, 5) }, window.EvaluationBars.Select(x => x.Bar.Date));
        Assert.False(window.HasShortWarmup);
    }

    [Fact]
    public void MissingCalendarDates_AreNotInventedOrCountedAsSessions()
    {
        var series = new PriceSeries([Bar(new(2020, 1, 2), 10), Bar(new(2020, 1, 6), 20)]);
        Assert.Equal(new double?[] { null, 15 }, Indicators.SimpleMovingAverage(series, 2));
        Assert.Equal(2, series.Count);
    }

    [Fact]
    public void Equality_CloseEqualsSmaWithoutFloatingDrift()
    {
        var result = Indicators.SimpleMovingAverage(Series(10, 10, 10), 3);
        Assert.Equal(10d, result[2]);
    }

    [Fact]
    public void DuplicateOrUnorderedSessionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new PriceSeries([Bar(new(2020, 1, 2), 10), Bar(new(2020, 1, 2), 11)]));
    }

    private static PriceSeries Series(params decimal[] closes) => new(closes.Select((close, index) => Bar(new DateOnly(2020, 1, 1).AddDays(index), close)));
    private static PriceBar Bar(DateOnly date, decimal close) => new(date, close, close, close, close);
}
