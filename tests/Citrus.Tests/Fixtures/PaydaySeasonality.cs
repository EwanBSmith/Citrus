using Citrus.Trading;

/// <summary>Trades SCHB's payday windows using daily decisions for the next session's closing auction.</summary>
public sealed class PaydaySeasonality : Strategy
{
    /// <summary>Allocates the strategy account to SCHB; each entry uses a fixed $810 notional.</summary>
    protected override void Initialize()
    {
        Context.Register("PaydaySeason", 1m);
        market = new InstrumentContext(Context, "PaydaySeason", "SCHB");
    }
    private InstrumentContext market = null!;

    /// <summary>Signals after sessions 7, 11, 15 and the penultimate session to fill on 8, 12, 16 and month end.</summary>
    protected override void OnClose()
    {
        var next = market.TradingDay(1);
        if (next is null) return;

        if (next.DayOfMonth == 12 || next.IsMonthEnd)
            market.ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
        else if (next.DayOfMonth is 8 or 16)
            market.BuyNotional(810, OrderType.MarketOnClose, TimeInForce.Day);
    }
}
