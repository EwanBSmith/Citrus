using System.Collections.Generic;
using System.Linq;
using Citrus.Contracts;

public sealed class PerpetualTrend : IStrategy
{
    public void OnStart(IStrategyContext context)
    {
        context.Register("trend", 0.7m);
        context.Register("hold", 0.3m);
    }

    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        foreach (var bar in bars)
        {
            var history = context.History(bar.Instrument, 20);
            var mean = Indicators.Sma(history.Select(b => b.Close), 20);
            if (mean is not null)
                context.Rebalance("trend", new Dictionary<Instrument, decimal> { [bar.Instrument] = bar.Close > mean ? 0.5m : -0.5m });
            if (history.Count == 1)
                context.Rebalance("hold", new Dictionary<Instrument, decimal> { [bar.Instrument] = 0.5m });
        }
    }
}
