namespace Citrus.Trading;

/// <summary>Breaks instrument profit into realized, unrealized, and income/fee components for a substrategy.</summary>
public sealed record InstrumentAttribution(string Substrategy, Instrument Instrument, decimal Quantity,
    decimal MarkPrice, decimal RealizedPnl, decimal UnrealizedPnl, decimal IncomeAndFees, decimal NetPnl);
