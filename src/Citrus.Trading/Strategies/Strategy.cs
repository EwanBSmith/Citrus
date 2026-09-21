namespace Citrus.Trading;

/// <summary>Provides daily portfolio hooks using completed bars and ordinary pre-close auction submissions.</summary>
public abstract class Strategy
{
    /// <summary>Invokes Configure before the trading context exists.</summary>
    internal void ConfigureRun(StrategyOptions options) => Configure(options);
    /// <summary>Declares authoritative settings before historical data or accounts are created.</summary>
    protected virtual void Configure(StrategyOptions options) { }
    private IStrategyContext? context;
    private TradingCalendar? calendar;
    private bool initializing;

    /// <summary>Gets the market time zone used for session dates and monthly trading-day counts.</summary>
    protected virtual TimeZoneInfo ExchangeTimeZone => Context.Sessions.All(s => s.Close - s.Open == TimeSpan.FromDays(1))
        ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    /// <summary>Gets the number of completed bars required by entry and notional-target helpers; exits remain available.</summary>
    protected virtual int WarmupBars => 0;
    /// <summary>Gets the underlying API for advanced orders, history, and external data.</summary>
    protected IStrategyContext Context => context ?? throw new InvalidOperationException("The strategy has not started.");
    /// <summary>Gets the daily bars completed at this close, or an empty batch for a session without observations.</summary>
    protected IReadOnlyList<Bar> CompletedBars { get; private set; } = Array.Empty<Bar>();
    /// <summary>Gets the current simulation time in UTC.</summary>
    protected DateTimeOffset Time => Context.Time;
    /// <summary>Gets the UTC date of the current callback.</summary>
    protected DateOnly Date => DateOnly.FromDateTime(Time.UtcDateTime);

    /// <summary>Returns the latest opened market session shifted by signed sessions; requires complete calendar months.</summary>
    protected TradingDay? TradingDay(int offset = 0) => (calendar ?? throw new InvalidOperationException("The strategy has not started."))
        .TradingDay(Time, offset);
    /// <summary>Returns the current market session's one-based trading-day ordinal, excluding exchange holidays.</summary>
    protected int TradingDayOfMonth() => TradingDay()?.DayOfMonth
        ?? throw new InvalidOperationException("No market session has opened yet.");
    /// <summary>Returns the number of supplied market sessions in the current session's month; supply complete months even for short backtests.</summary>
    protected int TradingDaysInMonth() => TradingDay()?.DaysInMonth
        ?? throw new InvalidOperationException("No market session has opened yet.");

    /// <summary>Registers an account whose allocation is both its initial capital and default fixed entry notional.</summary>
    protected StrategyAccount Account(string name, decimal allocation, params string[] symbols)
    {
        if (!initializing) throw new InvalidOperationException("Create accounts during Initialize.");
        return new(Context, name, allocation, WarmupBars, symbols);
    }

    #region Callbacks
    /// <summary>Resets run state and initializes accounts against the validated daily market calendar.</summary>
    internal void Start(IStrategyContext context)
    {
        this.context = context;
        if (context.Sessions.Count == 0)
            throw new InvalidOperationException("Strategy requires a market session calendar.");
        calendar = new TradingCalendar(context.Sessions, ExchangeTimeZone);
        CompletedBars = Array.Empty<Bar>();
        ArgumentOutOfRangeException.ThrowIfNegative(WarmupBars);
        initializing = true;
        try { Initialize(); }
        finally { initializing = false; }
    }

    /// <summary>Publishes the completed daily batch and invokes the end-of-day decision.</summary>
    internal void Close(IReadOnlyList<Bar> bars)
    {
        CompletedBars = Array.AsReadOnly(bars.ToArray());
        OnClose();
    }

    /// <summary>Invokes the pre-close auction decision using previously completed history.</summary>
    internal void PrepareClose() => BeforeClose();
    /// <summary>Notifies the strategy after order state updates.</summary>
    internal void NotifyOrder(OrderUpdate update) => OnOrderUpdate(update);
    /// <summary>Notifies the strategy after portfolio fills.</summary>
    internal void NotifyFill(Fill fill) => OnFill(fill);
    /// <summary>Invokes OnStop after pending orders are cancelled.</summary>
    internal void Stop() => OnStop();

    /// <summary>Creates accounts and resets strategy-owned indicators at the beginning of every run.</summary>
    protected virtual void Initialize() { }
    /// <summary>Evaluates the market close after all bars closing at that time have entered history; market orders remain eligible only at a subsequent open.</summary>
    protected virtual void OnClose() { }
    /// <summary>Runs one minute before a session close using only previously completed bars; request MarketOnClose explicitly.</summary>
    protected virtual void BeforeClose() { }
    /// <summary>Receives sequential order state changes.</summary>
    protected virtual void OnOrderUpdate(OrderUpdate update) { }
    /// <summary>Receives sequential fills already reflected in positions.</summary>
    protected virtual void OnFill(Fill fill) { }
    /// <summary>Runs optional cleanup during shutdown.</summary>
    protected virtual void OnStop() { }
    #endregion
}
