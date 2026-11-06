using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Infrastructure.Tests;

public sealed class MarketDataCoverageServiceTests
{
    [Fact]
    public async Task Coverage_ReportsDatesSourcesAndMissingWeekdays()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new SignalLedgerDbContext(new DbContextOptionsBuilder<SignalLedgerDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Instruments.Add(new Instrument { Symbol = "SYNTH", DisplayName = "Synthetic", AssetClass = AssetClass.Etf, Currency = "USD" });
        db.DailyBars.AddRange(Bar(new(2026, 11, 2)), Bar(new(2026, 11, 3)), Bar(new(2026, 11, 5)), Bar(new(2026, 11, 6)));
        await db.SaveChangesAsync();

        var result = await new MarketDataCoverageService(db).GetAsync("synth");

        var source = Assert.Single(result!.Sources);
        Assert.Equal(new DateOnly(2026, 11, 2), source.FirstDate);
        Assert.Equal(new DateOnly(2026, 11, 6), source.LastDate);
        Assert.Equal(4, source.BarCount);
        Assert.Equal(new DateOnly(2026, 11, 4), Assert.Single(source.MissingSessions));
    }

    [Theory]
    [InlineData(2026, 11, 26)]
    [InlineData(2026, 4, 3)]
    [InlineData(2025, 1, 9)]
    public void Calendar_ExcludesExchangeHolidaysAndExceptionalClosures(int year, int month, int day)
    {
        Assert.False(UsEquityTradingCalendar.IsTradingSession(new(year, month, day)));
    }

    private static DailyBar Bar(DateOnly date) => new() { Symbol = "SYNTH", SessionDate = date, Open = 10, High = 11, Low = 9, Close = 10, Currency = "USD", Source = "Synthetic", UpdatedAtUtc = DateTimeOffset.UnixEpoch, IsFinal = true };
}
