using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Infrastructure.Providers;

[Flags]
public enum SyncReason
{
    None = 0,
    Initial = 1,
    Backfill = 2,
    Gap = 4,
    Tail = 8,
    Reconciliation = 16,
    Refresh = 32
}

public enum SyncMode { Incremental, RepairGaps, Refresh }

/// <summary>One provider request: an inclusive date range plus the expected sessions it should fill.</summary>
public sealed record SyncSegment(DateOnly From, DateOnly To, SyncReason Reason, int ExpectedSessions);

public sealed record SyncPlan(DateOnly LatestCompletedSession, DateOnly DesiredStart, IReadOnlyList<SyncSegment> Segments, int MissingSessions)
{
    public bool UpToDate => Segments.Count == 0;
}

public sealed record SyncPlanInput(
    IReadOnlyCollection<DateOnly> CachedDates,
    IReadOnlySet<DateOnly> UnavailableSessions,
    DateOnly? EarliestRequestedDate,
    DateOnly? LatestRequestedDate,
    DateOnly DesiredStart,
    DateOnly LatestCompletedSession,
    int ReconciliationSessions,
    SyncMode Mode,
    DateOnly? RefreshFrom = null,
    DateOnly? RefreshTo = null,
    int MaxSegmentYears = 10);

/// <summary>
/// Decides which trading sessions must be requested. Uses only cached state; it never contacts a provider.
/// A cache holding every expected session through the latest completed session yields an empty plan (zero requests).
/// </summary>
public static class MarketDataSyncPlanner
{
    public static SyncPlan Plan(SyncPlanInput input)
    {
        var latest = input.LatestCompletedSession;
        var segments = new List<SyncSegment>();

        if (input.Mode == SyncMode.Refresh)
        {
            var from = input.RefreshFrom ?? throw new ArgumentException("Refresh requires a start date.");
            var to = Min(input.RefreshTo ?? latest, latest);
            if (from <= to) segments.AddRange(Split(new(from, to, SyncReason.Refresh, Count(from, to)), input.MaxSegmentYears));
            return new(latest, input.DesiredStart, segments, 0);
        }

        var cached = input.CachedDates as IReadOnlySet<DateOnly> ?? input.CachedDates.ToHashSet();
        DateOnly? earliestCached = cached.Count == 0 ? null : cached.Min();
        DateOnly? latestCached = cached.Count == 0 ? null : cached.Max();
        var skipUnavailable = input.Mode != SyncMode.RepairGaps;
        var missing = new List<(DateOnly Date, SyncReason Reason)>();

        if (input.Mode == SyncMode.Incremental)
        {
            // Backfill only history older than anything already requested; pre-inception dates are never retried.
            var boundary = Earliest(input.EarliestRequestedDate, earliestCached);
            if (boundary is null)
            {
                if (input.DesiredStart <= latest) segments.AddRange(Split(new(input.DesiredStart, latest, SyncReason.Initial, Count(input.DesiredStart, latest)), input.MaxSegmentYears));
                return new(latest, input.DesiredStart, segments, segments.Sum(x => x.ExpectedSessions));
            }
            if (input.DesiredStart < boundary.Value)
            {
                var end = boundary.Value.AddDays(-1);
                if (Count(input.DesiredStart, end) > 0) segments.AddRange(Split(new(input.DesiredStart, end, SyncReason.Backfill, Count(input.DesiredStart, end)), input.MaxSegmentYears));
            }
        }

        // Gaps inside cached history.
        if (earliestCached is { } first && latestCached is { } last)
            foreach (var date in UsEquityTradingCalendar.Sessions(first, Min(last, latest)))
                if (!cached.Contains(date) && !(skipUnavailable && input.UnavailableSessions.Contains(date)))
                    missing.Add((date, SyncReason.Gap));

        if (input.Mode == SyncMode.Incremental)
        {
            // Tail: completed sessions after the newest cached (or newest requested, when nothing was returned) date.
            var tailAfter = latestCached ?? input.LatestRequestedDate ?? input.DesiredStart.AddDays(-1);
            if (tailAfter < latest)
                foreach (var date in UsEquityTradingCalendar.Sessions(tailAfter.AddDays(1), latest))
                    if (!cached.Contains(date) && !input.UnavailableSessions.Contains(date))
                        missing.Add((date, SyncReason.Tail));

            // Optional reconciliation: re-read only the last N cached sessions.
            if (input.ReconciliationSessions > 0 && latestCached is { } newest)
            {
                var recent = cached.Where(x => x <= newest).OrderByDescending(x => x).Take(input.ReconciliationSessions);
                missing.AddRange(recent.Select(x => (x, SyncReason.Reconciliation)));
            }
        }

        segments.AddRange(Group(missing).SelectMany(x => Split(x, input.MaxSegmentYears)));
        var ordered = segments.OrderBy(x => x.From).ToArray();
        return new(latest, input.DesiredStart, ordered, missing.Count(x => x.Reason != SyncReason.Reconciliation));
    }

    /// <summary>Merges dates that are consecutive trading sessions into minimal contiguous request ranges.</summary>
    private static IEnumerable<SyncSegment> Group(IEnumerable<(DateOnly Date, SyncReason Reason)> dates)
    {
        var ordered = dates.GroupBy(x => x.Date).Select(g => (Date: g.Key, Reason: g.Aggregate(SyncReason.None, (r, x) => r | x.Reason))).OrderBy(x => x.Date).ToList();
        if (ordered.Count == 0) yield break;
        var start = ordered[0].Date; var end = start; var reason = ordered[0].Reason; var count = 1;
        for (var i = 1; i < ordered.Count; i++)
        {
            var (date, r) = ordered[i];
            if (UsEquityTradingCalendar.NextSession(end) == date) { end = date; reason |= r; count++; continue; }
            yield return new(start, end, reason, count);
            start = end = date; reason = r; count = 1;
        }
        yield return new(start, end, reason, count);
    }

    private static IEnumerable<SyncSegment> Split(SyncSegment segment, int maxYears)
    {
        var from = segment.From;
        while (from <= segment.To)
        {
            var to = Min(from.AddYears(Math.Max(1, maxYears)).AddDays(-1), segment.To);
            yield return segment with { From = from, To = to, ExpectedSessions = Count(from, to) };
            from = to.AddDays(1);
        }
    }

    private static int Count(DateOnly from, DateOnly to) => from > to ? 0 : UsEquityTradingCalendar.Sessions(from, to).Count();
    private static DateOnly Min(DateOnly a, DateOnly b) => a <= b ? a : b;
    private static DateOnly? Earliest(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a < b ? a : b;
}
