using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using System.Text;
using System.Text.Json.Serialization;
using SignalLedger.Api.Backtests;
using SignalLedger.Api.Experiments;
using SignalLedger.Infrastructure.Imports;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Providers;
using SignalLedger.Infrastructure.Reporting;

var builder = WebApplication.CreateBuilder(args);

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
});
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
var yahooOptions = builder.Configuration.GetSection("SignalLedger:Yahoo").Get<YahooProviderOptions>() ?? new();
builder.Services.AddSingleton(yahooOptions);
builder.Services.AddHttpClient<IMarketDataProvider, YahooChartAdapter>();
builder.Services.AddScoped<MarketDataSyncService>();
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

app.MapGet("/api/instruments", async (MarketDataCoverageService coverage, CancellationToken cancellationToken) =>
    Results.Ok(await coverage.GetAllAsync(cancellationToken))).WithName("Instruments");

app.MapGet("/api/instruments/{symbol}/coverage", async (string symbol, MarketDataCoverageService coverage, CancellationToken cancellationToken) =>
    await coverage.GetAsync(symbol, cancellationToken) is { } result ? Results.Ok(result) : Results.NotFound())
    .WithName("InstrumentCoverage");

app.MapPost("/api/imports/csv", async (IFormFile file, string symbol, IConfiguration configuration, SignalLedgerDbContext db, CsvImportService importer, CancellationToken cancellationToken) =>
{
    if (configuration.GetValue<bool>("SignalLedger:PublicDemoMode"))
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

app.MapPost("/api/market-data/sync", async (string symbol, DateOnly from, DateOnly to, IConfiguration configuration, MarketDataSyncService sync, CancellationToken cancellationToken) =>
{
    if (configuration.GetValue<bool>("SignalLedger:PublicDemoMode"))
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Provider sync disabled", detail: "Provider sync is disabled in public demo mode.");
    try { return Results.Accepted(value: await sync.SyncAsync(symbol, from, to, cancellationToken)); }
    catch (ProviderDisabledException exception) { return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Provider disabled", detail: exception.Message); }
    catch (ArgumentException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid sync request", detail: exception.Message); }
}).RequireRateLimiting("writes").WithName("SyncMarketData");

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
