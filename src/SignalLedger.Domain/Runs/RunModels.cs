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
