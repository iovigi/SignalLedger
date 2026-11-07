using SignalLedger.Infrastructure.Providers;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Infrastructure.Tests;

public sealed class MarketDataSyncPlannerTests
{
    private static HashSet<DateOnly> Sessions(DateOnly from, DateOnly to) => UsEquityTradingCalendar.Sessions(from, to).ToHashSet();
    private static SyncPlanInput Input(HashSet<DateOnly> cached, DateOnly desiredStart, DateOnly latest, SyncMode mode = SyncMode.Incremental, HashSet<DateOnly>? unavailable = null, DateOnly? earliestRequested = null, int reconciliation = 0) =>
        new(cached, unavailable ?? [], earliestRequested ?? (cached.Count == 0 ? null : cached.Min()), cached.Count == 0 ? null : cached.Max(), desiredStart, latest, reconciliation, mode);

    [Fact]
    public void EmptyCache_RequestsFullHistoryInBoundedChunks()
    {
        var plan = MarketDataSyncPlanner.Plan(new([], new HashSet<DateOnly>(), null, null, new(1996, 11, 6), new(2026, 11, 6), 0, SyncMode.Incremental));

        Assert.Equal(4, plan.Segments.Count); // 10-year chunks: 3 full decades plus the final session
        Assert.All(plan.Segments, x => Assert.Equal(SyncReason.Initial, x.Reason));
        Assert.Equal(new DateOnly(1996, 11, 6), plan.Segments[0].From);
        Assert.Equal(new DateOnly(2026, 11, 6), plan.Segments[^1].To);
        Assert.All(plan.Segments.Zip(plan.Segments.Skip(1)), pair => Assert.Equal(pair.First.To.AddDays(1), pair.Second.From));
    }

    [Fact]
    public void CachedThroughFriday_RequestsOnlySessionsAfterIt()
    {
        // The spec example: ^NDX cached 1996-01-01..2026-11-06, today 2026-11-09 after the close.
        var cached = Sessions(new(1996, 1, 2), new(2026, 11, 6));
        var plan = MarketDataSyncPlanner.Plan(Input(cached, new(1996, 1, 2), new(2026, 11, 9)));

        var segment = Assert.Single(plan.Segments);
        Assert.Equal((new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9), SyncReason.Tail), (segment.From, segment.To, segment.Reason));
        Assert.Equal(1, plan.MissingSessions);
    }

    [Fact]
    public void CurrentCache_ProducesEmptyPlan()
    {
        var cached = Sessions(new(2020, 1, 2), new(2026, 11, 6));
        Assert.True(MarketDataSyncPlanner.Plan(Input(cached, new(2020, 1, 2), new(2026, 11, 6))).UpToDate);
    }

    [Fact]
    public void WeekendAndHolidays_AreNeverRequested()
    {
        // Wednesday 2026-11-25 cached; Thanksgiving 2026-11-26 is closed; latest completed session before Friday's close is the 25th.
        var cached = Sessions(new(2026, 11, 2), new(2026, 11, 25));
        Assert.True(MarketDataSyncPlanner.Plan(Input(cached, new(2026, 11, 2), UsEquityTradingCalendar.LatestCompletedSession(new(2026, 11, 27, 15, 0, 0, TimeSpan.Zero)))).UpToDate);
        // Friday 2026-11-06 cached; checked on Sunday.
        var friday = Sessions(new(2026, 11, 2), new(2026, 11, 6));
        Assert.True(MarketDataSyncPlanner.Plan(Input(friday, new(2026, 11, 2), UsEquityTradingCalendar.LatestCompletedSession(new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero)))).UpToDate);
    }

    [Fact]
    public void HistoricalGap_RequestsOnlyTheMissingSession()
    {
        HashSet<DateOnly> cached = [new(2020, 1, 2), new(2020, 1, 3), new(2020, 1, 7)];
        var plan = MarketDataSyncPlanner.Plan(Input(cached, new(2020, 1, 2), new(2020, 1, 7)));

        var segment = Assert.Single(plan.Segments);
        Assert.Equal((new DateOnly(2020, 1, 6), new DateOnly(2020, 1, 6), SyncReason.Gap), (segment.From, segment.To, segment.Reason));
    }

    [Fact]
    public void SeparateGaps_BecomeSeparateMinimalRanges_AndConsecutiveSessionsMerge()
    {
        var cached = Sessions(new(2021, 3, 1), new(2021, 3, 31));
        cached.Remove(new(2021, 3, 5)); cached.Remove(new(2021, 3, 8)); cached.Remove(new(2021, 3, 9)); cached.Remove(new(2021, 3, 22));
        var plan = MarketDataSyncPlanner.Plan(Input(cached, new(2021, 3, 1), new(2021, 3, 31)));

        Assert.Equal([(new DateOnly(2021, 3, 5), new DateOnly(2021, 3, 9)), (new DateOnly(2021, 3, 22), new DateOnly(2021, 3, 22))], plan.Segments.Select(x => (x.From, x.To)).ToArray());
        Assert.Equal(4, plan.MissingSessions);
    }

    [Fact]
    public void UnavailableSessions_AreSkippedByIncrementalSync_ButRetriedByRepair()
    {
        HashSet<DateOnly> cached = [new(2020, 1, 2), new(2020, 1, 3), new(2020, 1, 7)];
        HashSet<DateOnly> unavailable = [new(2020, 1, 6)];

        Assert.True(MarketDataSyncPlanner.Plan(Input(cached, new(2020, 1, 2), new(2020, 1, 7), unavailable: unavailable)).UpToDate);
        var repair = MarketDataSyncPlanner.Plan(Input(cached, new(2020, 1, 2), new(2020, 1, 7), SyncMode.RepairGaps, unavailable));
        Assert.Equal(new DateOnly(2020, 1, 6), Assert.Single(repair.Segments).From);
    }

    [Fact]
    public void PreInceptionHistory_IsNotRetriedOnceRequested()
    {
        // QQQ starts in 1999; a 30-year request already went back to 1996 and returned nothing before 1999-03-10.
        var cached = Sessions(new(1999, 3, 10), new(2026, 11, 6));
        var plan = MarketDataSyncPlanner.Plan(Input(cached, new(1996, 11, 6), new(2026, 11, 6), earliestRequested: new(1996, 11, 6)));
        Assert.True(plan.UpToDate);
    }

    [Fact]
    public void LongerHistoricalWindow_BackfillsOnlyTheOlderRange()
    {
        var cached = Sessions(new(2016, 1, 4), new(2026, 11, 6));
        var plan = MarketDataSyncPlanner.Plan(Input(cached, new(2010, 1, 4), new(2026, 11, 6)));
        var segment = Assert.Single(plan.Segments);
        Assert.Equal((new DateOnly(2010, 1, 4), new DateOnly(2016, 1, 3), SyncReason.Backfill), (segment.From, segment.To, segment.Reason));
    }

    [Fact]
    public void Reconciliation_RefreshesOnlyTheConfiguredRecentWindow()
    {
        var cached = Sessions(new(2026, 1, 2), new(2026, 11, 6));
        var none = MarketDataSyncPlanner.Plan(Input(cached, new(2026, 1, 2), new(2026, 11, 6)));
        var plan = MarketDataSyncPlanner.Plan(Input(cached, new(2026, 1, 2), new(2026, 11, 6), reconciliation: 3));

        Assert.True(none.UpToDate);
        var segment = Assert.Single(plan.Segments);
        Assert.Equal((new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 6), SyncReason.Reconciliation), (segment.From, segment.To, segment.Reason));
        Assert.Equal(0, plan.MissingSessions);
    }

    [Fact]
    public void Refresh_IsExplicitAndClippedToCompletedSessions()
    {
        var plan = MarketDataSyncPlanner.Plan(new([], new HashSet<DateOnly>(), null, null, new(2000, 1, 3), new(2026, 11, 6), 0, SyncMode.Refresh, new(2026, 10, 1), new(2026, 12, 31)));
        var segment = Assert.Single(plan.Segments);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 6), SyncReason.Refresh), (segment.From, segment.To, segment.Reason));
    }
}

public sealed class TradingCalendarTests
{
    [Theory]
    [InlineData(2021, 12, 31, true)]   // New Year's Day 2022 fell on Saturday: NYSE stayed open.
    [InlineData(2010, 12, 31, true)]
    [InlineData(2023, 1, 2, false)]    // Sunday New Year's Day observed on Monday.
    [InlineData(2020, 7, 3, false)]    // Saturday Independence Day observed on Friday.
    [InlineData(2022, 6, 20, false)]   // Juneteenth observed.
    [InlineData(1997, 1, 20, true)]    // MLK Day not a market holiday before 1998.
    [InlineData(2012, 10, 29, false)]  // Hurricane Sandy.
    public void Calendar_MatchesNyseSessions(int year, int month, int day, bool open) =>
        Assert.Equal(open, UsEquityTradingCalendar.IsTradingSession(new(year, month, day)));

    [Theory]
    [InlineData("2026-11-09T22:59:00Z", "2026-11-06")] // 17:59 EST: Monday not settled yet.
    [InlineData("2026-11-09T23:00:00Z", "2026-11-09")] // 18:00 EST.
    [InlineData("2026-07-06T21:59:00Z", "2026-07-02")] // 17:59 EDT; Friday July 3 is the observed holiday.
    [InlineData("2026-07-06T22:00:00Z", "2026-07-06")] // 18:00 EDT (daylight saving time).
    [InlineData("2026-11-08T12:00:00Z", "2026-11-06")] // Sunday.
    public void LatestCompletedSession_UsesExchangeTimeAndDaylightSaving(string now, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), UsEquityTradingCalendar.LatestCompletedSession(DateTimeOffset.Parse(now)));
}
