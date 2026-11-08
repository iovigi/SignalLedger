using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using System.Text;
using System.Text.Json.Serialization;
using SignalLedger.Api.Backtests;
using SignalLedger.Api.Experiments;
using SignalLedger.Api.MarketData;
using SignalLedger.Api.Monitoring;
using SignalLedger.Infrastructure.Diagnostics;
using SignalLedger.Infrastructure.Imports;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Providers;
using SignalLedger.Infrastructure.Reporting;

// A published artifact carries its own wwwroot next to the DLL. Anchor the content root there so
// `dotnet <path>\SignalLedger.Api.dll` serves the SPA regardless of the working directory.
// Development builds (no wwwroot/index.html in the output folder) keep the default content root.
var publishedContentRoot = File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"))
    ? AppContext.BaseDirectory
    : null;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = publishedContentRoot,
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 5 * 1024 * 1024);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("writes", limiter =>
    {
        limiter.PermitLimit = 12;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // Per-client budget for provider and settings operations, partitioned by the TCP peer address only.
    // X-Forwarded-For is not trusted: no forwarded-headers middleware is configured.
    options.AddPolicy(MarketDataEndpoints.ProviderRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy(MonitoringEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
// Recent log entries are always captured in a bounded in-memory buffer (levels: Logging:Monitoring), so they are already
// there when the diagnostics endpoint is switched on. The endpoint itself is off unless SignalLedger:Monitoring:Enabled.
var logStore = new InMemoryLogStore(MonitoringEndpoints.ReadOptions(builder.Configuration).LogCapacity);
builder.Services.AddSingleton(logStore);
builder.Logging.AddProvider(new InMemoryLoggerProvider(logStore));
builder.Services.AddSingleton<BackgroundWorkerMonitor>();
builder.Services.AddOpenApi();
builder.Services.AddDbContext<SignalLedgerDbContext>((services, options) =>
{
    var configuredDataDirectory = services.GetRequiredService<IConfiguration>()["SignalLedger:DataDirectory"];
    var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SignalLedger", "data")
        : Path.GetFullPath(configuredDataDirectory);
    Directory.CreateDirectory(dataDirectory);
    options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "signalledger.db")};Default Timeout=30");
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CsvImportService>();
builder.Services.AddScoped<MarketDataCoverageService>();
builder.Services.AddSingleton<MarketDataSettingsService>();
builder.Services.AddSingleton<IMarketDataAccessPolicy>(services => services.GetRequiredService<MarketDataSettingsService>());
// Yahoo is contacted only from the server; per-attempt timeouts and bounded retries live in the adapter.
builder.Services.AddHttpClient<IMarketDataProvider, YahooChartAdapter>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SignalLedger/1.0");
});
builder.Services.AddSingleton<MarketDataSyncLocks>();
builder.Services.AddScoped<MarketDataSyncService>();
builder.Services.AddSingleton<MarketDataJobSignal>();
builder.Services.AddScoped<MarketDataJobService>();
builder.Services.AddHostedService<MarketDataJobWorker>();
builder.Services.AddHostedService<MarketDataAutoSyncService>();
builder.Services.AddScoped<BacktestService>();
builder.Services.AddSingleton<ExperimentQueue>();
builder.Services.AddScoped<ExperimentService>();
builder.Services.AddHostedService<ExperimentWorker>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await DatabaseInitializer.InitializeAsync(
        scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>(),
        builder.Configuration.GetValue<bool>("SignalLedger:SeedSyntheticDemo"));
}

if (args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase))
{
    return;
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseStaticFiles();

app.MapGet("/api/health", (IHostEnvironment environment) => Results.Ok(new
{
    status = "healthy",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
    environment = environment.EnvironmentName,
    timestampUtc = DateTimeOffset.UtcNow
})).WithName("Health");

// Lightweight liveness probe for IIS Application Initialization and load balancers.
// It never touches the provider, never triggers synchronization and exposes no configuration.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).WithName("Liveness");

app.MapGet("/api/instruments", async (MarketDataCoverageService coverage, CancellationToken cancellationToken) =>
    Results.Ok(await coverage.GetAllAsync(cancellationToken))).WithName("Instruments");

app.MapGet("/api/instruments/{symbol}/coverage", async (string symbol, MarketDataCoverageService coverage, CancellationToken cancellationToken) =>
    await coverage.GetAsync(symbol, cancellationToken) is { } result ? Results.Ok(result) : Results.NotFound())
    .WithName("InstrumentCoverage");

app.MapPost("/api/imports/csv", async (IFormFile file, string symbol, IConfiguration configuration, SignalLedgerDbContext db, CsvImportService importer, CancellationToken cancellationToken) =>
{
    if (configuration.GetValue<bool>("SignalLedger:PublicDemoMode") || configuration.GetValue<bool>("SignalLedger:ReadOnlyMode"))
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Import disabled", detail: "CSV import is disabled in public demo mode. Run locally to import your own data.");
    if (file.Length == 0 || file.Length > 5 * 1024 * 1024)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid CSV file", detail: "CSV file must be between 1 byte and 5 MB.");
    if (!string.Equals(Path.GetExtension(Path.GetFileName(file.FileName)), ".csv", StringComparison.OrdinalIgnoreCase))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid CSV file", detail: "Only .csv files are accepted.");
    var normalized = symbol.Trim().ToUpperInvariant();
    if (!await db.Instruments.AnyAsync(x => x.Symbol == normalized, cancellationToken))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid symbol", detail: "Symbol is not in the configured allowlist.");
    try
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        return Results.Ok(await importer.ImportAsync(reader, new(normalized, "UserCsv", "USD"), cancellationToken));
    }
    catch (CsvImportException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "CSV import failed", detail: exception.Message);
    }
}).DisableAntiforgery().RequireRateLimiting("writes").WithName("ImportCsv");

app.MapMarketDataEndpoints();
app.MapMonitoringEndpoints();

app.MapPost("/api/backtests", async (BacktestRequest request, BacktestService service, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.RunAsync(request, cancellationToken);
        return Results.Created($"/api/backtests/{result.Id}", result);
    }
    catch (BacktestValidationException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid backtest request", detail: exception.Message);
    }
}).RequireRateLimiting("writes").WithName("CreateBacktest");

app.MapGet("/api/backtests/{id:guid}", async (Guid id, BacktestService service, CancellationToken cancellationToken) =>
    await service.GetAsync(id, cancellationToken) is { } result ? Results.Ok(result) : Results.NotFound()).WithName("GetBacktest");

app.MapGet("/api/backtests/{id:guid}/trades", async (Guid id, int page, int pageSize, BacktestService service, CancellationToken cancellationToken) =>
{
    try { return await service.GetTradesAsync(id, page, pageSize, cancellationToken) is { } result ? Results.Ok(result) : Results.NotFound(); }
    catch (BacktestValidationException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid pagination", detail: exception.Message); }
}).WithName("GetBacktestTrades");

app.MapPost("/api/experiments", async (ExperimentRequest request, ExperimentService service, CancellationToken cancellationToken) =>
{
    try { var result = await service.CreateAsync(request, cancellationToken); return Results.Accepted($"/api/experiments/{result.Id}", result); }
    catch (BacktestValidationException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid experiment request", detail: exception.Message); }
}).RequireRateLimiting("writes").WithName("CreateExperiment");

app.MapGet("/api/experiments/{id:guid}", async (Guid id, ExperimentService service, CancellationToken cancellationToken) =>
    await service.GetAsync(id, cancellationToken) is { } result ? Results.Ok(result) : Results.NotFound()).WithName("GetExperiment");

app.MapPost("/api/experiments/{id:guid}/cancel", async (Guid id, ExperimentService service, CancellationToken cancellationToken) =>
    await service.CancelAsync(id, cancellationToken) ? Results.Accepted() : Results.NotFound()).RequireRateLimiting("writes").WithName("CancelExperiment");

app.Map("/api/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    title: "API endpoint not found",
    type: "https://httpstatuses.com/404"));

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
