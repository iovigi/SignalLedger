namespace SignalLedger.Domain.Runs;

public enum RunStatus { Pending, Running, Completed, Failed, Cancelled }

public sealed class ImportRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Source { get; init; }
    public required string Symbol { get; init; }
    public DateOnly? RequestedFrom { get; init; }
    public DateOnly? RequestedTo { get; init; }
    public DateOnly? ActualFrom { get; set; }
    public DateOnly? ActualTo { get; set; }
    public int RecordsAdded { get; set; }
    public int RecordsUpdated { get; set; }
    public int RecordsRejected { get; set; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public RunStatus Status { get; set; }
    public string? Warning { get; set; }
    public string? Error { get; set; }
    public string? ProviderMetadataJson { get; set; }
}

public sealed class BacktestRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string EngineVersion { get; init; }
    public required string Symbol { get; init; }
    public required string Source { get; init; }
    public required string DataFingerprint { get; init; }
    public required string StrategyName { get; init; }
    public required string ParametersJson { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public decimal InitialCapital { get; init; }
    public RunStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string? SummaryJson { get; set; }
    public string? Error { get; set; }
}

public sealed class StoredTrade
{
    public long Id { get; set; }
    public Guid BacktestRunId { get; set; }
    public int Sequence { get; set; }
    public DateOnly SignalDate { get; set; }
    public DateOnly ExecutionDate { get; set; }
    public required string Side { get; set; }
    public decimal ReferencePrice { get; set; }
    public decimal EffectivePrice { get; set; }
    public decimal Units { get; set; }
    public decimal Notional { get; set; }
    public decimal Commission { get; set; }
    public decimal CashAfter { get; set; }
    public decimal EquityAfter { get; set; }
    public required string Reason { get; set; }
}

public sealed class StoredEquityPoint
{
    public long Id { get; set; }
    public Guid BacktestRunId { get; set; }
    public required string Series { get; set; }
    public DateOnly SessionDate { get; set; }
    public decimal Equity { get; set; }
    public decimal Cash { get; set; }
    public decimal Units { get; set; }
    public decimal Close { get; set; }
    public double? Indicator { get; set; }
}

public sealed class ExperimentJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Symbol { get; set; }
    public required string Source { get; set; }
    public required string RequestJson { get; set; }
    public RunStatus Status { get; set; }
    public int CompletedCells { get; set; }
    public int TotalCells { get; set; }
    public bool CancellationRequested { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class ExperimentCell
{
    public long Id { get; set; }
    public Guid ExperimentJobId { get; set; }
    public int Period { get; set; }
    public decimal CommissionPercent { get; set; }
    public double StrategyReturn { get; set; }
    public double BenchmarkReturn { get; set; }
    public double NetOutperformance { get; set; }
    public string? WindowLabel { get; set; }
    public bool IsHoldout { get; set; }
}
