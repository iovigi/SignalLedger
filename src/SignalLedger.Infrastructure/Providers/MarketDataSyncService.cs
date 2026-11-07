using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Infrastructure.Providers;

public sealed record MarketDataSyncResult(Guid RunId, int Added, int Updated, DateOnly? FirstDate, DateOnly? LastDate, int ProviderRequests, bool UpToDate, int SegmentsPlanned);
public sealed record SyncProgress(int CompletedSegments, int TotalSegments, int Added, int Updated, int ProviderRequests, SyncSegment? Segment);
public sealed record SyncRequestOptions(SyncMode Mode = SyncMode.Incremental, DateOnly? From = null, DateOnly? To = null, int? HistoricalYears = null, int? ReconciliationSessions = null);

/// <summary>Process-wide per-instrument locks so concurrent sync requests never duplicate provider work.</summary>
public sealed class MarketDataSyncLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);
    public SemaphoreSlim For(string symbol, string source) => locks.GetOrAdd($"{source}|{symbol}", _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Incremental synchronizer. Plans from SQLite first; when every completed session is cached it returns
/// "up to date" without calling the provider. Otherwise it requests only the missing contiguous ranges and
/// upserts them transactionally, so a failed request never removes or overwrites valid cached bars.
/// </summary>
public sealed class MarketDataSyncService(SignalLedgerDbContext dbContext, IMarketDataProvider provider, TimeProvider timeProvider, MarketDataSyncLocks? locks = null, ILogger<MarketDataSyncService>? logger = null)
{
    /// <summary>Missing sessions older than this are recorded as unavailable instead of being re-requested on every sync.</summary>
    public static readonly TimeSpan UnavailableAfter = TimeSpan.FromDays(7);
    private readonly MarketDataSyncLocks locks = locks ?? new();
    private readonly ILogger logger = logger ?? NullLogger<MarketDataSyncService>.Instance;

    public string Source => provider.ProviderId;

    public async Task<SyncPlan> PlanAsync(string symbol, SyncRequestOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(symbol);
        var latest = UsEquityTradingCalendar.LatestCompletedSession(timeProvider.GetUtcNow());
        var years = Math.Clamp(options.HistoricalYears ?? 30, 1, 60);
        var desiredStart = latest.AddYears(-years);
        if (options.Mode == SyncMode.Refresh)
        {
            if (options.From is null || options.To is null || options.To < options.From) throw new ArgumentException("Refresh requires an ordered From/To date range.");
            return MarketDataSyncPlanner.Plan(new([], new HashSet<DateOnly>(), null, null, desiredStart, latest, 0, SyncMode.Refresh, options.From, options.To));
        }
        var cached = (await dbContext.DailyBars.AsNoTracking().Where(x => x.Symbol == normalized && x.Source == Source).Select(x => x.SessionDate).ToListAsync(cancellationToken)).ToHashSet();
        var unavailable = (await dbContext.UnavailableSessions.AsNoTracking().Where(x => x.Symbol == normalized && x.Source == Source).Select(x => x.SessionDate).ToListAsync(cancellationToken)).ToHashSet();
        var state = await dbContext.MarketDataSyncStates.AsNoTracking().SingleOrDefaultAsync(x => x.Symbol == normalized && x.Source == Source, cancellationToken);
        return MarketDataSyncPlanner.Plan(new(cached, unavailable, state?.EarliestRequestedDate, state?.LatestRequestedDate, desiredStart, latest, Math.Clamp(options.ReconciliationSessions ?? 0, 0, 10), options.Mode));
    }

    public async Task<MarketDataSyncResult> SyncAsync(string symbol, SyncRequestOptions options, IProgress<SyncProgress>? progress = null, DateOnly? resumeFrom = null, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(symbol);
        var gate = locks.For(normalized, Source);
        await gate.WaitAsync(cancellationToken);
        try { return await SyncLockedAsync(normalized, options, progress, resumeFrom, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<MarketDataSyncResult> SyncLockedAsync(string symbol, SyncRequestOptions options, IProgress<SyncProgress>? progress, DateOnly? resumeFrom, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(symbol, options, cancellationToken);
        var segments = resumeFrom is { } resume ? plan.Segments.Where(x => x.To >= resume).Select(x => x.From < resume ? x with { From = resume } : x).ToArray() : plan.Segments.ToArray();
        if (segments.Length == 0)
        {
            logger.LogInformation("{Symbol}/{Source} is up to date through {Latest}; no provider request", symbol, Source, plan.LatestCompletedSession);
            progress?.Report(new(0, 0, 0, 0, 0, null));
            return new(Guid.Empty, 0, 0, null, null, 0, true, 0);
        }

        var run = new ImportRun { Source = Source, Symbol = symbol, RequestedFrom = segments[0].From, RequestedTo = segments[^1].To, StartedAtUtc = timeProvider.GetUtcNow(), Status = RunStatus.Running };
        dbContext.ImportRuns.Add(run);
        await dbContext.SaveChangesAsync(cancellationToken);
        int added = 0, updated = 0, requests = 0; DateOnly? first = null, last = null;
        try
        {
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                cancellationToken.ThrowIfCancellationRequested();
                requests++;
                var bars = await provider.GetDailyBarsAsync(symbol, segment.From, segment.To, cancellationToken);
                var (a, u) = await PersistSegmentAsync(symbol, segment, plan.LatestCompletedSession, bars, cancellationToken);
                added += a; updated += u;
                foreach (var bar in bars) { first = first is null || bar.SessionDate < first ? bar.SessionDate : first; last = last is null || bar.SessionDate > last ? bar.SessionDate : last; }
                progress?.Report(new(index + 1, segments.Length, added, updated, requests, segment));
            }
            await UpdateStateAsync(symbol, success: true, error: null, CancellationToken.None);
            var stored = await dbContext.ImportRuns.SingleAsync(x => x.Id == run.Id, CancellationToken.None);
            stored.RecordsAdded = added; stored.RecordsUpdated = updated; stored.ActualFrom = first; stored.ActualTo = last; stored.CompletedAtUtc = timeProvider.GetUtcNow(); stored.Status = RunStatus.Completed;
            stored.ProviderMetadataJson = $"{{\"requests\":{requests},\"segments\":{segments.Length}}}";
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return new(run.Id, added, updated, first, last, requests, false, segments.Length);
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException;
            dbContext.ChangeTracker.Clear();
            await UpdateStateAsync(symbol, success: false, error: cancelled ? "Cancelled." : exception.Message, CancellationToken.None);
            var stored = await dbContext.ImportRuns.SingleAsync(x => x.Id == run.Id, CancellationToken.None);
            stored.Status = cancelled ? RunStatus.Cancelled : RunStatus.Failed; stored.Error = cancelled ? "Cancelled." : exception.Message; stored.CompletedAtUtc = timeProvider.GetUtcNow();
            stored.RecordsAdded = added; stored.RecordsUpdated = updated;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<(int Added, int Updated)> PersistSegmentAsync(string symbol, SyncSegment segment, DateOnly latestCompleted, IReadOnlyList<DailyBar> provided, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        // Only final bars for completed sessions inside the requested range are accepted; a partial current-day bar is ignored.
        var bars = provided.Where(x => x.SessionDate >= segment.From && x.SessionDate <= segment.To && x.SessionDate <= latestCompleted)
            .GroupBy(x => x.SessionDate).Select(g => g.Last()).OrderBy(x => x.SessionDate).ToList();
        foreach (var bar in bars)
        {
            if (!string.Equals(bar.Symbol, symbol, StringComparison.Ordinal) || !string.Equals(bar.Source, Source, StringComparison.Ordinal))
                throw new MarketDataProviderException($"Provider returned {bar.Symbol}/{bar.Source} while {symbol}/{Source} was requested.");
            if (bar.Open <= 0 || bar.Low <= 0 || bar.High < Math.Max(bar.Open, bar.Close) || bar.Low > Math.Min(bar.Open, bar.Close))
                throw new MarketDataProviderException($"Provider returned invalid OHLC values for {bar.SessionDate:yyyy-MM-dd}.");
        }

        var allowOverwrite = (segment.Reason & (SyncReason.Reconciliation | SyncReason.Refresh)) != 0;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var existing = await dbContext.DailyBars.Where(x => x.Symbol == symbol && x.Source == Source && x.SessionDate >= segment.From && x.SessionDate <= segment.To).ToDictionaryAsync(x => x.SessionDate, cancellationToken);
        int added = 0, updated = 0;
        foreach (var bar in bars)
        {
            if (existing.TryGetValue(bar.SessionDate, out var current))
            {
                if (!allowOverwrite) continue;
                if (current.Open == bar.Open && current.High == bar.High && current.Low == bar.Low && current.Close == bar.Close && current.AdjustedClose == bar.AdjustedClose && current.Volume == bar.Volume) continue;
                current.Open = bar.Open; current.High = bar.High; current.Low = bar.Low; current.Close = bar.Close; current.AdjustedClose = bar.AdjustedClose; current.Volume = bar.Volume; current.Currency = bar.Currency; current.UpdatedAtUtc = now; current.IsFinal = true;
                updated++;
            }
            else
            {
                bar.Id = 0; bar.UpdatedAtUtc = now; bar.IsFinal = true;
                dbContext.DailyBars.Add(bar); added++;
            }
        }

        // Expected sessions the provider did not return become "unavailable" once they are old enough, so ordinary syncs stop retrying them.
        // Dates before the first bar of an initial/backfill request are pre-inception history and are covered by EarliestRequestedDate instead.
        var returned = bars.Select(x => x.SessionDate).Concat(existing.Keys).ToHashSet();
        var firstReturned = bars.Count == 0 ? (DateOnly?)null : bars[0].SessionDate;
        var historical = (segment.Reason & (SyncReason.Initial | SyncReason.Backfill)) != 0;
        var cutoff = UsEquityTradingCalendar.ExchangeDate(now - UnavailableAfter);
        var knownUnavailable = (await dbContext.UnavailableSessions.Where(x => x.Symbol == symbol && x.Source == Source && x.SessionDate >= segment.From && x.SessionDate <= segment.To).ToListAsync(cancellationToken)).ToDictionary(x => x.SessionDate);
        foreach (var date in UsEquityTradingCalendar.Sessions(segment.From, Min(segment.To, latestCompleted)))
        {
            if (returned.Contains(date))
            {
                if (knownUnavailable.TryGetValue(date, out var resolved)) dbContext.UnavailableSessions.Remove(resolved);
                continue;
            }
            if (date > cutoff || (historical && (firstReturned is null || date < firstReturned))) continue;
            if (knownUnavailable.TryGetValue(date, out var known)) known.CheckedAtUtc = now;
            else dbContext.UnavailableSessions.Add(new UnavailableSession { Symbol = symbol, Source = Source, SessionDate = date, CheckedAtUtc = now });
        }

        var state = await GetOrCreateStateAsync(symbol, cancellationToken);
        state.EarliestRequestedDate = state.EarliestRequestedDate is { } e && e <= segment.From ? e : segment.From;
        state.LatestRequestedDate = state.LatestRequestedDate is { } l && l >= segment.To ? l : segment.To;
        state.LastAttemptUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await RefreshCachedRangeAsync(state, symbol, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("{Symbol}/{Source} {Reason} {From}..{To}: {Added} added, {Updated} updated", symbol, Source, segment.Reason, segment.From, segment.To, added, updated);
        return (added, updated);
    }

    private async Task UpdateStateAsync(string symbol, bool success, string? error, CancellationToken cancellationToken)
    {
        var state = await GetOrCreateStateAsync(symbol, cancellationToken);
        var now = timeProvider.GetUtcNow();
        state.LastAttemptUtc = now;
        if (success) { state.LastSuccessfulSyncUtc = now; state.LastError = null; }
        else state.LastError = error is { Length: > 1000 } ? error[..1000] : error;
        await RefreshCachedRangeAsync(state, symbol, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RefreshCachedRangeAsync(MarketDataSyncState state, string symbol, CancellationToken cancellationToken)
    {
        var query = dbContext.DailyBars.Where(x => x.Symbol == symbol && x.Source == Source);
        state.EarliestCachedDate = await query.MinAsync(x => (DateOnly?)x.SessionDate, cancellationToken);
        state.LatestCachedDate = await query.MaxAsync(x => (DateOnly?)x.SessionDate, cancellationToken);
    }

    private async Task<MarketDataSyncState> GetOrCreateStateAsync(string symbol, CancellationToken cancellationToken)
    {
        var state = await dbContext.MarketDataSyncStates.SingleOrDefaultAsync(x => x.Symbol == symbol && x.Source == Source, cancellationToken)
            ?? dbContext.MarketDataSyncStates.Local.SingleOrDefault(x => x.Symbol == symbol && x.Source == Source);
        if (state is null) { state = new MarketDataSyncState { Symbol = symbol, Source = Source }; dbContext.MarketDataSyncStates.Add(state); }
        return state;
    }

    private string Normalize(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        if (!provider.SupportedSymbols.Contains(normalized)) throw new ArgumentException($"Symbol '{normalized}' is not supported by {Source}.", nameof(symbol));
        return normalized;
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a <= b ? a : b;
}
