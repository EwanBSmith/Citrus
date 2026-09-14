using Citrus.Data;
using Citrus.Trading;
using System.Text.Json;
using System.Security.Cryptography;

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

    /// <summary>Runs the matching built-in strategy using its saved configuration and historical-data.json without reading the live cache.</summary>
    public static CompletedBacktest Replay(string resultsDirectory)
    {
        var directory = Path.GetFullPath(resultsDirectory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var recorded = manifest.RootElement.GetProperty("componentHashes");
        foreach (var (name, hash) in BuiltInStrategies.ComponentHashes())
            if (!recorded.TryGetProperty(name, out var value) || value.GetString() != hash)
                throw new InvalidDataException("Replay requires the original Citrus build; component differs: " + name);
        var dataPath = Path.Combine(directory, "historical-data.json");
        if (manifest.RootElement.GetProperty("dataHash").GetString() != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dataPath))))
            throw new InvalidDataException("Captured historical data has changed.");
        return Execute(Path.Combine(directory, "run.json"), null,
            _ => Json.Read<MarketDataset>(Path.Combine(directory, "historical-data.json")), null);
    }

    /// <summary>Creates a fresh built-in strategy for configuration, simulation, and export.</summary>
    private static CompletedBacktest Execute(string configurationPath, RunConfiguration? configuration,
        Func<RunConfiguration, MarketDataset> loadData, Action<RunConfiguration, IReadOnlyCollection<string>>? onConfigured)
    {
        configurationPath = Path.GetFullPath(configurationPath);
        configuration ??= RunConfiguration.Read(configurationPath);
        var strategy = BuiltInStrategies.Create(configuration.StrategyType);
        var effective = StrategyConfiguration.Resolve(strategy, configuration);
        onConfigured?.Invoke(effective, StrategyConfiguration.Declarations(strategy).Keys.Select(StrategyConfiguration.Field).ToArray());
        if (string.IsNullOrWhiteSpace(effective.Output))
            throw new ArgumentException("An output path is required.");

        var output = Path.GetFullPath(effective.Output, Path.GetDirectoryName(configurationPath)!);
        var data = loadData(effective);
        Directory.CreateDirectory(output);
        var dataPath = Path.Combine(output, "historical-data.json");
        Json.Write(dataPath, data);
        var result = new BacktestEngine().Run(strategy, data, effective);
        Reports.Export(output, result, effective, data, strategy.GetType().Assembly.Location, dataPath, BuiltInStrategies.ComponentHashes(), strategy);
        var performance = Reports.Metrics(result.Equity.Select(point => (point.Time, point.Equity)), effective.RiskFreeRate,
            data.Bars.Any(bar => bar.Instrument.AssetClass == AssetClass.LinearPerpetual) ? 365 : 252);
        return new CompletedBacktest(result, performance, output);
    }
}
