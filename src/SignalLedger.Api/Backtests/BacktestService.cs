using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.Backtesting;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Api.Backtests;

public sealed record StrategyRequest(string Type, int? Period = null);
public sealed record ExecutionRequest(decimal CommissionPercentPerSide = 0, decimal SlippageBpsPerSide = 0, bool LiquidateAtEnd = false, string CashYieldMode = "None");
public sealed record BacktestRequest(string Symbol, string Source, DateOnly StartDate, DateOnly EndDate, decimal InitialCapital, StrategyRequest Strategy, ExecutionRequest Execution);
public sealed record BacktestSeriesPoint(DateOnly Date, decimal Close, double? Indicator, decimal StrategyEquity, decimal BenchmarkEquity);
public sealed record BacktestResponse(Guid Id, string EngineVersion, string Symbol, string Source, string DataFingerprint, string StrategyName, DateOnly StartDate, DateOnly EndDate, PerformanceSummary Strategy, PerformanceSummary Benchmark, double OutperformanceVsBenchmark, IReadOnlyList<BacktestSeriesPoint> Series, IReadOnlyList<LedgerTrade> Trades, IReadOnlyList<string> Warnings);
public sealed record TradePage(int Page, int PageSize, int Total, IReadOnlyList<LedgerTrade> Items);

public sealed class BacktestService(SignalLedgerDbContext dbContext, TimeProvider timeProvider)
{
    public const string EngineVersion = "1.0.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Symbols = new(StringComparer.Ordinal) { "^NDX", "^IXIC", "^GSPC", "QQQ", "SYNTH" };

    public async Task<BacktestResponse> RunAsync(BacktestRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var symbol = request.Symbol.Trim().ToUpperInvariant(); var source = request.Source.Trim();
        var storedBars = await dbContext.DailyBars.AsNoTracking().Where(x => x.Symbol == symbol && x.Source == source && x.SessionDate <= request.EndDate).OrderBy(x => x.SessionDate).ToListAsync(cancellationToken);
        var series = new PriceSeries(storedBars.Select(x => new PriceBar(x.SessionDate, x.Open, x.High, x.Low, x.Close, x.Volume)));
        if (series.Count(x => x.Date >= request.StartDate && x.Date <= request.EndDate) < 2)
        {
            // Backtests read only the local cache; missing data is reported, never fetched or substituted.
            var available = await dbContext.DailyBars.AsNoTracking().Where(x => x.Symbol == symbol && x.Source == source).GroupBy(x => 1).Select(g => new { First = g.Min(x => x.SessionDate), Last = g.Max(x => x.SessionDate) }).SingleOrDefaultAsync(cancellationToken);
            throw new BacktestValidationException(available is null
                ? $"At least two bars are required in the evaluation window. No cached data exists for {symbol}/{source}; import a CSV or download history in the Data Manager."
                : $"At least two bars are required in the evaluation window. Cached {symbol}/{source} data covers {available.First:yyyy-MM-dd} to {available.Last:yyyy-MM-dd}.");
        }
        var strategy = CreateStrategy(request.Strategy);
        var settings = new ExecutionSettings(request.Execution.CommissionPercentPerSide, request.Execution.SlippageBpsPerSide, request.Execution.LiquidateAtEnd);
        var strategyRun = BacktestEngine.Run(series, request.StartDate, request.EndDate, request.InitialCapital, strategy, settings);
        var benchmarkRun = BacktestEngine.Run(series, request.StartDate, request.EndDate, request.InitialCapital, new BuyAndHoldStrategy(), settings);
        var strategyMetrics = PerformanceMetrics.Calculate(strategyRun.Ledger); var benchmarkMetrics = PerformanceMetrics.Calculate(benchmarkRun.Ledger);
        var fingerprint = Fingerprint(symbol, source, storedBars);
        var warnings = new List<string>();
        if (symbol.StartsWith('^')) warnings.Add("Non-tradable price index; executions are theoretical proxy fills, not executable index trades.");
        if (strategyRun.Evaluation.HasShortWarmup) warnings.Add("The requested indicator warm-up was shorter than the configured period.");
        warnings.AddRange(strategyMetrics.Warnings);
        var parametersJson = JsonSerializer.Serialize(request with { Symbol = symbol, Source = source }, JsonOptions);
        var run = new BacktestRun { EngineVersion = EngineVersion, Symbol = symbol, Source = source, DataFingerprint = fingerprint, StrategyName = strategy.Name, ParametersJson = parametersJson, From = request.StartDate, To = request.EndDate, InitialCapital = request.InitialCapital, Status = RunStatus.Completed, CreatedAtUtc = timeProvider.GetUtcNow() };
        var summary = new StoredSummary(strategyMetrics, benchmarkMetrics, warnings);
        run.SummaryJson = JsonSerializer.Serialize(summary, JsonOptions);
        dbContext.BacktestRuns.Add(run);

        var indicatorByDate = strategyRun.Evaluation.Bars.Select((bar, i) => (bar.Date, Value: strategyRun.Schedule.Indicator[i])).ToDictionary(x => x.Date, x => x.Value);
        foreach (var point in strategyRun.Ledger.Equity)
            dbContext.EquityPoints.Add(ToStored(run.Id, "Strategy", point, indicatorByDate.GetValueOrDefault(point.Date)));
        foreach (var point in benchmarkRun.Ledger.Equity)
            dbContext.EquityPoints.Add(ToStored(run.Id, "Benchmark", point, null));
        foreach (var item in strategyRun.Ledger.Trades.Select((trade, sequence) => (trade, sequence))) dbContext.Trades.Add(ToStored(run.Id, item.sequence, item.trade));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetAsync(run.Id, cancellationToken) ?? throw new InvalidOperationException("Persisted backtest could not be reloaded.");
    }

    public async Task<BacktestResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var run = await dbContext.BacktestRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (run is null || run.SummaryJson is null) return null;
        var summary = JsonSerializer.Deserialize<StoredSummary>(run.SummaryJson, JsonOptions) ?? throw new InvalidOperationException("Stored backtest summary is invalid.");
        var points = await dbContext.EquityPoints.AsNoTracking().Where(x => x.BacktestRunId == id).OrderBy(x => x.SessionDate).ToListAsync(cancellationToken);
        var strategy = points.Where(x => x.Series == "Strategy").ToDictionary(x => x.SessionDate);
        var benchmark = points.Where(x => x.Series == "Benchmark").ToDictionary(x => x.SessionDate);
        var combined = strategy.Values.OrderBy(x => x.SessionDate).Select(x => new BacktestSeriesPoint(x.SessionDate, x.Close, x.Indicator, x.Equity, benchmark[x.SessionDate].Equity)).ToArray();
        var storedTrades = await dbContext.Trades.AsNoTracking().Where(x => x.BacktestRunId == id).OrderBy(x => x.Sequence).ToListAsync(cancellationToken);
        var trades = storedTrades.Select(x => new LedgerTrade(x.SignalDate, x.ExecutionDate, Enum.Parse<TradeSide>(x.Side), x.ReferencePrice, Math.Abs(x.EffectivePrice - x.ReferencePrice), x.EffectivePrice, x.Units, x.Notional, x.Commission, x.CashAfter, x.EquityAfter, x.Reason)).ToArray();
        return new(run.Id, run.EngineVersion, run.Symbol, run.Source, run.DataFingerprint, run.StrategyName, run.From, run.To, summary.Strategy, summary.Benchmark, summary.Strategy.TotalReturn - summary.Benchmark.TotalReturn, combined, trades, summary.Warnings);
    }

    public async Task<TradePage?> GetTradesAsync(Guid id, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 200) throw new BacktestValidationException("Page must be positive and pageSize must be between 1 and 200.");
        if (!await dbContext.BacktestRuns.AnyAsync(x => x.Id == id, cancellationToken)) return null;
        var query = dbContext.Trades.AsNoTracking().Where(x => x.BacktestRunId == id).OrderBy(x => x.Sequence);
        var total = await query.CountAsync(cancellationToken);
        var stored = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(page, pageSize, total, stored.Select(ToLedgerTrade).ToArray());
    }

    private static void Validate(BacktestRequest request)
    {
        if (!Symbols.Contains(request.Symbol.Trim().ToUpperInvariant())) throw new BacktestValidationException("Symbol is not supported.");
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > 40) throw new BacktestValidationException("Source is required and limited to 40 characters.");
        if (request.EndDate < request.StartDate || request.EndDate.DayNumber - request.StartDate.DayNumber > 366 * 60) throw new BacktestValidationException("Date range is invalid or exceeds 60 years.");
        if (request.InitialCapital is < 1 or > 1_000_000_000) throw new BacktestValidationException("Initial capital must be between 1 and 1,000,000,000.");
        if (request.Execution.CommissionPercentPerSide is < 0 or > 10) throw new BacktestValidationException("Commission must be between 0% and 10% per side.");
        if (request.Execution.SlippageBpsPerSide is < 0 or > 1_000) throw new BacktestValidationException("Slippage must be between 0 and 1,000 bps per side.");
        if (!string.Equals(request.Execution.CashYieldMode, "None", StringComparison.OrdinalIgnoreCase)) throw new BacktestValidationException("Historical cash yield is not enabled; CashYieldMode must be None.");
        _ = CreateStrategy(request.Strategy);
    }

    private static IBacktestStrategy CreateStrategy(StrategyRequest request) => request.Type.Trim().ToUpperInvariant() switch
    {
        "BUYANDHOLD" or "BUY & HOLD" => new BuyAndHoldStrategy(),
        "SMA" when request.Period is >= 1 and <= 500 => new SmaStrategy(request.Period.Value),
        "EMA" when request.Period is >= 1 and <= 500 => new EmaStrategy(request.Period.Value),
        _ => throw new BacktestValidationException("Strategy must be BuyAndHold, SMA, or EMA; moving-average period must be 1–500.")
    };

    private static string Fingerprint(string symbol, string source, IReadOnlyList<DailyBar> bars)
    {
        var text = new StringBuilder().Append(symbol).Append('|').Append(source).Append('\n');
        foreach (var bar in bars) text.Append(bar.SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|').Append(bar.Open.ToString("G29", CultureInfo.InvariantCulture)).Append('|').Append(bar.High.ToString("G29", CultureInfo.InvariantCulture)).Append('|').Append(bar.Low.ToString("G29", CultureInfo.InvariantCulture)).Append('|').Append(bar.Close.ToString("G29", CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static StoredEquityPoint ToStored(Guid runId, string series, EquityPoint point, double? indicator) => new() { BacktestRunId = runId, Series = series, SessionDate = point.Date, Equity = point.Equity, Cash = point.Cash, Units = point.Units, Close = point.ClosingPrice, Indicator = indicator };
    private static StoredTrade ToStored(Guid runId, int sequence, LedgerTrade trade) => new() { BacktestRunId = runId, Sequence = sequence, SignalDate = trade.SignalDate, ExecutionDate = trade.ExecutionDate, Side = trade.Side.ToString(), ReferencePrice = trade.ReferencePrice, EffectivePrice = trade.EffectivePrice, Units = trade.Units, Notional = trade.Notional, Commission = trade.Commission, CashAfter = trade.CashAfter, EquityAfter = trade.EquityAfter, Reason = trade.Reason };
    private static LedgerTrade ToLedgerTrade(StoredTrade x) => new(x.SignalDate, x.ExecutionDate, Enum.Parse<TradeSide>(x.Side), x.ReferencePrice, Math.Abs(x.EffectivePrice - x.ReferencePrice), x.EffectivePrice, x.Units, x.Notional, x.Commission, x.CashAfter, x.EquityAfter, x.Reason);
    private sealed record StoredSummary(PerformanceSummary Strategy, PerformanceSummary Benchmark, IReadOnlyList<string> Warnings);
}

public sealed class BacktestValidationException(string message) : ArgumentException(message);
