using System.Text.Json;
using Citrus.Data;
using Citrus.Engine;
using Citrus.Trading;

namespace Citrus.Desktop;

/// <summary>Retains a completed run and its report location for desktop presentation.</summary>
internal sealed record WorkspaceResult(BacktestResult Result, Performance Performance, string Output);

/// <summary>Resolves file-based desktop inputs and delegates trading behavior to the existing engine.</summary>
internal static class BacktestWorkspace
{
    /// <summary>Parses the same strict JSON configuration format accepted by the CLI.</summary>
    internal static RunConfiguration Parse(string text) =>
        JsonSerializer.Deserialize<RunConfiguration>(text, Json.Options)
        ?? throw new InvalidDataException("The run configuration must be a JSON object.");

    /// <summary>Resolves a configured path relative to its run file, never the application directory.</summary>
    internal static string Resolve(string configurationPath, string path) =>
        StrategyFolder.Resolve(configurationPath, path);

    /// <summary>Loads a market dataset, compiles trusted source, executes, and exports a completed backtest.</summary>
    internal static Task<WorkspaceResult> RunAsync(string configurationPath, RunConfiguration config, string? historicalDataDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(config.Output))
            throw new ArgumentException("An output path is required.");
        var strategy = StrategyFolder.Source(configurationPath, config);
        var output = Resolve(configurationPath, config.Output);
        var data = DataCache.Load(historicalDataDirectory ?? GlobalConfiguration.Load().ResolveHistoricalDataDirectory(),
            config.Interval, config.Start, config.End);
        Directory.CreateDirectory(output);
        var dataPath = Path.Combine(output, "historical-data.json");
        Json.Write(dataPath, data);
        using var compiled = CompiledStrategy.Load(strategy, config.References.Select(p => Resolve(configurationPath, p)));
        var result = new BacktestEngine().Run(compiled.Strategy, data, config);
        Reports.Export(output, result, config, data, strategy, dataPath, compiled.DependencyHashes);
        var metrics = Reports.Metrics(result.Equity.Select(p => (p.Time, p.Equity)), config.RiskFreeRate,
            data.Bars.Any(b => b.Instrument.AssetClass == AssetClass.LinearPerpetual) ? 365 : 252);
        return Task.FromResult(new WorkspaceResult(result, metrics, output));
    }

    /// <summary>Creates an offline example workspace and refreshes its synthetic prices in the main cache.</summary>
    internal static string CreateExample(string parent, string? historicalDataDirectory = null)
    {
        var root = Path.Combine(parent, "Citrus-example-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "Strategy.cs"), """
            using System.Collections.Generic;
            using Citrus.Trading;

            /// <summary>Buys one unit of the example instrument after the first completed bar.</summary>
            public sealed class ExampleStrategy : IStrategy
            {
                private static readonly Instrument Instrument = new("citrus-example", AssetClass.LinearPerpetual, "BTC");

                /// <summary>Allocates starting capital to the holding strategy.</summary>
                public void OnStart(IStrategyContext context) => context.Register("hold", 1m);

                /// <summary>Places a single quantity-based order using completed history.</summary>
                public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
                {
                    foreach (var bar in bars)
                        if (bar.Instrument == Instrument && context.History(Instrument, 2).Count == 1)
                            context.Buy("hold", Instrument, 1m);
                }
            }
            """);
        var cache = historicalDataDirectory ?? GlobalConfiguration.Load().ResolveHistoricalDataDirectory();
        Directory.CreateDirectory(cache);
        Json.Write(Path.Combine(cache, "desktop-example.json"), BrownianGenerator.Generate(
            new Instrument("citrus-example", AssetClass.LinearPerpetual, "BTC"), BarInterval.Daily,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 365, 42, 40000, 0.05, 0.5));
        Directory.CreateDirectory(Path.Combine(root, "Backtests"));
        var path = Path.Combine(root, "Backtests", "Default.json");
        Json.Write(path, new RunConfiguration { Interval = BarInterval.Daily, Output = "Results/Default" });
        return path;
    }
}
