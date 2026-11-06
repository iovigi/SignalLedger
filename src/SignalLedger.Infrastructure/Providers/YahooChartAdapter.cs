using System.Net;
using System.Text.Json;
using SignalLedger.Domain.MarketData;

namespace SignalLedger.Infrastructure.Providers;

public sealed record YahooProviderOptions
{
    public bool Enabled { get; set; }
    public bool AuthorizedUseConfirmed { get; set; }
    public int MaximumYears { get; set; } = 40;
    public int RequestTimeoutSeconds { get; set; } = 30;
}

public interface IMarketDataProvider
{
    Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

public sealed class YahooChartAdapter(HttpClient httpClient, YahooProviderOptions options) : IMarketDataProvider
{
    private static readonly HashSet<string> AllowedSymbols = new(StringComparer.Ordinal) { "^NDX", "^IXIC", "^GSPC", "QQQ" };

    public async Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        EnsureAuthorized();
        var normalized = symbol.Trim().ToUpperInvariant();
        if (!AllowedSymbols.Contains(normalized)) throw new ArgumentException("Symbol is not in the configured provider allowlist.", nameof(symbol));
        if (to < from) throw new ArgumentException("End date must not precede start date.");
        if (to.DayNumber - from.DayNumber > options.MaximumYears * 366) throw new ArgumentOutOfRangeException(nameof(from), $"Provider range cannot exceed {options.MaximumYears} years.");

        var period1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var uri = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(normalized)}?period1={period1}&period2={period2}&interval=1d&events=history";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 1, 120)));

        for (var attempt = 0; ; attempt++)
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                return await ParseAsync(stream, normalized, timeout.Token);
            }
            if (attempt < 2 && IsTransient(response.StatusCode))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), timeout.Token);
                continue;
            }
            var detail = await response.Content.ReadAsStringAsync(timeout.Token);
            throw new MarketDataProviderException($"Yahoo Chart request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}). {Limit(detail)}", response.StatusCode);
        }
    }

    private void EnsureAuthorized()
    {
        if (!options.Enabled) throw new ProviderDisabledException("Yahoo provider is disabled. Import an authorized CSV or enable the adapter explicitly.");
        if (!options.AuthorizedUseConfirmed) throw new ProviderDisabledException("Yahoo provider use has not been confirmed as authorized by the deployer.");
    }

    private static async Task<IReadOnlyList<DailyBar>> ParseAsync(Stream stream, string symbol, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var chart = document.RootElement.GetProperty("chart");
        if (chart.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
            throw new MarketDataProviderException($"Yahoo Chart returned an error: {error}");
        var result = chart.GetProperty("result");
        if (result.GetArrayLength() == 0) return [];
        var series = result[0];
        var currency = series.GetProperty("meta").TryGetProperty("currency", out var currencyNode) ? currencyNode.GetString() ?? "USD" : "USD";
        var timestamps = series.GetProperty("timestamp");
        var quote = series.GetProperty("indicators").GetProperty("quote")[0];
        var opens = quote.GetProperty("open"); var highs = quote.GetProperty("high"); var lows = quote.GetProperty("low"); var closes = quote.GetProperty("close");
        var volumes = quote.GetProperty("volume");
        JsonElement? adjusted = null;
        if (series.GetProperty("indicators").TryGetProperty("adjclose", out var adjustedArray) && adjustedArray.GetArrayLength() > 0) adjusted = adjustedArray[0].GetProperty("adjclose");
        var bars = new List<DailyBar>(timestamps.GetArrayLength());
        for (var i = 0; i < timestamps.GetArrayLength(); i++)
        {
            if (!TryDecimal(opens[i], out var open) || !TryDecimal(highs[i], out var high) || !TryDecimal(lows[i], out var low) || !TryDecimal(closes[i], out var close)) continue;
            if (open <= 0 || high < Math.Max(Math.Max(open, close), low) || low > Math.Min(Math.Min(open, close), high)) throw new MarketDataProviderException($"Provider returned invalid OHLC values at index {i}.");
            bars.Add(new DailyBar
            {
                Symbol = symbol, SessionDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime),
                Open = open, High = high, Low = low, Close = close,
                AdjustedClose = adjusted is { } values && TryDecimal(values[i], out var adjustedClose) ? adjustedClose : null,
                Volume = volumes[i].ValueKind is JsonValueKind.Number && volumes[i].TryGetInt64(out var volume) ? volume : null,
                Currency = currency, Source = "YahooChart", UpdatedAtUtc = DateTimeOffset.UtcNow, IsFinal = true
            });
        }
        return bars;
    }

    private static bool TryDecimal(JsonElement element, out decimal value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value);
    }
    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static string Limit(string value) => value.Length <= 500 ? value : value[..500];
}

public sealed class ProviderDisabledException(string message) : InvalidOperationException(message);
public sealed class MarketDataProviderException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
