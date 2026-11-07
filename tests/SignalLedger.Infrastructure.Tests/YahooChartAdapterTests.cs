using System.Net;
using SignalLedger.Infrastructure.Providers;

namespace SignalLedger.Infrastructure.Tests;

public sealed class YahooChartAdapterTests
{
    // 2021-01-04 and 2021-01-05 regular sessions; timestamps are the 09:30 New York open (14:30 UTC).
    private const string Fixture = """
    {"chart":{"result":[{"meta":{"currency":"USD","symbol":"QQQ"},"timestamp":[1609770600,1609857000],"indicators":{"quote":[{"open":[100.0,101.0],"high":[102.0,103.0],"low":[99.0,100.0],"close":[101.0,102.0],"volume":[1000,1100]}],"adjclose":[{"adjclose":[100.5,101.5]}]}}],"error":null}}
    """;

    private static YahooChartAdapter Adapter(HttpMessageHandler handler, YahooProviderOptions? options = null) =>
        new(new HttpClient(handler), new StaticMarketDataAccessPolicy(options ?? Enabled()));
    private static YahooProviderOptions Enabled() => new() { Enabled = true, AuthorizedUseConfirmed = true, MaximumYears = 40, RetryBaseDelayMilliseconds = 1 };

    [Fact]
    public async Task DisabledProvider_FailsBeforeNetworkAccess()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network must not be called."));
        await Assert.ThrowsAsync<ProviderDisabledException>(() => Adapter(handler, new YahooProviderOptions()).GetDailyBarsAsync("QQQ", new(2020, 1, 1), new(2020, 1, 2), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AuthorizationConfirmation_IsRequiredEvenWhenEnabled()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK));
        var error = await Assert.ThrowsAsync<ProviderDisabledException>(() => Adapter(handler, new YahooProviderOptions { Enabled = true }).GetDailyBarsAsync("QQQ", new(2020, 1, 1), new(2020, 1, 2), default));
        Assert.Contains("authorized", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AuthorizedFixture_ParsesOhlcvAdjustedCloseAndExchangeDates()
    {
        var handler = new StubHandler(_ => StubHandler.Json(Fixture));
        var bars = await Adapter(handler).GetDailyBarsAsync("QQQ", new(2021, 1, 4), new(2021, 1, 5), default);

        Assert.Equal(2, bars.Count);
        Assert.Equal(new DateOnly(2021, 1, 4), bars[0].SessionDate);
        Assert.Equal((100m, 102m, 99m, 101m, 100.5m, 1000L), (bars[0].Open, bars[0].High, bars[0].Low, bars[0].Close, bars[0].AdjustedClose!.Value, bars[0].Volume!.Value));
        Assert.Equal("YahooChart", bars[0].Source);
        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("interval=1d", query, StringComparison.Ordinal);
        // period2 is exclusive and must lie after the last requested session.
        var period2 = long.Parse(query.Split('&').Single(x => x.StartsWith("period2=", StringComparison.Ordinal))["period2=".Length..]);
        Assert.True(DateTimeOffset.FromUnixTimeSeconds(period2) > new DateTimeOffset(2021, 1, 5, 21, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("909.9199829101562", "909.92")]       // ^GSPC 2008-10-09 close as Yahoo sends it
    [InlineData("2237.39990234375", "2237.4")]
    [InlineData("51.15625", "51.15625")]              // exact binary fraction stays exact
    [InlineData("42.982276916503906", "42.982277")]
    [InlineData("101.25", "101.25")]
    [InlineData("1234.567890123", "1234.567890123")]  // not a float32 value: decimal text kept as sent
    public void Float32Prices_AreDecodedToTheIntendedDecimal(string json, string expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(YahooChartAdapter.TryDecimal(document.RootElement, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public async Task BarsOutsideTheRequestedRange_AreDiscarded()
    {
        var bars = await Adapter(new StubHandler(_ => StubHandler.Json(Fixture))).GetDailyBarsAsync("QQQ", new(2021, 1, 5), new(2021, 1, 5), default);
        Assert.Equal(new DateOnly(2021, 1, 5), Assert.Single(bars).SessionDate);
    }

    [Theory]
    [InlineData("""{"chart":{"result":[{"meta":{"symbol":"QQQ"},"timestamp":[1609770600],"indicators":{"quote":[{"open":[100.0],"high":[98.0],"low":[99.0],"close":[101.0],"volume":[1]}]}}],"error":null}}""")]
    [InlineData("""{"chart":{"result":[{"meta":{"symbol":"SPY"},"timestamp":[1609770600],"indicators":{"quote":[{"open":[100.0],"high":[102.0],"low":[99.0],"close":[101.0],"volume":[1]}]}}],"error":null}}""")]
    [InlineData("""{"chart":{"result":[{"meta":{"symbol":"QQQ"},"timestamp":[1609770600,1609857000],"indicators":{"quote":[{"open":[100.0],"high":[102.0],"low":[99.0],"close":[101.0],"volume":[1]}]}}],"error":null}}""")]
    [InlineData("""{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found"}}}""")]
    [InlineData("""{"chart": not json""")]
    [InlineData("""{"unexpected":true}""")]
    public async Task InvalidProviderResponses_AreRejected(string json)
    {
        await Assert.ThrowsAsync<MarketDataProviderException>(() => Adapter(new StubHandler(_ => StubHandler.Json(json))).GetDailyBarsAsync("QQQ", new(2021, 1, 4), new(2021, 1, 5), default));
    }

    [Fact]
    public async Task TransientFailures_RetryWithBoundedAttempts()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("maintenance") });
        var error = await Assert.ThrowsAsync<MarketDataProviderException>(() => Adapter(handler).GetDailyBarsAsync("^NDX", new(2021, 1, 4), new(2021, 1, 5), default));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.True(error.IsTransient);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task TooManyRequests_HonoursShortRetryAfterThenSucceeds()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            if (calls++ > 0) return StubHandler.Json(Fixture);
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return limited;
        });
        Assert.Equal(2, (await Adapter(handler).GetDailyBarsAsync("QQQ", new(2021, 1, 4), new(2021, 1, 5), default)).Count);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task TooManyRequests_WithLongRetryAfter_StopsAndReportsTheDelay()
    {
        var handler = new StubHandler(_ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return limited;
        });
        var error = await Assert.ThrowsAsync<MarketDataProviderException>(() => Adapter(handler).GetDailyBarsAsync("QQQ", new(2021, 1, 4), new(2021, 1, 5), default));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(TimeSpan.FromMinutes(10), error.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    [Fact]
    public async Task NonTransientFailure_IsNotRetried()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.NotFound) { Content = new StringContent("missing") });
        var error = await Assert.ThrowsAsync<MarketDataProviderException>(() => Adapter(handler).GetDailyBarsAsync("QQQ", new(2021, 1, 4), new(2021, 1, 5), default));
        Assert.False(error.IsTransient);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Cancellation_StopsTheRequest()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var handler = new StubHandler(_ => StubHandler.Json(Fixture));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Adapter(handler).GetDailyBarsAsync("QQQ", new(2021, 1, 4), new(2021, 1, 5), cancellation.Token));
    }

    [Fact]
    public async Task BoundsAndAllowlist_AreEnforced()
    {
        var adapter = Adapter(new StubHandler(_ => new(HttpStatusCode.OK)), Enabled() with { MaximumYears = 1 });
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetDailyBarsAsync("AAPL", new(2021, 1, 1), new(2021, 1, 2), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => adapter.GetDailyBarsAsync("QQQ", new(2020, 1, 1), new(2022, 1, 1), default));
    }
}
