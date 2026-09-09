using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Citrus.Contracts;
using Citrus.Data;
using Citrus.Engine;
using Citrus.Simulation;

// Keep regression checks offline and package-free; named actions are executed by the runner below.
var tests = new List<(string Name, Action Test)>();
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
Test("External snapshots replay without fetching", () =>
{
    var strategy = new CallbackStrategy(onStart: c => Equal("value", Encoding.UTF8.GetString(c.ExternalData("key", () => throw new Exception("Unexpected fetch")))));
    new BacktestEngine().Run(strategy, Data(100), Config(), snapshots: new Dictionary<string, byte[]> { ["key"] = Encoding.UTF8.GetBytes("value") });
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
    var directory = Temporary(); var path = Path.Combine(directory, "strategy.cs"); File.WriteAllText(path, "using Citrus.Contracts; public sealed class Example : IStrategy { }");
    using var compiled = CompiledStrategy.Load(path); Run(compiled.Strategy);
    File.WriteAllText(path, "this is invalid C#"); Throws<InvalidDataException>(() => CompiledStrategy.Load(path));
});
Test("Export totals and portable replay inputs", () =>
{
    var directory = Temporary(); var strategy = Path.Combine(directory, "strategy.cs"); var dataPath = Path.Combine(directory, "data.json");
    File.WriteAllText(strategy, "using Citrus.Contracts; public class Example : IStrategy { }"); var data = Data(100, 110); Json.Write(dataPath, data);
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", instrument, 2))), data);
    var output = Path.Combine(directory, "result"); Reports.Export(output, result, Config(), data, strategy, dataPath, new Dictionary<string, string>());
    using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
    Equal(1020m, summary.RootElement.GetProperty("final").GetProperty("equity").GetDecimal());
    Equal(0.02m, summary.RootElement.GetProperty("portfolio").GetProperty("totalReturn").GetDecimal());
    True(File.Exists(Path.Combine(output, "inputs", "run.json"))); Equal(2, File.ReadAllLines(Path.Combine(output, "fills.csv")).Length);
});

Test("Repeated exports replace results and inputs while retaining unrelated files", () =>
{
    var directory = Temporary(); var strategy = Path.Combine(directory, "strategy.cs"); var dataPath = Path.Combine(directory, "data.json");
    File.WriteAllText(strategy, "using Citrus.Contracts; public class First : IStrategy { }");
    var data = Data(100, 110); Json.Write(dataPath, data);
    var output = Path.Combine(directory, "result");
    Reports.Export(output, Run(new CallbackStrategy(onStart: c => c.Buy("a", instrument, 2)), data), Config(), data, strategy, dataPath, new Dictionary<string, string>());
    File.WriteAllText(Path.Combine(output, "notes.txt"), "keep");
    File.WriteAllText(Path.Combine(output, "inputs", "old-dependency.dll"), "stale");
    File.WriteAllText(strategy, "using Citrus.Contracts; public class Second : IStrategy { }");
    data = Data(100, 120); Json.Write(dataPath, data);
    var result = Run(new CallbackStrategy(), data);
    Reports.Export(output, result, Config(), data, strategy, dataPath, new Dictionary<string, string>());
    Equal(0, Json.Read<List<Fill>>(Path.Combine(output, "fills.json")).Count);
    Equal(1, File.ReadAllLines(Path.Combine(output, "fills.csv")).Length);
    Equal(File.ReadAllText(strategy), File.ReadAllText(Path.Combine(output, "inputs", "strategy.cs")));
    Equal(File.ReadAllText(dataPath), File.ReadAllText(Path.Combine(output, "inputs", "data.json")));
    Equal("keep", File.ReadAllText(Path.Combine(output, "notes.txt")));
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")));
    True(!manifest.RootElement.GetProperty("inputHashes").TryGetProperty("old-dependency.dll", out _));
    // Rerunning with captured inputs already in the destination must not copy a file onto itself.
    Reports.Export(output, result, Config(), data, Path.Combine(output, "inputs", "strategy.cs"), Path.Combine(output, "inputs", "data.json"), new Dictionary<string, string>());
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
Test("Split adjusts positions and outstanding limits", () =>
{
    var data = Equities(); data.CorporateActions.Add(new("split", equity, data.Bars[1].OpenTime, ActionType.Split, Ratio: 2));
    var result = Run(new CallbackStrategy(onStart: c => { c.Submit(new("a", equity, 1)); c.Submit(new("a", equity, 1, OrderType.Limit, 90)); }), data);
    Equal(2m, result.Final.Positions.Single().Quantity); Equal(1000m, result.Final.Equity); Equal(45m, result.Orders.Last(o => o.Request.Type == OrderType.Limit).Request.LimitPrice);
});
Test("Dividends credit holdings and shorts incur borrow", () =>
{
    var data = Equities(); data.Bars[1] = data.Bars[1] with { Open = 100, High = 100, Low = 100, Close = 100 };
    data.CorporateActions.Add(new("dividend", equity, data.Bars[1].OpenTime, ActionType.Dividend, Amount: 2));
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", equity, 3))), data); Equal(1006m, result.Final.Equity);
    result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", equity, -3))), data, new() { AnnualBorrowRate = 0.1m });
    True(result.Costs.Any(c => c.Kind == "Borrow" && c.Amount < 0)); Equal(-6m, result.Costs.Single(c => c.Kind == "Dividend").Amount);
});
Test("Cash merger closes holdings and records consideration", () =>
{
    var data = Equities(); data.CorporateActions.Add(new("merger", equity, data.Bars[1].OpenTime, ActionType.Merger, Amount: 110));
    var result = Run(new CallbackStrategy(onStart: c => c.Submit(new("a", equity, 2))), data); Equal(1020m, result.Final.Equity); Equal(0m, result.Final.Positions.Single().Quantity);
});
Test("Unresolved corporate actions fail validation", () =>
{
    var data = Equities(); data.CorporateActions.Add(new("unknown", equity, data.Bars[1].OpenTime, ActionType.Delisting)); Throws<InvalidDataException>(() => DatasetValidator.Validate(data));
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
Test("Stock and cash merger combines already-held successor and reconciles", () =>
{
    var data = Equities(); var successor = equity with { Symbol = "NEW" };
    data.Bars.AddRange(data.Bars.ToArray().Select(b => b with { Instrument = successor, Open = 20, High = 20, Low = 20, Close = 20 }));
    data.CorporateActions.Add(new("exchange", equity, data.Sessions[1].Open, ActionType.Merger, Amount: 10, Ratio: 5, Successor: successor));
    var result = Run(new CallbackStrategy(onStart: c => { c.Submit(new("a", equity, 2)); c.Submit(new("a", successor, 3)); }), data);
    Equal(13m, result.Final.Positions.Single(p => p.Instrument == successor).Quantity);
    Equal(1020m, result.Final.Equity); Equal(20m, result.Attribution.Sum(p => p.NetPnl));
});
Test("Retired instruments reject new orders", () =>
{
    var data = Equities(); data.CorporateActions.Add(new("delist", equity, data.Sessions[1].Open, ActionType.Delisting, Amount: 0));
    var result = Run(new CallbackStrategy(onBar: (c, _) => { if (c.History(equity, 2).Count == 2) c.Submit(new("a", equity, 1)); }), data);
    Equal(OrderStatus.Rejected, result.Orders.Single().Status);
});
Test("Alpaca pagination aggregates raw regular-session minutes", () =>
{
    var calls = 0; var data = Equities();
    using var http = new HttpClient(new ResponseHandler(message =>
    {
        calls++;
        True(message.Headers.Contains("APCA-API-KEY-ID"));
        var url = message.RequestUri!.ToString();
        var body = url.Contains("corporate-actions") ? "{\"corporate_actions\":{},\"next_page_token\":null}" :
            url.Contains("page_token=p2") ? "{\"bars\":{\"ABC\":[{\"t\":\"2024-03-08T14:31:00Z\",\"o\":101,\"h\":103,\"l\":99,\"c\":102,\"v\":20}]},\"next_page_token\":null}" :
            "{\"bars\":{\"ABC\":[{\"t\":\"2024-03-08T14:30:00Z\",\"o\":100,\"h\":101,\"l\":98,\"c\":101,\"v\":10}]},\"next_page_token\":\"p2\"}";
        return new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }));
    var result = new AlpacaProvider(http, "key", "secret", data.Sessions).FetchAsync(new(equity, BarInterval.Daily, data.Sessions[0].Open, data.Sessions[0].Close)).GetAwaiter().GetResult();
    Equal(3, calls); Equal(100m, result.Bars.Single().Open); Equal(102m, result.Bars.Single().Close); Equal(30m, result.Bars.Single().Volume);
});
Test("Alpaca corporate action fields normalize explicit terms", () =>
{
    var data = Equities();
    using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("""
        {"corporate_actions":{"forward_splits":[{"id":"split","ex_date":"2024-03-11","new_rate":2,"old_rate":1}],"cash_dividends":[{"id":"dividend","ex_date":"2024-03-11","rate":0.5}]},"next_page_token":null}
        """) }));
    var actions = new AlpacaProvider(http, "key", "secret", data.Sessions).CorporateActionsAsync(new(equity, BarInterval.Daily, data.Sessions[0].Open, data.Sessions[1].Close)).GetAwaiter().GetResult();
    Equal(2m, actions.Single(a => a.Type == ActionType.Split).Ratio); Equal(0.5m, actions.Single(a => a.Type == ActionType.Dividend).Amount);
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
        using Citrus.Contracts;
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

if (args.Length == 3 && args[0] == "--compare")
{
    foreach (var name in new[] { "fills.json", "equity.json", "orders.json", "instrument-attribution.json" })
        if (!File.ReadAllBytes(Path.Combine(args[1], name)).SequenceEqual(File.ReadAllBytes(Path.Combine(args[2], name))))
            throw new Exception($"Replay mismatch: {name}");
    Console.WriteLine("Replay outputs are byte-identical."); return 0;
}

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
