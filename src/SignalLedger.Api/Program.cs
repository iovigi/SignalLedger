var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

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

app.Map("/api/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    title: "API endpoint not found",
    type: "https://httpstatuses.com/404"));

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
