namespace Citrus.Trading;

/// <summary>Defines an equity event with per-unit cash amount, conversion ratio, and successor where applicable.</summary>
public sealed record CorporateAction(string Id, Instrument Instrument, DateTimeOffset Time,
    ActionType Type, decimal? Amount = null, decimal? Ratio = null, Instrument? Successor = null);
