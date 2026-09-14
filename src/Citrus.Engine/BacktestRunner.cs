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
        string? historicalDataDirectory = null, Action<RunConfiguration, IReadOnlyCollection<string>>? onConfigured = null)
    {
        configurationPath = Path.GetFullPath(configurationPath);
        configuration ??= RunConfiguration.Read(configurationPath);
        var strategy = BuiltInStrategies.Create(configuration.StrategyType);
        var effective = StrategyConfiguration.Resolve(strategy, configuration);
        onConfigured?.Invoke(effective, StrategyConfiguration.Declarations(strategy).Keys.Select(StrategyConfiguration.Field).ToArray());
        if (string.IsNullOrWhiteSpace(effective.Output))
            throw new ArgumentException("An output path is required.");

        var output = Path.GetFullPath(effective.Output, Path.GetDirectoryName(configurationPath)!);
        var data = DataCache.Load(historicalDataDirectory ?? GlobalConfiguration.Load().ResolveHistoricalDataDirectory(),
            effective.Interval, effective.Start, effective.End);
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
