using Citrus.Data;
using Citrus.Engine;
using Citrus.Trading;

/// <summary>Checks that strategy declarations take precedence across configuration, execution and report capture.</summary>
internal static class StrategyConfigurationTests
{
    /// <summary>Registers configuration regressions in the existing engine test executable.</summary>
    internal static void Register(Action<string, Action> test)
    {
        test("Strategy assignments override JSON including zero false and null; other settings survive", () =>
        {
            var strategy = new OptionsFixture();
            var requested = new RunConfiguration { InitialCash = -1, Seed = 99, Start = DateTimeOffset.MaxValue,
                End = DateTimeOffset.MinValue, Simulation = new() { SpreadBps = 90000, AnnualBorrowRate = 9, ShortsAvailable = true } };
            var actual = StrategyConfiguration.Resolve(strategy, requested);
            Require(actual.InitialCash == 2500 && actual.Seed == 99 && actual.Start is null && actual.End is null,
                "Explicit declarations or unassigned settings were lost.");
            Require(actual.Simulation.SpreadBps == 0 && actual.Simulation.AnnualBorrowRate == 0 && !actual.Simulation.ShortsAvailable,
                "Zero and false must be authoritative.");
            strategy.Captured!.InitialCash = 1;
            var second = StrategyConfiguration.Resolve(strategy, requested);
            Require(second.InitialCash == 2500 && strategy.ConfigureCalls == 1, "Declarations were mutable or Configure ran twice.");
            Require(requested.InitialCash == -1 && requested.Simulation.SpreadBps == 90000, "Resolution mutated the requested configuration.");
        });
        test("Direct engine execution applies authoritative cash and dates before startup", () =>
        {
            var strategy = new DatedFixture();
            var data = Data();
            var requested = new RunConfiguration { InitialCash = 5, Interval = BarInterval.Daily };
            var result = new BacktestEngine().Run(strategy, data, requested);
            Require(result.Equity[0].Equity == 2500 && strategy.Seen == 2 && strategy.ConfigureCalls == 1,
                "The engine did not apply strategy settings before creating the account and selecting bars.");
            ExpectFailure(() => new BacktestEngine().Run(new DatedFixture(), data with { Interval = BarInterval.Daily }, requested));
        });
        test("Invalid strategy declarations fail before strategy startup", () =>
        {
            var strategy = new InvalidFixture();
            ExpectFailure(() => new BacktestEngine().Run(strategy, Data(), new()));
            Require(!strategy.Started, "An invalid strategy configuration reached OnStart.");
        });
        test("Strategy declarations and exported manifests use the same effective settings", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "citrus-options-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var requested = new RunConfiguration { InitialCash = 12 };
            var strategy = new OptionsFixture();
            var effective = StrategyConfiguration.Resolve(strategy, requested);
            var data = Data();
            var result = new BacktestEngine().Run(strategy, data, requested);
            var dataPath = Path.Combine(root, "historical-data.json");
            Json.Write(dataPath, data);
            Reports.Export(root, result, requested, data, strategy.GetType().Assembly.Location, dataPath, BuiltInStrategies.ComponentHashes(), strategy);
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            Require(result.Final.Equity == 2500 && effective.InitialCash == 2500 &&
                manifest.RootElement.GetProperty("configuration").GetProperty("initialCash").GetDecimal() == 2500 &&
                manifest.RootElement.GetProperty("strategyOptions").GetProperty("InitialCash").GetDecimal() == 2500,
                "Captured configuration differs from execution or declarations.");
        });
    }

    /// <summary>Creates four consecutive hourly bars for configuration and direct-engine tests.</summary>
    private static MarketDataset Data()
    {
        var start = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var instrument = new Instrument("fixture", AssetClass.LinearPerpetual, "BTC");
        return new() { Interval = BarInterval.Hourly, Bars = Enumerable.Range(0, 4).Select(i =>
            new Bar(instrument, start.AddHours(i), start.AddHours(i + 1), 100, 101, 99, 100, 1000)).ToList() };
    }

    /// <summary>Fails on a violated configuration contract.</summary>
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    /// <summary>Requires invalid settings to fail with a validation error.</summary>
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Invalid strategy options were accepted.");
    }

    /// <summary>Declares values that must win even when they resemble defaults.</summary>
    private class OptionsFixture : IStrategy
    {
        public int ConfigureCalls { get; private set; }
        public StrategyOptions? Captured { get; private set; }
        /// <summary>Captures an options reference to test that the engine freezes declarations.</summary>
        public virtual void Configure(StrategyOptions options)
        {
            ConfigureCalls++;
            Captured = options;
            options.InitialCash = 2500;
            options.Start = null; options.End = null;
            options.Interval = BarInterval.Hourly;
            options.SpreadBps = 0; options.AnnualBorrowRate = 0; options.ShortsAvailable = false;
        }
    }

    /// <summary>Constrains the data window so bars outside it must never reach the strategy.</summary>
    private sealed class DatedFixture : OptionsFixture, IStrategy
    {
        public int Seen { get; private set; }
        /// <summary>Selects exactly two of the supplied hourly bars.</summary>
        public override void Configure(StrategyOptions options)
        {
            base.Configure(options);
            options.Start = DateTimeOffset.Parse("2024-01-01T01:00:00Z");
            options.End = DateTimeOffset.Parse("2024-01-01T03:00:00Z");
        }
        /// <summary>Counts the selected completed bars.</summary>
        public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars) => Seen += bars.Count;
    }

    /// <summary>Defines invalid authoritative cash to prove early validation.</summary>
    private sealed class InvalidFixture : IStrategy
    {
        public bool Started { get; private set; }
        /// <summary>Rejects the run through an invalid capital declaration.</summary>
        public void Configure(StrategyOptions options) => options.InitialCash = 0;
        /// <summary>Records any erroneous startup after validation failed.</summary>
        public void OnStart(IStrategyContext context) => Started = true;
    }
}
