namespace Citrus.Trading;

/// <summary>Registers one substrategy and dispatches only its instrument's bars through a convenient trading context.</summary>
public abstract class InstrumentStrategy : IStrategy
{
    /// <summary>Forwards the configuration phase before the instrument context exists.</summary>
    void IStrategy.Configure(StrategyOptions options) => Configure(options);
    /// <summary>Declares authoritative settings before historical data or accounts are created.</summary>
    protected virtual void Configure(StrategyOptions options) { }
    private readonly Instrument instrument;
    private readonly string substrategy;
    private readonly decimal capitalWeight;
    private InstrumentContext? market;

    /// <summary>Selects the instrument, account name, and fraction of initial capital to register on each run.</summary>
    protected InstrumentStrategy(Instrument instrument, string substrategy, decimal capitalWeight = 1m)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentException.ThrowIfNullOrWhiteSpace(substrategy);
        this.instrument = instrument;
        this.substrategy = substrategy;
        this.capitalWeight = capitalWeight;
    }

    /// <summary>Registers the account and creates a fresh convenience context before invoking startup logic.</summary>
    void IStrategy.OnStart(IStrategyContext context)
    {
        context.Register(substrategy, capitalWeight);
        market = new InstrumentContext(context, substrategy, instrument);
        OnStart(market);
    }

    /// <summary>Filters the completed batch to the selected instrument while retaining the engine's event ordering.</summary>
    void IStrategy.OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        foreach (var bar in bars)
            if (bar.Instrument == instrument) OnBar(market!, bar);
    }

    /// <summary>Runs optional startup logic after account registration.</summary>
    protected virtual void OnStart(InstrumentContext market) { }

    /// <summary>Receives each completed bar for the selected instrument; orders retain normal future-event eligibility.</summary>
    protected abstract void OnBar(InstrumentContext market, Bar bar);

    /// <summary>Handles scheduled callbacks with access to the full strategy API.</summary>
    public virtual void OnScheduled(IStrategyContext context, string name) { }

    /// <summary>Handles order notifications with access to the full strategy API.</summary>
    public virtual void OnOrderUpdate(IStrategyContext context, OrderUpdate update) { }

    /// <summary>Handles applied fills with access to the full strategy API.</summary>
    public virtual void OnFill(IStrategyContext context, Fill fill) { }

    /// <summary>Handles shutdown with access to the full strategy API.</summary>
    public virtual void OnStop(IStrategyContext context) { }
}
