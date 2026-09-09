namespace Citrus.Contracts;

/// <summary>Identifies cash equities and linear perpetual contracts supported by the simulator.</summary>
public enum AssetClass { Equity, LinearPerpetual }
/// <summary>Identifies the execution host exposed to a strategy; it does not select a venue adapter.</summary>
public enum ExecutionMode { Backtest, Live }
/// <summary>Selects market, limit, or equity session auction execution.</summary>
public enum OrderType { Market, Limit, MarketOnOpen, MarketOnClose }
/// <summary>Controls whether an unfilled order persists or expires at the session close (UTC midnight for perpetuals).</summary>
public enum TimeInForce { GoodTillCancelled, Day }
/// <summary>Describes acceptance or the terminal outcome of an order.</summary>
public enum OrderStatus { Accepted, Filled, Cancelled, Rejected }
/// <summary>Identifies supported equity distributions, conversions, and retirements.</summary>
public enum ActionType { Dividend, Split, SymbolChange, Merger, Delisting }

/// <summary>Identifies an instrument by venue, asset class, and symbol using record value equality.</summary>
public sealed record Instrument(string Venue, AssetClass AssetClass, string Symbol)
{
    /// <summary>Gets the venue, asset class, and symbol joined by colons for reporting and deterministic ordering.</summary>
    public string Key => $"{Venue}:{AssetClass}:{Symbol}";
}

/// <summary>Describes a named bar duration in minutes; equity bars are clipped to session boundaries.</summary>
public sealed record BarInterval(string Name, int Minutes)
{
    /// <summary>Gets the standard 60-minute interval.</summary>
    public static BarInterval Hourly { get; } = new("1h", 60);
    /// <summary>Gets the daily interval, representing one regular session for equities.</summary>
    public static BarInterval Daily { get; } = new("1d", 1440);
}

/// <summary>Contains OHLC prices, volume, UTC boundaries, and equity session boundary flags for one instrument.</summary>
public sealed record Bar(Instrument Instrument, DateTimeOffset OpenTime, DateTimeOffset CloseTime,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume,
    bool SessionOpen = false, bool SessionClose = false);
/// <summary>Defines an explicit exchange session as UTC opening and closing instants.</summary>
public sealed record MarketSession(DateTimeOffset Open, DateTimeOffset Close);
/// <summary>Defines an equity event with per-unit cash amount, conversion ratio, and successor where applicable.</summary>
public sealed record CorporateAction(string Id, Instrument Instrument, DateTimeOffset Time,
    ActionType Type, decimal? Amount = null, decimal? Ratio = null, Instrument? Successor = null);
/// <summary>Defines a perpetual funding payment; positive rates charge longs and credit shorts at the supplied mark.</summary>
public sealed record FundingEvent(Instrument Instrument, DateTimeOffset Time, decimal Rate, decimal MarkPrice);
/// <summary>Requests a signed quantity in instrument units: positive buys and negative sells; only limits specify a price.</summary>
public sealed record OrderRequest(string Substrategy, Instrument Instrument, decimal Quantity,
    OrderType Type = OrderType.Market, decimal? LimitPrice = null,
    TimeInForce TimeInForce = TimeInForce.GoodTillCancelled);
/// <summary>Records an order status change at a simulation time, with an optional rejection or cancellation reason.</summary>
public sealed record OrderUpdate(long OrderId, OrderRequest Request, OrderStatus Status,
    /// <summary>Gets the current simulation time in UTC.</summary>
    DateTimeOffset Time, string? Reason = null);
/// <summary>Records signed executed units and commission; execution cost is embedded in price, and internal fills transfer between substrategies.</summary>
public sealed record Fill(long OrderId, string Substrategy, Instrument Instrument, DateTimeOffset Time,
    decimal Quantity, decimal Price, decimal Commission, bool Internal, bool Liquidation = false,
    decimal ExecutionCost = 0);
/// <summary>Tracks signed holdings, average entry price, and cumulative realized trading profit for a substrategy.</summary>
public sealed record Position(string Substrategy, Instrument Instrument, decimal Quantity,
    decimal AveragePrice, decimal RealizedPnl);
/// <summary>Records an attributed cash adjustment or diagnostic execution cost; positive amounts denote credits.</summary>
public sealed record CashMovement(string Substrategy, Instrument Instrument, DateTimeOffset Time,
    string Kind, decimal Amount);
/// <summary>Breaks instrument profit into realized, unrealized, and income/fee components for a substrategy.</summary>
public sealed record InstrumentAttribution(string Substrategy, Instrument Instrument, decimal Quantity,
    decimal MarkPrice, decimal RealizedPnl, decimal UnrealizedPnl, decimal IncomeAndFees, decimal NetPnl);
/// <summary>Captures cash, marked equity, exposure after netting each instrument, and virtual substrategy positions.</summary>
public sealed record PortfolioSnapshot(DateTimeOffset Time, decimal Cash, decimal Equity,
    decimal GrossExposure, IReadOnlyList<Position> Positions);

/// <summary>Receives sequential lifecycle and market callbacks; all callbacks are optional and execute as trusted local code.</summary>
public interface IStrategy
{
    /// <summary>Runs before market events; register substrategies and optionally submit initial orders here.</summary>
    void OnStart(IStrategyContext context) { }
    /// <summary>Receives bars closing at the current time after execution and accounting; these bars are already available in history.</summary>
    void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars) { }
    /// <summary>Handles a named scheduled event after any completed-bar callback at the same time.</summary>
    void OnScheduled(IStrategyContext context, string name) { }
    /// <summary>Receives an order status change in execution notification order.</summary>
    void OnOrderUpdate(IStrategyContext context, OrderUpdate update) { }
    /// <summary>Receives a fill after it has been applied to the portfolio ledger.</summary>
    void OnFill(IStrategyContext context, Fill fill) { }
    /// <summary>Runs during shutdown after pending orders are cancelled; new orders are no longer accepted.</summary>
    void OnStop(IStrategyContext context) { }
}

/// <summary>Receives sequential lifecycle and market callbacks; all callbacks are optional and execute as trusted local code.</summary>
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
    /// <summary>Allocates a positive fraction of initial capital during startup; names must be unique and total weights at most one.</summary>
    void Register(string substrategy, decimal capitalWeight);
    /// <summary>Returns up to count completed bars in chronological order as a copy; count must be nonnegative.</summary>
    IReadOnlyList<Bar> History(Instrument instrument, int count);
    /// <summary>Submits an additional signed-quantity order and returns its ID; acceptance does not guarantee execution.</summary>
    long Submit(OrderRequest order);
    /// <summary>Cancels a pending order by ID, returning false if it is no longer pending.</summary>
    bool Cancel(long orderId);
    /// <summary>Targets a complete substrategy portfolio using current equity and completed prices; omitted holdings target zero and pending quantities count toward targets.</summary>
    void Rebalance(string substrategy, IReadOnlyDictionary<Instrument, decimal> weights);
    /// <summary>Schedules a named callback at a future UTC time; times beyond the run do not execute.</summary>
    void Schedule(DateTimeOffset time, string name);
    /// <summary>Captures fetched bytes once per key and returns copies; replay requires the key in supplied snapshots and never fetches missing data.</summary>
    byte[] ExternalData(string key, Func<byte[]> fetch);
}

/// <summary>Computes indicators over values supplied in chronological order.</summary>
public static class Indicators
{
    /// <summary>Returns the mean of the last period values, or null before warmup; period must be positive.</summary>
    public static decimal? Sma(IEnumerable<decimal> source, int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        var values = source.TakeLast(period).ToArray();
        return values.Length < period ? null : values.Average();
    }
    /// <summary>Seeds from the first period values and smooths subsequent values with alpha 2/(period+1); returns null before warmup.</summary>
    public static decimal? Ema(IEnumerable<decimal> source, int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        var values = source.ToArray();
        if (values.Length < period) return null;
        var ema = values.Take(period).Average();
        var alpha = 2m / (period + 1);
        foreach (var value in values.Skip(period)) ema += alpha * (value - ema);
        return ema;
    }
}
