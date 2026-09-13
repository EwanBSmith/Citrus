using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Citrus.Data;
using Citrus.Trading;

namespace Citrus.Engine;

/// <summary>Resolves strategy declarations into validated effective settings shared by all execution entry points.</summary>
public static class StrategyConfiguration
{
    private static readonly ConditionalWeakTable<IStrategy, IReadOnlyDictionary<string, object?>> declarations = new();

    /// <summary>Evaluates Configure once per strategy instance and freezes its explicitly assigned values.</summary>
    public static IReadOnlyDictionary<string, object?> Declarations(IStrategy strategy) => declarations.GetValue(strategy, Read);

    /// <summary>Snapshots declarations so retaining and mutating the options object cannot change an active run.</summary>
    private static IReadOnlyDictionary<string, object?> Read(IStrategy strategy)
    {
        var options = new StrategyOptions();
        strategy.Configure(options);
        return new ReadOnlyDictionary<string, object?>(options.AssignedValues.ToDictionary(p => p.Key, p => p.Value));
    }

    /// <summary>Gets the corresponding configuration field, including simulation fields nested in the GUI.</summary>
    public static string Field(string name) => typeof(RunConfiguration).GetProperty(name) is not null ? name : "Simulation." + name;

    /// <summary>Applies declarations over requested settings, validating only the resulting effective configuration.</summary>
    public static RunConfiguration Resolve(IStrategy strategy, RunConfiguration requested) => Apply(requested, Declarations(strategy));

    /// <summary>Applies frozen declarations without invoking strategy code again.</summary>
    public static RunConfiguration Apply(RunConfiguration requested, IReadOnlyDictionary<string, object?> values)
    {
        if (requested.Simulation is null) throw new ArgumentException("Simulation settings are required.");
        var node = JsonSerializer.SerializeToNode(requested)!;
        foreach (var (name, value) in values)
        {
            var field = Field(name).Split('.');
            var owner = field.Length == 1 ? node : node["Simulation"]!;
            owner[field[^1]] = JsonSerializer.SerializeToNode(value);
        }
        var result = node.Deserialize<RunConfiguration>()!;
        Validate(result);
        return result;
    }

    /// <summary>Enforces strategy-defined data bounds for direct engine callers while retaining the full exchange calendar.</summary>
    public static MarketDataset SelectData(IStrategy strategy, MarketDataset data, RunConfiguration configuration)
    {
        var values = Declarations(strategy);
        if (values.ContainsKey(nameof(StrategyOptions.Interval)) && data.Interval != configuration.Interval)
            throw new ArgumentException("The supplied dataset interval does not match the interval defined by the strategy.");
        if (!values.ContainsKey(nameof(StrategyOptions.Start)) && !values.ContainsKey(nameof(StrategyOptions.End))) return data;
        var bars = data.Bars.Where(b => (configuration.Start is null || b.OpenTime >= configuration.Start) &&
            (configuration.End is null || b.CloseTime <= configuration.End)).ToList();
        if (bars.Count == 0) throw new ArgumentException("No historical bars remain within the strategy-defined dates.");
        var first = bars.Min(b => b.OpenTime); var last = bars.Max(b => b.CloseTime);
        return data with { Bars = bars, Funding = data.Funding.Where(f => f.Time >= first && f.Time <= last).ToList() };
    }

    /// <summary>Rejects invalid effective capital, interval, time boundaries and execution settings before data is loaded.</summary>
    public static void Validate(RunConfiguration configuration)
    {
        StrategyFolder.Validate(configuration);
        if (configuration.SchemaVersion != 1 || configuration.InitialCash <= 0) throw new ArgumentException("Initial cash must be positive and the configuration schema must be supported.");
        if (configuration.Interval is null || configuration.Interval.Minutes <= 0 || string.IsNullOrWhiteSpace(configuration.Interval.Name))
            throw new ArgumentException("Strategy interval must have a name and positive duration.");
        if (configuration.Start is not null && configuration.End is not null && configuration.Start >= configuration.End)
            throw new ArgumentException("Start must precede the exclusive end.");
        if (configuration.Simulation is null) throw new ArgumentException("Simulation settings are required.");
        configuration.Simulation.Validate();
    }
}
