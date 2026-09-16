namespace Citrus.Trading;

/// <summary>Provides strategy access to simulated time, portfolio state, orders, completed history, and captured external data.</summary>
public interface IStrategyContext
{
    /// <summary>Gets the execution mode reported by the host.</summary>
    ExecutionMode Mode { get; }
    /// <summary>Gets the current simulation time in UTC.</summary>
    DateTimeOffset Time { get; }
    /// <summary>Gets a current portfolio snapshot including each substrategy position.</summary>
    PortfolioSnapshot Portfolio { get; }
    /// <summary>Gets pending orders with request quantities set to their signed unfilled balances.</summary>
    IReadOnlyList<OrderUpdate> OpenOrders { get; }
    /// <summary>Gets the strategy market's shared session calendar, including future session times but no future prices. Supply complete months for monthly seasonality.</summary>
    IReadOnlyList<MarketSession> Sessions => Array.Empty<MarketSession>();
    /// <summary>Allocates a positive fraction of initial capital during startup; names must be unique and total weights at most one.</summary>
    void Register(string substrategy, decimal capitalWeight);
    /// <summary>Resolves a symbol to its dataset instrument metadata; missing or ambiguous history is an error.</summary>
    Instrument ResolveInstrument(string symbol);
    /// <summary>Returns completed history by symbol, independently of provider and venue labels.</summary>
    IReadOnlyList<Bar> History(string symbol, int count) => History(ResolveInstrument(symbol), count);
    /// <summary>Returns up to count completed bars in chronological order as a copy; count must be nonnegative.</summary>
    IReadOnlyList<Bar> History(Instrument instrument, int count);
    /// <summary>Submits an additional signed-quantity order and returns its ID; acceptance does not guarantee execution.</summary>
    long Submit(OrderRequest order);
    /// <summary>Cancels a pending order by ID, returning false if it is no longer pending.</summary>
    bool Cancel(long orderId);
    /// <summary>Targets a complete substrategy portfolio using current equity and completed prices; omitted holdings target zero and pending quantities count toward targets.</summary>
    void Rebalance(string substrategy, IReadOnlyDictionary<Instrument, decimal> weights);
    /// <summary>Fetches bytes once per key during a run and returns defensive copies.</summary>
    byte[] ExternalData(string key, Func<byte[]> fetch);
}
