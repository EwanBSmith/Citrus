namespace Citrus.Trading;

/// <summary>Binds calendar, sizing, and order helpers to one instrument and registered substrategy. Multi-instrument strategies can construct one per account/instrument.</summary>
public sealed class InstrumentContext
{
    private readonly Lazy<TradingDay[]> calendar;
    /// <summary>Gets the complete underlying API for scheduling, portfolio access, and advanced orders.</summary>
    public IStrategyContext Context { get; }
    /// <summary>Gets the instrument used by this context.</summary>
    public Instrument Instrument { get; }
    /// <summary>Gets the registered account used by this context.</summary>
    public string Substrategy { get; }
    /// <summary>Gets this account's signed holding in the instrument, excluding pending orders.</summary>
    public decimal Quantity => Context.Portfolio.Positions.Where(p => p.Substrategy == Substrategy && p.Instrument == Instrument).Sum(p => p.Quantity);
    /// <summary>Gets the latest completed close, or null before any price has been observed.</summary>
    public decimal? Close => Context.History(Instrument, 1).LastOrDefault()?.Close;

    /// <summary>Binds an already registered account and instrument; calendar dates default to New York or an explicit exchange zone.</summary>
    public InstrumentContext(IStrategyContext context, string substrategy, Instrument instrument, TimeZoneInfo? exchangeZone = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentException.ThrowIfNullOrWhiteSpace(substrategy);
        Context = context;
        Substrategy = substrategy;
        Instrument = instrument;
        calendar = new Lazy<TradingDay[]>(() => BuildCalendar(exchangeZone ?? TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));
    }

    /// <summary>Returns the latest opened session shifted by signed trading sessions (1 is next); null outside coverage or before the first open. Requires complete calendar months.</summary>
    public TradingDay? TradingDay(int offset = 0)
    {
        var days = calendar.Value;
        var low = 0;
        var high = days.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (days[middle].Session.Open <= Context.Time) low = middle + 1;
            else high = middle - 1;
        }
        if (high < 0) return null;
        var index = (long)high + offset;
        return index >= 0 && index < days.Length ? days[(int)index] : null;
    }

    /// <summary>Buys an additional positive quantity with the requested execution type and optional limit price.</summary>
    public long Buy(decimal quantity, OrderType type = OrderType.Market, TimeInForce timeInForce = TimeInForce.GoodTillCancelled, decimal? limitPrice = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        return Context.Submit(new(Substrategy, Instrument, quantity, type, limitPrice, timeInForce));
    }

    /// <summary>Sells an additional positive quantity; may establish a short position.</summary>
    public long Sell(decimal quantity, OrderType type = OrderType.Market, TimeInForce timeInForce = TimeInForce.GoodTillCancelled, decimal? limitPrice = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        return Context.Submit(new(Substrategy, Instrument, -quantity, type, limitPrice, timeInForce));
    }

    /// <summary>Rounds positive notional down to a positive lot size using only the latest completed close; returns zero without history.</summary>
    public decimal LotsForNotional(decimal notional, decimal lotSize = 1m)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(notional);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lotSize);
        var price = Close;
        return price is > 0m ? Math.Floor(notional / price.Value / lotSize) * lotSize : 0m;
    }

    /// <summary>Buys additional notional rounded down to lots; returns null when no price or affordable lot exists.</summary>
    public long? BuyNotional(decimal notional, OrderType type = OrderType.Market, TimeInForce timeInForce = TimeInForce.GoodTillCancelled, decimal lotSize = 1m)
    {
        var quantity = LotsForNotional(notional, lotSize);
        return quantity > 0 ? Buy(quantity, type, timeInForce) : null;
    }

    /// <summary>Sells additional notional rounded down to lots; may establish a short and returns null without an affordable lot.</summary>
    public long? SellNotional(decimal notional, OrderType type = OrderType.Market, TimeInForce timeInForce = TimeInForce.GoodTillCancelled, decimal lotSize = 1m)
    {
        var quantity = LotsForNotional(notional, lotSize);
        return quantity > 0 ? Sell(quantity, type, timeInForce) : null;
    }

    /// <summary>Cancels this account/instrument's pending orders and submits a sale of its actual long holding; returns null when flat or short.</summary>
    public long? ExitLong(OrderType type = OrderType.Market, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
    {
        CancelOrders();
        var quantity = Quantity;
        return quantity > 0 ? Sell(quantity, type, timeInForce) : null;
    }

    /// <summary>Cancels this account/instrument's pending orders and buys its actual short holding; returns null when flat or long.</summary>
    public long? ExitShort(OrderType type = OrderType.Market, TimeInForce timeInForce = TimeInForce.GoodTillCancelled)
    {
        CancelOrders();
        var quantity = Quantity;
        return quantity < 0 ? Buy(-quantity, type, timeInForce) : null;
    }

    /// <summary>Cancels pending orders only for this account and instrument, returning the successful cancellation count.</summary>
    public int CancelOrders()
    {
        var count = 0;
        foreach (var order in Context.OpenOrders.Where(o => o.Request.Substrategy == Substrategy && o.Request.Instrument == Instrument).ToArray())
            if (Context.Cancel(order.OrderId)) count++;
        return count;
    }

    /// <summary>Indexes session ordinals using exchange-local months; future information is limited to calendar times.</summary>
    private TradingDay[] BuildCalendar(TimeZoneInfo zone)
    {
        if (Context.Sessions.Count == 0) throw new InvalidOperationException("Trading-day queries require a complete exchange session calendar.");
        return Context.Sessions.OrderBy(s => s.Open)
            .Select(s => (Session: s, Date: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Open, zone).DateTime)))
            .GroupBy(s => (s.Date.Year, s.Date.Month))
            .SelectMany(month => month.Select((s, i) => new TradingDay(s.Session, s.Date, i + 1, month.Count())))
            .ToArray();
    }
}
