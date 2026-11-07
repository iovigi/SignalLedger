using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SignalLedger.Infrastructure.Providers;

namespace SignalLedger.Api.IntegrationTests;

public sealed class SignalLedgerApiFactory : WebApplicationFactory<Program>
{
    private readonly string dataDirectory;
    private readonly bool deleteOnDispose;
    private readonly IReadOnlyDictionary<string, string?> settings;
    private readonly HttpMessageHandler? yahooHandler;

    public SignalLedgerApiFactory() : this(Path.Combine(Path.GetTempPath(), $"signalledger-api-{Guid.NewGuid():N}"), true) { }
    private SignalLedgerApiFactory(string dataDirectory, bool deleteOnDispose, IReadOnlyDictionary<string, string?>? settings = null, HttpMessageHandler? yahooHandler = null)
    {
        this.dataDirectory = dataDirectory;
        this.deleteOnDispose = deleteOnDispose;
        this.settings = settings ?? new Dictionary<string, string?>();
        this.yahooHandler = yahooHandler;
    }

    public string DataDirectory => dataDirectory;

    public static SignalLedgerApiFactory ForPersistentDirectory(string dataDirectory, bool deleteOnDispose, IReadOnlyDictionary<string, string?>? settings = null, HttpMessageHandler? yahooHandler = null) =>
        new(dataDirectory, deleteOnDispose, settings, yahooHandler);
    public static SignalLedgerApiFactory ForSettings(IReadOnlyDictionary<string, string?> settings, HttpMessageHandler? yahooHandler = null) =>
        new(Path.Combine(Path.GetTempPath(), $"signalledger-api-{Guid.NewGuid():N}"), true, settings, yahooHandler);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>(settings) { ["SignalLedger:DataDirectory"] = dataDirectory };
            values.TryAdd("SignalLedger:Yahoo:RetryBaseDelayMilliseconds", "1");
            values.TryAdd("SignalLedger:Jobs:PollIntervalSeconds", "1");
            configuration.AddInMemoryCollection(values);
        });
        // Tests never reach the real Yahoo endpoint: every request goes to a local stub (or fails loudly).
        var handler = yahooHandler ?? new FailingHandler();
        builder.ConfigureTestServices(services => services.AddHttpClient<IMarketDataProvider, YahooChartAdapter>().ConfigurePrimaryHttpMessageHandler(() => handler));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        if (deleteOnDispose && Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected outbound request in tests: {request.RequestUri}");
    }
}
