namespace Citrus.Trading;

/// <summary>Defines a perpetual funding payment; positive rates charge longs and credit shorts at the supplied mark.</summary>
public sealed record FundingEvent(Instrument Instrument, DateTimeOffset Time, decimal Rate, decimal MarkPrice);
