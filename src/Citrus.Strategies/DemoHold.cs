using Citrus.Trading;

namespace Citrus.Strategies;

/// <summary>Buys one BTC unit after the first completed bar for an offline integration example.</summary>
public sealed class DemoHold : Strategy
{
    private InstrumentContext market = null!;
    private bool submitted = false;

    /// <summary>Allocates capital and binds the demo instrument by symbol.</summary>
    protected override void Initialize()
    {
        submitted = false;
        Context.Register("hold", 1m);
        market = new InstrumentContext(Context, "hold", "BTC");
    }

    /// <summary>Places one order whose fill must occur on a subsequent bar.</summary>
    protected override void OnClose()
    {
        if (submitted) return;
        market.Buy(1m);
        submitted = true;
    }
}
