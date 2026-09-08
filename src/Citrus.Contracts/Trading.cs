namespace Citrus.Contracts;

public enum AssetClass { Equity, LinearPerpetual }
public enum ExecutionMode { Backtest, Live }
public enum OrderType { Market, Limit, MarketOnOpen, MarketOnClose }
public enum TimeInForce { GoodTillCancelled, Day }
public enum OrderStatus { Accepted, Filled, Cancelled, Rejected }
public enum ActionType { Dividend, Split, SymbolChange, Merger, Delisting }

public sealed record Instrument(string Venue, AssetClass AssetClass, string Symbol)
{
    public string Key => $"{Venue}:{AssetClass}:{Symbol}";
}

public sealed record BarInterval(string Name, int Minutes)
{
    public static BarInterval Hourly { get; } = new("1h", 60);
    public static BarInterval Daily { get; } = new("1d", 1440);
}

public sealed record Bar(Instrument Instrument, DateTimeOffset OpenTime, DateTimeOffset CloseTime,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume,
    bool SessionOpen = false, bool SessionClose = false);
public sealed record MarketSession(DateTimeOffset Open, DateTimeOffset Close);
public sealed record CorporateAction(string Id, Instrument Instrument, DateTimeOffset Time,
    ActionType Type, decimal? Amount = null, decimal? Ratio = null, Instrument? Successor = null);
public sealed record FundingEvent(Instrument Instrument, DateTimeOffset Time, decimal Rate, decimal MarkPrice);
public sealed record OrderRequest(string Substrategy, Instrument Instrument, decimal Quantity,
    OrderType Type = OrderType.Market, decimal? LimitPrice = null,
    TimeInForce TimeInForce = TimeInForce.GoodTillCancelled);
public sealed record OrderUpdate(long OrderId, OrderRequest Request, OrderStatus Status,
    DateTimeOffset Time, string? Reason = null);
public sealed record Fill(long OrderId, string Substrategy, Instrument Instrument, DateTimeOffset Time,
    decimal Quantity, decimal Price, decimal Commission, bool Internal, bool Liquidation = false,
    decimal ExecutionCost = 0);
public sealed record Position(string Substrategy, Instrument Instrument, decimal Quantity,
    decimal AveragePrice, decimal RealizedPnl);
public sealed record CashMovement(string Substrategy, Instrument Instrument, DateTimeOffset Time,
    string Kind, decimal Amount);
public sealed record InstrumentAttribution(string Substrategy, Instrument Instrument, decimal Quantity,
    decimal MarkPrice, decimal RealizedPnl, decimal UnrealizedPnl, decimal IncomeAndFees, decimal NetPnl);
public sealed record PortfolioSnapshot(DateTimeOffset Time, decimal Cash, decimal Equity,
    decimal GrossExposure, IReadOnlyList<Position> Positions);

public interface IStrategy
{
    void OnStart(IStrategyContext context) { }
    void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars) { }
    void OnScheduled(IStrategyContext context, string name) { }
    void OnOrderUpdate(IStrategyContext context, OrderUpdate update) { }
    void OnFill(IStrategyContext context, Fill fill) { }
    void OnStop(IStrategyContext context) { }
}

public interface IStrategyContext
{
    ExecutionMode Mode { get; }
    DateTimeOffset Time { get; }
    PortfolioSnapshot Portfolio { get; }
    IReadOnlyList<OrderUpdate> OpenOrders { get; }
    void Register(string substrategy, decimal capitalWeight);
    IReadOnlyList<Bar> History(Instrument instrument, int count);
    long Submit(OrderRequest order);
    bool Cancel(long orderId);
    void Rebalance(string substrategy, IReadOnlyDictionary<Instrument, decimal> weights);
    void Schedule(DateTimeOffset time, string name);
    byte[] ExternalData(string key, Func<byte[]> fetch);
}

public static class Indicators
{
    public static decimal? Sma(IEnumerable<decimal> source, int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        var values = source.TakeLast(period).ToArray();
        return values.Length < period ? null : values.Average();
    }
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
