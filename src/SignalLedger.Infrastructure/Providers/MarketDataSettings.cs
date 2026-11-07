using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SignalLedger.Domain.MarketData;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Infrastructure.Providers;

/// <summary>Configuration-file/environment defaults for the Yahoo provider (section <c>SignalLedger:Yahoo</c>).</summary>
public sealed record YahooProviderOptions
{
    public bool Enabled { get; set; }
    /// <summary>Server-level deployer attestation. When false, Yahoo cannot be enabled by any means, including the UI.</summary>
    public bool AuthorizedUseConfirmed { get; set; }
    public bool AutoSyncEnabled { get; set; }
    public int SyncIntervalMinutes { get; set; } = 60;
    public int HistoricalYears { get; set; } = 30;
    public int RecentSessionReconciliationDays { get; set; }
    public int MaximumYears { get; set; } = 40;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 3;
    public int RetryBaseDelayMilliseconds { get; set; } = 1_000;
    public int MaxRetryAfterSeconds { get; set; } = 60;
}

/// <summary>Server-level switches for anonymous administrative HTTP operations (section <c>SignalLedger:Administration</c>).</summary>
public sealed record AdministrationOptions
{
    public bool AllowHttpSettingsChanges { get; set; }
    public bool AllowHttpDownloadOperations { get; set; }
}

public sealed record EffectiveMarketDataSettings(
    bool ReadOnlyMode,
    bool YahooAuthorizedByServer,
    bool YahooEnabledRequested,
    bool YahooEnabled,
    string? YahooDisabledReason,
    bool AutoSyncRequested,
    bool AutoSyncEnabled,
    int SyncIntervalMinutes,
    int HistoricalYears,
    int RecentSessionReconciliationDays,
    int RequestTimeoutSeconds,
    int MaximumYears,
    bool SettingsChangesAllowed,
    string? SettingsLockReason,
    bool DownloadOperationsAllowed,
    string? DownloadLockReason,
    IReadOnlyDictionary<string, string> ValueSources);

public sealed record MarketDataSettingsUpdate(bool? YahooEnabled = null, bool? AutoSyncEnabled = null, int? SyncIntervalMinutes = null, int? HistoricalYears = null, int? RecentSessionReconciliationDays = null);

public sealed class SettingsValidationException(string message) : Exception(message);
public sealed class SettingsForbiddenException(string message) : Exception(message);

/// <summary>Gate consulted by providers immediately before any network request.</summary>
public interface IMarketDataAccessPolicy
{
    ValueTask<ProviderAccess> GetYahooAccessAsync(CancellationToken cancellationToken);
}

public sealed record ProviderAccess(bool Allowed, string? Reason, YahooProviderOptions Options);

/// <summary>Fixed policy built from options only (tests and tools without a database).</summary>
public sealed class StaticMarketDataAccessPolicy(YahooProviderOptions options) : IMarketDataAccessPolicy
{
    public ValueTask<ProviderAccess> GetYahooAccessAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Access());
    private ProviderAccess Access() =>
        !options.Enabled ? new(false, "Yahoo provider is disabled. Import an authorized CSV or enable the adapter explicitly.", options)
        : !options.AuthorizedUseConfirmed ? new(false, "Yahoo provider use has not been confirmed as authorized by the deployer.", options)
        : new(true, null, options);
}

/// <summary>
/// Resolves effective market-data settings. Precedence: server restrictions (read-only mode, missing authorization attestation)
/// always win; operator values stored in SQLite override configuration defaults; configuration/environment supply the defaults.
/// Changes apply immediately without a restart.
/// </summary>
public sealed class MarketDataSettingsService(IConfiguration configuration, IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : IMarketDataAccessPolicy
{
    public const string YahooEnabledKey = "Yahoo:Enabled";
    public const string AutoSyncKey = "Yahoo:AutoSyncEnabled";
    public const string IntervalKey = "Yahoo:SyncIntervalMinutes";
    public const string YearsKey = "Yahoo:HistoricalYears";
    public const string ReconciliationKey = "Yahoo:RecentSessionReconciliationDays";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim loadLock = new(1, 1);
    private volatile OverrideSnapshot? overrides;

    public event Action<EffectiveMarketDataSettings>? Changed;

    public YahooProviderOptions ConfiguredYahoo() => configuration.GetSection("SignalLedger:Yahoo").Get<YahooProviderOptions>() ?? new();
    public AdministrationOptions ConfiguredAdministration() => configuration.GetSection("SignalLedger:Administration").Get<AdministrationOptions>() ?? new();
    public bool ReadOnlyMode => configuration.GetValue<bool>("SignalLedger:ReadOnlyMode") || configuration.GetValue<bool>("SignalLedger:PublicDemoMode");

    public async Task<EffectiveMarketDataSettings> GetAsync(CancellationToken cancellationToken = default) => Resolve(await LoadOverridesAsync(force: false, cancellationToken));

    public async ValueTask<ProviderAccess> GetYahooAccessAsync(CancellationToken cancellationToken)
    {
        var effective = await GetAsync(cancellationToken);
        var options = ConfiguredYahoo() with { RequestTimeoutSeconds = effective.RequestTimeoutSeconds };
        return new(effective.YahooEnabled, effective.YahooDisabledReason, options);
    }

    public async Task<EffectiveMarketDataSettings> UpdateAsync(MarketDataSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(cancellationToken);
        if (!current.SettingsChangesAllowed) throw new SettingsForbiddenException(current.SettingsLockReason!);
        if (update.YahooEnabled == true && !current.YahooAuthorizedByServer)
            throw new SettingsForbiddenException("The server operator has not confirmed authorized Yahoo use (SignalLedger:Yahoo:AuthorizedUseConfirmed), so Yahoo cannot be enabled from the UI.");
        if (update.SyncIntervalMinutes is < 5 or > 1_440) throw new SettingsValidationException("SyncIntervalMinutes must be between 5 and 1440.");
        if (update.HistoricalYears is { } years && (years < 1 || years > current.MaximumYears)) throw new SettingsValidationException($"HistoricalYears must be between 1 and {current.MaximumYears}.");
        if (update.RecentSessionReconciliationDays is < 0 or > 10) throw new SettingsValidationException("RecentSessionReconciliationDays must be between 0 and 10.");

        var values = new Dictionary<string, string>();
        if (update.YahooEnabled is { } enabled) values[YahooEnabledKey] = enabled ? "true" : "false";
        if (update.AutoSyncEnabled is { } auto) values[AutoSyncKey] = auto ? "true" : "false";
        if (update.SyncIntervalMinutes is { } interval) values[IntervalKey] = interval.ToString(CultureInfo.InvariantCulture);
        if (update.HistoricalYears is { } historical) values[YearsKey] = historical.ToString(CultureInfo.InvariantCulture);
        if (update.RecentSessionReconciliationDays is { } reconciliation) values[ReconciliationKey] = reconciliation.ToString(CultureInfo.InvariantCulture);

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
            var existing = await db.ApplicationSettings.Where(x => values.Keys.Contains(x.Key)).ToDictionaryAsync(x => x.Key, cancellationToken);
            foreach (var (key, value) in values)
            {
                if (existing.TryGetValue(key, out var row)) { row.Value = value; row.UpdatedAtUtc = timeProvider.GetUtcNow(); }
                else db.ApplicationSettings.Add(new ApplicationSetting { Key = key, Value = value, UpdatedAtUtc = timeProvider.GetUtcNow() });
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        var result = Resolve(await LoadOverridesAsync(force: true, cancellationToken));
        Changed?.Invoke(result);
        return result;
    }

    /// <summary>Re-reads operator overrides (for example after another process changed them).</summary>
    public async Task<EffectiveMarketDataSettings> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var before = overrides?.Values;
        var result = Resolve(await LoadOverridesAsync(force: true, cancellationToken));
        if (before is not null && !before.OrderBy(x => x.Key).SequenceEqual(overrides!.Values.OrderBy(x => x.Key))) Changed?.Invoke(result);
        return result;
    }

    private EffectiveMarketDataSettings Resolve(IReadOnlyDictionary<string, string> stored)
    {
        var yahoo = ConfiguredYahoo(); var admin = ConfiguredAdministration(); var readOnly = ReadOnlyMode;
        var sources = new Dictionary<string, string>();
        bool Bool(string key, bool fallback) { if (stored.TryGetValue(key, out var v) && bool.TryParse(v, out var b)) { sources[key] = "database"; return b; } sources[key] = "configuration"; return fallback; }
        int Int(string key, int fallback, int min, int max) { if (stored.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { sources[key] = "database"; return Math.Clamp(i, min, max); } sources[key] = "configuration"; return Math.Clamp(fallback, min, max); }

        var maximumYears = Math.Clamp(yahoo.MaximumYears, 1, 60);
        var requested = Bool(YahooEnabledKey, yahoo.Enabled);
        var autoRequested = Bool(AutoSyncKey, yahoo.AutoSyncEnabled);
        var interval = Int(IntervalKey, yahoo.SyncIntervalMinutes, 5, 1_440);
        var years = Int(YearsKey, yahoo.HistoricalYears, 1, maximumYears);
        var reconciliation = Int(ReconciliationKey, yahoo.RecentSessionReconciliationDays, 0, 10);

        string? yahooReason = readOnly ? "The server runs in read-only mode; provider downloads are disabled."
            : !yahoo.AuthorizedUseConfirmed ? "The server operator has not confirmed authorized Yahoo use (SignalLedger:Yahoo:AuthorizedUseConfirmed=false)."
            : !requested ? "Yahoo provider is disabled. Import an authorized CSV or enable the provider."
            : null;
        var yahooEnabled = yahooReason is null;
        string? settingsReason = readOnly ? "The server runs in read-only mode." : !admin.AllowHttpSettingsChanges ? "Settings changes over HTTP are disabled by the server operator (SignalLedger:Administration:AllowHttpSettingsChanges)." : null;
        string? downloadReason = readOnly ? "The server runs in read-only mode." : !admin.AllowHttpDownloadOperations ? "Download operations over HTTP are disabled by the server operator (SignalLedger:Administration:AllowHttpDownloadOperations)." : null;

        return new(readOnly, yahoo.AuthorizedUseConfirmed, requested, yahooEnabled, yahooReason, autoRequested, yahooEnabled && autoRequested,
            interval, years, reconciliation, Math.Clamp(yahoo.RequestTimeoutSeconds, 1, 120), maximumYears,
            settingsReason is null, settingsReason, downloadReason is null, downloadReason, sources);
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadOverridesAsync(bool force, CancellationToken cancellationToken)
    {
        var snapshot = overrides;
        if (!force && snapshot is not null && timeProvider.GetUtcNow() - snapshot.LoadedAtUtc < CacheLifetime) return snapshot.Values;
        await loadLock.WaitAsync(cancellationToken);
        try
        {
            snapshot = overrides;
            if (!force && snapshot is not null && timeProvider.GetUtcNow() - snapshot.LoadedAtUtc < CacheLifetime) return snapshot.Values;
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SignalLedgerDbContext>();
            var values = await db.ApplicationSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
            overrides = new(values, timeProvider.GetUtcNow());
            return values;
        }
        finally { loadLock.Release(); }
    }

    private sealed record OverrideSnapshot(IReadOnlyDictionary<string, string> Values, DateTimeOffset LoadedAtUtc);
}
