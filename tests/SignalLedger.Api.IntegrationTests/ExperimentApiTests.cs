using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignalLedger.Api.Backtests;
using SignalLedger.Api.Experiments;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Api.IntegrationTests;

public sealed class ExperimentApiTests
{
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task GridCells_EqualIndependentBacktests()
    {
        await using var factory = new SignalLedgerApiFactory(); await SeedAsync(factory, 120); using var client = factory.CreateClient();
        var request = new ExperimentRequest("SYNTH", "Synthetic", new(2020, 1, 10), new(2020, 4, 20), 1_000, [2, 5], [0m, .2m]);
        using var create = await client.PostAsJsonAsync("/api/experiments", request);
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var job = (await create.Content.ReadFromJsonAsync<ExperimentResponse>(ApiJson))!;
        job = await WaitForTerminalAsync(client, job.Id);
        Assert.True(job.Status == RunStatus.Completed, job.Error);
        Assert.Equal(4, job.Cells.Count);

        var independentRequest = new BacktestRequest("SYNTH", "Synthetic", request.StartDate, request.EndDate, request.InitialCapital, new("SMA", 2), new(0, 0));
        using var independentResponse = await client.PostAsJsonAsync("/api/backtests", independentRequest);
        var independent = (await independentResponse.Content.ReadFromJsonAsync<BacktestResponse>(ApiJson))!;
        var cell = Assert.Single(job.Cells, x => x.Period == 2 && x.CommissionPercent == 0);
        Assert.Equal(independent.Strategy.TotalReturn, cell.StrategyReturn, 12);
        Assert.Equal(independent.Benchmark.TotalReturn, cell.BenchmarkReturn, 12);
    }

    [Fact]
    public async Task RunningGrid_CanBeCancelledAndBoundsAreValidated()
    {
        await using var factory = new SignalLedgerApiFactory(); await SeedAsync(factory, 2_000); using var client = factory.CreateClient();
        var periods = Enumerable.Range(1, 20).ToArray(); var fees = new[] { 0m, .05m, .1m, .2m, .5m };
        var request = new ExperimentRequest("SYNTH", "Synthetic", new(2020, 1, 2), new(2025, 6, 1), 1_000, periods, fees);
        var create = await client.PostAsJsonAsync("/api/experiments", request); var job = (await create.Content.ReadFromJsonAsync<ExperimentResponse>(ApiJson))!;
        using var cancel = await client.PostAsync($"/api/experiments/{job.Id}/cancel", null); Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        job = await WaitForTerminalAsync(client, job.Id);
        Assert.Equal(RunStatus.Cancelled, job.Status);
        Assert.True(job.CompletedCells < job.TotalCells);

        var tooLarge = request with { CommissionPercents = Enumerable.Range(0, 10).Select(x => (decimal)x).Append(10m).ToArray() };
        using var rejected = await client.PostAsJsonAsync("/api/experiments", tooLarge);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task RollingWindowsAndHoldout_DoNotLeakHoldoutIntoTrainingSelection()
    {
        await using var factory = new SignalLedgerApiFactory(); await SeedAsync(factory, 500); using var client = factory.CreateClient();
        var request = new ExperimentRequest("SYNTH", "Synthetic", new(2020, 1, 10), new(2020, 12, 31), 1_000, [3, 10], [0m], RollingStartDates: [new(2020, 1, 10), new(2020, 3, 1)], RollingWindowYears: 1, TrainingEndDate: new(2020, 6, 30), HoldoutStartDate: new(2020, 7, 1));
        var first = await RunExperimentAsync(client, request);
        Assert.Equal(8, first.Cells.Count);
        Assert.Equal(first.Cells.Where(x => x.WindowLabel == "Training").OrderByDescending(x => x.NetOutperformance).ThenBy(x => x.Period).First().Period, first.SelectedTrainingPeriod);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
            var holdoutBars = await db.DailyBars.Where(x => x.Symbol == "SYNTH" && x.SessionDate >= new DateOnly(2020, 7, 1)).ToListAsync();
            foreach (var item in holdoutBars.Select((bar, index) => (bar, index))) { var factor = 1m + item.index / 20m; item.bar.Open *= factor; item.bar.High *= factor; item.bar.Low *= factor; item.bar.Close *= factor; }
            await db.SaveChangesAsync();
        }
        var second = await RunExperimentAsync(client, request);
        Assert.Equal(first.SelectedTrainingPeriod, second.SelectedTrainingPeriod);
        Assert.Equal(first.Cells.Where(x => x.WindowLabel == "Training").Select(x => x.NetOutperformance), second.Cells.Where(x => x.WindowLabel == "Training").Select(x => x.NetOutperformance));
        Assert.NotEqual(first.Cells.Where(x => x.IsHoldout).Select(x => x.NetOutperformance).ToArray(), second.Cells.Where(x => x.IsHoldout).Select(x => x.NetOutperformance).ToArray());
    }

    private static async Task SeedAsync(SignalLedgerApiFactory factory, int count)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
        if (await db.DailyBars.AnyAsync(x => x.Symbol == "SYNTH")) return;
        db.DailyBars.AddRange(Enumerable.Range(0, count).Select(i => { var close = 100m + i / 10m + (decimal)Math.Sin(i / 5d) * 5m; return new DailyBar { Symbol = "SYNTH", Source = "Synthetic", Currency = "USD", SessionDate = new DateOnly(2020, 1, 1).AddDays(i), Open = close, High = close + 1, Low = close - 1, Close = close, UpdatedAtUtc = DateTimeOffset.UnixEpoch, IsFinal = true }; }));
        await db.SaveChangesAsync();
        Assert.Equal(count, await db.DailyBars.CountAsync(x => x.Symbol == "SYNTH" && x.Source == "Synthetic"));
    }

    private static async Task<ExperimentResponse> WaitForTerminalAsync(HttpClient client, Guid id)
    {
        for (var i = 0; i < 100; i++)
        {
            var job = await client.GetFromJsonAsync<ExperimentResponse>($"/api/experiments/{id}", ApiJson) ?? throw new InvalidOperationException();
            if (job.Status is RunStatus.Completed or RunStatus.Cancelled or RunStatus.Failed) return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Experiment did not reach a terminal state.");
    }

    private static async Task<ExperimentResponse> RunExperimentAsync(HttpClient client, ExperimentRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/experiments", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExperimentResponse>(ApiJson))!;
        var completed = await WaitForTerminalAsync(client, created.Id);
        Assert.True(completed.Status == RunStatus.Completed, completed.Error);
        return completed;
    }
}
