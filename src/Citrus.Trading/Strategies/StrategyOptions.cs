using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace Citrus.Trading;

/// <summary>Declares authoritative run settings; only explicitly assigned properties override GUI or JSON values.</summary>
public sealed class StrategyOptions
{
    private readonly Dictionary<string, object?> values = [];
    /// <summary>Gets the explicitly assigned settings, including assignments of null, false, and zero.</summary>
    public IReadOnlyDictionary<string, object?> AssignedValues => new ReadOnlyDictionary<string, object?>(values);
    /// <summary>Reads an assigned value or its standard default while defining the strategy.</summary>
    private T Get<T>(T fallback, [CallerMemberName] string name = "") => values.TryGetValue(name, out var value) ? (T)value! : fallback;
    /// <summary>Records an explicit assignment independently of its value.</summary>
    private void Set<T>(T value, [CallerMemberName] string name = "") => values[name] = value;
    /// <summary>Inclusive UTC start; assign null to use all available starting history.</summary>
    public DateTimeOffset? Start { get => Get<DateTimeOffset?>(null); set => Set(value); }

    /// <summary>Exclusive UTC end; assign null to use all available ending history.</summary>
    public DateTimeOffset? End { get => Get<DateTimeOffset?>(null); set => Set(value); }


    /// <summary>Starting portfolio cash.</summary>
    public decimal InitialCash { get => Get<decimal>(100_000m); set => Set(value); }

    /// <summary>Seed for simulated order rejection.</summary>
    public int Seed { get => Get<int>(42); set => Set(value); }

    /// <summary>Annual fractional risk-free rate for performance reports.</summary>
    public decimal RiskFreeRate { get => Get<decimal>(0m); set => Set(value); }

    /// <summary>Fixed commission per external net order.</summary>
    public decimal CommissionFixed { get => Get<decimal>(0m); set => Set(value); }

    /// <summary>Commission per absolute externally executed unit.</summary>
    public decimal CommissionPerUnit { get => Get<decimal>(0m); set => Set(value); }

    /// <summary>Full bid/ask spread in basis points.</summary>
    public decimal SpreadBps { get => Get<decimal>(0m); set => Set(value); }

    /// <summary>Additional adverse execution movement in basis points.</summary>
    public decimal SlippageBps { get => Get<decimal>(0m); set => Set(value); }

    /// <summary>External order rejection probability from zero to one.</summary>
    public double RejectionProbability { get => Get<double>(0d); set => Set(value); }

    /// <summary>Annual fractional borrow rate on net short equities.</summary>
    public decimal AnnualBorrowRate { get => Get<decimal>(0.03m); set => Set(value); }

    /// <summary>Whether external orders can increase net short equity exposure.</summary>
    public bool ShortsAvailable { get => Get<bool>(true); set => Set(value); }

    /// <summary>Equity initial margin fraction.</summary>
    public decimal EquityInitialMargin { get => Get<decimal>(0.5m); set => Set(value); }

    /// <summary>Equity maintenance margin fraction.</summary>
    public decimal EquityMaintenanceMargin { get => Get<decimal>(0.25m); set => Set(value); }

    /// <summary>Perpetual initial margin fraction.</summary>
    public decimal PerpetualInitialMargin { get => Get<decimal>(0.1m); set => Set(value); }

    /// <summary>Perpetual maintenance margin fraction.</summary>
    public decimal PerpetualMaintenanceMargin { get => Get<decimal>(0.05m); set => Set(value); }
}
