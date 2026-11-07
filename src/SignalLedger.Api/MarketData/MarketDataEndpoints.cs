using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Providers;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Api.MarketData;

public sealed record MarketDataJobRequest(string? Symbol = null, IReadOnlyList<string>? Symbols = null);
public sealed record MarketDataRefreshRequest(string Symbol, DateOnly From, DateOnly To);
public sealed record MarketDataJobResponse(Guid Id, MarketDataJobType Type, MarketDataJobTrigger Trigger, string Symbol, string Source, DateOnly? RequestedFrom, DateOnly? RequestedTo, RunStatus Status,
    int CompletedSteps, int TotalSteps, double Progress, int RecordsAdded, int RecordsUpdated, int ProviderRequests, int RetryCount, bool CancellationRequested, string? Message, string? Error,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc, DateTimeOffset? NotBeforeUtc);
public sealed record EnqueueResponse(IReadOnlyList<MarketDataJobResponse> Jobs, IReadOnlyList<string> DuplicatesOf);
public sealed record InstrumentSyncStatus(string Symbol, string DisplayName, string Source, bool ProviderSupported, DateOnly? EarliestCachedDate, DateOnly? LatestCachedDate, int CachedBars,
    int MissingSessions, int UnavailableSessions, int PendingRequests, bool UpToDate, DateTimeOffset? LastSuccessfulSyncUtc, DateTimeOffset? LastAttemptUtc, string? LastError);
public sealed record MarketDataStatusResponse(EffectiveMarketDataSettings Settings, DateOnly LatestCompletedSession, bool HasAnyProviderData, IReadOnlyList<InstrumentSyncStatus> Instruments, IReadOnlyList<MarketDataJobResponse> ActiveJobs);
public sealed record CandleResponse(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, decimal? AdjustedClose, long? Volume);
public sealed record CandlePage(string Symbol, string Source, int Page, int PageSize, int Total, IReadOnlyList<CandleResponse> Items);

public static class MarketDataEndpoints
{
    public const string ProviderRateLimit = "provider";

    public static void MapMarketDataEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/market-data", async (MarketDataSettingsService settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct))).WithName("GetMarketDataSettings");

        app.MapPut("/api/settings/market-data", async (MarketDataSettingsUpdate update, MarketDataSettingsService settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.UpdateAsync(update, ct)); }
            catch (SettingsForbiddenException exception) { return Forbidden("Settings change not allowed", exception.Message); }
            catch (SettingsValidationException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid settings", detail: exception.Message); }
        }).RequireRateLimiting(ProviderRateLimit).WithName("UpdateMarketDataSettings");

        app.MapGet("/api/market-data/status", GetStatusAsync).WithName("MarketDataStatus");

        app.MapGet("/api/market-data/instruments", async (MarketDataCoverageService coverage, CancellationToken ct) => Results.Ok(await coverage.GetAllAsync(ct))).WithName("MarketDataInstruments");

        app.MapGet("/api/market-data/jobs", async (MarketDataJobService jobs, int? page, int? pageSize, bool? active, CancellationToken ct) =>
        {
            var p = page ?? 1; var size = pageSize ?? 25;
            if (p < 1 || size is < 1 or > 100) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid pagination", detail: "page must be positive and pageSize between 1 and 100.");
            return Results.Ok((await jobs.ListAsync(p, size, active == true, ct)).Select(ToResponse));
        }).WithName("MarketDataJobs");

        app.MapGet("/api/market-data/jobs/{id:guid}", async (Guid id, MarketDataJobService jobs, CancellationToken ct) =>
            await jobs.GetAsync(id, ct) is { } job ? Results.Ok(ToResponse(job)) : Results.NotFound()).WithName("MarketDataJob");

        app.MapPost("/api/market-data/jobs/{id:guid}/cancel", async (Guid id, MarketDataJobService jobs, MarketDataSettingsService settings, CancellationToken ct) =>
        {
            var effective = await settings.GetAsync(ct);
            if (!effective.DownloadOperationsAllowed) return Forbidden("Operation not allowed", effective.DownloadLockReason!);
            return await jobs.CancelAsync(id, ct) is { } job ? Results.Accepted($"/api/market-data/jobs/{id}", ToResponse(job)) : Results.NotFound();
        }).RequireRateLimiting(ProviderRateLimit).WithName("CancelMarketDataJob");

        app.MapPost("/api/market-data/download", (string? symbol, HttpRequest http, MarketDataJobService jobs, MarketDataSettingsService settings, CancellationToken ct) =>
            EnqueueAsync(MarketDataJobType.Download, symbol, http, jobs, settings, ct)).RequireRateLimiting(ProviderRateLimit).WithName("DownloadMarketData");
        app.MapPost("/api/market-data/sync", (string? symbol, HttpRequest http, MarketDataJobService jobs, MarketDataSettingsService settings, CancellationToken ct) =>
            EnqueueAsync(MarketDataJobType.Sync, symbol, http, jobs, settings, ct)).RequireRateLimiting(ProviderRateLimit).WithName("SyncMarketData");
        app.MapPost("/api/market-data/repair", (string? symbol, HttpRequest http, MarketDataJobService jobs, MarketDataSettingsService settings, CancellationToken ct) =>
            EnqueueAsync(MarketDataJobType.Repair, symbol, http, jobs, settings, ct)).RequireRateLimiting(ProviderRateLimit).WithName("RepairMarketData");

        app.MapPost("/api/market-data/refresh", async (MarketDataRefreshRequest request, MarketDataJobService jobs, MarketDataSettingsService settings, TimeProvider time, CancellationToken ct) =>
        {
            var effective = await settings.GetAsync(ct);
            if (Deny(effective) is { } denied) return denied;
            var symbol = request.Symbol?.Trim().ToUpperInvariant() ?? "";
            if (!YahooChartAdapter.AllowedSymbols.Contains(symbol)) return BadRequest("Symbol is not supported by the Yahoo provider.");
            var latest = UsEquityTradingCalendar.LatestCompletedSession(time.GetUtcNow());
            if (request.To < request.From || request.To > latest || request.From < latest.AddYears(-effective.MaximumYears))
                return BadRequest($"Refresh range must be ordered, end no later than {latest:yyyy-MM-dd} and start within {effective.MaximumYears} years.");
            var result = await jobs.EnqueueAsync(MarketDataJobType.Refresh, symbol, YahooChartAdapter.SourceId, request.From, request.To, MarketDataJobTrigger.Manual, ct);
            return Results.Accepted($"/api/market-data/jobs/{result.Job.Id}", new EnqueueResponse([ToResponse(result.Job)], result.Duplicate ? [symbol] : []));
        }).RequireRateLimiting(ProviderRateLimit).WithName("RefreshMarketData");

        // Literal routes above take precedence over this parameterized one.
        app.MapGet("/api/market-data/{symbol}", async (string symbol, string? source, DateOnly? from, DateOnly? to, int? page, int? pageSize, SignalLedgerDbContext db, CancellationToken ct) =>
        {
            var normalized = symbol.Trim().ToUpperInvariant(); var src = string.IsNullOrWhiteSpace(source) ? YahooChartAdapter.SourceId : source.Trim();
            var p = page ?? 1; var size = pageSize ?? 500;
            if (p < 1 || size is < 1 or > 5_000) return BadRequest("page must be positive and pageSize between 1 and 5000.");
            if (from > to) return BadRequest("from must not be after to.");
            if (!await db.Instruments.AnyAsync(x => x.Symbol == normalized, ct)) return Results.NotFound();
            var query = db.DailyBars.AsNoTracking().Where(x => x.Symbol == normalized && x.Source == src);
            if (from is { } f) query = query.Where(x => x.SessionDate >= f);
            if (to is { } t) query = query.Where(x => x.SessionDate <= t);
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(x => x.SessionDate).Skip((p - 1) * size).Take(size).Select(x => new CandleResponse(x.SessionDate, x.Open, x.High, x.Low, x.Close, x.AdjustedClose, x.Volume)).ToListAsync(ct);
            return Results.Ok(new CandlePage(normalized, src, p, size, total, items));
        }).WithName("MarketDataCandles");
    }

    private static async Task<IResult> GetStatusAsync(MarketDataSettingsService settings, SignalLedgerDbContext db, MarketDataSyncService sync, MarketDataJobService jobs, TimeProvider time, CancellationToken ct)
    {
        var effective = await settings.GetAsync(ct);
        var instruments = await db.Instruments.AsNoTracking().OrderBy(x => x.Symbol).ToListAsync(ct);
        var states = await db.MarketDataSyncStates.AsNoTracking().Where(x => x.Source == sync.Source).ToDictionaryAsync(x => x.Symbol, ct);
        var counts = await db.DailyBars.AsNoTracking().Where(x => x.Source == sync.Source).GroupBy(x => x.Symbol)
            .Select(g => new { Symbol = g.Key, Count = g.Count(), First = g.Min(x => x.SessionDate), Last = g.Max(x => x.SessionDate) }).ToDictionaryAsync(x => x.Symbol, ct);
        var unavailable = await db.UnavailableSessions.AsNoTracking().Where(x => x.Source == sync.Source).GroupBy(x => x.Symbol).Select(g => new { Symbol = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Symbol, x => x.Count, ct);
        var rows = new List<InstrumentSyncStatus>();
        foreach (var instrument in instruments)
        {
            var supported = YahooChartAdapter.AllowedSymbols.Contains(instrument.Symbol);
            states.TryGetValue(instrument.Symbol, out var state); counts.TryGetValue(instrument.Symbol, out var count);
            int missing = 0, pending = 0; var upToDate = false;
            if (supported)
            {
                // Planning reads SQLite only; it never contacts the provider.
                var plan = await sync.PlanAsync(instrument.Symbol, new(SyncMode.Incremental, HistoricalYears: effective.HistoricalYears), ct);
                missing = plan.MissingSessions; pending = plan.Segments.Count; upToDate = count is not null && plan.UpToDate;
            }
            rows.Add(new(instrument.Symbol, instrument.DisplayName, sync.Source, supported, count?.First, count?.Last, count?.Count ?? 0, missing, unavailable.GetValueOrDefault(instrument.Symbol), pending, upToDate,
                state?.LastSuccessfulSyncUtc, state?.LastAttemptUtc, state?.LastError));
        }
        var active = await jobs.ListAsync(1, 50, activeOnly: true, ct);
        return Results.Ok(new MarketDataStatusResponse(effective, UsEquityTradingCalendar.LatestCompletedSession(time.GetUtcNow()), counts.Count > 0, rows, active.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> EnqueueAsync(MarketDataJobType type, string? querySymbol, HttpRequest http, MarketDataJobService jobs, MarketDataSettingsService settings, CancellationToken ct)
    {
        var effective = await settings.GetAsync(ct);
        if (Deny(effective) is { } denied) return denied;
        // The JSON body is optional (an empty POST means "all provider instruments"), so it is read manually.
        MarketDataJobRequest? request = null;
        if (http.ContentLength is > 0 || http.HasJsonContentType())
        {
            if (!http.HasJsonContentType()) return BadRequest("Request body must be JSON.");
            try { request = await http.ReadFromJsonAsync<MarketDataJobRequest>(ct); }
            catch (System.Text.Json.JsonException) { return BadRequest("Request body is not valid JSON."); }
        }
        var requested = new List<string>();
        if (!string.IsNullOrWhiteSpace(querySymbol)) requested.Add(querySymbol);
        if (!string.IsNullOrWhiteSpace(request?.Symbol)) requested.Add(request.Symbol);
        if (request?.Symbols is { } many) requested.AddRange(many);
        if (requested.Count > 10) return BadRequest("At most 10 symbols per request.");
        var symbols = requested.Count == 0 ? YahooChartAdapter.AllowedSymbols.Order(StringComparer.Ordinal).ToArray() : requested.Select(x => x.Trim().ToUpperInvariant()).Distinct().ToArray();
        if (symbols.FirstOrDefault(x => !YahooChartAdapter.AllowedSymbols.Contains(x)) is { } invalid) return BadRequest($"Symbol '{invalid}' is not supported by the Yahoo provider.");
        var created = new List<MarketDataJobResponse>(); var duplicates = new List<string>();
        foreach (var symbol in symbols)
        {
            var result = await jobs.EnqueueAsync(type, symbol, YahooChartAdapter.SourceId, null, null, MarketDataJobTrigger.Manual, ct);
            created.Add(ToResponse(result.Job)); if (result.Duplicate) duplicates.Add(symbol);
        }
        return Results.Accepted("/api/market-data/jobs", new EnqueueResponse(created, duplicates));
    }

    private static IResult? Deny(EffectiveMarketDataSettings effective) =>
        !effective.DownloadOperationsAllowed ? Forbidden("Provider sync disabled", effective.DownloadLockReason!)
        : !effective.YahooEnabled ? Forbidden("Provider disabled", effective.YahooDisabledReason!)
        : null;

    private static IResult Forbidden(string title, string detail) => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: title, detail: detail);
    private static IResult BadRequest(string detail) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid market-data request", detail: detail);

    public static MarketDataJobResponse ToResponse(MarketDataJob x) => new(x.Id, x.Type, x.Trigger, x.Symbol, x.Source, x.RequestedFrom, x.RequestedTo, x.Status, x.CompletedSteps, x.TotalSteps,
        x.Status == RunStatus.Completed ? 1 : x.TotalSteps == 0 ? 0 : Math.Round((double)x.CompletedSteps / x.TotalSteps, 4), x.RecordsAdded, x.RecordsUpdated, x.ProviderRequests, x.RetryCount,
        x.CancellationRequested, x.Message, x.Error, x.CreatedAtUtc, x.StartedAtUtc, x.CompletedAtUtc, x.NotBeforeUtc);
}
