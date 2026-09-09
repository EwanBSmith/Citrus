using System.Collections.Generic;
using System.Linq;
using Citrus.Contracts;

/// <summary>Demonstrates a single-instrument SMA trend allocation alongside a passive holding allocation.</summary>
public sealed class PerpetualTrend : IStrategy
{
    /// <summary>Splits starting capital between trend and holding substrategies in a 70/30 ratio.</summary>
    public void OnStart(IStrategyContext context)
    {
        context.Register("trend", 0.7m);
        context.Register("hold", 0.3m);
    }

    /// <summary>Targets half of trend equity long or short after SMA warmup and initializes the passive half-equity holding.</summary>
    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        foreach (var bar in bars)
        {
            var history = context.History(bar.Instrument, 20);
            var mean = Indicators.Sma(history.Select(b => b.Close), 20);
            // This example assumes one instrument: each rebalance dictionary replaces the full target portfolio.
            if (mean is not null)
                context.Rebalance("trend", new Dictionary<Instrument, decimal> { [bar.Instrument] = bar.Close > mean ? 0.5m : -0.5m });
            if (history.Count == 1)
                context.Rebalance("hold", new Dictionary<Instrument, decimal> { [bar.Instrument] = 0.5m });
        }
    }
}
