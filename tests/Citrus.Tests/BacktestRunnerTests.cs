using Citrus.Data;
using Citrus.Engine;
using Citrus.Trading;

/// <summary>Checks shared execution boundaries, effective settings, and replay independence from live inputs.</summary>
internal static class BacktestRunnerTests
{
    /// <summary>Registers source, assembly, cache, and export integration cases with the offline runner.</summary>
    internal static void Register(Action<string, Action> test)
    {
        foreach (var assetClass in new[] { AssetClass.Equity, AssetClass.LinearPerpetual })
            test($"Shared runner resolves {assetClass} settings before selecting history and exports matching metrics", () =>
            {
                var fixture = CreateFixture(assetClass);
                var completed = BacktestRunner.Run(fixture.ConfigurationPath, historicalDataDirectory: fixture.Cache);
                Require(completed.Output == Path.Combine(fixture.Root, "Results", "Default"), "Saved output did not resolve against the workspace.");
                Require(completed.Result.Equity[0].Equity == 2500 && completed.Result.Fills.Count == 1,
                    "Authoritative capital or strategy execution was lost.");
                var captured = Json.Read<MarketDataset>(Path.Combine(completed.Output, "historical-data.json"));
                Require(captured.Interval == fixture.Data.Interval && captured.Bars.SequenceEqual(fixture.Data.Bars.Skip(1).Take(2)),
                    "Cache selection did not use the strategy's interval and dates.");
                Require(captured.Sessions.SequenceEqual(fixture.Data.Sessions), "Data selection discarded calendar coverage.");
                using var summary = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(completed.Output, "summary.json")));
                var portfolio = System.Text.Json.JsonSerializer.Deserialize<Performance>(summary.RootElement.GetProperty("portfolio"), Json.Options);
                Require(portfolio == completed.Performance && summary.RootElement.GetProperty("annualizationDays").GetInt32() ==
                    (assetClass == AssetClass.Equity ? 252 : 365), "Displayed performance differs from the exported summary.");

                var supplied = StrategyFolder.Read(fixture.ConfigurationPath) with { Output = "Results/Edited", Seed = 99, RiskFreeRate = .03m };
                var notified = 0;
                var edited = BacktestRunner.Run(fixture.ConfigurationPath, supplied, fixture.Cache, (effective, fields) =>
                {
                    notified++;
                    Require(effective.InitialCash == 2500 && effective.Seed == 99 && fields.Contains("Simulation.AnnualBorrowRate"),
                        "Configuration notification lost strategy declarations or editable settings.");
                    Require(!Directory.Exists(Path.Combine(fixture.Root, "Results", "Edited")), "Output was written before configuration notification.");
                });
                Require(notified == 1 && edited.Output == Path.Combine(fixture.Root, "Results", "Edited"), "Supplied desktop settings were ignored.");
                using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(edited.Output, "manifest.json")));
                var recorded = manifest.RootElement.GetProperty("configuration");
                Require(recorded.GetProperty("initialCash").GetDecimal() == 2500 && recorded.GetProperty("seed").GetInt32() == 99,
                    "Export did not record the effective configuration.");
                Require(StrategyFolder.Read(fixture.ConfigurationPath).InitialCash == -1 && supplied.InitialCash == -1,
                    "Execution mutated saved or supplied settings.");
            });

        test("Shared replay uses captured strategy assets and history and safely overwrites its own output", () =>
        {
            var fixture = CreateFixture(AssetClass.LinearPerpetual, assembly: true);
            var first = BacktestRunner.Run(fixture.ConfigurationPath, historicalDataDirectory: fixture.Cache);
            var exports = new[] { "summary.json", "orders.json", "fills.json", "costs.json", "equity.csv", "historical-data.json" }
                .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(first.Output, name)));
            File.Delete(Path.Combine(fixture.Cache, "fixture.json"));
            File.Delete(Path.Combine(fixture.Root, "build", "Strategy.dll"));
            File.WriteAllText(Path.Combine(fixture.Root, "build", "input.txt"), "changed");
            var previousCache = Environment.GetEnvironmentVariable("CITRUS_HISTORICAL_DATA");
            try
            {
                Environment.SetEnvironmentVariable("CITRUS_HISTORICAL_DATA", Path.Combine(fixture.Root, "missing-cache"));
                var replay = BacktestRunner.Replay(first.Output);
                Require(replay.Output == Path.Combine(first.Output, "replay-results"), "Replay output resolved against the working directory.");
                foreach (var (name, bytes) in exports)
                    Require(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(replay.Output, name))), "Replay changed " + name);

                var path = Path.Combine(first.Output, "run.json");
                Json.Write(path, StrategyFolder.Read(path) with { Output = "." });
                File.WriteAllText(Path.Combine(first.Output, "retain.txt"), "unrelated");
                var overwritten = BacktestRunner.Replay(first.Output);
                foreach (var (name, bytes) in exports)
                    Require(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(overwritten.Output, name))), "In-place replay changed " + name);
                Require(File.ReadAllText(Path.Combine(first.Output, "retain.txt")) == "unrelated", "Replay removed unrelated files.");
            }
            finally { Environment.SetEnvironmentVariable("CITRUS_HISTORICAL_DATA", previousCache); }
        });

        test("Shared runner publishes effective settings even when historical data loading fails", () =>
        {
            var fixture = CreateFixture(AssetClass.LinearPerpetual);
            var notified = false;
            Throws<DirectoryNotFoundException>(() => BacktestRunner.Run(fixture.ConfigurationPath,
                historicalDataDirectory: Path.Combine(fixture.Root, "missing-cache"), onConfigured: (effective, fields) =>
                {
                    notified = true;
                    Require(effective.InitialCash == 2500 && fields.Contains(nameof(StrategyOptions.InitialCash)), "Effective settings were unavailable.");
                }));
            Require(notified && !Directory.Exists(Path.Combine(fixture.Root, "Results")), "Notification or failure ordering changed.");
        });

        test("Shared runner rejects invalid declarations and empty output before cache access or export", () =>
        {
            var fixture = CreateFixture(AssetClass.LinearPerpetual);
            var requested = StrategyFolder.Read(fixture.ConfigurationPath);
            Throws<ArgumentException>(() => BacktestRunner.Run(fixture.ConfigurationPath, requested with { Output = " " },
                Path.Combine(fixture.Root, "missing-cache")));
            var source = Path.Combine(fixture.Root, "Strategy.cs");
            File.WriteAllText(source, File.ReadAllText(source).Replace("options.InitialCash = 2500;", "options.InitialCash = 0;"));
            var notified = false;
            Throws<ArgumentException>(() => BacktestRunner.Run(fixture.ConfigurationPath, requested,
                Path.Combine(fixture.Root, "missing-cache"), (_, _) => notified = true));
            Require(!notified && !Directory.Exists(Path.Combine(fixture.Root, "Results")), "Invalid settings reached notification or export.");
        });
    }

    /// <summary>Creates isolated history and a strategy whose declarations override deliberately incompatible saved settings.</summary>
    private static Fixture CreateFixture(AssetClass assetClass, bool assembly = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "citrus-runner-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(Path.Combine(root, "Backtests"));
        var start = DateTimeOffset.Parse("2024-01-01T14:30:00Z");
        var instrument = new Instrument("fixture", assetClass, "TEST");
        var equity = assetClass == AssetClass.Equity;
        var sessions = equity ? Enumerable.Range(0, 4).Select(i => new MarketSession(start.AddDays(i), start.AddDays(i).AddHours(6.5))).ToList() : [];
        var data = new MarketDataset
        {
            Interval = equity ? BarInterval.Daily : BarInterval.Hourly,
            Sessions = sessions,
            Bars = Enumerable.Range(0, 4).Select(i => new Bar(instrument,
                equity ? sessions[i].Open : start.AddHours(i), equity ? sessions[i].Close : start.AddHours(i + 1),
                100 + i * 10, 106 + i * 10, 95 + i * 10, 105 + i * 10, 1000, equity, equity)).ToList()
        };
        Json.Write(Path.Combine(cache, "fixture.json"), data);
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using Citrus.Trading;
            /// <summary>Trades one unit after the first completed observation.</summary>
            public sealed class Strategy : IStrategy
            {
                private int configureCalls;
                /// <summary>Declares the effective capital and historical selection once per loaded instance.</summary>
                public void Configure(StrategyOptions options)
                {
                    if (++configureCalls != 1) throw new Exception("Configure ran twice.");
                    options.InitialCash = 2500;
                    options.AnnualBorrowRate = 0;
                    options.Interval = BarInterval.{{(equity ? "Daily" : "Hourly")}};
                    options.Start = DateTimeOffset.Parse("{{data.Bars[1].OpenTime:O}}");
                    options.End = DateTimeOffset.Parse("{{data.Bars[2].CloseTime:O}}");
                }
                /// <summary>Checks captured content when present and registers one trading account.</summary>
                public void OnStart(IStrategyContext context)
                {
                    {{(assembly ? "if (System.IO.File.ReadAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Strategy).Assembly.Location)!, \"input.txt\")) != \"fixture\") throw new Exception(\"Strategy content changed.\");" : "")}}
                    context.Register("hold", 1m);
                }
                /// <summary>Submits an order using only completed history.</summary>
                public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
                {
                    if (context.History("TEST", 2).Count == 1) context.Buy("hold", bars[0].Instrument, 1m);
                }
            }
            """;
        File.WriteAllText(Path.Combine(root, "Strategy.cs"), source);
        if (assembly)
        {
            var build = Path.Combine(root, "build");
            Directory.CreateDirectory(build);
            using var stream = File.Create(Path.Combine(build, "Strategy.dll"));
            var emitted = StrategyCompilation.Create(source, Path.Combine(root, "Strategy.cs")).Emit(stream);
            Require(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            File.WriteAllText(Path.Combine(build, "input.txt"), "fixture");
        }
        var configurationPath = Path.Combine(root, "Backtests", "Default.json");
        Json.Write(configurationPath, new RunConfiguration
        {
            StrategyAssembly = assembly ? "build/Strategy.dll" : null,
            InitialCash = -1, Interval = equity ? BarInterval.Hourly : BarInterval.Daily,
            Start = DateTimeOffset.MaxValue, End = DateTimeOffset.MinValue, Output = "Results/Default"
        });
        return new(root, cache, configurationPath, data);
    }

    /// <summary>Retains isolated paths and original history for one runner integration case.</summary>
    private sealed record Fixture(string Root, string Cache, string ConfigurationPath, MarketDataset Data);

    /// <summary>Fails when a shared execution contract is violated.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    /// <summary>Requires the expected failure category instead of allowing an unrelated exception to satisfy the check.</summary>
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
