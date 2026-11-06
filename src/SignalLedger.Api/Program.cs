using Microsoft.EntityFrameworkCore;
using SignalLedger.Infrastructure.Imports;
using SignalLedger.Infrastructure.Persistence;
using SignalLedger.Infrastructure.Providers;
using SignalLedger.Infrastructure.Reporting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
var configuredDataDirectory = builder.Configuration["SignalLedger:DataDirectory"];
var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SignalLedger", "data")
    : configuredDataDirectory;
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContext<SignalLedgerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "signalledger.db")};Default Timeout=30"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CsvImportService>();
builder.Services.AddScoped<MarketDataCoverageService>();
var yahooOptions = builder.Configuration.GetSection("SignalLedger:Yahoo").Get<YahooProviderOptions>() ?? new();
builder.Services.AddSingleton(yahooOptions);
builder.Services.AddHttpClient<IMarketDataProvider, YahooChartAdapter>();
builder.Services.AddScoped<MarketDataSyncService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await DatabaseInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>());
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
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

app.MapPost("/api/market-data/sync", async (string symbol, DateOnly from, DateOnly to, MarketDataSyncService sync, CancellationToken cancellationToken) =>
{
    try { return Results.Accepted(value: await sync.SyncAsync(symbol, from, to, cancellationToken)); }
    catch (ProviderDisabledException exception) { return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Provider disabled", detail: exception.Message); }
    catch (ArgumentException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid sync request", detail: exception.Message); }
}).WithName("SyncMarketData");

app.Map("/api/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    title: "API endpoint not found",
    type: "https://httpstatuses.com/404"));

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
