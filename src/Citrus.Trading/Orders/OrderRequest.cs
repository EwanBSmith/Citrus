namespace Citrus.Trading;

/// <summary>Requests a signed quantity in instrument units: positive buys and negative sells; only limits specify a price.</summary>
public sealed record OrderRequest(string Substrategy, Instrument Instrument, decimal Quantity,
    OrderType Type = OrderType.Market, decimal? LimitPrice = null,
    TimeInForce TimeInForce = TimeInForce.GoodTillCancelled);
