namespace Citrus.Trading;

/// <summary>
/// Describes a named bar duration in minutes; equity bars are clipped to session boundaries.
/// </summary>
public sealed record BarInterval(string Name, int Minutes)
{
    /// <summary>
    /// Gets the standard 60-minute interval.
    /// </summary>
    public static BarInterval Hourly { get; } = new("1h", 60);
    /// <summary>
    /// Gets the daily interval, representing one regular session for equities.
    /// </summary>
    public static BarInterval Daily { get; } = new("1d", 1440);
}
