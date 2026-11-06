using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Api.IntegrationTests;

public sealed class PublicDemoTests
{
    [Fact]
    public async Task Public_demo_seeds_synthetic_data_and_blocks_mutating_data_endpoints()
    {
        using var factory = SignalLedgerApiFactory.ForSettings(new Dictionary<string, string?>
        {
            ["SignalLedger:PublicDemoMode"] = "true",
            ["SignalLedger:SeedSyntheticDemo"] = "true"
        });
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
        Assert.Equal(2_600, await db.DailyBars.CountAsync(x => x.Symbol == "SYNTH" && x.Source == "Synthetic"));

        using var csv = new MultipartFormDataContent();
        csv.Add(new ByteArrayContent("Date,Open,High,Low,Close\n2020-01-01,1,1,1,1"u8.ToArray()), "file", "demo.csv");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/imports/csv?symbol=SYNTH", csv)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/market-data/sync?symbol=QQQ&from=2020-01-01&to=2020-02-01", null)).StatusCode);
    }

    [Fact]
    public async Task Write_endpoints_are_rate_limited()
    {
        using var factory = new SignalLedgerApiFactory();
        using var client = factory.CreateClient();
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 13; i++)
            responses.Add(await client.PostAsJsonAsync("/api/backtests", new { }));

        Assert.Equal(HttpStatusCode.TooManyRequests, responses[^1].StatusCode);
        foreach (var response in responses) response.Dispose();
    }
}
