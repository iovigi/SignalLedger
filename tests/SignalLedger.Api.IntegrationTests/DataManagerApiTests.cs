using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Api.IntegrationTests;

public sealed class DataManagerApiTests : IClassFixture<SignalLedgerApiFactory>
{
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient client;
    public DataManagerApiTests(SignalLedgerApiFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task Instruments_ReturnsConfiguredCatalogWithTradabilityWarnings()
    {
        using var response = await client.GetAsync("/api/instruments");
        var instruments = await response.Content.ReadFromJsonAsync<List<InstrumentCoverage>>(ApiJson);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, instruments?.Count);
        var ndx = Assert.Single(instruments!, x => x.Symbol == "^NDX");
        Assert.False(ndx.Tradable);
        Assert.Contains(ndx.Warnings, x => x.Contains("Non-tradable", StringComparison.Ordinal));
        Assert.True(Assert.Single(instruments!, x => x.Symbol == "QQQ").Tradable);
        Assert.Equal("Synthetic Demo Series", Assert.Single(instruments!, x => x.Symbol == "SYNTH").DisplayName);
    }

    [Fact]
    public async Task UnknownInstrument_ReturnsNotFound()
    {
        using var response = await client.GetAsync("/api/instruments/UNKNOWN/coverage");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProviderSync_IsForbiddenByDefault()
    {
        using var response = await client.PostAsync("/api/market-data/sync?symbol=QQQ&from=2020-01-01&to=2020-01-03", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CsvUpload_ImportsAuthorizedUserDataAndUpdatesCoverage()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Date,Open,High,Low,Close,Volume\n2026-10-01,100,102,99,101,1000\n2026-10-02,101,103,100,102,1100\n"), "file", "synthetic.csv");
        using var response = await client.PostAsync("/api/imports/csv?symbol=QQQ", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var coverageResponse = await client.GetAsync("/api/instruments/QQQ/coverage");
        var coverage = await coverageResponse.Content.ReadFromJsonAsync<InstrumentCoverage>(ApiJson);
        var source = Assert.Single(coverage!.Sources, x => x.Source == "UserCsv");
        Assert.Equal(2, source.BarCount);
    }

    [Fact]
    public async Task CsvUpload_RejectsUnsafeFileType()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("not csv"), "file", "payload.exe");
        using var response = await client.PostAsync("/api/imports/csv?symbol=QQQ", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
