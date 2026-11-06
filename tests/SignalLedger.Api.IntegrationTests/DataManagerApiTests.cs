using System.Net;
using System.Net.Http.Json;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Api.IntegrationTests;

public sealed class DataManagerApiTests : IClassFixture<SignalLedgerApiFactory>
{
    private readonly HttpClient client;
    public DataManagerApiTests(SignalLedgerApiFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task Instruments_ReturnsConfiguredCatalogWithTradabilityWarnings()
    {
        using var response = await client.GetAsync("/api/instruments");
        var instruments = await response.Content.ReadFromJsonAsync<List<InstrumentCoverage>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, instruments?.Count);
        var ndx = Assert.Single(instruments!, x => x.Symbol == "^NDX");
        Assert.False(ndx.Tradable);
        Assert.Contains(ndx.Warnings, x => x.Contains("Non-tradable", StringComparison.Ordinal));
        Assert.True(Assert.Single(instruments!, x => x.Symbol == "QQQ").Tradable);
    }

    [Fact]
    public async Task UnknownInstrument_ReturnsNotFound()
    {
        using var response = await client.GetAsync("/api/instruments/UNKNOWN/coverage");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
