using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Providers;
using SignalLedger.Infrastructure.Reporting;

namespace SignalLedger.Infrastructure.Tests;

public sealed class MarketDataSyncServiceTests
{
    // Monday 2026-11-09, 19:00 New York time: the Monday session has settled.
    private static readonly DateTimeOffset MondayEvening = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private static SyncRequestOptions Years(int years) => new(SyncMode.Incremental, HistoricalYears: years);

    [Fact]
    public async Task InitialDownload_ThenSecondSync_MakesZeroProviderRequests()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider(); var time = new FixedTimeProvider(MondayEvening);

        await using (var db = database.Context())
        {
            var result = await new MarketDataSyncService(db, provider, time).SyncAsync("^NDX", Years(2));
            Assert.False(result.UpToDate);
            Assert.Equal(UsEquityTradingCalendar.Sessions(new(2024, 11, 9), new(2026, 11, 9)).Count(), result.Added);
            var state = await db.MarketDataSyncStates.SingleAsync();
            Assert.Equal(new DateOnly(2026, 11, 9), state.LatestCachedDate);
            Assert.NotNull(state.LastSuccessfulSyncUtc);
            Assert.Null(state.LastError);
            Assert.Equal(RunStatus.Completed, (await db.ImportRuns.SingleAsync()).Status);
        }
        var initialCalls = provider.Calls.Count;

        await using (var db = database.Context())
        {
            var second = await new MarketDataSyncService(db, provider, time).SyncAsync("^NDX", Years(2));
            Assert.True(second.UpToDate);
            Assert.Equal(0, second.ProviderRequests);
        }
        Assert.Equal(initialCalls, provider.Calls.Count);
    }

    [Fact]
    public async Task NextDay_RequestsOnlyTheNewSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider(); var time = new FixedTimeProvider(MondayEvening);
        await using (var db = database.Context()) await new MarketDataSyncService(db, provider, time).SyncAsync("QQQ", Years(1));
        provider.Calls.Clear();

        time.Now = MondayEvening.AddDays(1); // Tuesday evening
        await using (var db = database.Context())
        {
            var result = await new MarketDataSyncService(db, provider, time).SyncAsync("QQQ", Years(1));
            Assert.Equal(1, result.Added);
        }
        var call = Assert.Single(provider.Calls);
        Assert.Equal((new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 10)), (call.From, call.To));
    }

    [Fact]
    public async Task UnsettledCurrentSessionBar_IsNotStored()
    {
        await using var database = await TestDatabase.CreateAsync();
        // Provider includes a live bar for Tuesday although the session has not settled yet.
        var provider = new RecordingProvider { Override = (s, from, to) => UsEquityTradingCalendar.Sessions(from, to.AddDays(1)).Select(d => RecordingProvider.Bar(s, d, 100)).ToArray() };
        await using var db = database.Context();
        await new MarketDataSyncService(db, provider, new FixedTimeProvider(MondayEvening.AddHours(15))).SyncAsync("QQQ", Years(1));
        Assert.Equal(new DateOnly(2026, 11, 9), await db.DailyBars.MaxAsync(x => x.SessionDate));
    }

    [Fact]
    public async Task HistoricalGap_IsRepairedWithASingleNarrowRequest()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider(); var time = new FixedTimeProvider(MondayEvening);
        await using (var db = database.Context()) await new MarketDataSyncService(db, provider, time).SyncAsync("^GSPC", Years(1));
        await using (var db = database.Context()) await db.DailyBars.Where(x => x.SessionDate == new DateOnly(2026, 3, 11)).ExecuteDeleteAsync();
        provider.Calls.Clear();

        await using (var db = database.Context())
        {
            var result = await new MarketDataSyncService(db, provider, time).SyncAsync("^GSPC", new SyncRequestOptions(SyncMode.RepairGaps, HistoricalYears: 1));
            Assert.Equal(1, result.Added);
        }
        Assert.Equal((new DateOnly(2026, 3, 11), new DateOnly(2026, 3, 11)), (Assert.Single(provider.Calls).From, provider.Calls.Single().To));
    }

    [Fact]
    public async Task SessionsTheProviderNeverReturns_AreRecordedAndNotRetried()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider(); provider.Withheld.Add(new(2026, 5, 13));
        var time = new FixedTimeProvider(MondayEvening);
        await using (var db = database.Context()) await new MarketDataSyncService(db, provider, time).SyncAsync("^IXIC", Years(1));
        provider.Calls.Clear();

        await using (var db = database.Context())
        {
            Assert.Equal(new DateOnly(2026, 5, 13), (await db.UnavailableSessions.SingleAsync()).SessionDate);
            Assert.True((await new MarketDataSyncService(db, provider, time).SyncAsync("^IXIC", Years(1))).UpToDate);
        }
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task ProviderFailure_LeavesCachedBarsIntactAndRecordsError()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider(); var time = new FixedTimeProvider(MondayEvening);
        await using (var db = database.Context()) await new MarketDataSyncService(db, provider, time).SyncAsync("QQQ", Years(1));
        int before; await using (var db = database.Context()) before = await db.DailyBars.CountAsync();

        provider.Override = (_, _, _) => throw new MarketDataProviderException("Upstream unavailable");
        time.Now = MondayEvening.AddDays(1);
        await using (var db = database.Context())
            await Assert.ThrowsAsync<MarketDataProviderException>(() => new MarketDataSyncService(db, provider, time).SyncAsync("QQQ", Years(1)));

        await using (var db = database.Context())
        {
            Assert.Equal(before, await db.DailyBars.CountAsync());
            Assert.Equal("Upstream unavailable", (await db.MarketDataSyncStates.SingleAsync()).LastError);
            Assert.Contains(await db.ImportRuns.ToListAsync(), x => x.Status == RunStatus.Failed);
        }
    }

    [Fact]
    public async Task MalformedBars_AreRejectedWithoutPartialWrites()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider
        {
            Override = (s, from, to) => UsEquityTradingCalendar.Sessions(from, to).Select((d, i) => { var bar = RecordingProvider.Bar(s, d, 100); if (i == 3) bar.High = bar.Low - 5; return bar; }).ToArray()
        };
        await using var db = database.Context();
        await Assert.ThrowsAsync<MarketDataProviderException>(() => new MarketDataSyncService(db, provider, new FixedTimeProvider(MondayEvening)).SyncAsync("QQQ", Years(1)));
        Assert.Equal(0, await db.DailyBars.CountAsync());
    }

    [Fact]
    public async Task DuplicateProviderRows_AreStoredOnce()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider { Override = (s, from, to) => UsEquityTradingCalendar.Sessions(from, to).SelectMany(d => new[] { RecordingProvider.Bar(s, d, 100), RecordingProvider.Bar(s, d, 101) }).ToArray() };
        await using var db = database.Context();
        var result = await new MarketDataSyncService(db, provider, new FixedTimeProvider(MondayEvening)).SyncAsync("QQQ", Years(1));
        Assert.Equal(result.Added, await db.DailyBars.CountAsync());
        Assert.Equal(await db.DailyBars.Select(x => x.SessionDate).Distinct().CountAsync(), await db.DailyBars.CountAsync());
    }

    [Fact]
    public async Task Refresh_UpdatesOnlyTheExplicitRange()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider(); var time = new FixedTimeProvider(MondayEvening);
        await using (var db = database.Context()) await new MarketDataSyncService(db, provider, time).SyncAsync("QQQ", Years(1));
        provider.Calls.Clear();
        provider.Override = (s, from, to) => UsEquityTradingCalendar.Sessions(from, to).Select(d => RecordingProvider.Bar(s, d, 500)).ToArray();

        await using (var db = database.Context())
        {
            var result = await new MarketDataSyncService(db, provider, time).SyncAsync("QQQ", new SyncRequestOptions(SyncMode.Refresh, new(2026, 11, 2), new(2026, 11, 6)));
            Assert.Equal(5, result.Updated);
            Assert.Equal(5, await db.DailyBars.CountAsync(x => x.Close == 500));
        }
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 6)), (Assert.Single(provider.Calls).From, provider.Calls.Single().To));
    }

    [Fact]
    public async Task ConcurrentSyncs_ForTheSameInstrument_DoNotDuplicateWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider { Delay = TimeSpan.FromMilliseconds(200) };
        var time = new FixedTimeProvider(MondayEvening); var locks = new MarketDataSyncLocks();
        async Task<MarketDataSyncResult> Run() { await using var db = database.Context(); return await new MarketDataSyncService(db, provider, time, locks).SyncAsync("QQQ", Years(1)); }

        var results = await Task.WhenAll(Run(), Run(), Run());

        Assert.Single(results, x => !x.UpToDate);
        Assert.Single(provider.Calls);
        await using var check = database.Context();
        Assert.Equal(results.Sum(x => x.Added), await check.DailyBars.CountAsync());
    }

    [Fact]
    public async Task CachedData_SurvivesReopeningTheDatabase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new RecordingProvider(); var time = new FixedTimeProvider(MondayEvening);
        await using (var db = database.Context()) await new MarketDataSyncService(db, provider, time).SyncAsync("QQQ", Years(1));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using var reopened = database.Context();
        Assert.True(await reopened.DailyBars.CountAsync() > 240);
        Assert.Equal(new DateOnly(2026, 11, 9), (await reopened.MarketDataSyncStates.SingleAsync()).LatestCachedDate);
    }
}
