using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SignalLedger.Api.Backtests;
using SignalLedger.Domain.MarketData;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Api.IntegrationTests;

public sealed class BacktestApiTests
{
    [Fact]
    public async Task SameRunReloadedAfterApplicationRestart_IsDeterministic()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"signalledger-restart-{Guid.NewGuid():N}");
        Guid id; BacktestResponse created;
        await using (var first = SignalLedgerApiFactory.ForPersistentDirectory(directory, deleteOnDispose: false))
        {
            using (var scope = first.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
                if (!db.DailyBars.Any(x => x.Symbol == "SYNTH" && x.Source == "Synthetic"))
                {
                    db.DailyBars.AddRange(Enumerable.Range(0, 6).Select(index => Bar(index)));
                    await db.SaveChangesAsync();
                }
            }
            using var client = first.CreateClient();
            using var response = await client.PostAsJsonAsync("/api/backtests", Request());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            created = (await response.Content.ReadFromJsonAsync<BacktestResponse>())!;
            id = created.Id;
        }

        await using (var second = SignalLedgerApiFactory.ForPersistentDirectory(directory, deleteOnDispose: true))
        {
            using var client = second.CreateClient();
            var reloaded = await client.GetFromJsonAsync<BacktestResponse>($"/api/backtests/{id}");
            Assert.NotNull(reloaded);
            Assert.Equal(created.DataFingerprint, reloaded.DataFingerprint);
            Assert.Equal(JsonSerializer.Serialize(created), JsonSerializer.Serialize(reloaded));
        }
    }

    [Fact]
    public async Task InvalidRequest_ReturnsProblemDetails()
    {
        await using var factory = new SignalLedgerApiFactory(); using var client = factory.CreateClient();
        var invalid = Request() with { InitialCapital = -1 };
        using var response = await client.PostAsJsonAsync("/api/backtests", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static BacktestRequest Request() => new("SYNTH", "Synthetic", new(2020, 1, 2), new(2020, 1, 6), 1_000, new("SMA", 2), new(0.2m, 5m));
    private static DailyBar Bar(int index)
    {
        var close = 100m + index;
        return new() { Symbol = "SYNTH", Source = "Synthetic", Currency = "USD", SessionDate = new DateOnly(2020, 1, 1).AddDays(index), Open = close, High = close + 1, Low = close - 1, Close = close, UpdatedAtUtc = DateTimeOffset.UnixEpoch, IsFinal = true };
    }
}
