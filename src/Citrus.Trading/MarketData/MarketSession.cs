namespace Citrus.Trading;

/// <summary>Defines an explicit exchange session as UTC opening and closing instants.</summary>
public sealed record MarketSession(DateTimeOffset Open, DateTimeOffset Close);
