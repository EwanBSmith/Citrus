namespace Citrus.Trading;

/// <summary>Computes indicators over values supplied in chronological order.</summary>
public static class Indicators
{
    /// <summary>Returns the mean of the last period values, or null before warmup; period must be positive.</summary>
    public static decimal? Sma(IEnumerable<decimal> source, int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        var values = source.TakeLast(period).ToArray();
        return values.Length < period ? null : values.Average();
    }
    /// <summary>Seeds from the first period values and smooths subsequent values with alpha 2/(period+1); returns null before warmup.</summary>
    public static decimal? Ema(IEnumerable<decimal> source, int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        var values = source.ToArray();
        if (values.Length < period) return null;
        var ema = values.Take(period).Average();
        var alpha = 2m / (period + 1);
        foreach (var value in values.Skip(period)) ema += alpha * (value - ema);
        return ema;
    }
}
