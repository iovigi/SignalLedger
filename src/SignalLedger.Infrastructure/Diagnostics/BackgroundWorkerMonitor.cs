using System.Collections.Concurrent;

namespace SignalLedger.Infrastructure.Diagnostics;

public enum WorkerState { NotStarted, Running, Idle, Disabled, Faulted, Stopped }

public sealed record WorkerStatus(
    string Name,
    WorkerState State,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastHeartbeatUtc,
    string? LastActivity,
    DateTimeOffset? LastActivityUtc,
    long Iterations,
    DateTimeOffset? NextRunUtc,
    string? LastError,
    DateTimeOffset? LastErrorUtc,
    long ErrorCount,
    IReadOnlyDictionary<string, string> Details);

/// <summary>
/// In-memory heartbeat registry for hosted background services. Workers report what they are doing so the diagnostics
/// endpoint can tell a stalled, disabled or crashed worker apart from one that simply has nothing to do.
/// It is purely observational: nothing reads it to make scheduling decisions.
/// </summary>
public sealed class BackgroundWorkerMonitor(TimeProvider timeProvider)
{
    public const string MarketDataJobWorker = "MarketDataJobWorker";
    public const string MarketDataAutoSync = "MarketDataAutoSync";
    public const string ExperimentWorker = "ExperimentWorker";

    private readonly ConcurrentDictionary<string, Entry> workers = new(StringComparer.Ordinal);

    public void Started(string name) => Update(name, e => { e.State = WorkerState.Running; e.StartedAtUtc = Now; e.LastHeartbeatUtc = Now; e.LastActivity = "Started"; e.LastActivityUtc = Now; });
    public void Disabled(string name, string reason) => Update(name, e => { e.State = WorkerState.Disabled; e.LastActivity = reason; e.LastActivityUtc = Now; e.LastHeartbeatUtc = Now; });
    public void Stopped(string name) => Update(name, e => { e.State = WorkerState.Stopped; e.LastActivity = "Stopped"; e.LastActivityUtc = Now; e.LastHeartbeatUtc = Now; });

    /// <summary>Marks one loop iteration. <paramref name="busy"/> distinguishes "working" from "idle, waiting".</summary>
    public void Heartbeat(string name, bool busy, DateTimeOffset? nextRunUtc = null) => Update(name, e =>
    {
        e.State = busy ? WorkerState.Running : WorkerState.Idle; e.LastHeartbeatUtc = Now; e.Iterations++;
        if (nextRunUtc is not null) e.NextRunUtc = nextRunUtc;
    });

    public void Activity(string name, string activity) => Update(name, e => { e.LastActivity = activity.Length > 500 ? activity[..500] : activity; e.LastActivityUtc = Now; e.LastHeartbeatUtc = Now; });

    public void Detail(string name, string key, string value) => Update(name, e => e.Details[key] = value);

    public void Error(string name, Exception exception, bool fatal = false) => Update(name, e =>
    {
        e.ErrorCount++; e.LastErrorUtc = Now; e.LastHeartbeatUtc = Now;
        var text = $"{exception.GetType().Name}: {exception.Message}";
        e.LastError = text.Length > 2000 ? text[..2000] : text;
        if (fatal) e.State = WorkerState.Faulted;
    });

    public IReadOnlyList<WorkerStatus> Snapshot() =>
        workers.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
        {
            lock (x.Value)
            {
                var e = x.Value;
                return new WorkerStatus(x.Key, e.State, e.StartedAtUtc, e.LastHeartbeatUtc, e.LastActivity, e.LastActivityUtc, e.Iterations, e.NextRunUtc, e.LastError, e.LastErrorUtc, e.ErrorCount,
                    new Dictionary<string, string>(e.Details, StringComparer.Ordinal));
            }
        }).ToArray();

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    private void Update(string name, Action<Entry> change)
    {
        var entry = workers.GetOrAdd(name, _ => new Entry());
        lock (entry) change(entry);
    }

    private sealed class Entry
    {
        public WorkerState State = WorkerState.NotStarted;
        public DateTimeOffset? StartedAtUtc, LastHeartbeatUtc, LastActivityUtc, NextRunUtc, LastErrorUtc;
        public string? LastActivity, LastError;
        public long Iterations, ErrorCount;
        public readonly Dictionary<string, string> Details = new(StringComparer.Ordinal);
    }
}
