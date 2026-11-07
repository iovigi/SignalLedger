namespace SignalLedger.Infrastructure.Reporting;

/// <summary>NYSE/Nasdaq regular-session calendar (full-day closures only; early closes still produce a daily bar).</summary>
public static class UsEquityTradingCalendar
{
    public const string ExchangeTimeZoneId = "America/New_York";

    /// <summary>A session's daily bar is treated as final only after this exchange-local time on the session date.</summary>
    public static readonly TimeOnly SessionSettledAt = new(18, 0);

    private static readonly Lazy<TimeZoneInfo> ExchangeTimeZone = new(() =>
        TimeZoneInfo.TryFindSystemTimeZoneById(ExchangeTimeZoneId, out var zone) ? zone : TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

    private static readonly HashSet<DateOnly> ExceptionalClosures = new()
    {
        new(2001, 9, 11), new(2001, 9, 12), new(2001, 9, 13), new(2001, 9, 14),
        new(2004, 6, 11), new(2007, 1, 2), new(2012, 10, 29), new(2012, 10, 30),
        new(2018, 12, 5), new(2025, 1, 9)
    };

    public static TimeZoneInfo TimeZone => ExchangeTimeZone.Value;

    /// <summary>Exchange-local calendar date of an instant (daylight-saving aware).</summary>
    public static DateOnly ExchangeDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    /// <summary>The most recent session whose daily bar is complete at <paramref name="nowUtc"/>. Today's session counts only once it has settled.</summary>
    public static DateOnly LatestCompletedSession(DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, TimeZone).DateTime;
        var date = DateOnly.FromDateTime(local);
        return IsTradingSession(date) && TimeOnly.FromDateTime(local) >= SessionSettledAt ? date : PreviousSession(date);
    }

    public static DateOnly PreviousSession(DateOnly date)
    {
        do date = date.AddDays(-1); while (!IsTradingSession(date));
        return date;
    }

    public static DateOnly NextSession(DateOnly date)
    {
        do date = date.AddDays(1); while (!IsTradingSession(date));
        return date;
    }

    public static IEnumerable<DateOnly> Sessions(DateOnly first, DateOnly last)
    {
        for (var date = first; date <= last; date = date.AddDays(1))
            if (IsTradingSession(date))
                yield return date;
    }

    public static bool IsTradingSession(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || ExceptionalClosures.Contains(date)) return false;
        return !Holidays(date.Year).Contains(date);
    }

    private static HashSet<DateOnly> Holidays(int year)
    {
        var holidays = new HashSet<DateOnly>
        {
            NewYearsDay(year),
            NthWeekday(year, 2, DayOfWeek.Monday, 3),
            GoodFriday(year),
            LastWeekday(year, 5, DayOfWeek.Monday),
            Observed(new(year, 7, 4)),
            NthWeekday(year, 9, DayOfWeek.Monday, 1),
            NthWeekday(year, 11, DayOfWeek.Thursday, 4),
            Observed(new(year, 12, 25))
        };
        if (year >= 1998) holidays.Add(NthWeekday(year, 1, DayOfWeek.Monday, 3));
        if (year >= 2022) holidays.Add(Observed(new(year, 6, 19)));
        return holidays;
    }

    // NYSE does not close on the preceding Friday when New Year's Day falls on a Saturday
    // (2010-12-31 and 2021-12-31 were full sessions); a Sunday holiday moves to Monday.
    private static DateOnly NewYearsDay(int year)
    {
        var date = new DateOnly(year, 1, 1);
        return date.DayOfWeek == DayOfWeek.Sunday ? date.AddDays(1) : date;
    }

    private static DateOnly Observed(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date
    };

    private static DateOnly NthWeekday(int year, int month, DayOfWeek day, int occurrence)
    {
        var date = new DateOnly(year, month, 1);
        while (date.DayOfWeek != day) date = date.AddDays(1);
        return date.AddDays(7 * (occurrence - 1));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek day)
    {
        var date = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        while (date.DayOfWeek != day) date = date.AddDays(-1);
        return date;
    }

    private static DateOnly GoodFriday(int year) => EasterSunday(year).AddDays(-2);

    private static DateOnly EasterSunday(int year)
    {
        var a = year % 19; var b = year / 100; var c = year % 100; var d = b / 4; var e = b % 4;
        var f = (b + 8) / 25; var g = (b - f + 1) / 3; var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4; var k = c % 4; var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451; var month = (h + l - 7 * m + 114) / 31;
        return new(year, month, (h + l - 7 * m + 114) % 31 + 1);
    }
}
