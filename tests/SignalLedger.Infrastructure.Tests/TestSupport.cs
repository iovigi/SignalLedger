using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Providers;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Infrastructure.Tests;

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Temporary file-backed SQLite database so several contexts can share it (as in production).</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"signalledger-test-{Guid.NewGuid():N}.db");
    public DbContextOptions<SignalLedgerDbContext> Options { get; }

    private TestDatabase() => Options = new DbContextOptionsBuilder<SignalLedgerDbContext>().UseSqlite($"Data Source={path};Default Timeout=30").Options;

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        return database;
    }

    public SignalLedgerDbContext Context() => new(Options);

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Deterministic provider that returns a bar for every trading session in the requested range and records calls.</summary>
public sealed class RecordingProvider : IMarketDataProvider
{
    public ConcurrentQueue<(string Symbol, DateOnly From, DateOnly To)> Calls { get; } = new();
    public Func<string, DateOnly, DateOnly, IReadOnlyList<DailyBar>>? Override { get; set; }
    public HashSet<DateOnly> Withheld { get; } = [];
    public DateOnly? FirstAvailable { get; set; }
    public TimeSpan Delay { get; set; }
    public string ProviderId => YahooChartAdapter.SourceId;
    public IReadOnlySet<string> SupportedSymbols => YahooChartAdapter.AllowedSymbols;

    public async Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        Calls.Enqueue((symbol, from, to));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (Override is not null) return Override(symbol, from, to);
        var start = FirstAvailable is { } first && first > from ? first : from;
        return UsEquityTradingCalendar.Sessions(start, to).Where(x => !Withheld.Contains(x)).Select(x => Bar(symbol, x, 100 + x.DayNumber % 50)).ToArray();
    }

    public static DailyBar Bar(string symbol, DateOnly date, decimal close, string source = YahooChartAdapter.SourceId) => new()
    {
        Symbol = symbol, SessionDate = date, Open = close, High = close + 1, Low = close - 1, Close = close, Volume = 1_000,
        Currency = "USD", Source = source, UpdatedAtUtc = DateTimeOffset.UnixEpoch, IsFinal = true
    };
}

public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; LastRequest = request; return Task.FromResult(response(request)); }
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
