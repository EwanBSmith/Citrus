using Citrus.Simulation;
using Citrus.Trading;

namespace Citrus.Engine;

/// <summary>Defines strategy inputs, reproducibility settings, output paths, and simulation parameters for a run.</summary>
public sealed record RunConfiguration
{
    /// <summary>Gets the serialized format version; the current supported version is one.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the strategy source path, resolved relative to the run configuration by the CLI.</summary>
    public string Strategy { get; init; } = "";
    /// <summary>Gets additional assembly paths, resolved relative to the run configuration by the CLI.</summary>
    public string[] References { get; init; } = [];
    /// <summary>Gets the market dataset path, resolved relative to the run configuration by the CLI.</summary>
    public string Data { get; init; } = "";
    /// <summary>Gets the inclusive UTC start for universe-driven data preparation.</summary>
    public DateTimeOffset? Start { get; init; }
    /// <summary>Gets the exclusive UTC bar-open limit; only bars closing by this time are included.</summary>
    public DateTimeOffset? End { get; init; }
    /// <summary>Gets the requested provider bar interval.</summary>
    public BarInterval Interval { get; init; } = BarInterval.Daily;
    /// <summary>Gets the provider cache directory relative to the run configuration.</summary>
    public string Cache { get; init; } = ".cache";
    /// <summary>Gets the explicit historical revision used to partition the cache.</summary>
    public string DataVersion { get; init; } = "1";
    /// <summary>Gets the Alpaca market data feed.</summary>
    public string Feed { get; init; } = "iex";
    /// <summary>Gets the results directory, resolved relative to the run configuration by the CLI.</summary>
    public string Output { get; init; } = "results";
    /// <summary>Gets starting portfolio cash, including capital not allocated to a substrategy.</summary>
    public decimal InitialCash { get; init; } = 100_000;
    /// <summary>Gets the seed used for simulated external order rejection draws.</summary>
    public int Seed { get; init; } = 42;
    /// <summary>Gets the annual fractional risk-free rate used in reported Sharpe calculations.</summary>
    public decimal RiskFreeRate { get; init; }
    /// <summary>Gets the maximum number of substrategies that may register during startup.</summary>
    public int MaximumSubstrategies { get; init; } = 1000;
    /// <summary>Gets execution and accounting settings for the simulated venue.</summary>
    public SimulationOptions Simulation { get; init; } = new();
}
