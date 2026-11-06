using System.Collections.Immutable;

namespace SignalLedger.Domain.Backtesting;

public readonly record struct PriceBar(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long? Volume = null)
{
    public void Validate()
    {
        if (Open <= 0 || High <= 0 || Low <= 0 || Close <= 0) throw new ArgumentException($"OHLC must be positive on {Date:yyyy-MM-dd}.");
        if (High < Math.Max(Math.Max(Open, Close), Low)) throw new ArgumentException($"High is inconsistent on {Date:yyyy-MM-dd}.");
        if (Low > Math.Min(Math.Min(Open, Close), High)) throw new ArgumentException($"Low is inconsistent on {Date:yyyy-MM-dd}.");
        if (Volume < 0) throw new ArgumentException($"Volume cannot be negative on {Date:yyyy-MM-dd}.");
    }
}

public sealed class PriceSeries : IReadOnlyList<PriceBar>
{
    private readonly ImmutableArray<PriceBar> bars;

    public PriceSeries(IEnumerable<PriceBar> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        bars = values.ToImmutableArray();
        for (var i = 0; i < bars.Length; i++)
        {
            bars[i].Validate();
            if (i > 0 && bars[i].Date <= bars[i - 1].Date)
                throw new ArgumentException("Price bars must be strictly ordered by unique session date.", nameof(values));
        }
    }

    public int Count => bars.Length;
    public PriceBar this[int index] => bars[index];
    public IEnumerator<PriceBar> GetEnumerator() => ((IEnumerable<PriceBar>)bars).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public EvaluationSeries ForEvaluation(DateOnly from, DateOnly to, int warmupSessions)
    {
        if (to < from) throw new ArgumentException("Evaluation end must not precede start.");
        if (warmupSessions < 0) throw new ArgumentOutOfRangeException(nameof(warmupSessions));
        var firstEvaluation = -1; var lastEvaluation = -1;
        for (var i = 0; i < Count; i++)
        {
            if (bars[i].Date >= from && bars[i].Date <= to && firstEvaluation < 0) firstEvaluation = i;
            if (bars[i].Date <= to) lastEvaluation = i;
        }
        if (firstEvaluation < 0 || lastEvaluation < firstEvaluation) return new(new PriceSeries([]), 0, from, to, true);
        var loadStart = Math.Max(0, firstEvaluation - warmupSessions);
        var loaded = new PriceSeries(bars.Skip(loadStart).Take(lastEvaluation - loadStart + 1));
        return new(loaded, firstEvaluation - loadStart, from, to, firstEvaluation - loadStart < warmupSessions);
    }
}

public sealed record EvaluationSeries(PriceSeries Bars, int EvaluationStartIndex, DateOnly From, DateOnly To, bool HasShortWarmup)
{
    public IEnumerable<(int Index, PriceBar Bar)> EvaluationBars => Bars.Select((bar, index) => (index, bar)).Where(x => x.bar.Date >= From && x.bar.Date <= To);
}
