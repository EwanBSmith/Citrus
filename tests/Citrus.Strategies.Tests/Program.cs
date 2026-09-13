using Citrus.Data;
using Citrus.Engine;
using Citrus.Strategies;
using Citrus.Trading;

var start = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
var instrument = new Instrument("fixture", AssetClass.LinearPerpetual, "BTC");
var data = new MarketDataset { Interval = BarInterval.Daily, Bars = Enumerable.Range(0, 3)
    .Select(i => new Bar(instrument, start.AddDays(i), start.AddDays(i + 1), 100 + i * 10, 120 + i * 10, 90 + i * 10, 100 + i * 10, 1000)).ToList() };
var config = new RunConfiguration { InitialCash = 1000 };
var result = new BacktestEngine().Run(new DemoHold(), data, config);
if (result.Fills.Count != 1 || result.Fills[0].Quantity != 1 || result.Fills[0].Price != 110 || result.Final.Equity != 1010)
    throw new Exception("Demo must buy one unit at the next open and preserve cash/equity accounting.");
var assembly = typeof(DemoHold).Assembly.Location;
using var loaded = CompiledStrategy.LoadAssembly(assembly, "Citrus.Strategies.DemoHold");
var external = new BacktestEngine().Run(loaded.Strategy, data, config);
if (external.Final.Equity != result.Final.Equity || external.Fills.Count != result.Fills.Count)
    throw new Exception("External loading changed strategy behaviour.");
using var portfolio = CompiledStrategy.LoadAssembly(assembly, "ZorroPortfolio");
foreach (var symbol in new[] { "VIX", "VIX3M" })
    if (!portfolio.DependencyHashes.ContainsKey(Path.Combine("Data", symbol + ".t6"))) throw new Exception("Portfolio data was not captured.");
Console.WriteLine("PASS: next-open execution, quantity/accounting, direct/external parity, portfolio discovery and bundled index data.");
