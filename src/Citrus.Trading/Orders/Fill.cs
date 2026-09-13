namespace Citrus.Trading;

/// <summary>
/// Records signed executed units and commission; execution cost is embedded in price, and internal fills transfer between substrategies.
/// </summary>
public sealed record Fill(long OrderId, string Substrategy, Instrument Instrument, DateTimeOffset Time,
    decimal Quantity, decimal Price, decimal Commission, bool Internal, bool Liquidation = false,
    decimal ExecutionCost = 0);
