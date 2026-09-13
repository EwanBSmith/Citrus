using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Citrus.Trading;
using Citrus.Data;
using Citrus.Engine;
using Citrus.Simulation;

// Keep regression checks offline and package-free; named actions are executed by the runner below.
var tests = new List<(string Name, Action Test)>();
ExternalStrategyTests.Register((name, action) => tests.Add((name, action)));
StrategyConfigurationTests.Register((name, action) => tests.Add((name, action)));
BacktestRunnerTests.Register((name, action) => tests.Add((name, action)));
var instrument = new Instrument("test", AssetClass.LinearPerpetual, "BTC");
var start = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
// Build hourly perpetual bars with the supplied open/close prices and a fixed intrabar price range.
MarketDataset Data(params decimal[] prices) => new() { Interval = BarInterval.Hourly, Bars = prices.Select((p, i) =>
    new Bar(instrument, start.AddHours(i), start.AddHours(i + 1), p, p + 5, p - 5, p, 1000)).ToList() };
// Create a small-capital run configuration with zero borrow by default unless options are supplied.
RunConfiguration Config(SimulationOptions? simulation = null) => new() { InitialCash = 1000, Simulation = simulation ?? new() { AnnualBorrowRate = 0 } };
// Register a named regression action for the package-free test runner.
void Test(string name, Action action) => tests.Add((name, action));
// Fail unless expected and actual values match under the default equality comparer.
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
// Fail with the supplied reason when the asserted condition is false.
void True(bool condition, string reason = "Assertion failed") { if (!condition) throw new Exception(reason); }
// Require the action to throw the specified exception type, allowing derived exception types.
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
// Run a fixture strategy against supplied data or the default three-bar scenario.
BacktestResult Run(IStrategy strategy, MarketDataset? data = null, SimulationOptions? options = null) => new BacktestEngine().Run(strategy, data ?? Data(100, 110, 120), Config(options));
// Create a unique temporary directory for filesystem and compilation fixtures.
string Temporary() { var path = Path.Combine(Path.GetTempPath(), "citrus-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }

Test("Strategy folders select named backtests and resolve paths independently of the working directory", () =>
{
    var root = Temporary();
    Directory.CreateDirectory(Path.Combine(root, "Backtests"));
    File.WriteAllText(Path.Combine(root, "Strategy.cs"), "// fixture");
    var first = Path.Combine(root, "Backtests", "Default.json");
    Json.Write(first, new RunConfiguration { Output = "Results/Default" });
    True(!File.ReadAllText(first).Contains("\"strategy\""));
    Equal(first, StrategyFolder.ConfigurationPath(root));
    Equal(Path.Combine(root, "Strategy.cs"), StrategyFolder.Source(first, StrategyFolder.Read(first)));
    Equal(Path.Combine(root, "Results", "Default"), StrategyFolder.Resolve(first, "Results/Default"));
    var second = Path.Combine(root, "Backtests", "HigherCosts.json");
    Json.Write(second, new RunConfiguration { InitialCash = 2500 });
    Equal(second, StrategyFolder.ConfigurationPath(root, "HigherCosts"));
    Equal(2500m, StrategyFolder.Read(second).InitialCash);
    Throws<FileNotFoundException>(() => StrategyFolder.ConfigurationPath(root, "../missing"));
    File.Delete(first);
    Equal(second, StrategyFolder.ConfigurationPath(root));
    Json.Write(Path.Combine(root, "Backtests", "Other.json"), new RunConfiguration());
    Throws<InvalidDataException>(() => StrategyFolder.ConfigurationPath(root));
    Json.Write(second, new RunConfiguration { Strategy = "elsewhere.cs" });
    Throws<InvalidDataException>(() => StrategyFolder.Read(second));
    var legacy = Path.Combine(root, "run.json");
    Json.Write(legacy, new RunConfiguration { Strategy = "Old.cs" });
    Equal(Path.Combine(root, "Old.cs"), StrategyFolder.Source(legacy, StrategyFolder.Read(legacy)));
});

Test("Global settings round trip, replace and reject malformed files without exposing values", () =>
{
    var path = Path.Combine(Temporary(), "profile", "config.json");
    Equal("", GlobalConfiguration.Load(path).AlpacaApiKeyId);
    True(!File.Exists(path));
    var historical = Path.Combine(Temporary(), "history");
    new GlobalConfiguration { AlpacaApiKeyId = "fixture-key", AlpacaApiSecretKey = "fixture-secret", HistoricalDataDirectory = historical }.Save(path);
    var restored = GlobalConfiguration.Load(path);
    Equal(Path.GetFullPath(historical), restored.ResolveHistoricalDataDirectory(_ => null));
    Equal(("fixture-key", "fixture-secret"), restored.ResolveAlpacaCredentials(_ => null));
    Equal(("override", "fixture-secret"), restored.ResolveAlpacaCredentials(name => name == "APCA_API_KEY_ID" ? "override" : ""));
    restored.AlpacaApiSecretKey = "";
    restored.Save(path);
    Throws<InvalidOperationException>(() => GlobalConfiguration.Load(path).ResolveAlpacaCredentials(_ => null));
    Equal(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    foreach (var invalid in new[] { "null", "{", "{\"schemaVersion\":2}", "{\"alpacaApiKeyId\":null}", "{\"historicalDataDirectory\":null}", "{\"fixture-secret\":123}" })
    {
        File.WriteAllText(path, invalid);
        Throws<InvalidDataException>(() => GlobalConfiguration.Load(path));
        Equal(invalid, File.ReadAllText(path));
    }
});

Test("Substrategy registration permits 100 accounts and rejects the 101st", () =>
{
    var result = Run(new CallbackStrategy(weight: 0.001m, onStart: c =>
    {
        for (var i = 1; i < 100; i++) c.Register($"s{i}", 0.001m);
        Throws<InvalidOperationException>(() => c.Register("overflow", 0.001m));
    }));
    Equal(100, result.Equity.First().Substrategies.Count);
});
Test("Substrategy limit is not a configuration setting", () =>
{
    Throws<JsonException>(() => JsonSerializer.Deserialize<RunConfiguration>("{\"maximumSubstrategies\":200}", Json.Options));
    True(!JsonSerializer.Serialize(Config(), Json.Options).Contains("maximumSubstrategies", StringComparison.OrdinalIgnoreCase));
});
Test("Strategies can observe and trade all dataset instruments without configuration lists", () =>
{
    var other = instrument with { Symbol = "ETH" };
    var data = Data(100, 110);
    data = data with { Bars = data.Bars.Concat(data.Bars.Select(b => b with { Instrument = other })).OrderBy(b => b.OpenTime).ToList() };
    var config = Config();
    var path = Path.Combine(Temporary(), "run.json"); Json.Write(path, config);
    var restored = Json.Read<RunConfiguration>(path);
    var observedOther = false;
    var result = new BacktestEngine().Run(new CallbackStrategy(
        onStart: c => c.Buy("a", instrument, 1),
        onBar: (c, bars) =>
        {
            observedOther |= bars.Any(b => b.Instrument == other);
            True(c.History(other, 10).Count > 0);
            if (c.History(other, 2).Count == 1) c.Buy("a", other, 1);
        }), data, restored);
    Equal(2, result.Fills.Count); True(observedOther);
    Equal(instrument, result.Fills[0].Instrument);
    Equal(other, result.Fills[1].Instrument);
});
Test("Orders require market bars for the requested instrument", () =>
{
    Throws<ArgumentException>(() => Run(new CallbackStrategy(onStart: c => c.Buy("a", instrument with { Symbol = "MISSING" }, 1))));
});
Test("Market decisions see only completed bars and fill next open", () =>
{
    var strategy = new CallbackStrategy(onBar: (c, b) => { Equal(b[^1].CloseTime, c.Time); True(c.History(instrument, 100).All(x => x.CloseTime <= c.Time)); if (c.History(instrument, 100).Count == 1) c.Submit(new("a", instrument, 1)); });
    var result = Run(strategy); Equal(1, result.Fills.Count); Equal(110m, result.Fills[0].Price); Equal(start.AddHours(1), result.Fills[0].Time); Equal(1010m, result.Final.Equity);
});
Test("Startup has no future observations", () => Run(new CallbackStrategy(onStart: c => Equal(0, c.History(instrument, 100).Count))));
Test("Limit gap improvement and limit cost clamp", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 1, OrderType.Limit, 105))), options: new() { SlippageBps = 1000 });
    Equal(105m, result.Fills.Single().Price);
    var improved = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 1, OrderType.Limit, 105)))); Equal(100m, improved.Fills.Single().Price);
});
Test("Untouched limits remain pending and cancel at end", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 1, OrderType.Limit, 50))));
    Equal(0, result.Fills.Count); Equal(OrderStatus.Cancelled, result.Orders[^1].Status);
});
Test("Internal crossing, residual fills and commission reconcile", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => { c.Register("b", 0.5m); c.Submit(new("a", instrument, 10)); c.Submit(new("b", instrument, -4)); }, weight: 0.5m), options: new() { CommissionFixed = 6 });
    Equal(3, result.Fills.Count); Equal(2, result.Fills.Count(f => f.Internal)); Equal(6m, result.Fills.Sum(f => f.Commission));
    Equal(6m, result.Final.Positions.Sum(p => p.Quantity)); Equal(result.Final.Equity, result.Equity[^1].Substrategies.Values.Sum());
    Equal(1114m, result.Final.Equity);
});
Test("Full internal offset has no external rejection or costs", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => { c.Register("b", 0.5m); c.Submit(new("a", instrument, 2)); c.Submit(new("b", instrument, -2)); }, weight: 0.5m), options: new() { RejectionProbability = 1, CommissionFixed = 50 });
    Equal(2, result.Fills.Count); True(result.Fills.All(f => f.Internal)); Equal(1000m, result.Final.Equity);
});
Test("Residual rejection preserves internal fills", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => { c.Register("b", 0.5m); c.Submit(new("a", instrument, 3)); c.Submit(new("b", instrument, -2)); }, weight: 0.5m), options: new() { RejectionProbability = 1 });
    Equal(2, result.Fills.Count); Equal(0m, result.Final.Positions.Sum(p => p.Quantity)); True(result.Orders.Any(o => o.Status == OrderStatus.Rejected));
});
Test("Different limits and TIF never cross", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => { c.Register("b", 0.5m); c.Submit(new("a", instrument, 1, OrderType.Limit, 101)); c.Submit(new("b", instrument, -1, OrderType.Limit, 99)); }, weight: 0.5m));
    Equal(2, result.Fills.Count); True(result.Fills.All(f => !f.Internal));
});
Test("Proportional commission allocation conserves exact totals", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => { c.Register("b", 0.5m); c.Submit(new("a", instrument, 1)); c.Submit(new("b", instrument, 2)); }, weight: 0.5m), options: new() { CommissionFixed = 1 });
    Equal(1m, result.Fills.Sum(f => f.Commission)); Equal(1m / 3, result.Fills[0].Commission);
});
Test("Initial margin rejects oversized orders", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 101)))); Equal(0, result.Fills.Count); True(result.Orders.Any(o => o.Reason == "Insufficient initial margin"));
});
Test("Perpetual funding debits longs", () =>
{
    var data = Data(100, 100); data.Funding.Add(new(instrument, start.AddHours(1), 0.01m, 100));
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 2))), data); Equal(998m, result.Final.Equity); Equal(-2m, result.Costs.Single(c => c.Kind == "Funding").Amount);
});
Test("Maintenance breach liquidates at observed opening gap", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 50))), Data(100, 81, 80));
    True(result.Fills.Any(f => f.Liquidation)); Equal(0m, result.Final.Positions.Sum(p => p.Quantity)); Equal(50m, result.Final.Equity);
});
Test("Seeded rejection is reproducible with both outcomes", () =>
{
    // Create a fresh strategy that submits on every bar to compare seeded rejection sequences.
    CallbackStrategy Strategy() => new(onBar: (c, _) => c.Submit(new("a", instrument, 1)));
    var data = Data(Enumerable.Repeat(100m, 100).ToArray()); var options = new SimulationOptions { RejectionProbability = 0.5 };
    var one = Run(Strategy(), data, options); var two = Run(Strategy(), data, options);
    Equal(JsonSerializer.Serialize(one, Json.Options), JsonSerializer.Serialize(two, Json.Options)); True(one.Fills.Count is > 10 and < 90);
});
Test("Equivalent backtest and fake live-context decisions", () =>
{
    // Create the same first-bar decision independently for each execution-mode comparison.
    CallbackStrategy Strategy() => new(onBar: (c, _) => { if (c.History(instrument, 100).Count == 1) c.Submit(new("a", instrument, 2)); });
    var engine = new BacktestEngine(); var backtest = engine.Run(Strategy(), Data(100, 110), Config()); var live = engine.Run(Strategy(), Data(100, 110), Config(), ExecutionMode.Live);
    Equal(JsonSerializer.Serialize(backtest.Orders), JsonSerializer.Serialize(live.Orders));
});
Test("External data fetches once per key and returns copies", () =>
{
    var fetches = 0;
    var strategy = new CallbackStrategy(onStart: c =>
    {
        var first = c.ExternalData("key", () => { fetches++; return [1, 2]; });
        first[0] = 9;
        Equal((byte)1, c.ExternalData("key", () => throw new Exception("Unexpected second fetch"))[0]);
    });
    new BacktestEngine().Run(strategy, Data(100), Config()); Equal(1, fetches);
});
Test("Scheduled events are ordered and cannot inspect unfinished bars", () =>
{
    var seen = new List<DateTimeOffset>();
    Run(new CallbackStrategy(onStart: c => c.Schedule(start.AddMinutes(30), "event"), onScheduled: (c, _) => { seen.Add(c.Time); Equal(0, c.History(instrument, 1).Count); }));
    Equal(start.AddMinutes(30), seen.Single());
});
Test("Rebalance accounts for outstanding orders", () =>
{
    var result = Run(new CallbackStrategy(onBar: (c, _) => { if (c.History(instrument, 100).Count == 1) { var weights = new Dictionary<Instrument, decimal> { [instrument] = 0.5m }; c.Rebalance("a", weights); c.Rebalance("a", weights); } }));
    Equal(1, result.Fills.Count); Equal(5m, result.Fills[0].Quantity);
});
Test("SMA and EMA warmup and reference values", () => { Equal<decimal?>(null, Indicators.Sma([1, 2], 3)); Equal<decimal?>(3, Indicators.Sma([1, 2, 3, 4], 3)); Equal<decimal?>(3, Indicators.Ema([1, 2, 3, 4], 3)); });
Test("Data rejects duplicates, malformed bars and gaps", () =>
{
    var data = Data(100, 110); data.Bars.Add(data.Bars[0]); Throws<InvalidDataException>(() => DatasetValidator.Validate(data));
    data = Data(100); data.Bars[0] = data.Bars[0] with { Low = 200 }; Throws<InvalidDataException>(() => DatasetValidator.Validate(data));
    data = Data(100, 110, 120); data.Bars.RemoveAt(1); Throws<InvalidDataException>(() => DatasetValidator.RequireCoverage(data, instrument, start, start.AddHours(3)));
});
Test("GBM is seeded and produces valid positive OHLC", () =>
{
    var first = BrownianGenerator.Generate(instrument, BarInterval.Hourly, start, 100, 42); DatasetValidator.Validate(first);
    Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(BrownianGenerator.Generate(instrument, BarInterval.Hourly, start, 100, 42)));
});
Test("Cache fetches only missing coverage and reuses valid bars", () =>
{
    var provider = new FixtureProvider(Data(100, 110, 120)); var cache = new DataCache(Temporary());
    cache.GetAsync(provider, new(instrument, BarInterval.Hourly, start, start.AddHours(2)), []).GetAwaiter().GetResult();
    cache.GetAsync(provider, new(instrument, BarInterval.Hourly, start, start.AddHours(3)), []).GetAwaiter().GetResult();
    cache.GetAsync(provider, new(instrument, BarInterval.Hourly, start, start.AddHours(3)), []).GetAwaiter().GetResult();
    Equal(2, provider.Requests.Count); Equal(start.AddHours(2), provider.Requests[1].Start);
});
Test("Symbol binding resolves legacy venues and case across history orders and positions", () =>
{
    var alias = instrument with { Venue = "another-source", Symbol = "btc" };
    var result = Run(new CallbackStrategy(onStart: c =>
    {
        Equal(instrument, c.ResolveInstrument("btc"));
        Equal(0, c.History("BTC", 5).Count);
        new InstrumentContext(c, "a", "btc").Buy(1);
    }, onBar: (c, _) =>
    {
        Equal(c.History(instrument, 5).Count, c.History(alias, 5).Count);
        if (c.History(alias, 5).Count == 1)
        {
            Equal(1m, new InstrumentContext(c, "a", alias).Quantity);
            c.Rebalance("a", new Dictionary<Instrument, decimal> { [alias] = 0 });
        }
    }));
    Equal(2, result.Fills.Count);
    True(result.Fills.All(f => f.Instrument == instrument));
    Equal(0m, result.Final.Positions.Single().Quantity);
    var direct = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", alias, 1))));
    Equal(instrument, direct.Fills.Single().Instrument);
});
Test("Missing symbols and asset-class mismatches fail before silent warmup", () =>
{
    Run(new CallbackStrategy(onStart: c =>
    {
        Throws<ArgumentException>(() => new InstrumentContext(c, "a", "MISSING"));
        Throws<ArgumentException>(() => c.History("MISSING", 90));
        Throws<ArgumentException>(() => c.Submit(new("a", instrument with { AssetClass = AssetClass.Equity }, 1)));
        Throws<ArgumentException>(() => new InstrumentContext(c, "a", instrument with { AssetClass = AssetClass.Equity }));
    }));
});
Test("Symbol histories reject conflicting instrument definitions", () =>
{
    var data = Data(100, 110);
    var conflict = data.Bars[0] with { Instrument = instrument with { Venue = "other" } };
    Throws<InvalidDataException>(() => DatasetValidator.Validate(data with { Bars = data.Bars.Append(conflict).ToList() }));
});
Test("Symbol cache reuses source metadata without provider-keyed duplicates", () =>
{
    var directory = Temporary(); var cache = new DataCache(directory);
    var first = new FixtureProvider(Data(100, 110, 120));
    var request = new DataRequest(instrument, BarInterval.Hourly, start, start.AddHours(2));
    cache.GetAsync(first, request, []).GetAwaiter().GetResult();
    Equal("symbol-BTC-60.json", Path.GetFileName(Directory.GetFiles(directory).Single()));
    var aliasRequest = request with { Instrument = instrument with { Venue = "elsewhere", Symbol = "btc" } };
    var reused = cache.GetAsync(new NamedFixtureProvider(Data(900, 900), "different-source"), aliasRequest, []).GetAwaiter().GetResult();
    Equal("fixture", reused.Provider); Equal(100m, reused.Bars[0].Close);
    Equal(1, Directory.GetFiles(directory).Length);
    var before = File.ReadAllText(Directory.GetFiles(directory).Single());
    Throws<InvalidDataException>(() => cache.GetAsync(new NamedFixtureProvider(Data(900, 900, 900), "different-source"),
        aliasRequest with { End = start.AddHours(3) }, []).GetAwaiter().GetResult());
    Equal(before, File.ReadAllText(Directory.GetFiles(directory).Single()));
    var extended = cache.GetAsync(first, aliasRequest with { End = start.AddHours(3) }, []).GetAwaiter().GetResult();
    Equal(3, extended.Bars.Count); Equal(2, first.Requests.Count);
});
Test("Symbol cache rejects multiple files instead of selecting by provider", () =>
{
    var directory = Temporary(); var data = Data(100);
    Json.Write(Path.Combine(directory, "one.json"), data);
    Json.Write(Path.Combine(directory, "two.json"), data with { Provider = "other" });
    Throws<InvalidDataException>(() => new DataCache(directory).GetAsync(new FixtureProvider(data),
        new(instrument, BarInterval.Hourly, start, start.AddHours(1)), []).GetAwaiter().GetResult());
});
Test("Backtest data is assembled read-only from the main historical cache", () =>
{
    var directory = Temporary();
    var btc = Data(100, 110, 120) with { Provider = "fixture-a" };
    var eth = btc with { Provider = "fixture-b", Bars = btc.Bars.Select(b => b with { Instrument = instrument with { Symbol = "ETH" } }).ToList() };
    Json.Write(Path.Combine(directory, "btc.json"), btc);
    Json.Write(Path.Combine(directory, "eth.json"), eth);
    Json.Write(Path.Combine(directory, "daily.json"), BrownianGenerator.Generate(instrument, BarInterval.Daily, start, 2, 3));
    var loaded = DataCache.Load(directory, BarInterval.Hourly, start.AddHours(1), start.AddHours(3));
    Equal(4, loaded.Bars.Count); Equal(2, loaded.Bars.Select(b => b.Instrument).Distinct().Count());
    True(loaded.Bars.All(b => b.OpenTime >= start.AddHours(1) && b.CloseTime <= start.AddHours(3)));
    Equal(3, Directory.GetFiles(directory).Length);
    Throws<InvalidDataException>(() => DataCache.Load(directory, BarInterval.Hourly, start.AddYears(10), start.AddYears(11)));
});
Test("Legacy cache data is reused without data revisions", () =>
{
    var directory = Temporary(); var path = Path.Combine(directory, "old-hash.json");
    var data = Data(100, 110) with { Provider = "fixture" };
    var node = JsonSerializer.SerializeToNode(data, Json.Options)!;
    node["version"] = "obsolete";
    File.WriteAllText(path, node.ToJsonString());
    var provider = new FixtureProvider(data);
    new DataCache(directory).GetAsync(provider, new(instrument, BarInterval.Hourly, start, start.AddHours(2)), []).GetAwaiter().GetResult();
    Equal(0, provider.Requests.Count); Equal(1, Directory.GetFiles(directory).Length);
    True(!File.ReadAllText(path).Contains("\"version\""));
    Equal(2, DataCache.Load(directory, BarInterval.Hourly).Bars.Count);
});
Test("Missing history reports feed and dates and preserves cached coverage", () =>
{
    var directory = Temporary(); var provider = new FixtureProvider(Data(100)); var cache = new DataCache(directory);
    cache.GetAsync(provider, new(instrument, BarInterval.Hourly, start, start.AddHours(1)), []).GetAwaiter().GetResult();
    var path = Directory.GetFiles(directory).Single(); var before = File.ReadAllText(path);
    try
    {
        cache.GetAsync(provider, new(instrument, BarInterval.Hourly, start, start.AddHours(3)), []).GetAwaiter().GetResult();
        throw new Exception("Missing history unexpectedly succeeded.");
    }
    catch (InvalidDataException error)
    {
        True(error.Message.Contains("fixture") && error.Message.Contains("2024-01-01 01:00") && error.Message.Contains("Missing 2"));
    }
    Equal(before, File.ReadAllText(path));
});
Test("Provider errors do not expose response secrets", () =>
{
    using var http = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("SECRET-123") }));
    try { new HyperliquidProvider(http).FetchAsync(new(instrument, BarInterval.Hourly, start, start.AddHours(1))).GetAwaiter().GetResult(); throw new Exception("Expected HTTP error"); }
    catch (HttpRequestException e) { True(!e.Message.Contains("SECRET")); }
});
Test("Hyperliquid fails on missing candle history", () =>
{
    using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") }));
    Throws<InvalidDataException>(() => new HyperliquidProvider(http).FetchAsync(new(instrument, BarInterval.Hourly, start, start.AddHours(1))).GetAwaiter().GetResult());
});
Test("Roslyn compilation diagnostics and execution", () =>
{
    var directory = Temporary(); var path = Path.Combine(directory, "strategy.cs"); File.WriteAllText(path, "using Citrus.Trading; public sealed class Example : IStrategy { }");
    using var compiled = CompiledStrategy.Load(path); Run(compiled.Strategy);
    File.WriteAllText(path, "this is invalid C#"); Throws<InvalidDataException>(() => CompiledStrategy.Load(path));
});
Test("Export totals and provenance manifest", () =>
{
    var directory = Temporary(); var strategy = Path.Combine(directory, "strategy.cs"); var dataPath = Path.Combine(directory, "data.json");
    File.WriteAllText(strategy, "using Citrus.Trading; public class Example : IStrategy { }"); var data = Data(100, 110); Json.Write(dataPath, data);
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 2))), data);
    var output = Path.Combine(directory, "result"); Reports.Export(output, result, Config(), data, strategy, dataPath, new Dictionary<string, string>());
    using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
    Equal(1020m, summary.RootElement.GetProperty("final").GetProperty("equity").GetDecimal());
    Equal(0.02m, summary.RootElement.GetProperty("portfolio").GetProperty("totalReturn").GetDecimal());
    True(File.Exists(Path.Combine(output, "manifest.json"))); Equal(2, File.ReadAllLines(Path.Combine(output, "fills.csv")).Length);
});

Test("Repeated exports replace results while retaining unrelated files", () =>
{
    var directory = Temporary(); var strategy = Path.Combine(directory, "strategy.cs"); var dataPath = Path.Combine(directory, "data.json");
    File.WriteAllText(strategy, "using Citrus.Trading; public class First : IStrategy { }");
    var data = Data(100, 110); Json.Write(dataPath, data);
    var output = Path.Combine(directory, "result");
    Reports.Export(output, Run(new CallbackStrategy(onStart: c => c.Buy("a", instrument, 2)), data), Config(), data, strategy, dataPath, new Dictionary<string, string>());
    File.WriteAllText(Path.Combine(output, "notes.txt"), "keep");
    File.WriteAllText(strategy, "using Citrus.Trading; public class Second : IStrategy { }");
    data = Data(100, 120); Json.Write(dataPath, data);
    var result = Run(new CallbackStrategy(), data);
    Reports.Export(output, result, Config(), data, strategy, dataPath, new Dictionary<string, string>());
    Equal(0, Json.Read<List<Fill>>(Path.Combine(output, "fills.json")).Count);
    Equal(1, File.ReadAllLines(Path.Combine(output, "fills.csv")).Length);
    Equal("keep", File.ReadAllText(Path.Combine(output, "notes.txt")));
});

// Explicit sessions keep holiday and DST behavior independent of the host timezone.
var equity = new Instrument("alpaca", AssetClass.Equity, "ABC");
// Build equity bars against explicit sessions spanning a weekend and daylight-saving transition.
MarketDataset Equities() => new() { Bars = [
    new(equity, DateTimeOffset.Parse("2024-03-08T14:30:00Z"), DateTimeOffset.Parse("2024-03-08T21:00:00Z"), 100, 100, 100, 100, 1000, true, true),
    new(equity, DateTimeOffset.Parse("2024-03-11T13:30:00Z"), DateTimeOffset.Parse("2024-03-11T20:00:00Z"), 50, 50, 50, 50, 1000, true, true)],
    Sessions = [new(DateTimeOffset.Parse("2024-03-08T14:30:00Z"), DateTimeOffset.Parse("2024-03-08T21:00:00Z")), new(DateTimeOffset.Parse("2024-03-11T13:30:00Z"), DateTimeOffset.Parse("2024-03-11T20:00:00Z"))] };
Test("Equity sessions handle weekend and DST with next-open orders", () =>
{
    var data = Equities(); var result = Run(new CallbackStrategy(onBar: (c, _) => { if (c.History(equity, 10).Count == 1) c.Submit(new("a", equity, 1, OrderType.MarketOnOpen)); }), data);
    Equal(data.Bars[1].OpenTime, result.Fills.Single().Time); Equal(50m, result.Fills.Single().Price);
});
Test("MOC cannot fill the close that produced its decision", () =>
{
    var data = Equities(); var result = Run(new CallbackStrategy(onBar: (c, _) => { if (c.History(equity, 10).Count == 1) c.Submit(new("a", equity, 1, OrderType.MarketOnClose)); }), data);
    Equal(data.Bars[1].CloseTime, result.Fills.Single().Time);
});
Test("Adjusted equity returns preserve quantities and reconcile without cash income", () =>
{
    var data = Equities();
    data.Bars[0] = data.Bars[0] with { Open = 50, High = 50, Low = 50, Close = 50 };
    data.Bars[1] = data.Bars[1] with { Open = 51, High = 53, Low = 50, Close = 52 };
    var result = Run(new CallbackStrategy(onStart: c =>
    {
        c.Buy("a", equity, 3);
        c.BuyLimit("a", equity, 1, 40);
    }), data);
    Equal(3m, result.Final.Positions.Single().Quantity);
    Equal(50m, result.Final.Positions.Single().AveragePrice);
    Equal(1006m, result.Final.Equity);
    Equal(6m, result.Attribution.Sum(a => a.NetPnl));
    Equal(40m, result.Orders.Last(o => o.Request.Type == OrderType.Limit).Request.LimitPrice);
    True(result.Costs.All(c => c.Kind == "Commission"));
    var shortResult = Run(new CallbackStrategy(onStart: c => c.Sell("a", equity, 3)), data, new() { AnnualBorrowRate = .1m });
    True(shortResult.Costs.Any(c => c.Kind == "Borrow" && c.Amount < 0));
    Equal(-3m, shortResult.Final.Positions.Single().Quantity);
});
Test("Datasets round trip without version metadata and reject removed action fields", () =>
{
    var path = Path.Combine(Temporary(), "history.json");
    var data = Equities();
    Json.Write(path, data);
    var serialized = File.ReadAllText(path);
    True(!serialized.Contains("corporateActions"));
    var restored = Json.Read<MarketDataset>(path);
    DatasetValidator.Validate(restored);
    Equal(data.Bars[0], restored.Bars[0]);
    Equal(data.Bars.Count, restored.Bars.Count);
    var properties = JsonDocument.Parse(serialized).RootElement.EnumerateObject().Select(p => p.Name);
    Equal("provider,interval,bars,sessions,funding,notes", string.Join(',', properties));
    File.WriteAllText(path, serialized.Insert(serialized.IndexOf('{') + 1, "\"corporateActions\": [],"));
    Throws<JsonException>(() => Json.Read<MarketDataset>(path));
});
Test("Adjusted equity cache reuses snapshots and rejects mixed-basis extension", () =>
{
    var data = Equities(); var directory = Temporary(); var cache = new DataCache(directory);
    var provider = new FixtureProvider(data);
    var request = new DataRequest(equity, BarInterval.Daily, data.Sessions[0].Open, data.Sessions[0].Close);
    cache.GetAsync(provider, request, data.Sessions).GetAwaiter().GetResult();
    cache.GetAsync(provider, request, data.Sessions).GetAwaiter().GetResult();
    Equal(1, provider.Requests.Count);
    var path = Directory.GetFiles(directory, "*.json").Single(); var original = File.ReadAllText(path);
    Throws<InvalidDataException>(() => cache.GetAsync(provider, request with { End = data.Sessions[1].Close }, data.Sessions).GetAwaiter().GetResult());
    Equal(original, File.ReadAllText(path)); Equal(1, provider.Requests.Count);
    Equal(1, DataCache.Load(directory, BarInterval.Daily).Bars.Count);
});
Test("Unavailable equity shorts reject", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", equity, -1))), Equities(), new() { ShortsAvailable = false }); Equal(0, result.Fills.Count);
});

Test("Notification sequence accepts before filling", () =>
{
    var strategy = new NotificationStrategy(instrument); Run(strategy);
    Equal("Accepted,Fill,Filled", string.Join(',', strategy.Notifications));
});
Test("Perpetual day limits expire at UTC midnight", () =>
{
    var data = Data(Enumerable.Repeat(100m, 26).ToArray());
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 1, OrderType.Limit, 50, TimeInForce.Day))), data);
    Equal(start.AddDays(1), result.Orders.Last().Time); Equal("Day order expired", result.Orders.Last().Reason);
});
Test("Session gaps and holidays do not synthesize bars", () =>
{
    var sessions = new[] { new MarketSession(DateTimeOffset.Parse("2024-07-03T13:30:00Z"), DateTimeOffset.Parse("2024-07-03T17:00:00Z")),
        new MarketSession(DateTimeOffset.Parse("2024-07-05T13:30:00Z"), DateTimeOffset.Parse("2024-07-05T20:00:00Z")) };
    Equal(2, DatasetValidator.Expected(equity, BarInterval.Daily, sessions[0].Open, sessions[1].Close, sessions).Count);
    Equal(11, DatasetValidator.Expected(equity, BarInterval.Hourly, sessions[0].Open, sessions[1].Close, sessions).Count);
});
Test("Alpaca pagination aggregates adjusted regular-session minutes", () =>
{
    var calls = 0; var data = Equities();
    using var http = new HttpClient(new ResponseHandler(message =>
    {
        calls++;
        True(message.Headers.Contains("APCA-API-KEY-ID"));
        var url = message.RequestUri!.ToString();
        True(url.Contains("adjustment=all"));
        True(!url.Contains("corporate-actions"));
        var body =
            url.Contains("page_token=p2") ? "{\"bars\":{\"ABC\":[{\"t\":\"2024-03-08T14:31:00Z\",\"o\":101,\"h\":103,\"l\":99,\"c\":102,\"v\":20}]},\"next_page_token\":null}" :
            "{\"bars\":{\"ABC\":[{\"t\":\"2024-03-08T14:30:00Z\",\"o\":100,\"h\":101,\"l\":98,\"c\":101,\"v\":10}]},\"next_page_token\":\"p2\"}";
        return new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }));
    var result = new AlpacaProvider(http, "key", "secret", data.Sessions).FetchAsync(new(equity, BarInterval.Daily, data.Sessions[0].Open, data.Sessions[0].Close)).GetAwaiter().GetResult();
    Equal(2, calls); Equal(100m, result.Bars.Single().Open); Equal(102m, result.Bars.Single().Close); Equal(30m, result.Bars.Single().Volume);
    Equal(103m, result.Bars.Single().High); Equal(98m, result.Bars.Single().Low);
    DatasetValidator.Validate(result);
});
Test("Alpaca calendar converts New York DST and early closes", () =>
{
    using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("""
        [{"date":"2024-03-08","open":"09:30","close":"16:00"},{"date":"2024-03-11","open":"09:30","close":"13:00"}]
        """) }));
    var sessions = AlpacaProvider.CalendarAsync(http, "key", "secret", new(2024, 3, 8), new(2024, 3, 11)).GetAwaiter().GetResult();
    Equal(14, sessions[0].Open.Hour); Equal(13, sessions[1].Open.Hour); Equal(17, sessions[1].Close.Hour);
});
Test("Hyperliquid funding pagination advances and preserves rates", () =>
{
    var calls = 0; var fundingCalls = 0;
    using var http = new HttpClient(new ResponseHandler(message =>
    {
        calls++; var body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); string response;
        if (body.Contains("candleSnapshot")) response = JsonSerializer.Serialize(Enumerable.Range(0, 2).Select(i => new { t = start.AddHours(i).ToUnixTimeMilliseconds(), o = "100", h = "105", l = "95", c = "101", v = "10" }));
        else { fundingCalls++; response = fundingCalls <= 2 ? JsonSerializer.Serialize(new[] { new { time = start.AddHours(fundingCalls).ToUnixTimeMilliseconds(), fundingRate = "0.001" } }) : "[]"; }
        return new(HttpStatusCode.OK) { Content = new StringContent(response) };
    }));
    var data = new HyperliquidProvider(http).FetchAsync(new(instrument, BarInterval.Hourly, start, start.AddHours(2))).GetAwaiter().GetResult();
    Equal(3, calls); Equal(2, data.Funding.Count); Equal(0.001m, data.Funding[0].Rate); Equal(101m, data.Funding[1].MarkPrice);
});
Test("Daily returns include the first trading day's performance", () =>
{
    var metrics = Reports.Metrics([(start, 100m), (start.AddHours(12), 110m), (start.AddDays(1), 121m), (start.AddDays(2), 108.9m)], 0, 252);
    Equal(3, metrics.DailyObservations); Equal(0.089m, metrics.TotalReturn); Equal(0.1m, metrics.MaximumDrawdown); True(metrics.AnnualizedSharpe is not null);
});
Test("Risk checks value limit fills against current marks", () =>
{
    var data = Data(100); data.Bars[0] = data.Bars[0] with { Low = 10, Close = 10 };
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 20, OrderType.Limit, 100))), data);
    Equal(0, result.Fills.Count); True(result.Orders.Any(o => o.Reason == "Insufficient initial margin"));
});
Test("Malformed configuration fields are rejected", () =>
{
    Throws<JsonException>(() => JsonSerializer.Deserialize<RunConfiguration>("{\"initialCahs\":12}", Json.Options));
    Throws<JsonException>(() => JsonSerializer.Deserialize<RunConfiguration>("{\"data\":\"dataset.json\"}", Json.Options));
    True(!JsonSerializer.Serialize(Config(), Json.Options).Contains("\"data\"", StringComparison.OrdinalIgnoreCase));
});
Test("Direct buy and sell quantities trade independently of capital weights", () =>
{
    var result = Run(new CallbackStrategy(onBar: (c, _) =>
    {
        var count = c.History(instrument, 10).Count;
        if (count == 1) c.Buy("a", instrument, 2.5m);
        if (count == 2) c.Sell("a", instrument, 1m);
    }));
    Equal(2, result.Fills.Count); Equal(2.5m, result.Fills[0].Quantity); Equal(-1m, result.Fills[1].Quantity);
    Equal(1.5m, result.Final.Positions.Single().Quantity); Equal(1025m, result.Final.Equity);
});
Test("Direct order IDs support cancellation and sells can open shorts", () =>
{
    var result = Run(new CallbackStrategy(onStart: c =>
    {
        var id = c.BuyLimit("a", instrument, 1, 50);
        True(c.Cancel(id));
        c.Sell("a", instrument, 2);
    }));
    Equal(-2m, result.Fills.Single().Quantity); Equal(960m, result.Final.Equity);
});
Test("Direct limit functions preserve sides, prices and day expiry", () =>
{
    var result = Run(new CallbackStrategy(onStart: c =>
    {
        c.BuyLimit("a", instrument, 2, 99);
        c.SellLimit("a", instrument, 1, 104);
        c.SellLimit("a", instrument, 1, 500, TimeInForce.Day);
    }), Data(Enumerable.Repeat(100m, 25).ToArray()));
    Equal(99m, result.Fills.Single(f => f.Quantity > 0).Price);
    Equal(104m, result.Fills.Single(f => f.Quantity < 0).Price);
    Equal(start.AddDays(1), result.Orders.Single(o => o.Reason == "Day order expired").Time);
});
Test("Direct orders reject nonpositive quantities and limit prices", () =>
{
    Run(new CallbackStrategy(onStart: c =>
    {
        Throws<ArgumentOutOfRangeException>(() => c.Buy("a", instrument, 0));
        Throws<ArgumentOutOfRangeException>(() => c.Sell("a", instrument, -1));
        Throws<ArgumentOutOfRangeException>(() => c.BuyLimit("a", instrument, 1, 0));
        Throws<ArgumentOutOfRangeException>(() => c.SellLimit("a", instrument, 1, -1));
        Equal(0, c.OpenOrders.Count);
    }));
});
Test("Direct auction functions preserve session timing and quantities", () =>
{
    foreach (var sell in new[] { false, true })
    foreach (var close in new[] { false, true })
    {
        var data = Equities();
        var result = Run(new CallbackStrategy(onBar: (c, _) =>
        {
            if (c.History(equity, 2).Count != 1) return;
            if (close) { if (sell) c.SellOnClose("a", equity, 1); else c.BuyOnClose("a", equity, 1); }
            else { if (sell) c.SellOnOpen("a", equity, 1); else c.BuyOnOpen("a", equity, 1); }
        }), data);
        Equal(sell ? -1m : 1m, result.Fills.Single().Quantity);
        Equal(close ? data.Sessions[1].Close : data.Sessions[1].Open, result.Fills.Single().Time);
    }
});
Test("Compiled C# strategies can call direct order functions", () =>
{
    var path = Path.Combine(Temporary(), "direct.cs");
    File.WriteAllText(path, """
        using Citrus.Trading;
        public sealed class Direct : IStrategy
        {
            public void OnStart(IStrategyContext c)
            {
                c.Register("direct", 1);
                c.Buy("direct", new Instrument("test", AssetClass.LinearPerpetual, "BTC"), 1);
            }
        }
        """);
    using var compiled = CompiledStrategy.Load(path);
    Equal(1m, Run(compiled.Strategy).Fills.Single().Quantity);
});

if (args.Contains("--benchmark"))
{
    foreach (var (interval, count) in new[] { (BarInterval.Daily, 3650), (BarInterval.Hourly, 24 * 365 * 2) })
    {
        var bars = Enumerable.Range(0, 100).SelectMany(i => BrownianGenerator.Generate(instrument with { Symbol = $"S{i}" }, interval, start, count, i).Bars).OrderBy(b => b.OpenTime).ToList();
        var data = new MarketDataset { Bars = bars, Interval = interval };
        var stopwatch = Stopwatch.StartNew(); var result = new BacktestEngine().Run(new BenchmarkStrategy(), data, Config()); stopwatch.Stop();
        Console.WriteLine($"BENCHMARK {interval.Name}: {bars.Count:N0} bars, 100 symbols, 20 substrategies, {stopwatch.Elapsed.TotalSeconds:F3}s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F1} MiB, {result.Fills.Count} fills");
    }
    return 0;
}
// Build November/December 2024 exchange sessions with holidays, DST and shortened holiday auctions.
MarketDataset PaydayData(decimal price = 100m)
{
    var schb = new Instrument("US", AssetClass.Equity, "SCHB");
    var sessions = new List<MarketSession>();
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    for (var date = new DateTime(2024, 11, 1); date < new DateTime(2025, 1, 1); date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || date == new DateTime(2024, 11, 28) || date == new DateTime(2024, 12, 25)) continue;
        var early = date == new DateTime(2024, 11, 29) || date == new DateTime(2024, 12, 24);
        sessions.Add(new(TimeZoneInfo.ConvertTimeToUtc(date.AddHours(9.5), zone), TimeZoneInfo.ConvertTimeToUtc(date.AddHours(early ? 13 : 16), zone)));
    }
    return new() { Sessions = sessions, Bars = sessions.Select(s => new Bar(schb, s.Open, s.Close, price, price, price, price, 10000, true, true)).ToList() };
}
Test("Payday compiled example trades exact sessions and early month-end closes across months", () =>
{
    using var compiled = CompiledStrategy.Load(Path.Combine(AppContext.BaseDirectory, "PaydaySeasonality.cs"));
    var data = PaydayData();
    var result = Run(compiled.Strategy, data);
    var expected = data.Sessions.GroupBy(s => s.Open.Month).SelectMany(g => new[] { g.ElementAt(7), g.ElementAt(11), g.ElementAt(15), g.Last() }).ToArray();
    Equal(8, result.Fills.Count);
    for (var i = 0; i < expected.Length; i++)
    {
        Equal(expected[i].Close, result.Fills[i].Time);
        Equal(i % 2 == 0 ? 8m : -8m, result.Fills[i].Quantity);
    }
    True(result.Final.Positions.All(p => p.Quantity == 0));
    True(result.Orders.All(o => o.Request.Type == OrderType.MarketOnClose && o.Request.TimeInForce == TimeInForce.Day));
});
// Supply all nine ETF identities over holiday-aware daily fixtures for the ten-sleeve strategy.
MarketDataset CombinedSeasonalityData()
{
    var data = PaydayData();
    var symbols = new[] { "SCHB", "TLT", "GLDM", "UGA", "UVXY", "VXZ", "SVXY", "VIXY", "BOIL" };
    return data with { Bars = data.Bars.SelectMany((b, index) => symbols.Select(symbol =>
    {
        var price = symbol == "SCHB" ? 100m + index % 8 : 100m;
        return b with { Instrument = new("US", AssetClass.Equity, symbol), Open = price, High = price, Low = price, Close = price };
    })).ToList() };
}
// Supply enough daily observations for the source portfolio warmup, ending on a Thursday entry date.
MarketDataset ZorroData()
{
    var sessions = new List<MarketSession>();
    for (var date = new DateTime(2024, 1, 2); date <= new DateTime(2024, 8, 29); date = date.AddDays(1))
        if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            sessions.Add(new(new DateTimeOffset(date.AddHours(14), TimeSpan.Zero), new DateTimeOffset(date.AddHours(21), TimeSpan.Zero)));
    var symbols = new[] { "SCHB", "TLT", "GLDM", "UGA", "UVXY", "VXZ", "SVXY", "VIXY" };
    return new() { Sessions = sessions, Bars = sessions.SelectMany((session, index) => symbols.Select((symbol, asset) =>
    {
        var price = 100m + (index + asset) % 9;
        return new Bar(new("fixture", AssetClass.Equity, symbol), session.Open, session.Close, price, price + 2, price - 2, price + 1, 10000, true, true);
    })).ToList() };
}
Test("Source portfolio uses pre-close calendar auctions and next-open price signals", () =>
{
    var strategy = new ZorroPortfolio();
    var data = ZorroData(); var config = Config() with { InitialCash = 17000 };
    var result = new BacktestEngine().Run(strategy, data, config);
    Equal(9, result.Equity.First().Substrategies.Count);
    var gold = result.Fills.Where(f => f.Substrategy == "GoldSeason").ToArray();
    True(gold.Length > 0);
    foreach (var fill in gold)
    {
        var accepted = result.Orders.Single(o => o.OrderId == fill.OrderId && o.Status == OrderStatus.Accepted);
        Equal(OrderType.MarketOnClose, accepted.Request.Type);
        Equal(fill.Time.AddMinutes(-1), accepted.Time);
        var bar = data.Bars.Single(b => b.Instrument == fill.Instrument && b.CloseTime == fill.Time);
        Equal(bar.Close, fill.Price);
        if (fill.Quantity > 0)
        {
            Equal(DayOfWeek.Thursday, fill.Time.DayOfWeek);
            var previous = data.Bars.Last(b => b.Instrument == fill.Instrument && b.CloseTime < accepted.Time);
            Equal(Math.Floor(1256m / previous.Close), fill.Quantity);
        }
    }
    var signals = result.Fills.Where(f => f.Substrategy is "VIXHedge" or "VIXBasis" or "EqBondPair" or "RiskPremia" or "EqBondReversion").ToArray();
    True(signals.Any(f => f.Substrategy == "VIXHedge"));
    foreach (var fill in signals)
    {
        var accepted = result.Orders.Single(o => o.OrderId == fill.OrderId && o.Status == OrderStatus.Accepted);
        if (accepted.Request.Type != OrderType.Market) continue;
        True(accepted.Time < fill.Time);
        var bar = data.Bars.Single(b => b.Instrument == fill.Instrument && b.OpenTime == fill.Time);
        Equal(bar.Open, fill.Price);
    }
    // A month-end long-to-short reversal is one net sale, and the final Thursday entry remains marked open.
    True(result.Fills.Any(f => f.Substrategy == "BondSeason" && f.Quantity < -30));
    True(result.Final.Positions.Any(p => p.Substrategy == "GoldSeason" && p.Quantity > 0));
    var repeated = new BacktestEngine().Run(strategy, data, config);
    True(result.Fills.SequenceEqual(repeated.Fills));
});
Test("Source portfolio decisions do not depend on unobserved closing prices", () =>
{
    var strategy = new ZorroPortfolio();
    var data = ZorroData(); var config = Config() with { InitialCash = 17000 };
    var last = data.Sessions[^1].Close;
    var changed = data with { Bars = data.Bars.Select(b => b.CloseTime == last
        ? b with { High = b.High + 20, Close = b.Close + 20 } : b).ToList() };
    var before = new BacktestEngine().Run(strategy, data, config);
    var after = new BacktestEngine().Run(strategy, changed, config);
    True(before.Orders.Where(o => o.Time < last).SequenceEqual(after.Orders.Where(o => o.Time < last)));
    True(before.Fills.Where(f => f.Time < last).SequenceEqual(after.Fills.Where(f => f.Time < last)));
    Equal(before.Fills.Single(f => f.Substrategy == "GoldSeason" && f.Time == last).Quantity,
        after.Fills.Single(f => f.Substrategy == "GoldSeason" && f.Time == last).Quantity);
});
Test("Removed immediate-fill configuration fields are rejected", () =>
{
    foreach (var name in new[] { "executeMarketOrdersAtCompletedClose", "completedCloseTickSize", "zorroDailySlippageSeconds" })
        Throws<JsonException>(() => JsonSerializer.Deserialize<SimulationOptions>("{\"" + name + "\":1}", Json.Options));
});
Test("Combined strategy registers ten sleeves and trades holiday gold, oil and winter gas", () =>
{
    using var compiled = CompiledStrategy.Load(Path.Combine(AppContext.BaseDirectory, "SeasonalityRiskPremia.cs"));
    var data = CombinedSeasonalityData();
    var result = new BacktestEngine().Run(compiled.Strategy, data, Config() with { InitialCash = 17000 });
    Equal(10, result.Equity.First().Substrategies.Count);
    var gold = result.Fills.Where(f => f.Substrategy == "GoldSeason").ToArray();
    True(gold.Length > 0);
    True(gold.Where(f => f.Quantity > 0).All(f => f.Time.DayOfWeek == DayOfWeek.Thursday));
    True(gold.Where(f => f.Quantity < 0).All(f => f.Time.DayOfWeek != DayOfWeek.Thursday));
    var oil = result.Fills.Where(f => f.Substrategy == "OilSeason").ToArray();
    True(oil.Any(f => f.Time.Date == new DateTime(2024, 11, 21) && f.Quantity == 11));
    True(oil.Any(f => f.Time.Date == new DateTime(2024, 11, 26) && f.Quantity == -11));
    True(oil.Any(f => f.Time.Date == new DateTime(2024, 11, 27) && f.Quantity == -11));
    True(oil.Any(f => f.Time.Date == new DateTime(2024, 11, 29) && f.Quantity == 11));
    True(result.Final.Positions.Any(p => p.Substrategy == "ShortGas" && p.Quantity == -10));
    True(result.Fills.Any(f => f.Substrategy == "VIXHedge" && f.Instrument.Symbol == "UVXY" && f.Quantity < 0));
    True(result.Fills.Any(f => f.Substrategy == "VIXHedge" && f.Instrument.Symbol == "VXZ" && f.Quantity > 0));
    True(result.Fills.Any(f => f.Substrategy == "VIXBasis"));
    True(result.Fills.Any(f => f.Substrategy == "EqBondPair"));
    True(result.Fills.Where(f => f.Substrategy is "VIXBasis" or "VIXHedge" or "EqBondPair").All(f => data.Sessions.Any(s => s.Open == f.Time)));
    var repeated = new BacktestEngine().Run(compiled.Strategy, data, Config() with { InitialCash = 17000 });
    True(result.Fills.SequenceEqual(repeated.Fills), "A reused strategy must reset indicator state.");
});
Test("Combined strategy rejects missing ETF histories at symbol binding", () =>
{
    using var compiled = CompiledStrategy.Load(Path.Combine(AppContext.BaseDirectory, "SeasonalityRiskPremia.cs"));
    var data = CombinedSeasonalityData();
    Throws<ArgumentException>(() => new BacktestEngine().Run(compiled.Strategy,
        data with { Bars = data.Bars.Where(b => b.Instrument.Symbol != "VXZ").ToList() }, Config() with { InitialCash = 17000 }));
});
Test("Payday sizing uses completed prices, skips unaffordable lots, and never shorts after rejected entries", () =>
{
    using var compiled = CompiledStrategy.Load(Path.Combine(AppContext.BaseDirectory, "PaydaySeasonality.cs"));
    var data = PaydayData();
    data.Bars[7] = data.Bars[7] with { High = 110, Close = 110 };
    var result = Run(compiled.Strategy, data);
    Equal(8m, result.Fills[0].Quantity);
    Equal(110m, result.Fills[0].Price);
    Equal(0, Run(compiled.Strategy, PaydayData(900)).Orders.Count);
    var rejected = Run(compiled.Strategy, PaydayData(), new SimulationOptions { RejectionProbability = 1 });
    Equal(0, rejected.Fills.Count);
    True(rejected.Orders.All(o => o.Request.Quantity > 0));
});
Test("Payday mid-month data uses full calendar ordinals and does not close at a truncated run boundary", () =>
{
    using var compiled = CompiledStrategy.Load(Path.Combine(AppContext.BaseDirectory, "PaydaySeasonality.cs"));
    var full = PaydayData();
    var data = full with { Bars = full.Bars.Skip(5).Take(13).ToList() };
    var result = Run(compiled.Strategy, data);
    Equal(3, result.Fills.Count);
    Equal(full.Sessions[7].Close, result.Fills[0].Time);
    Equal(full.Sessions[15].Close, result.Fills[2].Time);
    Equal(8m, result.Final.Positions.Single().Quantity);
});
Test("Payday hourly input still decides once per session and matches daily input", () =>
{
    using var compiled = CompiledStrategy.Load(Path.Combine(AppContext.BaseDirectory, "PaydaySeasonality.cs"));
    var daily = PaydayData();
    var bars = new List<Bar>();
    foreach (var session in daily.Sessions)
        for (var open = session.Open; open < session.Close; open = open.AddHours(1))
        {
            var close = open.AddHours(1) < session.Close ? open.AddHours(1) : session.Close;
            var price = close == session.Close ? 120m : 90m;
            bars.Add(new(daily.Bars[0].Instrument, open, close, price, price, price, price, 10000,
                open == session.Open, close == session.Close));
        }
    var result = new BacktestEngine().Run(compiled.Strategy, daily with { Interval = BarInterval.Hourly, Bars = bars }, Config() with { InitialCash = 2000 });
    Equal(8, result.Fills.Count);
    Equal(6m, result.Fills[0].Quantity);
    Equal(120m, result.Fills[0].Price);
    Equal(daily.Sessions[7].Close, result.Fills[0].Time);
    Equal(daily.Sessions[6].Close, result.Orders.First().Time);
    var dailyResult = new BacktestEngine().Run(compiled.Strategy, PaydayData(120m), Config() with { InitialCash = 2000 });
    True(result.Fills.SequenceEqual(dailyResult.Fills));
});

Test("Instrument calendar handles offsets, holidays, month boundaries and absent coverage", () =>
{
    var data = PaydayData();
    InstrumentContext? market = null;
    Run(new CallbackStrategy(onStart: c =>
    {
        market = new(c, "a", data.Bars[0].Instrument);
        True(market.TradingDay() is null);
        True(market.Close is null);
        True(market.BuyNotional(810) is null);
    }, onBar: (c, bars) =>
    {
        var index = data.Sessions.FindIndex(s => s.Close == c.Time);
        var current = market!.TradingDay()!;
        Equal(data.Sessions[index], current.Session);
        Equal(100m, market.Close!.Value);
        if (index == 0) True(market.TradingDay(-1) is null);
        else Equal(data.Sessions[index - 1], market.TradingDay(-1)!.Session);
        if (index == data.Sessions.Count - 1) True(market.TradingDay(1) is null);
        else Equal(data.Sessions[index + 1], market.TradingDay(1)!.Session);
        if (current.Date == new DateOnly(2024, 11, 27))
            Equal(new DateOnly(2024, 11, 29), market.TradingDay(1)!.Date);
        if (current.Date == new DateOnly(2024, 11, 29))
        {
            True(current.IsMonthEnd);
            Equal(20, current.DaysInMonth);
            Equal(1, market.TradingDay(1)!.DayOfMonth);
            Equal(new DateOnly(2024, 12, 2), market.TradingDay(1)!.Date);
        }
        True(market.TradingDay(int.MaxValue) is null);
    }), data);
    Throws<InvalidOperationException>(() => Run(new CallbackStrategy(onBar: (c, _) => new InstrumentContext(c, "a", instrument).TradingDay())));
});
Test("Bound order helpers round lots and preserve direct quantities and limits", () =>
{
    var result = Run(new CallbackStrategy(onBar: (c, _) =>
    {
        if (c.History(instrument, 2).Count != 1) return;
        var market = new InstrumentContext(c, "a", instrument);
        Equal(8m, market.LotsForNotional(810));
        Equal(8.1m, market.LotsForNotional(810, 0.1m));
        True(market.BuyNotional(99) is null);
        Throws<ArgumentOutOfRangeException>(() => market.LotsForNotional(0));
        Throws<ArgumentOutOfRangeException>(() => market.LotsForNotional(100, 0));
        Throws<ArgumentOutOfRangeException>(() => market.Buy(-1));
        Throws<ArgumentOutOfRangeException>(() => market.Sell(0));
        market.Buy(2, OrderType.Limit, TimeInForce.Day, 110);
        market.SellNotional(100);
    }));
    Equal(1m, result.Final.Positions.Single().Quantity);
    True(result.Fills.Any(f => f.Quantity == 2));
    True(result.Fills.Any(f => f.Quantity == -1));
});
Test("Position intent reverses once and preserves pending exits across opposite-side calls", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => c.Buy("a", instrument, 3), onBar: (c, _) =>
    {
        var market = new InstrumentContext(c, "a", instrument) { EntryNotional = 250 };
        var count = market.History(10).Count;
        if (count == 1)
        {
            market.EnterShort();
            var order = c.OpenOrders.Single();
            Equal(-5m, order.Request.Quantity); Equal(-2m, market.ProjectedQuantity);
            True(market.ExitLong() is null);
            True(market.TargetNotional(-200) is null);
            Equal(order.OrderId, c.OpenOrders.Single().OrderId);
        }
        if (count == 2)
        {
            market.EnterLong();
            var order = c.OpenOrders.Single();
            Equal(4m, order.Request.Quantity); Equal(2m, market.ProjectedQuantity);
            True(market.ExitShort() is null);
            Equal(order.OrderId, c.OpenOrders.Single().OrderId);
        }
    }), Data(100, 110, 120));
    Equal("3,-5,4", string.Join(',', result.Fills.Select(f => f.Quantity)));
    Equal(2m, result.Final.Positions.Single().Quantity);
});
Test("Entry warmup and notional drift use completed prices and pending lots", () =>
{
    Run(new CallbackStrategy(onBar: (c, _) =>
    {
        var market = new InstrumentContext(c, "a", instrument) { EntryNotional = 250, WarmupBars = 2 };
        if (market.History(10).Count == 1)
        {
            True(market.EnterLong() is null); True(market.TargetNotional(250) is null);
            market.Buy(1); // Explicit quantity orders remain usable during indicator warmup.
        }
        if (market.History(10).Count == 2)
        {
            market.EnterLong(); market.EnterLong();
            Equal(5m, market.ProjectedQuantity); Equal(1, c.OpenOrders.Count);
            True(market.TargetNotional(570, tolerance: .05m) is null);
            market.TargetNotional(605, lotSize: .5m);
            Equal(5.5m, market.ProjectedQuantity);
            var order = c.OpenOrders.Single();
            Throws<ArgumentException>(() => market.TargetQuantity(0, OrderType.Limit));
            Throws<ArgumentException>(() => market.TargetQuantity(0, OrderType.MarketOnClose));
            Throws<ArgumentOutOfRangeException>(() => market.EnterLong(notional: -1));
            Throws<ArgumentOutOfRangeException>(() => market.TargetNotional(10, tolerance: -1));
            Throws<ArgumentOutOfRangeException>(() => market.TargetNotional(10, lotSize: 0));
            Equal(order.OrderId, c.OpenOrders.Single().OrderId);
            market.TargetNotional(0);
            Equal(0m, market.ProjectedQuantity);
        }
    }), Data(100, 110, 120));
});
Test("Rejected entries leave no projected holding or phantom exit", () =>
{
    var result = Run(new CallbackStrategy(onBar: (c, _) =>
    {
        var market = new InstrumentContext(c, "a", instrument) { EntryNotional = 250 };
        if (market.History(10).Count == 1) market.EnterLong();
        else
        {
            Equal(0m, market.ProjectedQuantity);
            True(market.ExitLong() is null); True(market.ExitShort() is null);
        }
    }), options: new() { RejectionProbability = 1 });
    Equal(0, result.Fills.Count);
    Equal(1, result.Orders.Count(o => o.Status == OrderStatus.Rejected));
});
Test("Daily API binds accounts and respects early auctions without consuming the unfinished bar", () =>
{
    var path = Path.Combine(Temporary(), "Strategy.cs");
    File.WriteAllText(path, """
        using Citrus.Trading;
        public sealed class Simple : DailyStrategy
        {
            private StrategyAccount account = null!;
            protected override string ClockSymbol => "SCHB";
            protected override void Initialize() => account = Account("daily", 810, "schb");
            protected override void BeforeClose()
            {
                var market = account["SCHB"];
                if (Date.Day == 29) market.EnterLong(OrderType.MarketOnClose);
            }
        }
        """);
    using var compiled = CompiledStrategy.Load(path);
    var data = PaydayData();
    var result = Run(compiled.Strategy, data);
    var fill = result.Fills.Single();
    Equal(8m, fill.Quantity);
    var early = data.Sessions.Single(s => s.Close.Month == 11 && s.Close.Day == 29);
    Equal(early.Close, fill.Time);
    Equal(early.Close.AddMinutes(-1), result.Orders.First().Time);
    Equal(TimeInForce.Day, result.Orders.First().Request.TimeInForce);
    Equal(810m, result.Equity.First().Substrategies["daily"]);
});
Test("ExitLong cancels only its account and instrument and repeated calls cannot oversell", () =>
{
    var other = instrument with { Symbol = "ETH" };
    var data = Data(100, 100, 100);
    data = data with { Bars = data.Bars.Concat(data.Bars.Select(b => b with { Instrument = other })).OrderBy(b => b.OpenTime).ToList() };
    var result = Run(new CallbackStrategy(weight: 0.5m, onStart: c =>
    {
        c.Register("b", 0.5m);
        c.Buy("a", instrument, 3);
        c.Buy("b", instrument, 2);
    }, onBar: (c, _) =>
    {
        if (c.History(instrument, 2).Count != 1) return;
        c.Buy("a", instrument, 1);
        var otherAccount = c.Buy("b", instrument, 1);
        var otherInstrument = c.Buy("a", other, 1);
        var market = new InstrumentContext(c, "a", instrument);
        market.ExitLong();
        market.ExitLong();
        Equal(1, c.OpenOrders.Count(o => o.Request.Substrategy == "a" && o.Request.Instrument == instrument));
        True(c.OpenOrders.Any(o => o.OrderId == otherAccount));
        True(c.OpenOrders.Any(o => o.OrderId == otherInstrument));
    }), data);
    Equal(0m, result.Final.Positions.Single(p => p.Substrategy == "a" && p.Instrument == instrument).Quantity);
    Equal(3m, result.Final.Positions.Single(p => p.Substrategy == "b").Quantity);
    Equal(1m, result.Final.Positions.Single(p => p.Instrument == other).Quantity);
    Equal(-3m, result.Fills.Where(f => f.Quantity < 0).Sum(f => f.Quantity));
});
Test("ExitShort covers actual shorts and ExitLong never adds to them", () =>
{
    var result = Run(new CallbackStrategy(onStart: c => new InstrumentContext(c, "a", instrument).Sell(2), onBar: (c, _) =>
    {
        if (c.History(instrument, 2).Count != 1) return;
        var market = new InstrumentContext(c, "a", instrument);
        True(market.ExitLong() is null);
        market.ExitShort();
        market.ExitShort();
    }));
    Equal(2, result.Fills.Count);
    Equal(0m, result.Final.Positions.Single().Quantity);
});

var failures = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed.");
return failures == 0 ? 0 : 1;

/// <summary>Adapts optional test callbacks to strategy events after registering a default account.</summary>
sealed class CallbackStrategy(Action<IStrategyContext>? onStart = null, Action<IStrategyContext, IReadOnlyList<Bar>>? onBar = null,
    Action<IStrategyContext, string>? onScheduled = null, decimal weight = 1) : IStrategy
{
    /// <summary>Registers the default account and invokes the optional startup test callback.</summary>
    public void OnStart(IStrategyContext context) { context.Register("a", weight); onStart?.Invoke(context); }
    /// <summary>Forwards completed bars to the optional test callback.</summary>
    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars) => onBar?.Invoke(context, bars);
    /// <summary>Forwards a named scheduled event to the optional test callback.</summary>
    public void OnScheduled(IStrategyContext context, string name) => onScheduled?.Invoke(context, name);
}
/// <summary>Serves in-memory bars and records requested ranges for cache regression checks.</summary>
sealed class FixtureProvider(MarketDataset data) : IMarketDataProvider
{
    public string Name => "fixture";
    public List<DataRequest> Requests { get; } = [];
    /// <summary>Records the request and returns fixture bars contained in its requested interval without network I/O.</summary>
    public Task<MarketDataset> FetchAsync(DataRequest request, CancellationToken cancellationToken = default)
    { Requests.Add(request); return Task.FromResult(data with { Bars = data.Bars.Where(b => b.OpenTime >= request.Start && b.CloseTime <= request.End).ToList() }); }
}
/// <summary>Seeds a cache using a production provider identity without accessing the network.</summary>
sealed class NamedFixtureProvider(MarketDataset data, string name) : IMarketDataProvider
{
    public string Name => name;
    /// <summary>Returns the supplied complete fixture with the requested provenance.</summary>
    public Task<MarketDataset> FetchAsync(DataRequest request, CancellationToken cancellationToken = default) => Task.FromResult(data with { Provider = Name });
}
/// <summary>Routes HTTP requests to an in-memory response factory for offline adapter tests.</summary>
sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    /// <summary>Returns the response supplied by the fixture delegate without opening a network connection.</summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
/// <summary>Exercises many substrategies, history lookups, indicators, and initial orders for explicit benchmarks.</summary>
sealed class BenchmarkStrategy : IStrategy
{
    /// <summary>Registers twenty equal-capital substrategies for the benchmark workload.</summary>
    public void OnStart(IStrategyContext c) { for (var i = 0; i < 20; i++) c.Register($"s{i}", 0.05m); }
    /// <summary>Computes each substrategy indicator and submits one small order per instrument on its first bar.</summary>
    public void OnBar(IStrategyContext c, IReadOnlyList<Bar> bars)
    {
        for (var i = 0; i < 20; i++) foreach (var bar in bars)
        {
            _ = Indicators.Sma(c.History(bar.Instrument, 20).Select(b => b.Close), 20);
            if (c.History(bar.Instrument, 2).Count == 1) c.Submit(new($"s{i}", bar.Instrument, 0.001m));
        }
    }
}
/// <summary>Records callback ordering for an order submitted during startup.</summary>
sealed class NotificationStrategy(Instrument instrument) : IStrategy
{
    public readonly List<string> Notifications = [];
    /// <summary>Registers an account and submits a one-unit order to exercise notification delivery.</summary>
    public void OnStart(IStrategyContext c) { c.Register("a", 1); c.Submit(new("a", instrument, 1)); }
    /// <summary>Records the delivered status name so the test can compare callback order.</summary>
    public void OnOrderUpdate(IStrategyContext c, OrderUpdate update) => Notifications.Add(update.Status.ToString());
    /// <summary>Records fill delivery in the same sequence as order status callbacks.</summary>
    public void OnFill(IStrategyContext c, Fill fill) => Notifications.Add("Fill");
}
