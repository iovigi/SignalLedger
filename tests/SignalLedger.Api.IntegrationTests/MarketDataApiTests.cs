using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Providers;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Api.IntegrationTests;

/// <summary>Local stand-in for Yahoo's chart endpoint: synthesizes a bar per trading session in the requested window.</summary>
public sealed class FakeYahooHandler : HttpMessageHandler
{
    public ConcurrentQueue<(string Symbol, DateOnly From, DateOnly To)> Calls { get; } = new();
    public volatile bool Block;
    public HttpStatusCode? FailWith { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Assert.Equal("query1.finance.yahoo.com", uri.Host);
        var symbol = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
        var query = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
        var from = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(long.Parse(query["period1"], CultureInfo.InvariantCulture)).UtcDateTime);
        var to = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(long.Parse(query["period2"], CultureInfo.InvariantCulture)).UtcDateTime).AddDays(-1);
        Calls.Enqueue((symbol, from, to));
        if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (FailWith is { } status) return new HttpResponseMessage(status) { Content = new StringContent("stub failure") };
        var sessions = UsEquityTradingCalendar.Sessions(from, to).ToArray();
        var timestamps = sessions.Select(d => new DateTimeOffset(d.ToDateTime(new TimeOnly(14, 30)), TimeSpan.Zero).ToUnixTimeSeconds());
        var closes = sessions.Select(d => 100 + d.DayNumber % 40).ToArray();
        string Join<T>(IEnumerable<T> values) => string.Join(',', values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
        var json = $$$"""{"chart":{"result":[{"meta":{"currency":"USD","symbol":"{{{symbol}}}"},"timestamp":[{{{Join(timestamps)}}}],"indicators":{"quote":[{"open":[{{{Join(closes)}}}],"high":[{{{Join(closes.Select(c => c + 1))}}}],"low":[{{{Join(closes.Select(c => c - 1))}}}],"close":[{{{Join(closes)}}}],"volume":[{{{Join(closes.Select(_ => 1000))}}}]}],"adjclose":[{"adjclose":[{{{Join(closes)}}}]}]}}],"error":null}}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

public sealed class MarketDataApiTests
{
    private static Dictionary<string, string?> Operator(bool autoSync = false) => new()
    {
        ["SignalLedger:Yahoo:Enabled"] = "true",
        ["SignalLedger:Yahoo:AuthorizedUseConfirmed"] = "true",
        ["SignalLedger:Yahoo:AutoSyncEnabled"] = autoSync ? "true" : "false",
        ["SignalLedger:Yahoo:HistoricalYears"] = "1",
        ["SignalLedger:Administration:AllowHttpSettingsChanges"] = "true",
        ["SignalLedger:Administration:AllowHttpDownloadOperations"] = "true"
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> WaitForJob(HttpClient client, string id, Func<JsonElement, bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds); JsonElement job = default;
        while (DateTime.UtcNow < deadline)
        {
            job = await Json(await client.GetAsync($"/api/market-data/jobs/{id}"));
            if (done(job)) return job;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Job {id} did not reach the expected state: {job}");
    }
    private static bool Finished(JsonElement job) => job.GetProperty("status").GetString() is "Completed" or "Failed" or "Cancelled";

    [Fact]
    public async Task Defaults_AreSafe_NoAdminOperationsAndNoProviderTraffic()
    {
        var yahoo = new FakeYahooHandler();
        using var factory = SignalLedgerApiFactory.ForSettings(new Dictionary<string, string?>(), yahoo);
        using var client = factory.CreateClient();

        var settings = await Json(await client.GetAsync("/api/settings/market-data"));
        Assert.False(settings.GetProperty("yahooEnabled").GetBoolean());
        Assert.False(settings.GetProperty("settingsChangesAllowed").GetBoolean());
        Assert.False(settings.GetProperty("downloadOperationsAllowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/settings/market-data", new { yahooEnabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/market-data/download", new { symbol = "QQQ" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/market-data/sync?symbol=QQQ", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/market-data/repair", new { })).StatusCode);
        Assert.Empty(yahoo.Calls);
    }

    [Fact]
    public async Task ServerRestriction_OverridesUi_YahooCannotBeEnabledWithoutAttestation()
    {
        using var factory = SignalLedgerApiFactory.ForSettings(new Dictionary<string, string?> { ["SignalLedger:Administration:AllowHttpSettingsChanges"] = "true" });
        using var client = factory.CreateClient();
        using var response = await client.PutAsJsonAsync("/api/settings/market-data", new { yahooEnabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("AuthorizedUseConfirmed", (await Json(response)).GetProperty("detail").GetString());
        // Non-provider settings may still change.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings/market-data", new { syncIntervalMinutes = 120 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings/market-data", new { syncIntervalMinutes = 1 })).StatusCode);
    }

    [Fact]
    public async Task ReadOnlyMode_BlocksEverythingAdministrative()
    {
        var values = Operator(); values["SignalLedger:ReadOnlyMode"] = "true";
        using var factory = SignalLedgerApiFactory.ForSettings(values);
        using var client = factory.CreateClient();
        var settings = await Json(await client.GetAsync("/api/settings/market-data"));
        Assert.False(settings.GetProperty("yahooEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/settings/market-data", new { yahooEnabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/market-data/download", new { symbol = "QQQ" })).StatusCode);
    }

    [Fact]
    public async Task UiSettings_ApplyImmediately_AndPersistAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"signalledger-settings-{Guid.NewGuid():N}");
        var values = Operator(); values["SignalLedger:Yahoo:Enabled"] = "false"; values["SignalLedger:Jobs:DisableWorker"] = "true";
        try
        {
            using (var factory = SignalLedgerApiFactory.ForPersistentDirectory(directory, false, values))
            using (var client = factory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/market-data/download", new { symbol = "QQQ" })).StatusCode);
                var updated = await Json(await client.PutAsJsonAsync("/api/settings/market-data", new { yahooEnabled = true, historicalYears = 5 }));
                Assert.True(updated.GetProperty("yahooEnabled").GetBoolean());
                Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/market-data/download", new { symbol = "QQQ" })).StatusCode);
            }
            using (var restarted = SignalLedgerApiFactory.ForPersistentDirectory(directory, true, values))
            using (var client = restarted.CreateClient())
            {
                var settings = await Json(await client.GetAsync("/api/settings/market-data"));
                Assert.True(settings.GetProperty("yahooEnabled").GetBoolean());
                Assert.Equal(5, settings.GetProperty("historicalYears").GetInt32());
                Assert.Equal("database", settings.GetProperty("valueSources").GetProperty("Yahoo:Enabled").GetString());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Download_RunsInTheBackground_ThenSyncNeedsNoProviderRequest()
    {
        var yahoo = new FakeYahooHandler();
        using var factory = SignalLedgerApiFactory.ForSettings(Operator(), yahoo);
        using var client = factory.CreateClient();

        using var accepted = await client.PostAsJsonAsync("/api/market-data/download", new { symbol = "QQQ" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await Json(accepted)).GetProperty("jobs")[0].GetProperty("id").GetString()!;
        var job = await WaitForJob(client, id, Finished);
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        Assert.True(job.GetProperty("recordsAdded").GetInt32() > 240);
        Assert.Equal(1.0, job.GetProperty("progress").GetDouble());

        var candles = await Json(await client.GetAsync("/api/market-data/QQQ?pageSize=10"));
        Assert.Equal(job.GetProperty("recordsAdded").GetInt32(), candles.GetProperty("total").GetInt32());
        Assert.Equal(10, candles.GetProperty("items").GetArrayLength());
        var status = await Json(await client.GetAsync("/api/market-data/status"));
        var qqq = status.GetProperty("instruments").EnumerateArray().Single(x => x.GetProperty("symbol").GetString() == "QQQ");
        Assert.True(qqq.GetProperty("upToDate").GetBoolean());
        Assert.Equal(0, qqq.GetProperty("missingSessions").GetInt32());
        Assert.Equal(UsEquityTradingCalendar.LatestCompletedSession(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd"), qqq.GetProperty("latestCachedDate").GetString());

        var callsAfterDownload = yahoo.Calls.Count;
        using var syncAccepted = await client.PostAsJsonAsync("/api/market-data/sync", new { symbol = "QQQ" });
        var sync = await WaitForJob(client, (await Json(syncAccepted)).GetProperty("jobs")[0].GetProperty("id").GetString()!, Finished);
        Assert.Equal("Completed", sync.GetProperty("status").GetString());
        Assert.Equal(0, sync.GetProperty("providerRequests").GetInt32());
        Assert.Equal(callsAfterDownload, yahoo.Calls.Count);
        Assert.All(yahoo.Calls, x => Assert.Equal("QQQ", x.Symbol));
    }

    [Fact]
    public async Task DuplicateJobs_ArePrevented_AndCancellationWorks()
    {
        var yahoo = new FakeYahooHandler { Block = true };
        using var factory = SignalLedgerApiFactory.ForSettings(Operator(), yahoo);
        using var client = factory.CreateClient();
        var first = await Json(await client.PostAsJsonAsync("/api/market-data/download", new { symbol = "^NDX" }));
        var id = first.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        await WaitForJob(client, id, x => x.GetProperty("status").GetString() == "Running");

        var second = await Json(await client.PostAsJsonAsync("/api/market-data/sync", new { symbol = "^NDX" }));
        Assert.Equal(id, second.GetProperty("jobs")[0].GetProperty("id").GetString());
        Assert.Equal("^NDX", second.GetProperty("duplicatesOf")[0].GetString());

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/market-data/jobs/{id}/cancel", null)).StatusCode);
        var finished = await WaitForJob(client, id, Finished);
        Assert.True(finished.GetProperty("status").GetString() == "Cancelled", finished.ToString());
    }

    [Fact]
    public async Task DisablingYahoo_CancelsRunningDownloads()
    {
        var yahoo = new FakeYahooHandler { Block = true };
        using var factory = SignalLedgerApiFactory.ForSettings(Operator(), yahoo);
        using var client = factory.CreateClient();
        var id = (await Json(await client.PostAsJsonAsync("/api/market-data/download", new { symbol = "^GSPC" }))).GetProperty("jobs")[0].GetProperty("id").GetString()!;
        await WaitForJob(client, id, x => x.GetProperty("status").GetString() == "Running");

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings/market-data", new { yahooEnabled = false })).StatusCode);

        var finished = await WaitForJob(client, id, Finished);
        Assert.True(finished.GetProperty("status").GetString() == "Cancelled", finished.ToString());
        var callsBefore = yahoo.Calls.Count;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/market-data/download", new { symbol = "^GSPC" })).StatusCode);
        Assert.Equal(callsBefore, yahoo.Calls.Count);
    }

    [Fact]
    public async Task ProviderFailure_FailsTheJob_AndLeavesCachedPricesIntact()
    {
        var yahoo = new FakeYahooHandler { FailWith = HttpStatusCode.NotFound };
        using var factory = SignalLedgerApiFactory.ForSettings(Operator(), yahoo);
        using var client = factory.CreateClient();
        await SeedAsync(factory, "QQQ", UsEquityTradingCalendar.Sessions(new(2025, 1, 2), new(2025, 6, 30)));

        var id = (await Json(await client.PostAsJsonAsync("/api/market-data/sync", new { symbol = "QQQ" }))).GetProperty("jobs")[0].GetProperty("id").GetString()!;
        var job = await WaitForJob(client, id, Finished);

        Assert.Equal("Failed", job.GetProperty("status").GetString());
        Assert.Contains("404", job.GetProperty("error").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
        Assert.Equal(UsEquityTradingCalendar.Sessions(new(2025, 1, 2), new(2025, 6, 30)).Count(), await db.DailyBars.CountAsync(x => x.Symbol == "QQQ"));
        Assert.Contains("404", (await db.MarketDataSyncStates.SingleAsync(x => x.Symbol == "QQQ")).LastError);
    }

    [Fact]
    public async Task InterruptedJob_IsRecoveredOnStartup_AndRequestsOnlyMissingSessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"signalledger-recovery-{Guid.NewGuid():N}");
        var latest = UsEquityTradingCalendar.LatestCompletedSession(DateTimeOffset.UtcNow);
        var cachedThrough = UsEquityTradingCalendar.PreviousSession(UsEquityTradingCalendar.PreviousSession(latest));
        Guid jobId;
        try
        {
            var stopped = Operator(); stopped["SignalLedger:Jobs:DisableWorker"] = "true";
            using (var first = SignalLedgerApiFactory.ForPersistentDirectory(directory, false, stopped))
            {
                using var _ = first.CreateClient();
                await SeedAsync(first, "^IXIC", UsEquityTradingCalendar.Sessions(latest.AddYears(-1), cachedThrough));
                await using var scope = first.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
                var job = new MarketDataJob { Type = MarketDataJobType.Sync, Symbol = "^IXIC", Source = YahooChartAdapter.SourceId, Status = RunStatus.Running, LeaseOwner = "dead-process", CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5), StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                db.MarketDataJobs.Add(job); await db.SaveChangesAsync(); jobId = job.Id;
            }

            var yahoo = new FakeYahooHandler();
            using var restarted = SignalLedgerApiFactory.ForPersistentDirectory(directory, true, Operator(), yahoo);
            using var client = restarted.CreateClient();
            var recovered = await WaitForJob(client, jobId.ToString(), Finished);

            Assert.Equal("Completed", recovered.GetProperty("status").GetString());
            Assert.Equal(2, recovered.GetProperty("recordsAdded").GetInt32());
            var call = Assert.Single(yahoo.Calls);
            Assert.True(call.From >= cachedThrough, $"Recovered job re-requested history from {call.From}.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AutoSync_QueuesWorkOnlyForInstrumentsMissingSessions()
    {
        var values = Operator(autoSync: true); values["SignalLedger:Jobs:DisableWorker"] = "true";
        using var factory = SignalLedgerApiFactory.ForSettings(values);
        using var _ = factory.CreateClient();
        var latest = UsEquityTradingCalendar.LatestCompletedSession(DateTimeOffset.UtcNow);
        await SeedAsync(factory, "QQQ", UsEquityTradingCalendar.Sessions(latest.AddYears(-1), latest));                                   // current
        await SeedAsync(factory, "^NDX", UsEquityTradingCalendar.Sessions(latest.AddYears(-1), UsEquityTradingCalendar.PreviousSession(latest))); // one session behind
        var settings = factory.Services.GetRequiredService<MarketDataSettingsService>();
        var auto = new MarketDataAutoSyncService(factory.Services.GetRequiredService<IServiceScopeFactory>(), settings, factory.Services.GetRequiredService<IConfiguration>(), TimeProvider.System, NullLogger<MarketDataAutoSyncService>.Instance);

        var queued = await auto.RunOnceAsync(await settings.GetAsync(), CancellationToken.None);
        var again = await auto.RunOnceAsync(await settings.GetAsync(), CancellationToken.None);

        var job = Assert.Single(queued);
        Assert.Equal(("^NDX", MarketDataJobTrigger.Automatic), (job.Symbol, job.Trigger));
        Assert.Empty(again); // duplicate prevented while the first job is pending
    }

    [Fact]
    public async Task Backtests_UseCachedProviderData_WhileYahooIsDisabled()
    {
        using var factory = new SignalLedgerApiFactory(); // Yahoo disabled, any outbound request would throw
        using var client = factory.CreateClient();
        await SeedAsync(factory, "^NDX", UsEquityTradingCalendar.Sessions(new(2024, 1, 2), new(2024, 12, 31)));

        using var ok = await client.PostAsJsonAsync("/api/backtests", Backtest("2024-02-01", "2024-12-31"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("YahooChart", (await Json(ok)).GetProperty("source").GetString());

        using var missing = await client.PostAsJsonAsync("/api/backtests", Backtest("2019-01-01", "2019-12-31"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("2024-01-02 to 2024-12-31", (await Json(missing)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task HealthAndCandleValidation()
    {
        using var factory = new SignalLedgerApiFactory();
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("healthy", (await Json(health)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/market-data/QQQ?pageSize=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/market-data/NOPE")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/market-data/instruments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/market-data/jobs")).StatusCode);
    }

    private static object Backtest(string from, string to) => new { symbol = "^NDX", source = "YahooChart", startDate = from, endDate = to, initialCapital = 1000, strategy = new { type = "SMA", period = 20 }, execution = new { commissionPercentPerSide = 0.1, slippageBpsPerSide = 0, liquidateAtEnd = false, cashYieldMode = "None" } };

    private static async Task SeedAsync(SignalLedgerApiFactory factory, string symbol, IEnumerable<DateOnly> sessions)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
        db.DailyBars.AddRange(sessions.Select((d, i) => new DailyBar { Symbol = symbol, SessionDate = d, Open = 100 + i % 7, High = 110 + i % 7, Low = 95, Close = 101 + i % 9, Volume = 1, Currency = "USD", Source = YahooChartAdapter.SourceId, UpdatedAtUtc = DateTimeOffset.UnixEpoch, IsFinal = true }));
        await db.SaveChangesAsync();
    }
}
