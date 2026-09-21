using Citrus.Data;
using Citrus.Engine;
using Citrus.Trading;

namespace Citrus.Desktop;

/// <summary>Creates offline example workspaces.</summary>
internal static class BacktestWorkspace
{
    /// <summary>Creates an offline example workspace and refreshes its synthetic prices in the main cache.</summary>
    internal static string CreateExample(string parent, string? historicalDataDirectory = null)
    {
        var root = Path.Combine(parent, "Citrus-example-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var cache = historicalDataDirectory ?? GlobalConfiguration.Load().ResolveHistoricalDataDirectory();
        Directory.CreateDirectory(cache);
        Json.Write(Path.Combine(cache, "desktop-example.json"), BrownianGenerator.Generate(
            new Instrument("citrus-example", AssetClass.LinearPerpetual, "BTC"), BarInterval.Daily,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 365, 42, 40000, 0.05, 0.5));

        var path = Path.Combine(root, "Default.json");
        Json.Write(path, new RunConfiguration { Interval = BarInterval.Daily, Output = "Results/Default",
            StrategyType = "Citrus.Strategies.DemoHold" });
        return path;
    }
}
