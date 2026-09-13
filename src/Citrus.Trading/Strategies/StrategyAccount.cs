namespace Citrus.Trading;

/// <summary>Groups instruments under a named account with a fixed default entry notional.</summary>
public sealed class StrategyAccount
{
    private readonly Dictionary<string, InstrumentContext> markets;
    /// <summary>Gets the fixed amount used to size entries independently of subsequent account profits.</summary>
    public decimal Allocation { get; }

    /// <summary>Registers the account and binds its declared symbols to fresh trading contexts.</summary>
    internal StrategyAccount(IStrategyContext context, string name, decimal allocation, int warmupBars, string[] symbols)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allocation);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupBars);
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Length == 0) throw new ArgumentException("An account requires at least one symbol.", nameof(symbols));
        markets = symbols.ToDictionary(s => s, s => new InstrumentContext(context, name, s)
        { EntryNotional = allocation, WarmupBars = warmupBars }, StringComparer.OrdinalIgnoreCase);
        context.Register(name, allocation / context.Portfolio.Equity);
        Allocation = allocation;
    }

    /// <summary>Returns this account's trading context for a declared symbol, ignoring symbol case.</summary>
    public InstrumentContext this[string symbol] => markets[symbol];
}
