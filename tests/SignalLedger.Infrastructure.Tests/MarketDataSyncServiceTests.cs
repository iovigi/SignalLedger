using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Providers;

namespace SignalLedger.Infrastructure.Tests;

public sealed class MarketDataSyncServiceTests
{
    [Fact]
    public async Task Sync_RequestsOnlyDatesAfterLatestCachedBarAndAuditsResult()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new SignalLedgerDbContext(new DbContextOptionsBuilder<SignalLedgerDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.DailyBars.Add(Bar(new(2021, 1, 2), 100)); await db.SaveChangesAsync();
        var provider = new FakeProvider([Bar(new(2021, 1, 3), 101), Bar(new(2021, 1, 4), 102)]);

        var result = await new MarketDataSyncService(db, provider, TimeProvider.System).SyncAsync("QQQ", new(2021, 1, 1), new(2021, 1, 4));

        Assert.Equal(new DateOnly(2021, 1, 3), provider.RequestedFrom);
        Assert.Equal(2, result.Added);
        Assert.Equal(3, await db.DailyBars.CountAsync());
        var run = await db.ImportRuns.SingleAsync();
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(2, run.RecordsAdded);
    }

    private static DailyBar Bar(DateOnly date, decimal close) => new() { Symbol = "QQQ", SessionDate = date, Open = close, High = close + 1, Low = close - 1, Close = close, Currency = "USD", Source = "YahooChart", UpdatedAtUtc = DateTimeOffset.UnixEpoch, IsFinal = true };

    private sealed class FakeProvider(IReadOnlyList<DailyBar> bars) : IMarketDataProvider
    {
        public DateOnly RequestedFrom { get; private set; }
        public Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken) { RequestedFrom = from; return Task.FromResult(bars); }
    }
}
