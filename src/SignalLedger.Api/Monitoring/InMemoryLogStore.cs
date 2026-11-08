using System.Collections.Concurrent;
using System.Globalization;

namespace SignalLedger.Api.Monitoring;

public sealed record CapturedLogEntry(
    long Sequence,
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    int EventId,
    string? EventName,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, string?> Properties,
    IReadOnlyList<string> Scopes,
    int ThreadId);

public sealed record LogQuery(LogLevel MinLevel = LogLevel.Trace, string? Category = null, string? Contains = null, long? AfterSequence = null, DateTimeOffset? SinceUtc = null, int Take = 500);

public sealed record LogQueryResult(long OldestRetainedSequence, long LatestSequence, long TotalCaptured, long Dropped, int Capacity, int Returned, bool Truncated, IReadOnlyList<CapturedLogEntry> Entries);

/// <summary>
/// Bounded in-memory ring buffer of recent log entries for the diagnostics endpoint. Oldest entries are discarded once
/// <see cref="Capacity"/> is reached, so memory use is fixed. Entries do not survive a process restart; persistent job
/// history lives in SQLite and is reported separately.
/// </summary>
public sealed class InMemoryLogStore(int capacity)
{
    private const int MaxMessageLength = 8_000;
    private const int MaxExceptionLength = 16_000;
    private readonly ConcurrentQueue<CapturedLogEntry> entries = new();
    private long sequence;
    private long dropped;

    public int Capacity { get; } = Math.Clamp(capacity, 100, 100_000);
    public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

    public void Add(DateTimeOffset timestamp, LogLevel level, string category, EventId eventId, string message, Exception? exception, IReadOnlyDictionary<string, string?> properties, IReadOnlyList<string> scopes)
    {
        var entry = new CapturedLogEntry(Interlocked.Increment(ref sequence), timestamp, level, category, eventId.Id, eventId.Name, Truncate(message, MaxMessageLength),
            exception is null ? null : Truncate(exception.ToString(), MaxExceptionLength), properties, scopes, Environment.CurrentManagedThreadId);
        entries.Enqueue(entry);
        while (entries.Count > Capacity && entries.TryDequeue(out _)) Interlocked.Increment(ref dropped);
    }

    public IReadOnlyDictionary<string, int> CountByLevel() =>
        entries.ToArray().GroupBy(x => x.Level).OrderBy(x => x.Key).ToDictionary(x => x.Key.ToString(), x => x.Count());

    public LogQueryResult Query(LogQuery query)
    {
        var snapshot = entries.ToArray();
        var take = Math.Clamp(query.Take, 1, 5_000);
        IEnumerable<CapturedLogEntry> filtered = snapshot.Where(x => x.Level >= query.MinLevel);
        if (!string.IsNullOrWhiteSpace(query.Category)) filtered = filtered.Where(x => x.Category.Contains(query.Category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.Contains))
            filtered = filtered.Where(x => x.Message.Contains(query.Contains, StringComparison.OrdinalIgnoreCase) || (x.Exception?.Contains(query.Contains, StringComparison.OrdinalIgnoreCase) ?? false));
        if (query.AfterSequence is { } after) filtered = filtered.Where(x => x.Sequence > after);
        if (query.SinceUtc is { } since) filtered = filtered.Where(x => x.TimestampUtc >= since);
        var matching = filtered.ToArray();
        // When tailing (afterSequence) return the oldest new entries first so nothing is skipped; otherwise the most recent ones.
        var selected = query.AfterSequence is not null ? matching.Take(take).ToArray() : matching.Skip(Math.Max(0, matching.Length - take)).ToArray();
        return new(snapshot.Length == 0 ? 0 : snapshot[0].Sequence, Interlocked.Read(ref sequence), Interlocked.Read(ref sequence), Interlocked.Read(ref dropped), Capacity, selected.Length, selected.Length < matching.Length, selected);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…[truncated]");

    internal static string? Format(object? value) => value switch
    {
        null => null,
        string s => s.Length > 2_000 ? s[..2_000] : s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}

/// <summary>Logging provider that feeds <see cref="InMemoryLogStore"/>. Its levels are configured under <c>Logging:Monitoring</c>.</summary>
[ProviderAlias("Monitoring")]
public sealed class InMemoryLoggerProvider(InMemoryLogStore store) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, store, () => scopes);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
    public void Dispose() { }

    private sealed class CapturingLogger(string category, InMemoryLogStore store, Func<IExternalScopeProvider> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes().Push(state);
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var (key, value) in pairs)
                        if (key != "{OriginalFormat}" && properties.Count < 32) properties[key] = InMemoryLogStore.Format(value);
                var scopeList = new List<string>();
                scopes().ForEachScope((scope, list) => { if (list.Count < 16 && scope is not null) list.Add(InMemoryLogStore.Format(scope) ?? ""); }, scopeList);
                store.Add(DateTimeOffset.UtcNow, logLevel, category, eventId, formatter(state, exception), exception, properties, scopeList);
            }
            catch
            {
                // Diagnostics capture must never break the code that is logging.
            }
        }
    }
}
