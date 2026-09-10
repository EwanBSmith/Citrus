namespace Citrus.Trading;

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
