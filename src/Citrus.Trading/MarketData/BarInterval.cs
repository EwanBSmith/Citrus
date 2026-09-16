namespace Citrus.Trading;

/// <summary>
/// Retains daily interval metadata in historical files; runtime boundaries reject any other value.
/// </summary>
public sealed record BarInterval(string Name, int Minutes)
{
    /// <summary>
    /// Gets the daily interval, representing one regular session for equities.
    /// </summary>
    public static BarInterval Daily { get; } = new("1d", 1440);
}
