namespace Citrus.Trading;

/// <summary>Captures cash, marked equity, exposure after netting each instrument, and virtual substrategy positions.</summary>
public sealed record PortfolioSnapshot(DateTimeOffset Time, decimal Cash, decimal Equity,
    decimal GrossExposure, IReadOnlyList<Position> Positions);
