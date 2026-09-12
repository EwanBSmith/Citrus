using System.Collections.Generic;
using Citrus.Trading;

/// <summary>Buys a small fixed quantity of each universe instrument after its first completed bar.</summary>
public sealed class UniverseHold : IStrategy
{
    /// <summary>Registers one holding account.</summary>
    public void OnStart(IStrategyContext context) => context.Register("hold", 1m);

    /// <summary>Places one initial buy for each instrument independently.</summary>
    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        foreach (var bar in bars)
            if (context.History(bar.Instrument, 2).Count == 1) context.Buy("hold", bar.Instrument, 0.01m);
    }
}
