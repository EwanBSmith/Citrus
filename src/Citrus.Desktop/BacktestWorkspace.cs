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
        Path.GetFullPath(path, Path.GetDirectoryName(Path.GetFullPath(configurationPath))!);

    /// <summary>Loads a market dataset, compiles trusted source, executes, and exports a completed backtest.</summary>
    internal static Task<WorkspaceResult> RunAsync(string configurationPath, RunConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Strategy) || string.IsNullOrWhiteSpace(config.Output))
            throw new ArgumentException("Strategy and output paths are required.");
        var strategy = Resolve(configurationPath, config.Strategy);
        var output = Resolve(configurationPath, config.Output);
        if (string.IsNullOrWhiteSpace(config.Data)) throw new ArgumentException("A market dataset path is required.");
        var dataPath = Resolve(configurationPath, config.Data);
        var data = Json.Read<MarketDataset>(dataPath);
        using var compiled = CompiledStrategy.Load(strategy, config.References.Select(p => Resolve(configurationPath, p)));
        var result = new BacktestEngine().Run(compiled.Strategy, data, config);
        Reports.Export(output, result, config, data, strategy, dataPath, compiled.DependencyHashes);
        var metrics = Reports.Metrics(result.Equity.Select(p => (p.Time, p.Equity)), config.RiskFreeRate,
            data.Bars.Any(b => b.Instrument.AssetClass == AssetClass.LinearPerpetual) ? 365 : 252);
        return Task.FromResult(new WorkspaceResult(result, metrics, output));
    }

    /// <summary>Creates a self-contained offline example in a new child folder, without overwriting user files.</summary>
    internal static string CreateExample(string parent)
    {
        var root = Path.Combine(parent, "Citrus-example-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Strategy.cs"), """
            using System.Collections.Generic;
            using Citrus.Trading;

            /// <summary>Buys one unit of the example instrument after the first completed bar.</summary>
            public sealed class ExampleStrategy : IStrategy
            {
                /// <summary>Allocates starting capital to the holding strategy.</summary>
                public void OnStart(IStrategyContext context) => context.Register("hold", 1m);

                /// <summary>Places a single quantity-based order using completed history.</summary>
                public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
                {
                    foreach (var bar in bars)
                        if (context.History(bar.Instrument, 2).Count == 1)
                            context.Buy("hold", bar.Instrument, 1m);
                }
            }
            """);
        Json.Write(Path.Combine(root, "data.json"), BrownianGenerator.Generate(
            new Instrument("hyperliquid", AssetClass.LinearPerpetual, "BTC"), BarInterval.Daily,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 365, 42, 40000, 0.05, 0.5));
        var path = Path.Combine(root, "run.json");
        Json.Write(path, new RunConfiguration { Strategy = "Strategy.cs", Data = "data.json", Output = "results" });
        return path;
    }
}
