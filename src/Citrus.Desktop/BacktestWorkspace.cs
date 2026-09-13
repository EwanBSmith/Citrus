using System.Text.Json;
using Citrus.Data;
using Citrus.Engine;
using Citrus.Trading;

namespace Citrus.Desktop;

/// <summary>Parses desktop configuration text and creates offline example workspaces.</summary>
internal static class BacktestWorkspace
{
    /// <summary>Parses the same strict JSON configuration format accepted by the CLI.</summary>
    internal static RunConfiguration Parse(string text) =>
        JsonSerializer.Deserialize<RunConfiguration>(text, Json.Options)
        ?? throw new InvalidDataException("The run configuration must be a JSON object.");

    /// <summary>Creates an offline example workspace and refreshes its synthetic prices in the main cache.</summary>
    internal static string CreateExample(string parent, string? historicalDataDirectory = null)
    {
        var root = Path.Combine(parent, "Citrus-example-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "References"));
        File.Copy(typeof(IStrategy).Assembly.Location, Path.Combine(root, "References", "Citrus.Trading.dll"));
        File.WriteAllText(Path.Combine(root, "Example.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup>
              <ItemGroup><Reference Include="Citrus.Trading"><HintPath>References/Citrus.Trading.dll</HintPath></Reference></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Example.slnx"), "<Solution><Project Path=\"Example.csproj\" /></Solution>");

        File.WriteAllText(Path.Combine(root, "Strategy.cs"), """
            using System.Collections.Generic;
            using Citrus.Trading;

            /// <summary>Buys one unit of the example instrument after the first completed bar.</summary>
            public sealed class ExampleStrategy : IStrategy
            {
                private InstrumentContext market = null!;

                /// <summary>Allocates starting capital to the holding strategy.</summary>
                public void OnStart(IStrategyContext context)
                {
                    context.Register("hold", 1m);
                    market = new InstrumentContext(context, "hold", "BTC");
                }

                /// <summary>Places a single quantity-based order using completed history.</summary>
                public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
                {
                    foreach (var bar in bars)
                        if (bar.Instrument == market.Instrument && context.History("BTC", 2).Count == 1)
                            market.Buy(1m);
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
        Json.Write(path, new RunConfiguration { Interval = BarInterval.Daily, Output = "Results/Default",
            StrategyProject = "Example.csproj", StrategyType = "ExampleStrategy", StrategySolution = "Example.slnx" });
        return path;
    }
}
