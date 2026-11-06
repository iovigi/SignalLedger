namespace SignalLedger.Domain.Backtesting;

public static class Indicators
{
    public static IReadOnlyList<double?> SimpleMovingAverage(PriceSeries series, int period)
    {
        ValidatePeriod(period);
        var result = new double?[series.Count];
        decimal sum = 0;
        for (var i = 0; i < series.Count; i++)
        {
            sum += series[i].Close;
            if (i >= period) sum -= series[i - period].Close;
            if (i >= period - 1) result[i] = (double)(sum / period);
        }
        return result;
    }

    public static IReadOnlyList<double?> ExponentialMovingAverage(PriceSeries series, int period)
    {
        ValidatePeriod(period);
        var result = new double?[series.Count];
        if (series.Count < period) return result;
        decimal seed = 0;
        for (var i = 0; i < period; i++) seed += series[i].Close;
        var ema = (double)(seed / period);
        result[period - 1] = ema;
        var alpha = 2d / (period + 1d);
        for (var i = period; i < series.Count; i++)
        {
            ema = alpha * (double)series[i].Close + (1d - alpha) * ema;
            result[i] = ema;
        }
        return result;
    }

    private static void ValidatePeriod(int period)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), "Indicator period must be positive.");
    }
}
