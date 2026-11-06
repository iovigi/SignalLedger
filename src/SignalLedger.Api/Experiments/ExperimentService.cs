using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Api.Backtests;
using SignalLedger.Domain.Backtesting;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Api.Experiments;

public sealed record ExperimentRequest(string Symbol, string Source, DateOnly StartDate, DateOnly EndDate, decimal InitialCapital, IReadOnlyList<int> Periods, IReadOnlyList<decimal> CommissionPercents, decimal SlippageBps = 0, IReadOnlyList<DateOnly>? RollingStartDates = null, int? RollingWindowYears = null, DateOnly? TrainingEndDate = null, DateOnly? HoldoutStartDate = null);
public sealed record ExperimentCellResponse(int Period, decimal CommissionPercent, double StrategyReturn, double BenchmarkReturn, double NetOutperformance, string? WindowLabel, bool IsHoldout);
public sealed record ExperimentResponse(Guid Id, string Symbol, string Source, RunStatus Status, int CompletedCells, int TotalCells, bool CancellationRequested, string? Error, int? SelectedTrainingPeriod, IReadOnlyList<ExperimentCellResponse> Cells);

public sealed class ExperimentQueue
{
    private readonly Channel<Guid> channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    public bool TryEnqueue(Guid id) => channel.Writer.TryWrite(id);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class ExperimentService(SignalLedgerDbContext dbContext, ExperimentQueue queue, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExperimentResponse> CreateAsync(ExperimentRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var normalized = request with { Symbol = request.Symbol.Trim().ToUpperInvariant(), Source = request.Source.Trim(), Periods = request.Periods.Distinct().Order().ToArray(), CommissionPercents = request.CommissionPercents.Distinct().Order().ToArray() };
        var job = new ExperimentJob { Symbol = normalized.Symbol, Source = normalized.Source, RequestJson = JsonSerializer.Serialize(normalized, JsonOptions), Status = RunStatus.Pending, TotalCells = normalized.Periods.Count * normalized.CommissionPercents.Count * Windows(normalized).Count, CreatedAtUtc = timeProvider.GetUtcNow() };
        dbContext.ExperimentJobs.Add(job); await dbContext.SaveChangesAsync(cancellationToken);
        if (!queue.TryEnqueue(job.Id)) { job.Status = RunStatus.Failed; job.Error = "Experiment queue is full; retry later."; job.CompletedAtUtc = timeProvider.GetUtcNow(); await dbContext.SaveChangesAsync(cancellationToken); }
        return (await GetAsync(job.Id, cancellationToken))!;
    }

    public async Task<ExperimentResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.ExperimentJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null) return null;
        var cells = await dbContext.ExperimentCells.AsNoTracking().Where(x => x.ExperimentJobId == id).OrderBy(x => x.Period).ThenBy(x => x.CommissionPercent).Select(x => new ExperimentCellResponse(x.Period, x.CommissionPercent, x.StrategyReturn, x.BenchmarkReturn, x.NetOutperformance, x.WindowLabel, x.IsHoldout)).ToListAsync(cancellationToken);
        var selected = cells.Where(x => x.WindowLabel == "Training" && !x.IsHoldout).OrderByDescending(x => x.NetOutperformance).ThenBy(x => x.Period).FirstOrDefault()?.Period;
        return new(job.Id, job.Symbol, job.Source, job.Status, job.CompletedCells, job.TotalCells, job.CancellationRequested, job.Error, selected, cells);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.ExperimentJobs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null) return false;
        if (job.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled) return true;
        job.CancellationRequested = true; await dbContext.SaveChangesAsync(cancellationToken); return true;
    }

    internal static void Validate(ExperimentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol) || string.IsNullOrWhiteSpace(request.Source)) throw new BacktestValidationException("Symbol and source are required.");
        if (request.EndDate <= request.StartDate || request.EndDate.DayNumber - request.StartDate.DayNumber > 366 * 60) throw new BacktestValidationException("Experiment date range is invalid or exceeds 60 years.");
        if (request.InitialCapital is < 1 or > 1_000_000_000) throw new BacktestValidationException("Initial capital is outside allowed bounds.");
        if (request.Periods.Count is < 1 or > 20 || request.CommissionPercents.Count is < 1 or > 10 || request.Periods.Count * request.CommissionPercents.Count > 100) throw new BacktestValidationException("Experiment grid must contain 1–100 cells (max 20 periods and 10 fee levels).");
        if (request.Periods.Any(x => x is < 1 or > 500) || request.CommissionPercents.Any(x => x is < 0 or > 10) || request.SlippageBps is < 0 or > 1_000) throw new BacktestValidationException("Experiment parameter is outside allowed bounds.");
        if ((request.RollingStartDates?.Count ?? 0) > 20 || request.RollingWindowYears is < 1 or > 30) throw new BacktestValidationException("Rolling experiments allow at most 20 starts and a 1–30 year window.");
        if ((request.RollingStartDates is null) != (request.RollingWindowYears is null)) throw new BacktestValidationException("RollingStartDates and RollingWindowYears must be supplied together.");
        if (request.RollingStartDates?.Any(x => x < request.StartDate || x > request.EndDate) == true) throw new BacktestValidationException("Rolling start dates must fall inside the experiment range.");
        if ((request.TrainingEndDate is null) != (request.HoldoutStartDate is null)) throw new BacktestValidationException("TrainingEndDate and HoldoutStartDate must be supplied together.");
        if (request.TrainingEndDate is { } training && request.HoldoutStartDate is { } holdout && (training < request.StartDate || holdout > request.EndDate || training >= holdout)) throw new BacktestValidationException("Training/holdout split must be ordered and inside the experiment range.");
        if (request.Periods.Count * request.CommissionPercents.Count * Windows(request).Count > 2_000) throw new BacktestValidationException("Expanded rolling/holdout experiment exceeds 2,000 cells.");
    }

    internal static ExperimentRequest Deserialize(string json) => JsonSerializer.Deserialize<ExperimentRequest>(json, JsonOptions) ?? throw new InvalidOperationException("Stored experiment request is invalid.");

    internal static IReadOnlyList<ExperimentWindow> Windows(ExperimentRequest request)
    {
        var windows = new List<ExperimentWindow>();
        if (request.RollingStartDates is { Count: > 0 } starts && request.RollingWindowYears is { } years)
            windows.AddRange(starts.Distinct().Order().Select(start => new ExperimentWindow(start, Min(start.AddYears(years).AddDays(-1), request.EndDate), $"Rolling {start:yyyy-MM-dd}", false)));
        if (request.TrainingEndDate is { } training && request.HoldoutStartDate is { } holdout)
        {
            windows.Add(new(request.StartDate, training, "Training", false));
            windows.Add(new(holdout, request.EndDate, "Holdout", true));
        }
        if (windows.Count == 0) windows.Add(new(request.StartDate, request.EndDate, null, false));
        return windows;
    }
    private static DateOnly Min(DateOnly left, DateOnly right) => left <= right ? left : right;
}

public sealed record ExperimentWindow(DateOnly Start, DateOnly End, string? Label, bool IsHoldout);

public sealed class ExperimentWorker(IServiceScopeFactory scopeFactory, ExperimentQueue queue, TimeProvider timeProvider, ILogger<ExperimentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var id in queue.ReadAllAsync(stoppingToken)) await RunAsync(id, stoppingToken);
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
        var unfinished = await db.ExperimentJobs.Where(x => x.Status == RunStatus.Pending || x.Status == RunStatus.Running).ToListAsync(cancellationToken);
        foreach (var job in unfinished) { job.Status = RunStatus.Pending; queue.TryEnqueue(job.Id); }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RunAsync(Guid id, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
            var job = await db.ExperimentJobs.SingleAsync(x => x.Id == id, stoppingToken);
            if (job.CancellationRequested) { job.Status = RunStatus.Cancelled; job.CompletedAtUtc = timeProvider.GetUtcNow(); await db.SaveChangesAsync(stoppingToken); return; }
            job.Status = RunStatus.Running; await db.SaveChangesAsync(stoppingToken);
            var request = ExperimentService.Deserialize(job.RequestJson);
            var stored = await db.DailyBars.AsNoTracking().Where(x => x.Symbol == request.Symbol && x.Source == request.Source && x.SessionDate <= request.EndDate).OrderBy(x => x.SessionDate).ToListAsync(stoppingToken);
            var series = new PriceSeries(stored.Select(x => new PriceBar(x.SessionDate, x.Open, x.High, x.Low, x.Close, x.Volume)));
            var evaluationCount = series.Count(x => x.Date >= request.StartDate && x.Date <= request.EndDate);
            if (evaluationCount < 2) throw new BacktestValidationException($"Insufficient bars for experiment window ({request.Symbol}/{request.Source}, {request.StartDate:yyyy-MM-dd}–{request.EndDate:yyyy-MM-dd}; loaded {stored.Count}, evaluation {evaluationCount}).");
            foreach (var window in ExperimentService.Windows(request))
            foreach (var period in request.Periods)
            foreach (var fee in request.CommissionPercents)
            {
                await db.Entry(job).ReloadAsync(stoppingToken);
                if (job.CancellationRequested) { job.Status = RunStatus.Cancelled; job.CompletedAtUtc = timeProvider.GetUtcNow(); await db.SaveChangesAsync(stoppingToken); return; }
                var settings = new ExecutionSettings(fee, request.SlippageBps);
                var strategy = PerformanceMetrics.Calculate(BacktestEngine.Run(series, window.Start, window.End, request.InitialCapital, new SmaStrategy(period), settings).Ledger);
                var benchmark = PerformanceMetrics.Calculate(BacktestEngine.Run(series, window.Start, window.End, request.InitialCapital, new BuyAndHoldStrategy(), settings).Ledger);
                db.ExperimentCells.Add(new() { ExperimentJobId = id, Period = period, CommissionPercent = fee, StrategyReturn = strategy.TotalReturn, BenchmarkReturn = benchmark.TotalReturn, NetOutperformance = strategy.TotalReturn - benchmark.TotalReturn, WindowLabel = window.Label, IsHoldout = window.IsHoldout });
                job.CompletedCells++; await db.SaveChangesAsync(stoppingToken);
            }
            job.Status = RunStatus.Completed; job.CompletedAtUtc = timeProvider.GetUtcNow(); await db.SaveChangesAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Experiment {ExperimentId} failed", id);
            await using var scope = scopeFactory.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>(); var job = await db.ExperimentJobs.SingleOrDefaultAsync(x => x.Id == id, CancellationToken.None);
            if (job is not null) { job.Status = RunStatus.Failed; job.Error = exception.Message; job.CompletedAtUtc = timeProvider.GetUtcNow(); await db.SaveChangesAsync(); }
        }
    }
}
