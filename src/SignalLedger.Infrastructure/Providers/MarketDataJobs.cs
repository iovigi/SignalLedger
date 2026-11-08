using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Diagnostics;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Infrastructure.Providers;

public sealed record MarketDataJobOptions
{
    public int MaxConcurrency { get; set; } = 1;
    public int PollIntervalSeconds { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 120;
    public int MaxRetries { get; set; } = 3;
}

public sealed record EnqueueResult(MarketDataJob Job, bool Duplicate);

/// <summary>Wakes the worker immediately after a job is queued and tracks running jobs so they can be cancelled at once.</summary>
public sealed class MarketDataJobSignal
{
    private readonly SemaphoreSlim signal = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();
    public void Notify() => signal.Release();
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => signal.WaitAsync(timeout, cancellationToken);
    internal void Register(Guid id, CancellationTokenSource source) => running[id] = source;
    internal void Unregister(Guid id) => running.TryRemove(id, out _);
    public bool Cancel(Guid id) { if (!running.TryGetValue(id, out var source)) return false; try { source.Cancel(); } catch (ObjectDisposedException) { } return true; }
    public void CancelAll() { foreach (var id in running.Keys) Cancel(id); }
}

/// <summary>Creates and inspects persisted download jobs. Duplicate active jobs for the same instrument are not created.</summary>
public sealed class MarketDataJobService(SignalLedgerDbContext dbContext, MarketDataJobSignal signal, TimeProvider timeProvider)
{
    public static readonly RunStatus[] ActiveStatuses = [RunStatus.Pending, RunStatus.Running];

    public async Task<EnqueueResult> EnqueueAsync(MarketDataJobType type, string symbol, string source, DateOnly? from, DateOnly? to, MarketDataJobTrigger trigger, CancellationToken cancellationToken = default)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        var active = await dbContext.MarketDataJobs.Where(x => x.Symbol == normalized && x.Source == source && ActiveStatuses.Contains(x.Status)).OrderBy(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
        // Any active incremental job already covers download/sync/repair needs; an identical refresh range is also a duplicate.
        var duplicate = active.FirstOrDefault(x => type != MarketDataJobType.Refresh ? x.Type != MarketDataJobType.Refresh : x.Type == MarketDataJobType.Refresh && x.RequestedFrom == from && x.RequestedTo == to);
        if (duplicate is not null) return new(duplicate, true);
        var job = new MarketDataJob { Type = type, Trigger = trigger, Symbol = normalized, Source = source, RequestedFrom = from, RequestedTo = to, Status = RunStatus.Pending, CreatedAtUtc = timeProvider.GetUtcNow(), Message = "Queued" };
        dbContext.MarketDataJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        signal.Notify();
        return new(job, false);
    }

    public Task<MarketDataJob?> GetAsync(Guid id, CancellationToken cancellationToken = default) => dbContext.MarketDataJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<MarketDataJob>> ListAsync(int page, int pageSize, bool activeOnly, CancellationToken cancellationToken = default)
    {
        var query = dbContext.MarketDataJobs.AsNoTracking();
        if (activeOnly) query = query.Where(x => ActiveStatuses.Contains(x.Status));
        return await query.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
    }

    public async Task<MarketDataJob?> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.MarketDataJobs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null) return null;
        if (job.Status == RunStatus.Pending)
        {
            job.Status = RunStatus.Cancelled; job.CancellationRequested = true; job.CompletedAtUtc = timeProvider.GetUtcNow(); job.Message = "Cancelled before start";
        }
        else if (job.Status == RunStatus.Running) { job.CancellationRequested = true; job.Message = "Cancellation requested"; }
        await dbContext.SaveChangesAsync(cancellationToken);
        signal.Cancel(id);
        return job;
    }

    /// <summary>Cancels every active job of a provider (used when the provider is disabled at runtime).</summary>
    public async Task<int> CancelProviderJobsAsync(string source, string reason, CancellationToken cancellationToken = default)
    {
        var jobs = await dbContext.MarketDataJobs.Where(x => x.Source == source && ActiveStatuses.Contains(x.Status)).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.CancellationRequested = true; job.Message = reason;
            if (job.Status == RunStatus.Pending) { job.Status = RunStatus.Cancelled; job.CompletedAtUtc = timeProvider.GetUtcNow(); }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (var job in jobs) signal.Cancel(job.Id);
        return jobs.Count;
    }
}

/// <summary>
/// Executes persisted market-data jobs in the ASP.NET Core process. Jobs are claimed with a database lease, so two
/// processes sharing the SQLite file (for example during an IIS overlapped recycle) never run the same job. On start,
/// jobs left Running by a terminated process are returned to Pending and re-planned from SQLite, so only still-missing
/// ranges are requested.
/// </summary>
public sealed class MarketDataJobWorker(IServiceScopeFactory scopeFactory, MarketDataJobSignal signal, MarketDataSettingsService settings, IConfiguration configuration, TimeProvider timeProvider, ILogger<MarketDataJobWorker> logger, BackgroundWorkerMonitor? monitor = null) : BackgroundService
{
    private const string Name = BackgroundWorkerMonitor.MarketDataJobWorker;
    private readonly string workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private MarketDataJobOptions Options => configuration.GetSection("SignalLedger:Jobs").Get<MarketDataJobOptions>() ?? new();

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        settings.Changed += OnSettingsChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        monitor?.Stopped(Name);
        return base.StopAsync(cancellationToken);
    }

    private void OnSettingsChanged(EffectiveMarketDataSettings effective)
    {
        if (effective.YahooEnabled) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var count = await scope.ServiceProvider.GetRequiredService<MarketDataJobService>().CancelProviderJobsAsync(YahooChartAdapter.SourceId, "Cancelled because the Yahoo provider was disabled.");
                if (count > 0) logger.LogInformation("Cancelled {Count} Yahoo jobs after the provider was disabled", count);
            }
            catch (Exception exception) { logger.LogWarning(exception, "Could not cancel Yahoo jobs after the provider was disabled"); }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration.GetValue<bool>("SignalLedger:Jobs:DisableWorker"))
        {
            logger.LogWarning("Market-data job worker is disabled by configuration");
            monitor?.Disabled(Name, "Disabled by configuration (SignalLedger:Jobs:DisableWorker=true)");
            return;
        }
        monitor?.Started(Name);
        monitor?.Detail(Name, "workerId", workerId);
        try { await RecoverAsync(stoppingToken); }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { logger.LogError(exception, "Market-data job recovery failed"); monitor?.Error(Name, exception); }
        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            running.RemoveAll(x => x.IsCompleted);
            var options = Options;
            try
            {
                while (running.Count < Math.Clamp(options.MaxConcurrency, 1, 4) && await TryClaimAsync(options, stoppingToken) is { } claimed)
                {
                    logger.LogInformation("Claimed market-data job {JobId}", claimed);
                    monitor?.Activity(Name, $"Claimed job {claimed}");
                    running.Add(RunAsync(claimed, options, stoppingToken));
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { logger.LogError(exception, "Market-data job polling failed"); monitor?.Error(Name, exception); }
            var pollSeconds = Math.Clamp(options.PollIntervalSeconds, 1, 300);
            monitor?.Heartbeat(Name, busy: running.Count > 0, nextRunUtc: timeProvider.GetUtcNow().AddSeconds(pollSeconds));
            monitor?.Detail(Name, "runningJobs", running.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try { await signal.WaitAsync(TimeSpan.FromSeconds(pollSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        signal.CancelAll();
        try { await Task.WhenAll(running); } catch { /* individual jobs record their own state */ }
    }

    /// <summary>Returns jobs orphaned by a terminated process to Pending. Leases still held by a live process are left alone.</summary>
    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
        var now = timeProvider.GetUtcNow();
        var orphaned = await db.MarketDataJobs.Where(x => x.Status == RunStatus.Running && (x.LeaseExpiresUtc == null || x.LeaseExpiresUtc < now)).ToListAsync(cancellationToken);
        foreach (var job in orphaned)
        {
            if (job.CancellationRequested) { job.Status = RunStatus.Cancelled; job.CompletedAtUtc = now; job.Message = "Cancelled (process stopped)"; continue; }
            job.Status = RunStatus.Pending; job.LeaseOwner = null; job.LeaseExpiresUtc = null; job.Message = "Recovered after process restart; remaining work is re-planned from the cache";
            logger.LogInformation("Recovered interrupted market-data job {JobId} for {Symbol}", job.Id, job.Symbol);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    internal async Task<Guid?> TryClaimAsync(MarketDataJobOptions options, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
        var now = timeProvider.GetUtcNow();
        // Expired leases of Running jobs (owner process died without a restart of this one) become claimable again.
        var candidates = await db.MarketDataJobs.AsNoTracking()
            .Where(x => (x.Status == RunStatus.Pending && (x.NotBeforeUtc == null || x.NotBeforeUtc <= now)) || (x.Status == RunStatus.Running && x.LeaseExpiresUtc < now))
            .OrderBy(x => x.CreatedAtUtc).Select(x => new { x.Id, x.Symbol, x.Source }).Take(10).ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            // One running job per instrument at a time.
            if (await db.MarketDataJobs.AnyAsync(x => x.Id != candidate.Id && x.Symbol == candidate.Symbol && x.Source == candidate.Source && x.Status == RunStatus.Running && x.LeaseExpiresUtc >= now, cancellationToken)) continue;
            var leaseUntil = now.AddSeconds(Math.Clamp(options.LeaseSeconds, 15, 3_600));
            var id = candidate.Id;
            var claimed = await db.MarketDataJobs
                .Where(x => x.Id == id && ((x.Status == RunStatus.Pending) || (x.Status == RunStatus.Running && x.LeaseExpiresUtc < now)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RunStatus.Running).SetProperty(x => x.LeaseOwner, workerId).SetProperty(x => x.LeaseExpiresUtc, leaseUntil), cancellationToken);
            if (claimed == 1) return id;
        }
        return null;
    }

    private async Task RunAsync(Guid id, MarketDataJobOptions options, CancellationToken stoppingToken)
    {
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        signal.Register(id, jobCancellation);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
        var job = await db.MarketDataJobs.SingleAsync(x => x.Id == id, CancellationToken.None);
        var lease = TimeSpan.FromSeconds(Math.Clamp(options.LeaseSeconds, 15, 3_600));
        using var heartbeat = new PeriodicTimer(lease / 3);
        var heartbeatTask = RenewLeaseAsync(id, lease, heartbeat, jobCancellation);
        try
        {
            if (job.CancellationRequested) throw new OperationCanceledException();
            job.StartedAtUtc ??= timeProvider.GetUtcNow(); job.Message = "Running"; job.Error = null;
            await db.SaveChangesAsync(CancellationToken.None);
            var effective = await settings.GetAsync(jobCancellation.Token);
            if (!effective.YahooEnabled) throw new ProviderDisabledException(effective.YahooDisabledReason ?? "Yahoo provider is disabled.");

            // Separate scope: the sync service clears its change tracker on failure and must not detach this job entity.
            await using var syncScope = scopeFactory.CreateAsyncScope();
            var sync = syncScope.ServiceProvider.GetRequiredService<MarketDataSyncService>();
            var request = job.Type switch
            {
                MarketDataJobType.Repair => new SyncRequestOptions(SyncMode.RepairGaps, HistoricalYears: effective.HistoricalYears),
                MarketDataJobType.Refresh => new SyncRequestOptions(SyncMode.Refresh, job.RequestedFrom, job.RequestedTo, effective.HistoricalYears),
                _ => new SyncRequestOptions(SyncMode.Incremental, HistoricalYears: effective.HistoricalYears, ReconciliationSessions: effective.RecentSessionReconciliationDays)
            };
            var baseAdded = job.RecordsAdded; var baseUpdated = job.RecordsUpdated; var baseRequests = job.ProviderRequests;
            var progress = new SynchronousProgress<SyncProgress>(p =>
            {
                job.CompletedSteps = p.CompletedSegments; job.TotalSteps = p.TotalSegments;
                job.RecordsAdded = baseAdded + p.Added; job.RecordsUpdated = baseUpdated + p.Updated; job.ProviderRequests = baseRequests + p.ProviderRequests;
                if (p.Segment is { } segment) { job.Message = $"Fetched {segment.From:yyyy-MM-dd}..{segment.To:yyyy-MM-dd} ({segment.Reason})"; if (job.Type == MarketDataJobType.Refresh) job.ResumeFrom = segment.To.AddDays(1); }
                db.SaveChanges();
            });
            logger.LogInformation("Running market-data job {JobId}: {Type} {Symbol}/{Source} (trigger {Trigger}, retry {RetryCount})", id, job.Type, job.Symbol, job.Source, job.Trigger, job.RetryCount);
            var result = await sync.SyncAsync(job.Symbol, request, progress, job.Type == MarketDataJobType.Refresh ? job.ResumeFrom : null, jobCancellation.Token);
            job.Status = RunStatus.Completed; job.CompletedAtUtc = timeProvider.GetUtcNow();
            job.Message = result.UpToDate ? "Already up to date; no provider request was needed" : $"Completed: {job.RecordsAdded} added, {job.RecordsUpdated} updated";
            logger.LogInformation("Market-data job {JobId} {Symbol} completed: {Added} added, {Updated} updated, {Requests} provider requests", id, job.Symbol, job.RecordsAdded, job.RecordsUpdated, job.ProviderRequests);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested && !job.CancellationRequested)
        {
            // Host shutdown: leave the job claimable after restart. Remaining work is re-planned from SQLite.
            job.Status = RunStatus.Pending; job.Message = "Interrupted by application shutdown; will resume";
        }
        catch (OperationCanceledException)
        {
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            job.Status = RunStatus.Cancelled; job.CompletedAtUtc = timeProvider.GetUtcNow(); job.Message = "Cancelled";
        }
        catch (ProviderDisabledException exception)
        {
            // The provider was switched off before or during the job: stop safely; cached data is untouched.
            job.Status = RunStatus.Cancelled; job.Error = exception.Message; job.CompletedAtUtc = timeProvider.GetUtcNow(); job.Message = "Cancelled because the provider is disabled";
            logger.LogWarning("Market-data job {JobId} {Symbol} cancelled: {Reason}", id, job.Symbol, exception.Message);
        }
        catch (Exception exception) when (exception is MarketDataProviderException { IsTransient: true } && job.RetryCount < Math.Clamp(options.MaxRetries, 0, 10))
        {
            job.RetryCount++;
            var delay = (exception as MarketDataProviderException)?.RetryAfter ?? TimeSpan.FromMinutes(Math.Pow(2, job.RetryCount));
            job.Status = RunStatus.Pending; job.NotBeforeUtc = timeProvider.GetUtcNow() + delay; job.Error = exception.Message;
            job.Message = $"Transient provider failure; retry {job.RetryCount} after {delay.TotalSeconds:0}s";
            logger.LogWarning(exception, "Market-data job {JobId} will retry", id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Market-data job {JobId} failed", id);
            monitor?.Error(Name, exception);
            job.Status = RunStatus.Failed; job.Error = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message; job.CompletedAtUtc = timeProvider.GetUtcNow(); job.Message = "Failed; cached data was left intact";
        }
        finally
        {
            signal.Unregister(id);
            monitor?.Activity(Name, $"Job {id} {job.Type} {job.Symbol} finished as {job.Status}: {job.Message}");
            await jobCancellation.CancelAsync();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
            job.LeaseOwner = null; job.LeaseExpiresUtc = null;
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception exception) { logger.LogError(exception, "Could not persist final state of market-data job {JobId}", id); }
            signal.Notify(); // claim the next queued job immediately instead of waiting for the poll interval
        }
    }

    private async Task RenewLeaseAsync(Guid id, TimeSpan lease, PeriodicTimer timer, CancellationTokenSource jobCancellation)
    {
        while (await timer.WaitForNextTickAsync(jobCancellation.Token))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
            var until = timeProvider.GetUtcNow() + lease;
            var job = await db.MarketDataJobs.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.CancellationRequested }).SingleOrDefaultAsync(jobCancellation.Token);
            if (job?.CancellationRequested == true) { await jobCancellation.CancelAsync(); return; }
            await db.MarketDataJobs.Where(x => x.Id == id && x.LeaseOwner == workerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresUtc, until), jobCancellation.Token);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>
/// Periodically checks the local cache and queues a sync job only for instruments that are missing completed sessions.
/// Checking SQLite every interval never implies a provider request; instruments that are current are skipped.
/// Instruments with no cached history are left for an explicit "Download historical data" action.
/// </summary>
public sealed class MarketDataAutoSyncService(IServiceScopeFactory scopeFactory, MarketDataSettingsService settings, IConfiguration configuration, TimeProvider timeProvider, ILogger<MarketDataAutoSyncService> logger, BackgroundWorkerMonitor? monitor = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string name = BackgroundWorkerMonitor.MarketDataAutoSync;
        if (configuration.GetValue<bool>("SignalLedger:Jobs:DisableWorker")) { monitor?.Disabled(name, "Disabled by configuration (SignalLedger:Jobs:DisableWorker=true)"); return; }
        monitor?.Started(name);
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(60);
            try
            {
                var effective = await settings.RefreshAsync(stoppingToken);
                interval = TimeSpan.FromMinutes(effective.SyncIntervalMinutes);
                if (effective.AutoSyncEnabled)
                {
                    var queued = await RunOnceAsync(effective, stoppingToken);
                    monitor?.Activity(name, queued.Count == 0 ? "Checked cache; nothing queued" : $"Queued sync for {string.Join(", ", queued.Select(x => x.Symbol))}");
                }
                else
                {
                    var reason = !effective.YahooEnabled ? $"Skipped: Yahoo disabled ({effective.YahooDisabledReason})" : "Skipped: automatic sync is turned off";
                    logger.LogDebug("Automatic market-data check: {Reason}", reason);
                    monitor?.Activity(name, reason);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Automatic market-data check failed"); monitor?.Error(name, exception); }
            monitor?.Heartbeat(name, busy: false, nextRunUtc: timeProvider.GetUtcNow() + interval);
            try { await Task.Delay(interval, timeProvider, stoppingToken); } catch (OperationCanceledException) { break; }
        }
        monitor?.Stopped(name);
    }

    public async Task<IReadOnlyList<MarketDataJob>> RunOnceAsync(EffectiveMarketDataSettings effective, CancellationToken cancellationToken)
    {
        var queued = new List<MarketDataJob>();
        if (!effective.YahooEnabled) return queued;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<MarketDataSyncService>();
        var jobs = scope.ServiceProvider.GetRequiredService<MarketDataJobService>();
        foreach (var symbol in YahooChartAdapter.AllowedSymbols.Order(StringComparer.Ordinal))
        {
            var hasHistory = await db.MarketDataSyncStates.AnyAsync(x => x.Symbol == symbol && x.Source == sync.Source && x.LatestRequestedDate != null, cancellationToken)
                || await db.DailyBars.AnyAsync(x => x.Symbol == symbol && x.Source == sync.Source, cancellationToken);
            if (!hasHistory) { logger.LogDebug("Automatic sync skips {Symbol}: no cached history (initial download must be started explicitly)", symbol); continue; }
            var plan = await sync.PlanAsync(symbol, new(SyncMode.Incremental, HistoricalYears: effective.HistoricalYears, ReconciliationSessions: effective.RecentSessionReconciliationDays), cancellationToken);
            if (plan.UpToDate) { logger.LogDebug("{Symbol} is current through {Latest}", symbol, plan.LatestCompletedSession); continue; }
            var result = await jobs.EnqueueAsync(MarketDataJobType.Sync, symbol, sync.Source, null, null, MarketDataJobTrigger.Automatic, cancellationToken);
            if (!result.Duplicate) { queued.Add(result.Job); logger.LogInformation("Queued automatic sync for {Symbol}: {Missing} missing sessions in {Segments} ranges", symbol, plan.MissingSessions, plan.Segments.Count); }
        }
        return queued;
    }
}
