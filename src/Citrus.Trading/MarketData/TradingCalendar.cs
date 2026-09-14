namespace Citrus.Trading;

/// <summary>Indexes one market's sessions by exchange-local month, independently of instruments and price coverage.</summary>
public sealed class TradingCalendar
{
    private readonly TradingDay[] days;

    /// <summary>Snapshots session dates and ordinals; supply complete months. The exchange zone defaults to New York.</summary>
    public TradingCalendar(IEnumerable<MarketSession> sessions, TimeZoneInfo? exchangeZone = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var zone = exchangeZone ?? TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        days = sessions.OrderBy(session => session.Open)
            .Select(session => (Session: session, Date: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.Open, zone).DateTime)))
            .GroupBy(session => (session.Date.Year, session.Date.Month))
            .SelectMany(month => month.Select((session, index) => new TradingDay(session.Session, session.Date, index + 1, month.Count())))
            .ToArray();
        if (days.Length == 0) throw new InvalidOperationException("Trading-day queries require a complete market session calendar.");
    }

    /// <summary>Returns the latest opened session shifted by signed sessions; null before the first open or when the offset exceeds coverage.</summary>
    public TradingDay? TradingDay(DateTimeOffset time, int offset = 0)
    {
        var low = 0;
        var high = days.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (days[middle].Session.Open <= time) low = middle + 1;
            else high = middle - 1;
        }
        if (high < 0) return null;
        var index = (long)high + offset;
        return index >= 0 && index < days.Length ? days[(int)index] : null;
    }

    /// <summary>Returns the one-based ordinal of the latest opened market session in its month, or null before the first open.</summary>
    public int? TradingDayOfMonth(DateTimeOffset time) => TradingDay(time)?.DayOfMonth;

    /// <summary>Returns the supplied session count for the latest opened session's month, or null before the first open.</summary>
    public int? TradingDaysInMonth(DateTimeOffset time) => TradingDay(time)?.DaysInMonth;
}
