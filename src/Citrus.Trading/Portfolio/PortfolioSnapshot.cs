namespace Citrus.Trading;

/// <summary>
/// Captures cash, marked equity, exposure after netting each instrument, and virtual substrategy positions for a specific point in time.
/// </summary>
public sealed record PortfolioSnapshot(DateTimeOffset Time, decimal Cash, decimal Equity,
    decimal GrossExposure, IReadOnlyList<Position> Positions);
