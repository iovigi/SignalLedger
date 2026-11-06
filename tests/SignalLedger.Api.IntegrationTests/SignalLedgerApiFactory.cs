using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace SignalLedger.Api.IntegrationTests;

public sealed class SignalLedgerApiFactory : WebApplicationFactory<Program>
{
    private readonly string dataDirectory;
    private readonly bool deleteOnDispose;
    private readonly IReadOnlyDictionary<string, string?> settings;

    public SignalLedgerApiFactory() : this(Path.Combine(Path.GetTempPath(), $"signalledger-api-{Guid.NewGuid():N}"), true) { }
    private SignalLedgerApiFactory(string dataDirectory, bool deleteOnDispose, IReadOnlyDictionary<string, string?>? settings = null)
    {
        this.dataDirectory = dataDirectory;
        this.deleteOnDispose = deleteOnDispose;
        this.settings = settings ?? new Dictionary<string, string?>();
    }

    public static SignalLedgerApiFactory ForPersistentDirectory(string dataDirectory, bool deleteOnDispose) => new(dataDirectory, deleteOnDispose);
    public static SignalLedgerApiFactory ForSettings(IReadOnlyDictionary<string, string?> settings) =>
        new(Path.Combine(Path.GetTempPath(), $"signalledger-api-{Guid.NewGuid():N}"), true, settings);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>(settings) { ["SignalLedger:DataDirectory"] = dataDirectory };
            configuration.AddInMemoryCollection(values);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        if (deleteOnDispose && Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    }
}
