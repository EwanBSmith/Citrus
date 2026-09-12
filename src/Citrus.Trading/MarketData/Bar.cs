namespace Citrus.Trading;

/// <summary>Contains OHLC prices, volume, UTC boundaries, and equity session boundary flags for one instrument.</summary>
public sealed record Bar(Instrument Instrument, DateTimeOffset OpenTime, DateTimeOffset CloseTime,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume,
    bool SessionOpen = false, bool SessionClose = false);
