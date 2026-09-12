using Citrus.Simulation;

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
    /// <summary>Gets the results directory, resolved relative to the run configuration by the CLI.</summary>
    public string Output { get; init; } = "results";
    /// <summary>Gets starting portfolio cash, including capital not allocated to a substrategy.</summary>
    public decimal InitialCash { get; init; } = 100_000;
    /// <summary>Gets the seed used for simulated external order rejection draws.</summary>
    public int Seed { get; init; } = 42;
    /// <summary>Gets the annual fractional risk-free rate used in reported Sharpe calculations.</summary>
    public decimal RiskFreeRate { get; init; }
    /// <summary>Gets execution and accounting settings for the simulated venue.</summary>
    public SimulationOptions Simulation { get; init; } = new();
}
