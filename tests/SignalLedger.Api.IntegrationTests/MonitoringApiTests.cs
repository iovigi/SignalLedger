using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalLedger.Api.Monitoring;

namespace SignalLedger.Api.IntegrationTests;

public sealed class MonitoringApiTests
{
    private const string Key = "test-monitoring-key-0123456789abcdef0123456789";

    private static Dictionary<string, string?> Monitoring(bool enabled = true, string key = Key) => new()
    {
        ["SignalLedger:Monitoring:Enabled"] = enabled ? "true" : "false",
        ["SignalLedger:Monitoring:ApiKey"] = key
    };

    private static HttpRequestMessage Get(string url, string? key = Key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (key is not null) request.Headers.Add(MonitoringEndpoints.HeaderName, key);
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static string[] FindingCodes(JsonElement snapshot) => snapshot.GetProperty("findings").EnumerateArray().Select(x => x.GetProperty("code").GetString()!).ToArray();

    [Fact]
    public async Task Disabled_ByDefault_LooksLikeUnknownApiRoute_EvenWithTheConfiguredKey()
    {
        using var factory = new SignalLedgerApiFactory();
        using var client = factory.CreateClient();
        // The checked-in key must not open anything while the feature flag is off.
        var shippedKey = factory.Services.GetRequiredService<IConfiguration>()["SignalLedger:Monitoring:ApiKey"];
        Assert.False(string.IsNullOrWhiteSpace(shippedKey));

        foreach (var url in new[] { "/api/monitoring", "/api/monitoring/logs", "/api/monitoring/jobs", "/api/monitoring/settings" })
        {
            using var response = await client.SendAsync(Get(url, shippedKey));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("API endpoint not found", (await Json(response)).GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task Enabled_WithTooShortKey_FailsClosed()
    {
        using var factory = SignalLedgerApiFactory.ForSettings(Monitoring(key: "short"));
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Get("/api/monitoring", "short"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Enabled_RequiresTheExactKey_ViaHeaderOrBearer()
    {
        using var factory = SignalLedgerApiFactory.ForSettings(Monitoring());
        using var client = factory.CreateClient();

        using (var missing = await client.SendAsync(Get("/api/monitoring", key: null))) Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using (var wrong = await client.SendAsync(Get("/api/monitoring", Key + "x"))) Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using (var ok = await client.SendAsync(Get("/api/monitoring")))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("no-store", ok.Headers.CacheControl?.ToString());
        }
        var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/monitoring/logs");
        bearer.Headers.Authorization = new("Bearer", Key);
        using (var viaBearer = await client.SendAsync(bearer)) Assert.Equal(HttpStatusCode.OK, viaBearer.StatusCode);
    }

    [Fact]
    public async Task FeatureFlag_TogglesAtRuntime_WithoutRestart()
    {
        using var factory = SignalLedgerApiFactory.ForSettings(Monitoring(enabled: false));
        using var client = factory.CreateClient();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        using (var off = await client.SendAsync(Get("/api/monitoring"))) Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        configuration["SignalLedger:Monitoring:Enabled"] = "true";
        using (var on = await client.SendAsync(Get("/api/monitoring"))) Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        configuration["SignalLedger:Monitoring:Enabled"] = "false";
        using (var offAgain = await client.SendAsync(Get("/api/monitoring"))) Assert.Equal(HttpStatusCode.NotFound, offAgain.StatusCode);
    }

    [Fact]
    public async Task Snapshot_ExplainsWhyYahooIsMissing_InPublicDemoMode_AndNeverLeaksTheKey()
    {
        var settings = Monitoring();
        settings["SignalLedger:PublicDemoMode"] = "true";
        settings["SignalLedger:Yahoo:Enabled"] = "true";
        settings["SignalLedger:Yahoo:AuthorizedUseConfirmed"] = "true";
        using var factory = SignalLedgerApiFactory.ForSettings(settings);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Get("/api/monitoring"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Key, body);
        var snapshot = JsonDocument.Parse(body).RootElement;

        var codes = FindingCodes(snapshot);
        Assert.Contains("yahoo.blocked.read-only", codes);
        Assert.Contains("yahoo.no-cached-data", codes);
        Assert.Equal("error", snapshot.GetProperty("findings")[0].GetProperty("severity").GetString()!.ToLowerInvariant());

        var values = snapshot.GetProperty("configuration").GetProperty("values").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x);
        Assert.StartsWith("[redacted", values["SignalLedger:Monitoring:ApiKey"].GetProperty("value").GetString());
        Assert.Equal("True", values["SignalLedger:PublicDemoMode"].GetProperty("value").GetString(), ignoreCase: true);
        Assert.Contains("MemoryConfigurationProvider", values["SignalLedger:PublicDemoMode"].GetProperty("provider").GetString());

        Assert.True(snapshot.GetProperty("marketData").GetProperty("effectiveSettings").GetProperty("readOnlyMode").GetBoolean());
        Assert.Contains(snapshot.GetProperty("workers").EnumerateArray(), x => x.GetProperty("name").GetString() == "MarketDataJobWorker");
        Assert.Empty(snapshot.GetProperty("database").GetProperty("pendingMigrations").EnumerateArray());
        Assert.True(snapshot.GetProperty("database").GetProperty("rowCounts").GetProperty("Instruments").GetInt32() > 0);
    }

    [Fact]
    public async Task Snapshot_FlagsThatTheInitialDownloadIsLocked_WhenHttpDownloadsAreDisabled()
    {
        var settings = Monitoring();
        settings["SignalLedger:Yahoo:Enabled"] = "true";
        settings["SignalLedger:Yahoo:AuthorizedUseConfirmed"] = "true";
        settings["SignalLedger:Administration:AllowHttpDownloadOperations"] = "false";
        using var factory = SignalLedgerApiFactory.ForSettings(settings);
        using var client = factory.CreateClient();

        var codes = FindingCodes(await Json(await client.SendAsync(Get("/api/monitoring"))));

        Assert.Contains("yahoo.enabled", codes);
        Assert.Contains("yahoo.initial-download-blocked", codes);
        Assert.DoesNotContain("yahoo.blocked.read-only", codes);
    }

    [Fact]
    public async Task AfterADownload_ReportsCoverageJobsAndWorkerLogs_WithoutContactingTheProvider()
    {
        var yahoo = new FakeYahooHandler();
        var settings = Monitoring();
        settings["SignalLedger:Yahoo:Enabled"] = "true";
        settings["SignalLedger:Yahoo:AuthorizedUseConfirmed"] = "true";
        settings["SignalLedger:Yahoo:HistoricalYears"] = "1";
        settings["SignalLedger:Administration:AllowHttpDownloadOperations"] = "true";
        using var factory = SignalLedgerApiFactory.ForSettings(settings, yahoo);
        using var client = factory.CreateClient();

        using var queued = await client.PostAsync("/api/market-data/download?symbol=QQQ", null);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var jobId = (await Json(queued)).GetProperty("jobs")[0].GetProperty("id").GetString();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await Json(await client.GetAsync($"/api/market-data/jobs/{jobId}"))).GetProperty("status").GetString() != "Completed")
        {
            Assert.True(DateTime.UtcNow < deadline, "Download job did not complete.");
            await Task.Delay(100);
        }
        var providerCalls = yahoo.Calls.Count;

        var snapshot = await Json(await client.SendAsync(Get("/api/monitoring?logLevel=Debug&logTake=1000")));
        var qqq = snapshot.GetProperty("marketData").GetProperty("instruments").EnumerateArray().Single(x => x.GetProperty("symbol").GetString() == "QQQ");
        var yahooSource = qqq.GetProperty("sources").EnumerateArray().Single(x => x.GetProperty("source").GetString() == "YahooChart");
        Assert.True(yahooSource.GetProperty("bars").GetInt32() > 200);
        Assert.True(qqq.GetProperty("yahoo").GetProperty("incrementalPlan").GetProperty("upToDate").GetBoolean());
        Assert.DoesNotContain("yahoo.no-cached-data", FindingCodes(snapshot));
        Assert.Contains(snapshot.GetProperty("jobs").GetProperty("marketData").GetProperty("recent").EnumerateArray(), x => x.GetProperty("id").GetString() == jobId && x.GetProperty("providerRequests").GetInt32() > 0);
        var worker = snapshot.GetProperty("workers").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "MarketDataJobWorker");
        Assert.Contains("finished as Completed", worker.GetProperty("lastActivity").GetString());

        var logs = await Json(await client.SendAsync(Get("/api/monitoring/logs?category=Providers&take=5000")));
        var messages = logs.GetProperty("entries").EnumerateArray().Select(x => x.GetProperty("message").GetString()!).ToArray();
        Assert.Contains(messages, x => x.StartsWith("Yahoo Chart request QQQ", StringComparison.Ordinal));
        Assert.Contains(messages, x => x.Contains($"Market-data job {jobId} QQQ completed", StringComparison.OrdinalIgnoreCase));
        var structured = logs.GetProperty("entries").EnumerateArray().First(x => x.GetProperty("message").GetString()!.StartsWith("Yahoo Chart request", StringComparison.Ordinal));
        Assert.Equal("QQQ", structured.GetProperty("properties").GetProperty("Symbol").GetString());

        var failedOnly = await Json(await client.SendAsync(Get("/api/monitoring/jobs?status=Completed&symbol=qqq")));
        Assert.Contains(failedOnly.EnumerateArray(), x => x.GetProperty("id").GetString() == jobId);

        Assert.Equal(providerCalls, yahoo.Calls.Count); // diagnostics never triggers provider traffic
    }

    [Fact]
    public async Task Settings_ShowEffectiveConfiguredStoredAndRawValues_WithSecretsRedacted()
    {
        var settings = Monitoring();
        settings["SignalLedger:Yahoo:Enabled"] = "true";
        settings["SignalLedger:Yahoo:AuthorizedUseConfirmed"] = "true";
        settings["SignalLedger:Yahoo:SyncIntervalMinutes"] = "45";
        settings["SignalLedger:Administration:AllowHttpSettingsChanges"] = "true";
        settings["Some:Other:Password"] = "hunter2-secret-value";
        settings["Some:Storage:Key"] = "storage-secret-value";
        settings["Some:Plain:Setting"] = "visible";
        using var factory = SignalLedgerApiFactory.ForSettings(settings);
        using var client = factory.CreateClient();
        // An operator override stored in SQLite must show up and win over configuration.
        using (var put = await client.PutAsJsonAsync("/api/settings/market-data", new { historicalYears = 7 })) Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var response = await client.SendAsync(Get("/api/monitoring/settings"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Key, body);
        var json = JsonDocument.Parse(body).RootElement;

        var effective = json.GetProperty("marketData").GetProperty("effective");
        Assert.True(effective.GetProperty("yahooEnabled").GetBoolean());
        Assert.Equal(7, effective.GetProperty("historicalYears").GetInt32());
        Assert.Equal("database", effective.GetProperty("valueSources").GetProperty("Yahoo:HistoricalYears").GetString());
        Assert.Contains(json.GetProperty("marketData").GetProperty("storedOverrides").EnumerateArray(), x => x.GetProperty("key").GetString() == "Yahoo:HistoricalYears" && x.GetProperty("value").GetString() == "7");

        var configured = json.GetProperty("configured");
        Assert.Equal(45, configured.GetProperty("yahoo").GetProperty("syncIntervalMinutes").GetInt32());
        Assert.True(configured.GetProperty("administration").GetProperty("allowHttpSettingsChanges").GetBoolean());
        Assert.Equal(Key.Length, configured.GetProperty("monitoring").GetProperty("apiKeyLength").GetInt32());
        Assert.True(configured.GetProperty("monitoring").GetProperty("apiKeyUsable").GetBoolean());

        string? Value(JsonElement root, string key) => root.GetProperty("configuration").GetProperty("values").EnumerateArray().FirstOrDefault(x => x.GetProperty("key").GetString() == key) is { ValueKind: JsonValueKind.Object } v ? v.GetProperty("value").GetString() : null;
        Assert.Equal("45", Value(json, "SignalLedger:Yahoo:SyncIntervalMinutes"));
        Assert.Equal("true", Value(json, "SignalLedger:Yahoo:AuthorizedUseConfirmed")); // diagnostic flags are not mistaken for secrets
        Assert.Null(Value(json, "Some:Plain:Setting")); // outside the application sections unless all=true

        var everything = await client.SendAsync(Get("/api/monitoring/settings?all=true"));
        var allBody = await everything.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2-secret-value", allBody);
        Assert.DoesNotContain("storage-secret-value", allBody);
        Assert.DoesNotContain(Key, allBody);
        var allJson = JsonDocument.Parse(allBody).RootElement;
        Assert.Equal("visible", Value(allJson, "Some:Plain:Setting"));
        Assert.StartsWith("[redacted", Value(allJson, "Some:Other:Password"));
    }

    [Fact]
    public async Task Logs_Validate_Filter_AndTail()
    {
        using var factory = SignalLedgerApiFactory.ForSettings(Monitoring());
        using var client = factory.CreateClient();
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SignalLedger.Tests.Probe");
        var marker = Guid.NewGuid().ToString("N");
        logger.LogWarning("probe warning {Marker}", marker);
        logger.LogInformation("probe info {Marker}", marker);

        using (var invalid = await client.SendAsync(Get("/api/monitoring/logs?minLevel=Loud"))) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var tooMany = await client.SendAsync(Get("/api/monitoring/logs?take=5001"))) Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        var warnings = await Json(await client.SendAsync(Get($"/api/monitoring/logs?minLevel=Warning&contains={marker}")));
        var entry = Assert.Single(warnings.GetProperty("entries").EnumerateArray());
        Assert.Equal("SignalLedger.Tests.Probe", entry.GetProperty("category").GetString());
        Assert.Equal(marker, entry.GetProperty("properties").GetProperty("Marker").GetString());

        var latest = warnings.GetProperty("latestSequence").GetInt64();
        logger.LogError(new InvalidOperationException("boom"), "probe error {Marker}", marker);
        var tail = await Json(await client.SendAsync(Get($"/api/monitoring/logs?afterSequence={latest}&contains={marker}")));
        var error = Assert.Single(tail.GetProperty("entries").EnumerateArray());
        Assert.Contains("InvalidOperationException: boom", error.GetProperty("exception").GetString());
    }

    [Fact]
    public void LogStore_IsBounded_AndKeepsTheNewestEntries()
    {
        var store = new InMemoryLogStore(100);
        for (var i = 0; i < 250; i++) store.Add(DateTimeOffset.UtcNow, LogLevel.Information, "c", default, $"m{i}", null, new Dictionary<string, string?>(), []);

        var result = store.Query(new(Take: 5_000));

        Assert.Equal(100, result.Entries.Count);
        Assert.Equal(150, result.Dropped);
        Assert.Equal("m249", result.Entries[^1].Message);
        Assert.Equal(151, result.OldestRetainedSequence);
    }
}
