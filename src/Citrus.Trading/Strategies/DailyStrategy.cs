namespace Citrus.Trading;

/// <summary>Provides daily portfolio hooks using completed bars and ordinary pre-close auction submissions.</summary>
public abstract class DailyStrategy : IStrategy
{
    private IStrategyContext? context;
    private Instrument? clock;
    private DateTimeOffset? lastClose;
    private bool initializing;
    private const string AuctionEvent = "Citrus.DailyStrategy.BeforeClose";

    /// <summary>Selects the symbol whose completed session bars trigger OnClose.</summary>
    protected abstract string ClockSymbol { get; }
    /// <summary>Gets the number of completed bars required by entry and notional-target helpers; exits remain available.</summary>
    protected virtual int WarmupBars => 0;
    /// <summary>Gets the underlying API for advanced orders, history, scheduling, and external data.</summary>
    protected IStrategyContext Context => context ?? throw new InvalidOperationException("The strategy has not started.");
    /// <summary>Gets the current simulation time in UTC.</summary>
    protected DateTimeOffset Time => Context.Time;
    /// <summary>Gets the UTC date of the current callback.</summary>
    protected DateOnly Date => DateOnly.FromDateTime(Time.UtcDateTime);

    /// <summary>Registers an account whose allocation is both its initial capital and default fixed entry notional.</summary>
    protected StrategyAccount Account(string name, decimal allocation, params string[] symbols)
    {
        if (!initializing) throw new InvalidOperationException("Create accounts during Initialize.");
        return new(Context, name, allocation, WarmupBars, symbols);
    }

    /// <summary>Resets run state, initializes accounts, and schedules callbacks one minute before each eligible close.</summary>
    void IStrategy.OnStart(IStrategyContext context)
    {
        this.context = context;
        lastClose = null;
        clock = context.ResolveInstrument(ClockSymbol);
        if (clock.AssetClass != AssetClass.Equity || context.Sessions.Count == 0)
            throw new InvalidOperationException("DailyStrategy requires an equity clock and an exchange session calendar.");
        ArgumentOutOfRangeException.ThrowIfNegative(WarmupBars);
        initializing = true;
        try { Initialize(); }
        finally { initializing = false; }
        foreach (var session in context.Sessions)
        {
            var time = session.Close.AddMinutes(-1);
            if (time > context.Time && time >= session.Open) context.Schedule(time, AuctionEvent);
        }
    }

    /// <summary>Dispatches the clock's completed daily session once; partial session bars are not supported.</summary>
    void IStrategy.OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        if (!bars.Any(b => b.Instrument == clock && b.SessionClose) || lastClose == context.Time) return;
        if (bars.Any(b => b.SessionClose && !b.SessionOpen)) throw new InvalidOperationException("DailyStrategy requires daily session bars.");
        lastClose = context.Time;
        OnClose();
    }

    /// <summary>Dispatches the reserved auction callback or forwards a user-scheduled event.</summary>
    void IStrategy.OnScheduled(IStrategyContext context, string name)
    {
        if (name == AuctionEvent) BeforeClose(); else OnScheduled(name);
    }
    /// <summary>Forwards order notifications after the engine updates order state.</summary>
    void IStrategy.OnOrderUpdate(IStrategyContext context, OrderUpdate update) => OnOrderUpdate(update);
    /// <summary>Forwards fills after they are applied to the portfolio.</summary>
    void IStrategy.OnFill(IStrategyContext context, Fill fill) => OnFill(fill);
    /// <summary>Forwards shutdown after outstanding orders have been cancelled.</summary>
    void IStrategy.OnStop(IStrategyContext context) => OnStop();

    /// <summary>Creates accounts and resets strategy-owned indicators at the beginning of every run.</summary>
    protected abstract void Initialize();
    /// <summary>Evaluates the completed daily bar; market orders remain eligible only at a subsequent open.</summary>
    protected virtual void OnClose() { }
    /// <summary>Runs one minute before a session close using only previously completed bars; request MarketOnClose explicitly.</summary>
    protected virtual void BeforeClose() { }
    /// <summary>Receives user-scheduled events through the underlying context.</summary>
    protected virtual void OnScheduled(string name) { }
    /// <summary>Receives sequential order state changes.</summary>
    protected virtual void OnOrderUpdate(OrderUpdate update) { }
    /// <summary>Receives sequential fills already reflected in positions.</summary>
    protected virtual void OnFill(Fill fill) { }
    /// <summary>Runs optional cleanup during shutdown.</summary>
    protected virtual void OnStop() { }
}
