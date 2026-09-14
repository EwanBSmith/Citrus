using System.Text.Json.Nodes;
using Citrus.Data;
using Citrus.Engine;
using Citrus.Trading;

/// <summary>Checks built-in execution, configuration paths, captured history and replay build identity.</summary>
internal static class BacktestRunnerTests
{
    /// <summary>Registers cache and export integration cases with the offline runner.</summary>
    internal static void Register(Action<string, Action> test)
    {
        foreach (var assetClass in new[] { AssetClass.Equity, AssetClass.LinearPerpetual })
            test($"Shared runner executes built-in strategy on {assetClass} history with standalone settings", () =>
            {
                var fixture = CreateFixture(assetClass);
                var completed = BacktestRunner.Run(fixture.Path, historicalDataDirectory: fixture.Cache);
                Require(completed.Output == Path.Combine(fixture.Root, "Results"), "Output was not relative to the configuration file.");
                Require(completed.Result.Equity[0].Equity == 2500 && completed.Result.Fills.Count == 1, "Built-in strategy did not execute.");
                var captured = Json.Read<MarketDataset>(Path.Combine(completed.Output, "historical-data.json"));
                Require(captured.Bars.SequenceEqual(fixture.Data.Bars.Skip(1).Take(2)) && captured.Sessions.SequenceEqual(fixture.Data.Sessions), "Data selection lost boundaries or calendar coverage.");
                using var summary = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(completed.Output, "summary.json")));
                Require(summary.RootElement.GetProperty("annualizationDays").GetInt32() == (assetClass == AssetClass.Equity ? 252 : 365), "Incorrect annualization.");
                var supplied = RunConfiguration.Read(fixture.Path) with { Output = "Edited", Seed = 99, RiskFreeRate = .03m };
                var notified = 0;
                var edited = BacktestRunner.Run(fixture.Path, supplied, fixture.Cache, (effective, _) =>
                {
                    notified++;
                    Require(effective.Seed == 99 && !Directory.Exists(Path.Combine(fixture.Root, "Edited")), "Notification ordering or supplied settings were lost.");
                });
                Require(notified == 1 && edited.Output == Path.Combine(fixture.Root, "Edited"), "Supplied settings were ignored.");
                Require(RunConfiguration.Read(fixture.Path).Seed == 42, "Execution mutated saved settings.");
            });

        test("Replay uses captured history and safely overwrites its own output", () =>
        {
            var fixture = CreateFixture(AssetClass.LinearPerpetual);
            var first = BacktestRunner.Run(fixture.Path, historicalDataDirectory: fixture.Cache);
            var exports = new[] { "summary.json", "orders.json", "fills.json", "costs.json", "equity.csv", "historical-data.json" }
                .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(first.Output, name)));
            File.Delete(Path.Combine(fixture.Cache, "fixture.json"));
            var replay = BacktestRunner.Replay(first.Output);
            Require(replay.Output == Path.Combine(first.Output, "replay-results"), "Replay output was not relative to run.json.");
            foreach (var (name, bytes) in exports)
                Require(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(replay.Output, name))), "Replay changed " + name);
            var path = Path.Combine(first.Output, "run.json");
            Json.Write(path, RunConfiguration.Read(path) with { Output = "." });
            File.WriteAllText(Path.Combine(first.Output, "retain.txt"), "unrelated");
            BacktestRunner.Replay(first.Output);
            foreach (var (name, bytes) in exports)
                Require(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(first.Output, name))), "In-place replay changed " + name);
            Require(File.ReadAllText(Path.Combine(first.Output, "retain.txt")) == "unrelated", "Replay removed unrelated files.");
        });
        test("Replay rejects a different Citrus build or changed captured history", () =>
        {
            var fixture = CreateFixture(AssetClass.LinearPerpetual);
            var first = BacktestRunner.Run(fixture.Path, historicalDataDirectory: fixture.Cache);
            var path = Path.Combine(first.Output, "manifest.json");
            var original = File.ReadAllText(path);
            var manifest = JsonNode.Parse(original)!;
            manifest["componentHashes"]!["Citrus.Strategies"] = "different-build";
            File.WriteAllText(path, manifest.ToJsonString());
            Throws<InvalidDataException>(() => BacktestRunner.Replay(first.Output));
            File.WriteAllText(path, original);
            File.AppendAllText(Path.Combine(first.Output, "historical-data.json"), " ");
            Throws<InvalidDataException>(() => BacktestRunner.Replay(first.Output));
            Require(!Directory.Exists(Path.Combine(first.Output, "replay-results")), "Invalid replay wrote output.");
        });
        test("Built-in declarations resolve before cache access and invalid selections fail before export", () =>
        {
            var fixture = CreateFixture(AssetClass.LinearPerpetual);
            var requested = RunConfiguration.Read(fixture.Path) with { StrategyType = "ZorroPortfolio", InitialCash = -1 };
            var notified = false;
            Throws<DirectoryNotFoundException>(() => BacktestRunner.Run(fixture.Path, requested,
                Path.Combine(fixture.Root, "missing-cache"), (effective, fields) =>
                {
                    notified = true;
                    Require(effective.InitialCash == 17000 && effective.Interval == BarInterval.Daily
                        && fields.Contains("Simulation.AnnualBorrowRate"), "Built-in declarations were not authoritative.");
                }));
            Require(notified && requested.InitialCash == -1, "Configuration notification or immutability failed.");
            Throws<InvalidDataException>(() => BacktestRunner.Run(fixture.Path, requested with { StrategyType = "Missing" }, fixture.Cache));
            Throws<ArgumentException>(() => BacktestRunner.Run(fixture.Path, requested with { Output = " " }, fixture.Cache));
            Require(!Directory.Exists(Path.Combine(fixture.Root, "Results")), "Invalid settings wrote output.");
        });
    }

    /// <summary>Creates standalone settings and a four-bar BTC dataset with explicit equity sessions when needed.</summary>
    private static Fixture CreateFixture(AssetClass assetClass)
    {
        var root = Path.Combine(Path.GetTempPath(), "citrus-runner-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(cache);
        var start = DateTimeOffset.Parse("2024-01-01T14:30:00Z");
        var instrument = new Instrument("fixture", assetClass, "BTC");
        var equity = assetClass == AssetClass.Equity;
        var sessions = equity ? Enumerable.Range(0, 4).Select(i => new MarketSession(start.AddDays(i), start.AddDays(i).AddHours(6.5))).ToList() : [];
        var data = new MarketDataset
        {
            Interval = equity ? BarInterval.Daily : BarInterval.Hourly, Sessions = sessions,
            Bars = Enumerable.Range(0, 4).Select(i => new Bar(instrument,
                equity ? sessions[i].Open : start.AddHours(i), equity ? sessions[i].Close : start.AddHours(i + 1),
                100 + i * 10, 106 + i * 10, 95 + i * 10, 105 + i * 10, 1000, equity, equity)).ToList()
        };
        Json.Write(Path.Combine(cache, "fixture.json"), data);
        var path = Path.Combine(root, "settings.json");
        Json.Write(path, new RunConfiguration { StrategyType = "Citrus.Strategies.DemoHold", InitialCash = 2500,
            Interval = data.Interval, Start = data.Bars[1].OpenTime, End = data.Bars[2].CloseTime, Output = "Results" });
        return new(root, cache, path, data);
    }

    /// <summary>Retains isolated paths and original history for one integration case.</summary>
    private sealed record Fixture(string Root, string Cache, string Path, MarketDataset Data);
    /// <summary>Fails when an execution contract is violated.</summary>
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    /// <summary>Requires the expected exception category.</summary>
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
