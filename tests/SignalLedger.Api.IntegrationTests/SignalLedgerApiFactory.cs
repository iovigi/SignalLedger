using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace SignalLedger.Api.IntegrationTests;

public sealed class SignalLedgerApiFactory : WebApplicationFactory<Program>
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), $"signalledger-api-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["SignalLedger:DataDirectory"] = dataDirectory }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    }
}
