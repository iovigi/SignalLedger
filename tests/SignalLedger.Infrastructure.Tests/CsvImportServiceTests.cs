using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Imports;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Infrastructure.Tests;

public sealed class CsvImportServiceTests
{
    [Fact]
    public async Task ThirtyYearLikeSyntheticFile_ImportsAndAuditsEveryRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var csv = new StringBuilder("Date,Open,High,Low,Close,Adj Close,Volume\n");
        var start = new DateOnly(1996, 1, 2);
        for (var i = 0; i < 7_560; i++)
        {
            var date = start.AddDays(i);
            csv.Append(CultureInvariant(date)).Append(',').Append(100 + i / 100m).Append(',').Append(102 + i / 100m)
                .Append(',').Append(99 + i / 100m).Append(',').Append(101 + i / 100m).Append(',').Append(101 + i / 100m).Append(",1000\n");
        }

        var result = await fixture.Service.ImportAsync(new StringReader(csv.ToString()), Request());

        Assert.Equal(7_560, result.Added);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(7_560, await fixture.Db.DailyBars.CountAsync());
        var run = await fixture.Db.ImportRuns.SingleAsync();
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(7_560, run.RecordsAdded);
    }

    [Fact]
    public async Task MalformedRows_AreRejectedWithActionableFieldAndRowErrors()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string csv = "Date,Open,High,Low,Close,Volume\n" +
                           "not-a-date,100,101,99,100,10\n" +
                           "2020-01-03,100,90,99,100,10\n" +
                           "2020-01-04,100,101,99,wat,10\n" +
                           "2020-01-05,100,101,99,100,-1\n";

        var result = await fixture.Service.ImportAsync(new StringReader(csv), Request());

        Assert.Equal(0, result.Added);
        Assert.Equal(4, result.Rejected);
        Assert.Contains(result.Errors, x => x.Row == 2 && x.Field == "Date" && x.Message.Contains("yyyy-MM-dd", StringComparison.Ordinal));
        Assert.Contains(result.Errors, x => x.Row == 3 && x.Field == "High");
        Assert.Contains(result.Errors, x => x.Row == 4 && x.Field == "Close");
        Assert.Contains(result.Errors, x => x.Row == 5 && x.Field == "Volume");
        Assert.Empty(fixture.Db.DailyBars);
    }

    [Fact]
    public async Task Reimport_UpdatesExistingBarWithoutDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ImportAsync(new StringReader("Date,Open,High,Low,Close\n2020-01-02,10,12,9,11\n"), Request());
        fixture.Db.ChangeTracker.Clear();
        var result = await fixture.Service.ImportAsync(new StringReader("Date,Open,High,Low,Close\n2020-01-02,10,13,9,12\n"), Request());

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, await fixture.Db.DailyBars.CountAsync());
        Assert.Equal(12m, (await fixture.Db.DailyBars.SingleAsync()).Close);
    }

    [Fact]
    public async Task CustomFieldMap_MapsOwnedCsvHeaders()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request() with { Fields = new("session", "o", "h", "l", "c", "adjusted", "vol") };
        var result = await fixture.Service.ImportAsync(new StringReader("session,o,h,l,c,vol\n2020-01-02,10,12,9,11,42\n"), request);
        Assert.Equal(1, result.Added);
        Assert.Equal(42, (await fixture.Db.DailyBars.SingleAsync()).Volume);
    }

    private static CsvImportRequest Request() => new("SYNTH", "UserCsv", "USD");
    private static string CultureInvariant(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public SignalLedgerDbContext Db { get; }
        public CsvImportService Service { get; }

        private Fixture(SqliteConnection connection, SignalLedgerDbContext db) { this.connection = connection; Db = db; Service = new(db, TimeProvider.System); }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new SignalLedgerDbContext(new DbContextOptionsBuilder<SignalLedgerDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(); return new(connection, db);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
