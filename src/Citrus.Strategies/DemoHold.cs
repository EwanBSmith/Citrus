using Citrus.Trading;

namespace Citrus.Strategies;

/// <summary>Buys one BTC unit after the first completed bar for an offline integration example.</summary>
public sealed class DemoHold : IStrategy
{
    private InstrumentContext market = null!;
    private bool submitted = false;

    /// <summary>Allocates capital and binds the demo instrument by symbol.</summary>
    public void OnStart(IStrategyContext context)
    {
        context.Register("hold", 1m);
        market = new InstrumentContext(context, "hold", "BTC");
    }

    /// <summary>Places one order whose fill must occur on a subsequent bar.</summary>
    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        if (submitted) return;
        market.Buy(1m);
        submitted = true;
    }
}
