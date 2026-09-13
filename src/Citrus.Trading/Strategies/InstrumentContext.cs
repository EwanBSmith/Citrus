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
    /// <summary>Gets the latest completed high/low midpoint, or null before history exists.</summary>
    public decimal? Midpoint => History(1).LastOrDefault() is { } bar ? (bar.High + bar.Low) / 2 : null;
    /// <summary>Gets the default fixed notional for EnterLong and EnterShort; direct Buy/Sell quantities remain available.</summary>
    public decimal EntryNotional { get; init; }
    /// <summary>Gets the minimum completed history required by entry and notional-target helpers.</summary>
    public int WarmupBars { get; init; }
    /// <summary>Gets the signed holding implied by actual positions plus this account/instrument's pending orders.</summary>
    public decimal ProjectedQuantity => Quantity + Context.OpenOrders
        .Where(o => o.Request.Substrategy == Substrategy && o.Request.Instrument == Instrument).Sum(o => o.Request.Quantity);

    /// <summary>Returns up to count completed bars in chronological order.</summary>
    public IReadOnlyList<Bar> History(int count) => Context.History(Instrument, count);
    /// <summary>Reports whether count completed bars are available without observing future prices.</summary>
    public bool HasHistory(int count) => History(count).Count == count;

    /// <summary>Replaces this market's pending orders with one delta to a signed quantity; closing auctions default to day orders.</summary>
    public long? TargetQuantity(decimal quantity, OrderType type = OrderType.Market, TimeInForce? timeInForce = null)
    {
        var duration = timeInForce ?? (type == OrderType.MarketOnClose ? TimeInForce.Day : TimeInForce.GoodTillCancelled);
        if (type is not (OrderType.Market or OrderType.MarketOnOpen or OrderType.MarketOnClose) || !Enum.IsDefined(duration) ||
            Instrument.AssetClass == AssetClass.LinearPerpetual && type != OrderType.Market)
            throw new ArgumentException("Position targets require a supported market or auction order.");
        CancelOrders();
        var delta = quantity - Quantity;
        return delta > 0 ? Buy(delta, type, duration) : delta < 0 ? Sell(-delta, type, duration) : null;
    }

    /// <summary>Adds lots to a projected long holding or reverses a short to a new long, sized from the completed close.</summary>
    public long? EnterLong(OrderType type = OrderType.Market, decimal? notional = null, decimal lotSize = 1m)
        => Enter(1, type, notional ?? EntryNotional, lotSize);
    /// <summary>Adds lots to a projected short holding or reverses a long to a new short, sized from the completed close.</summary>
    public long? EnterShort(OrderType type = OrderType.Market, decimal? notional = null, decimal lotSize = 1m)
        => Enter(-1, type, notional ?? EntryNotional, lotSize);

    /// <summary>Validates entry sizing, waits for warmup, and submits one net delta from actual holdings.</summary>
    private long? Enter(int side, OrderType type, decimal notional, decimal lotSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(notional);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lotSize);
        ArgumentOutOfRangeException.ThrowIfNegative(WarmupBars);
        if (!HasHistory(WarmupBars)) return null;
        var quantity = LotsForNotional(notional, lotSize);
        if (quantity == 0) return null;
        var projected = ProjectedQuantity;
        return TargetQuantity((Math.Sign(projected) == side ? projected : 0) + side * quantity, type);
    }

    /// <summary>Adjusts signed notional by whole lots when relative drift exceeds tolerance; zero flattens without warmup.</summary>
    public long? TargetNotional(decimal notional, decimal tolerance = 0m, decimal lotSize = 1m, OrderType type = OrderType.Market)
    {
        if (tolerance is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(tolerance));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lotSize);
        ArgumentOutOfRangeException.ThrowIfNegative(WarmupBars);
        if (notional == 0) return TargetQuantity(0, type);
        if (!HasHistory(WarmupBars) || Close is not > 0) return null;
        var price = Close.Value;
        var projected = ProjectedQuantity;
        var difference = notional - projected * price;
        var quantity = Math.Floor(Math.Abs(difference) / price / lotSize) * lotSize;
        if (quantity == 0 || Math.Abs(difference) / Math.Abs(notional) <= tolerance) return null;
        return TargetQuantity(projected + Math.Sign(difference) * quantity, type);
    }

    /// <summary>Binds an already registered account and instrument; calendar dates default to New York or an explicit exchange zone.</summary>
    public InstrumentContext(IStrategyContext context, string substrategy, Instrument instrument, TimeZoneInfo? exchangeZone = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentException.ThrowIfNullOrWhiteSpace(substrategy);
        Context = context;
        Substrategy = substrategy;
        Instrument = context.ResolveInstrument(instrument.Symbol);
        if (Instrument.AssetClass != instrument.AssetClass) throw new ArgumentException($"Asset class does not match history for {instrument.Symbol}.");
        calendar = new Lazy<TradingDay[]>(() => BuildCalendar(exchangeZone ?? TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));
    }

    /// <summary>Binds a symbol directly; venue and asset-class metadata come from the historical dataset.</summary>
    public InstrumentContext(IStrategyContext context, string substrategy, string symbol, TimeZoneInfo? exchangeZone = null)
        : this(context, substrategy, context.ResolveInstrument(symbol), exchangeZone) { }

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

    /// <summary>Targets flat when projected holdings are long; preserves an existing exit or short intent and never waits for warmup.</summary>
    public long? ExitLong(OrderType type = OrderType.Market, TimeInForce? timeInForce = null)
    {
        return ProjectedQuantity > 0 ? TargetQuantity(0, type, timeInForce) : null;
    }

    /// <summary>Targets flat when projected holdings are short; preserves an existing exit or long intent and never waits for warmup.</summary>
    public long? ExitShort(OrderType type = OrderType.Market, TimeInForce? timeInForce = null)
    {
        return ProjectedQuantity < 0 ? TargetQuantity(0, type, timeInForce) : null;
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
