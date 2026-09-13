namespace Citrus.Trading;

/// <summary>
/// Describes a session's exchange-local date and ordinal within a complete calendar month.
/// </summary>
public sealed record TradingDay(MarketSession Session, DateOnly Date, int DayOfMonth, int DaysInMonth)
{
    /// <summary>
    /// Gets whether this is the final supplied session of its month.
    /// </summary>
    public bool IsMonthEnd => DayOfMonth == DaysInMonth;
}
