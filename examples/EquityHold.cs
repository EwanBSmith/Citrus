using System.Collections.Generic;
using Citrus.Contracts;

public sealed class EquityHold : IStrategy
{
    public void OnStart(IStrategyContext context) => context.Register("hold", 1m);
    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        foreach (var bar in bars)
            if (context.History(bar.Instrument, 2).Count == 1)
                context.Submit(new("hold", bar.Instrument, 10, OrderType.MarketOnOpen));
    }
}
