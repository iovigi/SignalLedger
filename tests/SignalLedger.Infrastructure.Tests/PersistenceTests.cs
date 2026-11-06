using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Infrastructure.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task UniqueBarKey_RejectsDuplicateSymbolDateAndSource()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SignalLedgerDbContext>().UseSqlite(connection).Options;
        await using var db = new SignalLedgerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.DailyBars.Add(CreateBar()); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); db.DailyBars.Add(CreateBar());
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task FileDatabase_PersistsAcrossContextRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"signalledger-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<SignalLedgerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var first = new SignalLedgerDbContext(options)) { await first.Database.EnsureCreatedAsync(); first.DailyBars.Add(CreateBar()); await first.SaveChangesAsync(); }
            await using (var second = new SignalLedgerDbContext(options))
            {
                Assert.Equal(1, await second.DailyBars.CountAsync());
            }
        }
        finally { File.Delete(path); }
    }

    private static DailyBar CreateBar() => new() { Symbol = "SYNTH", SessionDate = new(2020, 1, 2), Open = 100m, High = 102m, Low = 99m, Close = 101m, Currency = "USD", Source = "Synthetic", UpdatedAtUtc = DateTimeOffset.UnixEpoch, IsFinal = true };
}
