using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace SignalLedger.Api.IntegrationTests;

public sealed class SignalLedgerApiFactory : WebApplicationFactory<Program>
{
    private readonly string dataDirectory;
    private readonly bool deleteOnDispose;

    public SignalLedgerApiFactory() : this(Path.Combine(Path.GetTempPath(), $"signalledger-api-{Guid.NewGuid():N}"), true) { }
    private SignalLedgerApiFactory(string dataDirectory, bool deleteOnDispose)
    {
        this.dataDirectory = dataDirectory;
        this.deleteOnDispose = deleteOnDispose;
    }

    public static SignalLedgerApiFactory ForPersistentDirectory(string dataDirectory, bool deleteOnDispose) => new(dataDirectory, deleteOnDispose);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["SignalLedger:DataDirectory"] = dataDirectory }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        if (deleteOnDispose && Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    }
}
