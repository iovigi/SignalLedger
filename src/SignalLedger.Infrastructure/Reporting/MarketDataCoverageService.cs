using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Infrastructure.Reporting;

public sealed record SourceCoverage(
    string Source,
    DateOnly FirstDate,
    DateOnly LastDate,
    int BarCount,
    IReadOnlyList<DateOnly> MissingSessions,
    DateTimeOffset LastUpdatedUtc);

public sealed record InstrumentCoverage(
    string Symbol,
    string DisplayName,
    AssetClass AssetClass,
    string Currency,
    PriceSeriesMeaning PriceSeriesMeaning,
    bool Tradable,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SourceCoverage> Sources);

public sealed class MarketDataCoverageService(SignalLedgerDbContext dbContext)
{
    public async Task<IReadOnlyList<InstrumentCoverage>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var instruments = await dbContext.Instruments.AsNoTracking().OrderBy(x => x.Symbol).ToListAsync(cancellationToken);
        var bars = await dbContext.DailyBars.AsNoTracking().OrderBy(x => x.SessionDate).ToListAsync(cancellationToken);
        return instruments.Select(instrument => Build(instrument, bars.Where(x => x.Symbol == instrument.Symbol))).ToArray();
    }

    public async Task<InstrumentCoverage?> GetAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        var instrument = await dbContext.Instruments.AsNoTracking().SingleOrDefaultAsync(x => x.Symbol == normalized, cancellationToken);
        if (instrument is null) return null;
        var bars = await dbContext.DailyBars.AsNoTracking().Where(x => x.Symbol == normalized).OrderBy(x => x.SessionDate).ToListAsync(cancellationToken);
        return Build(instrument, bars);
    }

    private static InstrumentCoverage Build(Instrument instrument, IEnumerable<DailyBar> allBars)
    {
        var sources = allBars.GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var ordered = group.OrderBy(x => x.SessionDate).ToArray();
            var dates = ordered.Select(x => x.SessionDate).ToHashSet();
            var missing = ExpectedSessions(ordered[0].SessionDate, ordered[^1].SessionDate).Where(x => !dates.Contains(x)).ToArray();
            return new SourceCoverage(group.Key, ordered[0].SessionDate, ordered[^1].SessionDate, ordered.Length, missing, ordered.Max(x => x.UpdatedAtUtc));
        }).OrderBy(x => x.Source).ToArray();
        var isIndex = instrument.AssetClass == AssetClass.Index;
        var warnings = isIndex
            ? new[] { "Non-tradable price index; any fills are theoretical.", "Daily OHLC values are not historical Bid/Ask quotes." }
            : new[] { "Raw OHLC price return excludes distributions unless explicitly stated otherwise." };
        return new(instrument.Symbol, instrument.DisplayName, instrument.AssetClass, instrument.Currency, instrument.PriceSeriesMeaning, !isIndex, warnings, sources);
    }

    private static IEnumerable<DateOnly> ExpectedSessions(DateOnly first, DateOnly last)
    {
        for (var date = first; date <= last; date = date.AddDays(1))
            if (UsEquityTradingCalendar.IsTradingSession(date))
                yield return date;
    }
}
