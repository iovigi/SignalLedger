using System.Net;
using System.Text;
using SignalLedger.Infrastructure.Providers;

namespace SignalLedger.Infrastructure.Tests;

public sealed class YahooChartAdapterTests
{
    [Fact]
    public async Task DisabledProvider_FailsBeforeNetworkAccess()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network must not be called."));
        var adapter = new YahooChartAdapter(new HttpClient(handler), new YahooProviderOptions());
        await Assert.ThrowsAsync<ProviderDisabledException>(() => adapter.GetDailyBarsAsync("QQQ", new(2020, 1, 1), new(2020, 1, 2), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AuthorizationConfirmation_IsRequiredEvenWhenEnabled()
    {
        var adapter = new YahooChartAdapter(new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))), new YahooProviderOptions { Enabled = true });
        var error = await Assert.ThrowsAsync<ProviderDisabledException>(() => adapter.GetDailyBarsAsync("QQQ", new(2020, 1, 1), new(2020, 1, 2), default));
        Assert.Contains("authorized", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthorizedFixture_ParsesDailyBarsWithoutLiveRequest()
    {
        const string json = """
        {"chart":{"result":[{"meta":{"currency":"USD"},"timestamp":[1609459200,1609545600],"indicators":{"quote":[{"open":[100.0,101.0],"high":[102.0,103.0],"low":[99.0,100.0],"close":[101.0,102.0],"volume":[1000,1100]}],"adjclose":[{"adjclose":[100.5,101.5]}]}}],"error":null}}
        """;
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var adapter = new YahooChartAdapter(new HttpClient(handler), Enabled());

        var bars = await adapter.GetDailyBarsAsync("QQQ", new(2021, 1, 1), new(2021, 1, 2), default);

        Assert.Equal(2, bars.Count);
        Assert.Equal(new DateOnly(2021, 1, 1), bars[0].SessionDate);
        Assert.Equal(100.5m, bars[0].AdjustedClose);
        Assert.Equal("YahooChart", bars[0].Source);
        Assert.Contains("period1=", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransientFailures_RetryWithBoundedAttempts()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("maintenance") });
        var adapter = new YahooChartAdapter(new HttpClient(handler), Enabled());
        var error = await Assert.ThrowsAsync<MarketDataProviderException>(() => adapter.GetDailyBarsAsync("^NDX", new(2021, 1, 1), new(2021, 1, 2), default));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task BoundsAndAllowlist_AreEnforced()
    {
        var adapter = new YahooChartAdapter(new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))), Enabled() with { MaximumYears = 1 });
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetDailyBarsAsync("AAPL", new(2021, 1, 1), new(2021, 1, 2), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => adapter.GetDailyBarsAsync("QQQ", new(2020, 1, 1), new(2022, 1, 1), default));
    }

    private static YahooProviderOptions Enabled() => new() { Enabled = true, AuthorizedUseConfirmed = true, MaximumYears = 40 };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; LastRequest = request; return Task.FromResult(response(request)); }
    }
}
