using Citrus.Data;
using Citrus.Trading;

namespace Citrus.Engine;

/// <summary>Shares strategy loading, historical inputs, execution, and report export across application entry points.</summary>
public static class BacktestRunner
{
    /// <summary>Runs saved or supplied settings against the historical cache; the callback receives effective settings before data loading.</summary>
    /// <remarks>Executes synchronously. UI callers should dispatch the work and marshal configuration notifications to their UI thread.</remarks>
    public static CompletedBacktest Run(string configurationPath, RunConfiguration? configuration = null,
        string? historicalDataDirectory = null, Action<RunConfiguration, IReadOnlyCollection<string>>? onConfigured = null) =>
        Execute(configurationPath, configuration, effective => DataCache.Load(
            historicalDataDirectory ?? GlobalConfiguration.Load().ResolveHistoricalDataDirectory(),
            effective.Interval, effective.Start, effective.End), onConfigured);

    /// <summary>Runs a captured strategy using its saved configuration and historical-data.json without reading the live cache.</summary>
    public static CompletedBacktest Replay(string resultsDirectory)
    {
        var directory = Path.GetFullPath(resultsDirectory);
        return Execute(Path.Combine(directory, "run.json"), null,
            _ => Json.Read<MarketDataset>(Path.Combine(directory, "historical-data.json")), null);
    }

    /// <summary>Owns the compiled strategy through configuration, simulation, and export, then releases its load context.</summary>
    private static CompletedBacktest Execute(string configurationPath, RunConfiguration? configuration,
        Func<RunConfiguration, MarketDataset> loadData, Action<RunConfiguration, IReadOnlyCollection<string>>? onConfigured)
    {
        configurationPath = Path.GetFullPath(configurationPath);
        configuration ??= StrategyFolder.Read(configurationPath);
        using var compiled = CompiledStrategy.LoadConfiguration(configurationPath, configuration);
        var effective = compiled.EffectiveConfiguration(configuration);
        onConfigured?.Invoke(effective, compiled.Options.Keys.Select(StrategyConfiguration.Field).ToArray());
        if (string.IsNullOrWhiteSpace(effective.Output))
            throw new ArgumentException("An output path is required.");

        var output = StrategyFolder.Resolve(configurationPath, effective.Output);
        var data = loadData(effective);
        Directory.CreateDirectory(output);
        var dataPath = Path.Combine(output, "historical-data.json");
        Json.Write(dataPath, data);
        var result = new BacktestEngine().Run(compiled.Strategy, data, effective);
        Reports.Export(output, result, effective, data, compiled.InputPath, dataPath, compiled.DependencyHashes, compiled);
        var performance = Reports.Metrics(result.Equity.Select(point => (point.Time, point.Equity)), effective.RiskFreeRate,
            data.Bars.Any(bar => bar.Instrument.AssetClass == AssetClass.LinearPerpetual) ? 365 : 252);
        return new CompletedBacktest(result, performance, output);
    }
}
