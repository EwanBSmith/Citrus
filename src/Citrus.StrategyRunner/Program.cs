using Citrus.Data;
using Citrus.Engine;
using Citrus.Strategies;
using Citrus.Trading;

// Find the workspace from either the launch directory or this executable's output directory.
string FindWorkspace()
{
    foreach (var initial in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var folder = new DirectoryInfo(initial); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Citrus.slnx"))) return folder.FullName;
    throw new DirectoryNotFoundException("Cannot locate Citrus.slnx.");
}

var root = FindWorkspace();
var name = args.FirstOrDefault() ?? "Demo";
var path = StrategyFolder.ConfigurationPath(root, name);
var config = StrategyFolder.Read(path);
MarketDataset data;
if (name == "Demo")
{
    data = BrownianGenerator.Generate(new Instrument("demo", AssetClass.LinearPerpetual, "BTC"),
        BarInterval.Daily, DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 365, 42, 40000);
    Directory.CreateDirectory(Path.Combine(root, "artifacts", "demo-cache"));
    Json.Write(Path.Combine(root, "artifacts", "demo-cache", "BTC.json"), data);
}
else data = DataCache.Load(GlobalConfiguration.Load().ResolveHistoricalDataDirectory(), config.Interval, config.Start, config.End);

// Construct directly so IDE breakpoints bind to the ordinary project build and its PDB.
IStrategy strategy = config.StrategyType switch
{
    "Citrus.Strategies.DemoHold" => new DemoHold(),
    "ZorroPortfolio" => new ZorroPortfolio(),
    _ => throw new ArgumentException("Add the selected strategy to the debug runner's factory.")
};
var result = new BacktestEngine().Run(strategy, data, config);
Console.WriteLine($"{name}: {result.Fills.Count} fills; final equity {result.Final.Equity:F2}.");
Console.WriteLine("Use the Citrus CLI or desktop to export a captured, replayable run. Demo data is in artifacts/demo-cache.");
