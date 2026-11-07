using SignalLedger.Domain.Runs;

namespace SignalLedger.Domain.MarketData;

/// <summary>Per-provider synchronization metadata for one instrument.</summary>
public sealed class MarketDataSyncState
{
    public required string Symbol { get; set; }
    public required string Source { get; set; }
    public DateOnly? EarliestCachedDate { get; set; }
    public DateOnly? LatestCachedDate { get; set; }
    /// <summary>Earliest date the provider has been asked for. Prevents re-requesting history that predates the instrument.</summary>
    public DateOnly? EarliestRequestedDate { get; set; }
    /// <summary>Latest date the provider has been asked for (used when a request returned no bars).</summary>
    public DateOnly? LatestRequestedDate { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>An expected trading session the provider was asked for and did not return (e.g. an unscheduled closure).</summary>
public sealed class UnavailableSession
{
    public long Id { get; set; }
    public required string Symbol { get; set; }
    public required string Source { get; set; }
    public DateOnly SessionDate { get; set; }
    public DateTimeOffset CheckedAtUtc { get; set; }
}

public enum MarketDataJobType { Download, Sync, Repair, Refresh }
public enum MarketDataJobTrigger { Manual, Automatic, Recovery }

public sealed class MarketDataJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MarketDataJobType Type { get; set; }
    public MarketDataJobTrigger Trigger { get; set; }
    public required string Symbol { get; set; }
    public required string Source { get; set; }
    public DateOnly? RequestedFrom { get; set; }
    public DateOnly? RequestedTo { get; set; }
    public RunStatus Status { get; set; }
    public int CompletedSteps { get; set; }
    public int TotalSteps { get; set; }
    public int RecordsAdded { get; set; }
    public int RecordsUpdated { get; set; }
    public int ProviderRequests { get; set; }
    /// <summary>For explicit refresh jobs: first date not yet refreshed, so a resumed job does not repeat completed chunks.</summary>
    public DateOnly? ResumeFrom { get; set; }
    public int RetryCount { get; set; }
    public bool CancellationRequested { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    public DateTimeOffset? NotBeforeUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

/// <summary>Operator-changed runtime setting persisted in SQLite.</summary>
public sealed class ApplicationSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
