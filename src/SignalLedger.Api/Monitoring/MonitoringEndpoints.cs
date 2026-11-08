using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Diagnostics;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Providers;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Api.Monitoring;

/// <summary>Section <c>SignalLedger:Monitoring</c>. Read on every request, so editing appsettings.json switches the endpoint on or off without a restart.</summary>
public sealed record MonitoringOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public int LogCapacity { get; set; } = 5_000;
}

public enum FindingSeverity { Error, Warning, Info }
public sealed record Finding(FindingSeverity Severity, string Code, string Message);

/// <summary>
/// Operator diagnostics: configuration with the provider that supplied each value, effective market-data settings, why Yahoo is
/// (not) active, cache coverage per source, sync state and plans, job history, background-worker heartbeats, database facts and
/// captured application logs. Disabled unless <c>SignalLedger:Monitoring:Enabled</c> is true and an API key of at least
/// <see cref="MinimumKeyLength"/> characters is configured; the key must be sent in the <c>X-Monitoring-Key</c> header (or as a
/// bearer token). While disabled the routes answer exactly like any unknown API route. Read-only: nothing here contacts a provider
/// or changes state.
/// </summary>
public static partial class MonitoringEndpoints
{
    public const string SectionName = "SignalLedger:Monitoring";
    public const string HeaderName = "X-Monitoring-Key";
    public const string RateLimitPolicy = "monitoring";
    public const int MinimumKeyLength = 32;
    private static readonly DateTimeOffset ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static MonitoringOptions ReadOptions(IConfiguration configuration) => configuration.GetSection(SectionName).Get<MonitoringOptions>() ?? new();

    public static void MapMonitoringEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/monitoring").AddEndpointFilter(AuthorizeAsync).RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();
        group.MapGet("", GetSnapshotAsync).WithName("MonitoringSnapshot");
        group.MapGet("/logs", (InMemoryLogStore logs, string? minLevel, string? category, string? contains, long? afterSequence, DateTimeOffset? sinceUtc, int? take) =>
        {
            if (!TryParseLevel(minLevel, LogLevel.Trace, out var level)) return BadRequest("minLevel must be Trace, Debug, Information, Warning, Error or Critical.");
            if (take is < 1 or > 5_000) return BadRequest("take must be between 1 and 5000.");
            return Results.Ok(logs.Query(new(level, category, contains, afterSequence, sinceUtc, take ?? 500)));
        }).WithName("MonitoringLogs");
        group.MapGet("/settings", GetSettingsAsync).WithName("MonitoringSettings");
        group.MapGet("/jobs", async (SignalLedgerDbContext db, string? status, string? symbol, int? take, CancellationToken ct) =>
        {
            if (take is < 1 or > 1_000) return BadRequest("take must be between 1 and 1000.");
            var query = db.MarketDataJobs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<RunStatus>(status, true, out var parsed)) return BadRequest("status must be Pending, Running, Completed, Failed or Cancelled.");
                query = query.Where(x => x.Status == parsed);
            }
            if (!string.IsNullOrWhiteSpace(symbol)) { var s = symbol.Trim().ToUpperInvariant(); query = query.Where(x => x.Symbol == s); }
            return Results.Ok(await query.OrderByDescending(x => x.CreatedAtUtc).Take(take ?? 100).ToListAsync(ct));
        }).WithName("MonitoringJobs");
    }

    private static async ValueTask<object?> AuthorizeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var options = ReadOptions(http.RequestServices.GetRequiredService<IConfiguration>());
        // Switched off, or switched on without a usable key: fail closed and look like any other unknown API route.
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey) || options.ApiKey.Trim().Length < MinimumKeyLength)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "API endpoint not found", type: "https://httpstatuses.com/404");
        if (!KeyMatches(PresentedKey(http.Request), options.ApiKey.Trim()))
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SignalLedger.Api.Monitoring")
                .LogWarning("Rejected monitoring request from {RemoteIp}: missing or invalid API key", http.Connection.RemoteIpAddress?.ToString());
            http.Response.Headers.WWWAuthenticate = $"ApiKey header=\"{HeaderName}\"";
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Monitoring API key required", detail: $"Send the configured key in the {HeaderName} header.");
        }
        http.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }

    private static string? PresentedKey(HttpRequest request)
    {
        var header = request.Headers[HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(header)) return header.Trim();
        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
    }

    /// <summary>Constant-time comparison of fixed-length digests, so neither content nor length of the key leaks through timing.</summary>
    internal static bool KeyMatches(string? presented, string expected) =>
        !string.IsNullOrEmpty(presented) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static async Task<IResult> GetSnapshotAsync(HttpContext http, SignalLedgerDbContext db, MarketDataSettingsService settings, MarketDataSyncService sync, InMemoryLogStore logs,
        BackgroundWorkerMonitor workers, IConfiguration configuration, IHostEnvironment environment, TimeProvider time, string? logLevel, int? logTake, CancellationToken ct)
    {
        if (!TryParseLevel(logLevel, LogLevel.Debug, out var level)) return BadRequest("logLevel must be Trace, Debug, Information, Warning, Error or Critical.");
        if (logTake is < 0 or > 5_000) return BadRequest("logTake must be between 0 and 5000.");
        var now = time.GetUtcNow();
        var effective = await settings.RefreshAsync(ct);
        var latestSession = UsEquityTradingCalendar.LatestCompletedSession(now);

        var overrides = await db.ApplicationSettings.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct);
        var instruments = await db.Instruments.AsNoTracking().OrderBy(x => x.Symbol).ToListAsync(ct);
        var coverage = await db.DailyBars.AsNoTracking().GroupBy(x => new { x.Symbol, x.Source })
            .Select(g => new { g.Key.Symbol, g.Key.Source, Bars = g.Count(), First = g.Min(x => x.SessionDate), Last = g.Max(x => x.SessionDate) }).ToListAsync(ct);
        var syncStates = await db.MarketDataSyncStates.AsNoTracking().ToListAsync(ct);
        var unavailable = await db.UnavailableSessions.AsNoTracking().GroupBy(x => new { x.Symbol, x.Source }).Select(g => new { g.Key.Symbol, g.Key.Source, Count = g.Count() }).ToListAsync(ct);

        var instrumentRows = new List<object>();
        var yahooPlans = new Dictionary<string, SyncPlan>();
        foreach (var instrument in instruments)
        {
            var supported = YahooChartAdapter.AllowedSymbols.Contains(instrument.Symbol);
            object? plan = null; string? planError = null;
            if (supported)
            {
                try
                {
                    // Planning reads SQLite only; it never contacts the provider.
                    var p = await sync.PlanAsync(instrument.Symbol, new(SyncMode.Incremental, HistoricalYears: effective.HistoricalYears, ReconciliationSessions: effective.RecentSessionReconciliationDays), ct);
                    yahooPlans[instrument.Symbol] = p;
                    plan = new { p.UpToDate, p.MissingSessions, p.LatestCompletedSession, Segments = p.Segments.Select(s => new { s.From, s.To, Reason = s.Reason.ToString() }) };
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { planError = $"{exception.GetType().Name}: {exception.Message}"; }
            }
            instrumentRows.Add(new
            {
                instrument.Symbol, instrument.DisplayName, AssetClass = instrument.AssetClass.ToString(), instrument.Currency, instrument.ExchangeTimeZone,
                Sources = coverage.Where(x => x.Symbol == instrument.Symbol).OrderBy(x => x.Source).Select(x => new
                {
                    x.Source, x.Bars, x.First, x.Last,
                    UnavailableSessions = unavailable.FirstOrDefault(u => u.Symbol == x.Symbol && u.Source == x.Source)?.Count ?? 0
                }),
                Yahoo = new
                {
                    ProviderSupported = supported,
                    SyncState = syncStates.FirstOrDefault(x => x.Symbol == instrument.Symbol && x.Source == YahooChartAdapter.SourceId),
                    IncrementalPlan = plan, PlanError = planError
                }
            });
        }

        var jobs = await db.MarketDataJobs.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(50).ToListAsync(ct);
        var jobCounts = await db.MarketDataJobs.AsNoTracking().GroupBy(x => new { x.Source, x.Status }).Select(g => new { g.Key.Source, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
        var everYahooJobs = jobCounts.Where(x => x.Source == YahooChartAdapter.SourceId).Sum(x => x.Count);
        var experiments = (await db.ExperimentJobs.AsNoTracking().Select(x => new { x.Id, x.Symbol, x.Source, x.Status, x.CompletedCells, x.TotalCells, x.CancellationRequested, x.CreatedAtUtc, x.CompletedAtUtc, x.Error }).ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAtUtc).ToList();
        var backtests = (await db.BacktestRuns.AsNoTracking().Select(x => new { x.Id, x.Symbol, x.Source, x.StrategyName, x.From, x.To, x.Status, x.CreatedAtUtc, x.Error }).ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAtUtc).ToList();
        var imports = (await db.ImportRuns.AsNoTracking().Select(x => new { x.Id, x.Source, x.Symbol, x.Status, x.RecordsAdded, x.RecordsUpdated, x.RecordsRejected, x.StartedAtUtc, x.CompletedAtUtc, x.Warning, x.Error }).ToListAsync(ct))
            .OrderByDescending(x => x.StartedAtUtc).ToList();

        var workerStatuses = workers.Snapshot();
        var database = await DescribeDatabaseAsync(db, ct);
        var jobOptions = configuration.GetSection("SignalLedger:Jobs").Get<MarketDataJobOptions>() ?? new();
        var yahooBars = coverage.Where(x => x.Source == YahooChartAdapter.SourceId).Sum(x => x.Bars);
        var findings = Diagnose(effective, configuration, yahooBars, everYahooJobs, jobs, yahooPlans, syncStates, workerStatuses, jobOptions, database.PendingMigrations, logs, now);

        return Results.Ok(new
        {
            GeneratedAtUtc = now,
            Findings = findings,
            Application = DescribeApplication(environment, now, latestSession),
            Configuration = DescribeConfiguration(configuration),
            MarketData = new
            {
                Provider = new { SourceId = YahooChartAdapter.SourceId, AllowedSymbols = YahooChartAdapter.AllowedSymbols.Order(StringComparer.Ordinal) },
                LatestCompletedSession = latestSession,
                EffectiveSettings = effective,
                StoredOverrides = overrides,
                Instruments = instrumentRows
            },
            Jobs = new
            {
                MarketData = new { CountsBySourceAndStatus = jobCounts.Select(x => new { x.Source, Status = x.Status.ToString(), x.Count }), Recent = jobs },
                Experiments = new { CountsByStatus = experiments.GroupBy(x => x.Status).ToDictionary(x => x.Key.ToString(), x => x.Count()), Recent = experiments.Take(20) },
                Backtests = new { Total = backtests.Count, Failed = backtests.Count(x => x.Status == RunStatus.Failed), Recent = backtests.Take(10) },
                Imports = new { Total = imports.Count, Recent = imports.Take(10) }
            },
            Workers = workerStatuses,
            Database = database,
            Logs = new
            {
                CapturedSinceUtc = logs.CreatedAtUtc,
                CountsByLevel = logs.CountByLevel(),
                Recent = logTake == 0 ? null : logs.Query(new(level, Take: logTake ?? 200)),
                Hint = "GET /api/monitoring/logs?minLevel=Warning&category=Yahoo&contains=QQQ&afterSequence=<latestSequence>&take=500"
            }
        });
    }

    internal static IReadOnlyList<Finding> Diagnose(EffectiveMarketDataSettings effective, IConfiguration configuration, int yahooBars, int everYahooJobs, IReadOnlyList<MarketDataJob> recentJobs,
        IReadOnlyDictionary<string, SyncPlan> plans, IReadOnlyList<MarketDataSyncState> syncStates, IReadOnlyList<WorkerStatus> workers, MarketDataJobOptions jobOptions,
        IReadOnlyList<string> pendingMigrations, InMemoryLogStore logs, DateTimeOffset now)
    {
        var findings = new List<Finding>();
        void Add(FindingSeverity severity, string code, string message) => findings.Add(new(severity, code, message));
        var publicDemo = configuration.GetValue<bool>("SignalLedger:PublicDemoMode"); var readOnly = configuration.GetValue<bool>("SignalLedger:ReadOnlyMode");
        string Source(string key) => effective.ValueSources.TryGetValue(key, out var s) ? s : "configuration";

        if (effective.ReadOnlyMode)
            Add(FindingSeverity.Error, "yahoo.blocked.read-only", $"Yahoo and all downloads are forced off because the server runs read-only (PublicDemoMode={publicDemo}, ReadOnlyMode={readOnly}). This overrides every other Yahoo setting. Set both to false (appsettings.Production.json or SignalLedger__PublicDemoMode / SignalLedger__ReadOnlyMode environment variables) to use the provider.");
        else if (!effective.YahooAuthorizedByServer)
            Add(FindingSeverity.Error, "yahoo.blocked.not-authorized", "SignalLedger:Yahoo:AuthorizedUseConfirmed is false, so Yahoo cannot be enabled by any means (including the UI).");
        else if (!effective.YahooEnabledRequested)
            Add(FindingSeverity.Warning, "yahoo.disabled", $"Yahoo is authorized but switched off (Yahoo:Enabled=false, value from {Source(MarketDataSettingsService.YahooEnabledKey)}).");
        else
            Add(FindingSeverity.Info, "yahoo.enabled", $"Yahoo provider is active. Auto-sync {(effective.AutoSyncEnabled ? $"every {effective.SyncIntervalMinutes} min" : "is off")}; history window {effective.HistoricalYears} years.");

        if (yahooBars == 0)
        {
            Add(FindingSeverity.Warning, "yahoo.no-cached-data", $"No bars with source '{YahooChartAdapter.SourceId}' are cached. The dashboard Source list only offers sources that have cached bars, so '{YahooChartAdapter.SourceId}' cannot appear until a download succeeds.");
            if (effective.YahooEnabled && !effective.DownloadOperationsAllowed)
                Add(FindingSeverity.Error, "yahoo.initial-download-blocked", $"Automatic sync never performs the initial download (it only tops up instruments that already have history), and the manual 'Download Historical Data' action is locked: {effective.DownloadLockReason} Set SignalLedger:Administration:AllowHttpDownloadOperations=true, download once, then it may be switched off again.");
            else if (effective.YahooEnabled && everYahooJobs == 0)
                Add(FindingSeverity.Warning, "yahoo.download-not-started", "Yahoo is active and downloads are allowed, but no Yahoo job has ever been queued. Start 'Download Historical Data' in the Data Manager (or POST /api/market-data/download).");
        }
        if (effective.YahooEnabled && !effective.AutoSyncEnabled)
            Add(FindingSeverity.Info, "yahoo.auto-sync-off", "Automatic sync is off; cached history will not advance unless a sync is started manually.");

        foreach (var (symbol, plan) in plans.OrderBy(x => x.Key, StringComparer.Ordinal))
            if (!plan.UpToDate && plan.MissingSessions > 0 && syncStates.Any(x => x.Symbol == symbol && x.Source == YahooChartAdapter.SourceId))
                Add(FindingSeverity.Info, "yahoo.missing-sessions", $"{symbol}: {plan.MissingSessions} completed sessions missing in {plan.Segments.Count} range(s).");
        foreach (var state in syncStates.Where(x => !string.IsNullOrWhiteSpace(x.LastError)).OrderBy(x => x.Symbol, StringComparer.Ordinal))
            Add(FindingSeverity.Warning, "sync.last-error", $"{state.Symbol}/{state.Source}: last sync attempt at {state.LastAttemptUtc:u} failed: {state.LastError}");

        var failed = recentJobs.Where(x => x.Status == RunStatus.Failed && x.CompletedAtUtc >= now.AddDays(-7)).ToArray();
        if (failed.Length > 0)
            Add(FindingSeverity.Warning, "jobs.failed", $"{failed.Length} market-data job(s) failed in the last 7 days; latest: {failed[0].Symbol} {failed[0].Type} at {failed[0].CompletedAtUtc:u}: {failed[0].Error}");
        foreach (var job in recentJobs.Where(x => x.Status == RunStatus.Pending && x.NotBeforeUtc > now))
            Add(FindingSeverity.Info, "jobs.retry-scheduled", $"Job {job.Id} ({job.Symbol}) waits for retry {job.RetryCount} until {job.NotBeforeUtc:u}: {job.Error}");
        var staleQueued = recentJobs.Where(x => x.Status == RunStatus.Pending && (x.NotBeforeUtc is null || x.NotBeforeUtc <= now) && x.CreatedAtUtc < now.AddMinutes(-10)).ToArray();
        if (staleQueued.Length > 0)
            Add(FindingSeverity.Warning, "jobs.stuck-pending", $"{staleQueued.Length} job(s) have been pending for more than 10 minutes without being claimed. Check the MarketDataJobWorker heartbeat.");

        var jobWorker = workers.FirstOrDefault(x => x.Name == BackgroundWorkerMonitor.MarketDataJobWorker);
        var staleAfter = TimeSpan.FromSeconds(Math.Max(120, 3 * Math.Clamp(jobOptions.PollIntervalSeconds, 1, 300)));
        if (jobWorker is null) Add(FindingSeverity.Warning, "worker.not-started", "MarketDataJobWorker has not reported since the process started.");
        else if (jobWorker.State == WorkerState.Disabled) Add(FindingSeverity.Warning, "worker.disabled", $"MarketDataJobWorker: {jobWorker.LastActivity}");
        else if (jobWorker.State == WorkerState.Faulted) Add(FindingSeverity.Error, "worker.faulted", $"MarketDataJobWorker faulted: {jobWorker.LastError}");
        else if (jobWorker.LastHeartbeatUtc < now - staleAfter && jobWorker.State != WorkerState.Stopped)
            Add(FindingSeverity.Warning, "worker.stalled", $"MarketDataJobWorker last heartbeat at {jobWorker.LastHeartbeatUtc:u} (> {staleAfter.TotalSeconds:0}s ago).");
        foreach (var worker in workers.Where(x => x.Name != BackgroundWorkerMonitor.MarketDataJobWorker && x.State == WorkerState.Faulted))
            Add(FindingSeverity.Error, "worker.faulted", $"{worker.Name} faulted: {worker.LastError}");

        if (pendingMigrations.Count > 0) Add(FindingSeverity.Error, "database.pending-migrations", $"Pending EF migrations: {string.Join(", ", pendingMigrations)}");
        var errors = logs.Query(new(LogLevel.Error, Take: 1));
        if (errors.Returned > 0) Add(FindingSeverity.Info, "logs.errors", $"Error-level log entries are present in the capture buffer; latest #{errors.Entries[0].Sequence} at {errors.Entries[0].TimestampUtc:u} ({errors.Entries[0].Category}): {errors.Entries[0].Message}");
        return findings.OrderBy(x => x.Severity).ToArray();
    }

    private static object DescribeApplication(IHostEnvironment environment, DateTimeOffset now, DateOnly latestSession)
    {
        using var process = Process.GetCurrentProcess();
        var assembly = typeof(Program).Assembly;
        var appPool = Environment.GetEnvironmentVariable("APP_POOL_ID");
        return new
        {
            Version = assembly.GetName().Version?.ToString(),
            InformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Environment = environment.EnvironmentName,
            environment.ContentRootPath,
            BaseDirectory = AppContext.BaseDirectory,
            Hosting = appPool is null ? "Kestrel/self-hosted" : "IIS",
            AppPoolId = appPool,
            Environment.MachineName,
            ProcessId = Environment.ProcessId,
            ProcessStartUtc,
            UptimeSeconds = Math.Round((now - ProcessStartUtc).TotalSeconds),
            ServerTimeUtc = now,
            ServerTimeZone = TimeZoneInfo.Local.Id,
            LatestCompletedSession = latestSession,
            Framework = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            WorkingSetBytes = process.WorkingSet64,
            ManagedHeapBytes = GC.GetTotalMemory(false),
            GcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            ThreadCount = process.Threads.Count
        };
    }

    private static readonly string[] ApplicationSections = ["SignalLedger", "Logging", "AllowedHosts", "urls", "Kestrel", "ConnectionStrings"];

    /// <summary>
    /// Settings view: effective market-data settings (with where each value came from), operator overrides stored in SQLite,
    /// each settings section as the application binds it, and the raw configuration values with the provider that won.
    /// <paramref name="all"/> includes every configuration key (environment variables included). Secrets are always redacted.
    /// </summary>
    private static async Task<IResult> GetSettingsAsync(SignalLedgerDbContext db, MarketDataSettingsService settings, IConfiguration configuration, IHostEnvironment environment, TimeProvider time, bool? all, CancellationToken ct)
    {
        var effective = await settings.RefreshAsync(ct);
        var monitoring = ReadOptions(configuration);
        var key = monitoring.ApiKey?.Trim() ?? "";
        return Results.Ok(new
        {
            GeneratedAtUtc = time.GetUtcNow(),
            Environment = environment.EnvironmentName,
            MarketData = new
            {
                Effective = effective,
                StoredOverrides = await db.ApplicationSettings.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct),
                Precedence = "Server restrictions (read-only/public demo, missing AuthorizedUseConfirmed) > values stored in SQLite (Data Manager) > configuration (appsettings / environment)."
            },
            Configured = new
            {
                General = new
                {
                    DataDirectory = configuration["SignalLedger:DataDirectory"],
                    PublicDemoMode = configuration.GetValue<bool>("SignalLedger:PublicDemoMode"),
                    ReadOnlyMode = configuration.GetValue<bool>("SignalLedger:ReadOnlyMode"),
                    SeedSyntheticDemo = configuration.GetValue<bool>("SignalLedger:SeedSyntheticDemo")
                },
                Yahoo = settings.ConfiguredYahoo(),
                Administration = settings.ConfiguredAdministration(),
                Jobs = configuration.GetSection("SignalLedger:Jobs").Get<MarketDataJobOptions>() ?? new(),
                JobWorkerDisabled = configuration.GetValue<bool>("SignalLedger:Jobs:DisableWorker"),
                Monitoring = new { monitoring.Enabled, monitoring.LogCapacity, ApiKeyConfigured = key.Length > 0, ApiKeyLength = key.Length, ApiKeyUsable = key.Length >= MinimumKeyLength }
            },
            Configuration = DescribeConfiguration(configuration, all == true)
        });
    }

    /// <summary>Configuration values with the provider that won. By default the application sections only; <paramref name="all"/> = every key. Secrets are redacted.</summary>
    private static object DescribeConfiguration(IConfiguration configuration, bool all = false)
    {
        var providers = (configuration as IConfigurationRoot)?.Providers.Reverse().ToArray() ?? [];
        var source = all
            ? configuration.AsEnumerable()
            : ApplicationSections.SelectMany(section => configuration.GetSection(section).AsEnumerable().Append(new(section, configuration[section])));
        var values = source
            .Where(x => x.Value is not null).DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                x.Key,
                Value = SensitiveKey().IsMatch(x.Key) ? (string.IsNullOrEmpty(x.Value) ? "" : $"[redacted, {x.Value.Length} chars]") : x.Value,
                Provider = providers.FirstOrDefault(p => p.TryGet(x.Key, out _))?.ToString()
            }).ToArray();
        return new { Providers = providers.Reverse().Select(x => x.ToString()), Values = values };
    }

    private static async Task<DatabaseInfo> DescribeDatabaseAsync(SignalLedgerDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetConnectionString();
        var path = connection is null ? null : new SqliteConnectionStringBuilder(connection).DataSource;
        long? Size(string? file) => file is not null && File.Exists(file) ? new FileInfo(file).Length : null;
        var counts = new Dictionary<string, int>
        {
            ["Instruments"] = await db.Instruments.CountAsync(ct), ["DailyBars"] = await db.DailyBars.CountAsync(ct), ["ImportRuns"] = await db.ImportRuns.CountAsync(ct),
            ["BacktestRuns"] = await db.BacktestRuns.CountAsync(ct), ["ExperimentJobs"] = await db.ExperimentJobs.CountAsync(ct), ["MarketDataJobs"] = await db.MarketDataJobs.CountAsync(ct),
            ["MarketDataSyncStates"] = await db.MarketDataSyncStates.CountAsync(ct), ["UnavailableSessions"] = await db.UnavailableSessions.CountAsync(ct), ["ApplicationSettings"] = await db.ApplicationSettings.CountAsync(ct)
        };
        return new(path, Size(path), Size(path + "-wal"), path is null ? null : Path.GetDirectoryName(path),
            (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray(), (await db.Database.GetPendingMigrationsAsync(ct)).ToArray(), counts);
    }

    private sealed record DatabaseInfo(string? Path, long? SizeBytes, long? WalSizeBytes, string? DataDirectory, IReadOnlyList<string> AppliedMigrations, IReadOnlyList<string> PendingMigrations, IReadOnlyDictionary<string, int> RowCounts);

    private static bool TryParseLevel(string? value, LogLevel fallback, out LogLevel level)
    {
        level = fallback;
        if (string.IsNullOrWhiteSpace(value)) return true;
        return Enum.TryParse(value, true, out level) && Enum.IsDefined(level) && level != LogLevel.None;
    }

    private static IResult BadRequest(string detail) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid monitoring request", detail: detail);

    [GeneratedRegex(@"(apikey|api_key|secret|password|passwd|pwd|token|connectionstring|credential|certificate|(^|[:_.])key$)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKey();
}
