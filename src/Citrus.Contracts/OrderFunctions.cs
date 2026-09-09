namespace Citrus.Contracts;

/// <summary>Place quantity-based orders through the shared strategy execution contract.</summary>
public static class OrderFunctions
{
    /// <summary>Buy a positive number of units with a market order. Returns the order ID.</summary>
    public static long Buy(this IStrategyContext context, string substrategy, Instrument instrument,
        decimal quantity, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
        => Place(context, substrategy, instrument, quantity, false, OrderType.Market, null, timeInForce);

    /// <summary>Sell a positive number of units with a market order; may open or increase a short position.</summary>
    public static long Sell(this IStrategyContext context, string substrategy, Instrument instrument,
        decimal quantity, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
        => Place(context, substrategy, instrument, quantity, true, OrderType.Market, null, timeInForce);

    /// <summary>Buy units at the limit price or lower. Returns the order ID.</summary>
    public static long BuyLimit(this IStrategyContext context, string substrategy, Instrument instrument,
        decimal quantity, decimal limitPrice, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
        => Place(context, substrategy, instrument, quantity, false, OrderType.Limit, limitPrice, timeInForce);

    /// <summary>Sell units at the limit price or higher. Returns the order ID.</summary>
    public static long SellLimit(this IStrategyContext context, string substrategy, Instrument instrument,
        decimal quantity, decimal limitPrice, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
        => Place(context, substrategy, instrument, quantity, true, OrderType.Limit, limitPrice, timeInForce);

    /// <summary>Buy units at an eligible equity session open. Submit before the opening event.</summary>
    public static long BuyOnOpen(this IStrategyContext context, string substrategy, Instrument instrument,
        decimal quantity, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
        => Place(context, substrategy, instrument, quantity, false, OrderType.MarketOnOpen, null, timeInForce);

    /// <summary>Sell units at an eligible equity session open. Submit before the opening event.</summary>
    public static long SellOnOpen(this IStrategyContext context, string substrategy, Instrument instrument,
        decimal quantity, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
        => Place(context, substrategy, instrument, quantity, true, OrderType.MarketOnOpen, null, timeInForce);

    /// <summary>Buy units at an eligible equity session close. Submit before the closing event.</summary>
    public static long BuyOnClose(this IStrategyContext context, string substrategy, Instrument instrument,
        decimal quantity, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
        => Place(context, substrategy, instrument, quantity, false, OrderType.MarketOnClose, null, timeInForce);

    /// <summary>Sell units at an eligible equity session close. Submit before the closing event.</summary>
    public static long SellOnClose(this IStrategyContext context, string substrategy, Instrument instrument,
        decimal quantity, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
        => Place(context, substrategy, instrument, quantity, true, OrderType.MarketOnClose, null, timeInForce);

    /// <summary>Validates positive helper arguments, converts the side to a signed quantity, and submits the order.</summary>
    private static long Place(IStrategyContext context, string substrategy, Instrument instrument, decimal quantity,
        bool sell, OrderType type, decimal? limitPrice, TimeInForce timeInForce)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (limitPrice.HasValue) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitPrice.Value, nameof(limitPrice));
        return context.Submit(new(substrategy, instrument, sell ? -quantity : quantity, type, limitPrice, timeInForce));
    }
}
