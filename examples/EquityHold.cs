using System.Collections.Generic;
using Citrus.Contracts;

/// <summary>Demonstrates buying ten units at the next eligible equity session open after the first completed bar.</summary>
public sealed class EquityHold : IStrategy
{
    /// <summary>Allocates all starting capital to the holding substrategy.</summary>
    public void OnStart(IStrategyContext context) => context.Register("hold", 1m);
    /// <summary>Submits one opening-auction order per instrument when its first bar becomes visible.</summary>
    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        foreach (var bar in bars)
            if (context.History(bar.Instrument, 2).Count == 1)
                context.BuyOnOpen("hold", bar.Instrument, 10);
    }
}
