namespace Citrus.Trading;

/// <summary>Tracks signed holdings, average entry price, and cumulative realized trading profit for a substrategy.</summary>
public sealed record Position(string Substrategy, Instrument Instrument, decimal Quantity,
    decimal AveragePrice, decimal RealizedPnl);
