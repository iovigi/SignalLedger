using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalLedger.Domain.MarketData;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Infrastructure.Providers;

public interface IMarketDataProvider
{
    /// <summary>Stable identifier stored as <see cref="DailyBar.Source"/>; values from different providers are never mixed.</summary>
    string ProviderId { get; }
    IReadOnlySet<string> SupportedSymbols { get; }
    /// <summary>Returns final daily bars with session dates in [<paramref name="from"/>, <paramref name="to"/>] (both inclusive).</summary>
    Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>
/// Optional adapter for Yahoo's unofficial, undocumented chart endpoint. It is not a supported public API.
/// Every call checks the access policy first, so a disabled provider never issues an HTTP request.
/// </summary>
public sealed class YahooChartAdapter(HttpClient httpClient, IMarketDataAccessPolicy accessPolicy, ILogger<YahooChartAdapter>? logger = null) : IMarketDataProvider
{
    public const string SourceId = "YahooChart";
    public static readonly IReadOnlySet<string> AllowedSymbols = new HashSet<string>(StringComparer.Ordinal) { "^NDX", "^IXIC", "^GSPC", "QQQ" };
    private readonly ILogger logger = logger ?? NullLogger<YahooChartAdapter>.Instance;

    public string ProviderId => SourceId;
    public IReadOnlySet<string> SupportedSymbols => AllowedSymbols;

    public async Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var access = await accessPolicy.GetYahooAccessAsync(cancellationToken);
        if (!access.Allowed) throw new ProviderDisabledException(access.Reason ?? "Yahoo provider is disabled.");
        var options = access.Options;
        var normalized = symbol.Trim().ToUpperInvariant();
        if (!AllowedSymbols.Contains(normalized)) throw new ArgumentException("Symbol is not in the configured provider allowlist.", nameof(symbol));
        if (to < from) throw new ArgumentException("End date must not precede start date.");
        if (to.DayNumber - from.DayNumber > options.MaximumYears * 366) throw new ArgumentOutOfRangeException(nameof(from), $"Provider range cannot exceed {options.MaximumYears} years.");

        // period2 is exclusive, so request through the start of the day after `to`. One extra day on each side absorbs
        // UTC/exchange-time offsets; bars outside [from, to] by exchange date are discarded after parsing.
        var period1 = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var uri = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(normalized)}?period1={period1}&period2={period2}&interval=1d&events=history&includeAdjustedClose=true";
        var maxAttempts = Math.Clamp(options.MaxAttempts, 1, 6);

        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 1, 120)));
            HttpResponseMessage response;
            try
            {
                logger.LogInformation("Yahoo Chart request {Symbol} {From}..{To} attempt {Attempt}/{MaxAttempts}", normalized, from, to, attempt, maxAttempts);
                response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or OperationCanceledException)
            {
                if (attempt < maxAttempts) { await DelayAsync(Backoff(options, attempt), cancellationToken); continue; }
                throw new MarketDataProviderException(exception is OperationCanceledException ? $"Yahoo Chart request timed out after {options.RequestTimeoutSeconds}s." : $"Yahoo Chart request failed: {exception.Message}", transient: true);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                        var bars = await ParseAsync(stream, normalized, from, to, timeout.Token);
                        logger.LogInformation("Yahoo Chart returned {Count} bars for {Symbol} {From}..{To}", bars.Count, normalized, from, to);
                        return bars;
                    }
                    catch (JsonException exception) { throw new MarketDataProviderException($"Yahoo Chart returned malformed JSON: {exception.Message}"); }
                    catch (KeyNotFoundException) { throw new MarketDataProviderException("Yahoo Chart response is missing required fields."); }
                    catch (InvalidOperationException exception) { throw new MarketDataProviderException($"Yahoo Chart response has an unexpected shape: {exception.Message}"); }
                }

                var retryAfter = RetryAfter(response);
                var transient = IsTransient(response.StatusCode);
                if (transient && attempt < maxAttempts)
                {
                    var delay = retryAfter ?? Backoff(options, attempt);
                    if (delay <= TimeSpan.FromSeconds(Math.Max(0, options.MaxRetryAfterSeconds)))
                    {
                        logger.LogWarning("Yahoo Chart HTTP {Status} for {Symbol}; retrying in {Delay}", (int)response.StatusCode, normalized, delay);
                        await DelayAsync(delay, cancellationToken);
                        continue;
                    }
                }
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new MarketDataProviderException($"Yahoo Chart request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}). {Limit(detail)}", response.StatusCode, transient, retryAfter);
            }
        }
    }

    internal static async Task<IReadOnlyList<DailyBar>> ParseAsync(Stream stream, string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var chart = document.RootElement.GetProperty("chart");
        if (chart.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
            throw new MarketDataProviderException($"Yahoo Chart returned an error: {Limit(error.ToString())}");
        var result = chart.GetProperty("result");
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) return [];
        var series = result[0];
        var meta = series.GetProperty("meta");
        if (meta.TryGetProperty("symbol", out var returnedSymbol) && returnedSymbol.GetString() is { } returned && !string.Equals(returned, symbol, StringComparison.OrdinalIgnoreCase))
            throw new MarketDataProviderException($"Yahoo Chart returned data for '{returned}' instead of '{symbol}'.");
        var currency = meta.TryGetProperty("currency", out var currencyNode) ? currencyNode.GetString() ?? "USD" : "USD";
        if (!series.TryGetProperty("timestamp", out var timestamps) || timestamps.ValueKind != JsonValueKind.Array) return [];
        var indicators = series.GetProperty("indicators");
        var quote = indicators.GetProperty("quote")[0];
        var opens = quote.GetProperty("open"); var highs = quote.GetProperty("high"); var lows = quote.GetProperty("low"); var closes = quote.GetProperty("close");
        var volumes = quote.TryGetProperty("volume", out var volumeNode) ? volumeNode : default;
        JsonElement? adjusted = null;
        if (indicators.TryGetProperty("adjclose", out var adjustedArray) && adjustedArray.GetArrayLength() > 0) adjusted = adjustedArray[0].GetProperty("adjclose");
        var count = timestamps.GetArrayLength();
        if (opens.GetArrayLength() != count || highs.GetArrayLength() != count || lows.GetArrayLength() != count || closes.GetArrayLength() != count)
            throw new MarketDataProviderException("Yahoo Chart returned OHLC arrays whose lengths differ from the timestamp array.");

        var byDate = new SortedDictionary<DateOnly, DailyBar>();
        for (var i = 0; i < count; i++)
        {
            // Yahoo emits null rows for some non-sessions; they are skipped, never filled.
            if (!TryDecimal(opens[i], out var open) || !TryDecimal(highs[i], out var high) || !TryDecimal(lows[i], out var low) || !TryDecimal(closes[i], out var close)) continue;
            if (open <= 0 || low <= 0 || high < Math.Max(open, close) || low > Math.Min(open, close))
                throw new MarketDataProviderException($"Provider returned invalid OHLC values at index {i}.");
            var date = UsEquityTradingCalendar.ExchangeDate(DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()));
            if (date < from || date > to) continue;
            long? volume = volumes.ValueKind == JsonValueKind.Array && i < volumes.GetArrayLength() && volumes[i].ValueKind is JsonValueKind.Number && volumes[i].TryGetInt64(out var v) ? v : null;
            if (volume < 0) throw new MarketDataProviderException($"Provider returned a negative volume at index {i}.");
            decimal? adjustedClose = adjusted is { } values && i < values.GetArrayLength() && TryDecimal(values[i], out var a) && a > 0 ? a : null;
            // A later row for the same exchange date (e.g. Yahoo's live bar) replaces the earlier one.
            byDate[date] = new DailyBar
            {
                Symbol = symbol, SessionDate = date, Open = open, High = high, Low = low, Close = close, AdjustedClose = adjustedClose, Volume = volume,
                Currency = currency, Source = SourceId, UpdatedAtUtc = DateTimeOffset.UtcNow, IsFinal = true
            };
        }
        return byDate.Values.ToArray();
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
    private static TimeSpan Backoff(YahooProviderOptions options, int attempt) => TimeSpan.FromMilliseconds(Math.Max(0, options.RetryBaseDelayMilliseconds) * Math.Pow(2, attempt - 1));
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date) { var wait = date - DateTimeOffset.UtcNow; return wait < TimeSpan.Zero ? TimeSpan.Zero : wait; }
        return null;
    }
    /// <summary>
    /// Yahoo serializes prices as single-precision floats widened to double (909.92 arrives as 909.9199829101562).
    /// When a value is exactly representable as a float, its shortest round-trip float text is the intended price.
    /// </summary>
    internal static bool TryDecimal(JsonElement element, out decimal value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
        var single = (float)number;
        if ((double)single == number && float.IsFinite(single))
            return decimal.TryParse(single.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        return element.TryGetDecimal(out value);
    }
    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static string Limit(string value) => value.Length <= 500 ? value : value[..500];
}

public sealed class ProviderDisabledException(string message) : InvalidOperationException(message);
public sealed class MarketDataProviderException(string message, HttpStatusCode? statusCode = null, bool transient = false, TimeSpan? retryAfter = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public bool IsTransient { get; } = transient;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
