using Citrus.Simulation;
using Citrus.Trading;

namespace Citrus.Engine;

/// <summary>Defines strategy inputs, reproducibility settings, output paths, and simulation parameters for a run.</summary>
public sealed record RunConfiguration
{
    /// <summary>Gets the serialized format version; the current supported version is one.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the legacy source path; folder backtests omit this property and discover Strategy.cs.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Strategy { get; init; }
    /// <summary>Gets a C# project to build in Release before loading its output; paths are relative to the strategy workspace.</summary>
    public string? StrategyProject { get; init; }
    /// <summary>Gets a prebuilt strategy assembly, used instead of building a project.</summary>
    public string? StrategyAssembly { get; init; }
    /// <summary>Gets the fully qualified concrete strategy type; required when an assembly contains multiple strategies.</summary>
    public string? StrategyType { get; init; }
    /// <summary>Gets an optional solution path to open in the associated IDE.</summary>
    public string? StrategySolution { get; init; }
    /// <summary>Gets additional assembly paths, resolved relative to the strategy folder or legacy run file.</summary>
    public string[] References { get; init; } = [];
    /// <summary>Gets the optional inclusive UTC lower bound applied to data from the main historical cache.</summary>
    public DateTimeOffset? Start { get; init; }
    /// <summary>Gets the optional exclusive UTC upper bound applied to data from the main historical cache.</summary>
    public DateTimeOffset? End { get; init; }
    /// <summary>Gets the bar interval selected from the main historical cache.</summary>
    public BarInterval Interval { get; init; } = BarInterval.Daily;
    /// <summary>Gets the results directory, resolved relative to the strategy folder or legacy run file.</summary>
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
