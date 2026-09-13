namespace Citrus.Trading;

/// <summary>
/// Tracks the quantity, average entry price, and cumulative realized trading profit for a position of an instrument within a substrategy.
/// </summary>
public sealed record Position(string Substrategy, Instrument Instrument, decimal Quantity,
    decimal AveragePrice, decimal RealizedPnl);
