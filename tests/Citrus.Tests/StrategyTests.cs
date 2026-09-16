using Citrus.Data;
using Citrus.Engine;
using Citrus.Trading;

/// <summary>Checks that daily callbacks follow one market calendar independently of instrument coverage.</summary>
internal static class StrategyTests
{
    /// <summary>Registers calendar timing, completed-history and input validation regressions.</summary>
    internal static void Register(Action<string, Action> test)
    {
        test("Non-daily metadata fails at every public data entry point", () =>
        {
            var interval = new BarInterval("unsupported", 60);
            var session = Calendar()[0];
            var bar = Bar("AAA", session);
            var data = new MarketDataset { Interval = interval, Sessions = [session], Bars = [bar] };
            Throws<InvalidDataException>(() => DatasetValidator.Validate(data));
            Throws<ArgumentException>(() => StrategyConfiguration.Validate(new() { Interval = interval }));
            Throws<ArgumentException>(() => DatasetValidator.Expected(bar.Instrument, interval, session.Open, session.Close, [session]));
            Throws<ArgumentException>(() => BrownianGenerator.Generate(bar.Instrument, interval, session.Open, 1, 42, sessions: [session]));
            Throws<ArgumentException>(() => DataCache.Load("unused", interval));
            using var http = new HttpClient(new ResponseHandler(_ => throw new Exception("Unsupported intervals must fail before HTTP.")));
            Throws<ArgumentException>(() => new AlpacaProvider(http, "fixture", "fixture", [session])
                .FetchAsync(new(bar.Instrument, interval, session.Open, session.Close)).GetAwaiter().GetResult());
            Throws<ArgumentException>(() => new HyperliquidProvider(http)
                .FetchAsync(new(new("fixture", AssetClass.LinearPerpetual, "BTC"), interval, session.Open, session.Close)).GetAwaiter().GetResult());
            Throws<ArgumentException>(() => new DataCache("unused").GetAsync(new FixtureProvider(data),
                new(bar.Instrument, interval, session.Open, session.Close), [session]).GetAwaiter().GetResult());
        });
        test("Daily perpetual boundaries never create a partial final bar", () =>
        {
            var instrument = new Instrument("fixture", AssetClass.LinearPerpetual, "BTC");
            var start = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
            var boundaries = DatasetValidator.Expected(instrument, BarInterval.Daily, start.AddHours(1), start.AddDays(3).AddHours(1), []);
            Require(boundaries.SequenceEqual(new[] { (start.AddDays(1), start.AddDays(2)), (start.AddDays(2), start.AddDays(3)) }),
                "Partial UTC days were emitted.");
        });
        test("Sliced perpetual runs call only their own daily closes", () =>
        {
            var instrument = new Instrument("fixture", AssetClass.LinearPerpetual, "BTC");
            var start = DateTimeOffset.Parse("2024-01-15T00:00:00Z");
            var data = BrownianGenerator.Generate(instrument, BarInterval.Daily, start, 2, 42);
            var strategy = new Observer("BTC");
            new BacktestEngine().Run(strategy, data, new());
            Require(strategy.Closes.Select(c => c.Time).SequenceEqual(new[] { start.AddDays(1), start.AddDays(2) }),
                "A close from before the selected slice reached the strategy.");
            Require(strategy.Before.Select(c => c.Bars).SequenceEqual(new[] { 0, 1 }), "Pre-close saw unfinished perpetual bars.");
        });
        test("Strategies have one public base and no arbitrary-time scheduling", () =>
        {
            var assembly = typeof(Strategy).Assembly;
            Require(assembly.GetTypes().Where(t => t.IsPublic && t.IsAbstract && !t.IsInterface && !t.IsSealed)
                .SequenceEqual(new[] { typeof(Strategy) }), "An alternative strategy base remains.");
            Require(typeof(IStrategyContext).GetMethod("Schedule") is null, "Arbitrary-time scheduling remains public.");
        });
        test("Daily market callbacks run without a clock symbol and include sessions without bars", () =>
        {
            var sessions = Calendar();
            var data = new MarketDataset { Sessions = sessions, Bars = [Bar("AAA", sessions[0]), Bar("BBB", sessions[2])] };
            var strategy = new Observer("AAA", "BBB");
            new BacktestEngine().Run(strategy, data, new());
            Require(strategy.Closes.Select(x => x.Time).SequenceEqual(sessions.Select(s => s.Close)), "Market closes depended on instrument coverage.");
            Require(strategy.Before.Select(x => x.Time).SequenceEqual(sessions.Select(s => s.Close.AddMinutes(-1))), "Pre-close timing lost an early close or empty session.");
            Require(strategy.Closes.Select(x => x.Bars).SequenceEqual(new[] { 1, 1, 2 }), "OnClose ran before completed history was published.");
            Require(strategy.Before.Select(x => x.Bars).SequenceEqual(new[] { 0, 1, 1 }), "BeforeClose saw unfinished bars.");
            var original = strategy.Closes.ToArray();
            new BacktestEngine().Run(strategy, data, new());
            Require(strategy.Closes.SequenceEqual(original), "Reusing a strategy retained calendar callback state.");
        });
        test("Daily market close fires once for simultaneous symbols and stays within run boundaries", () =>
        {
            var sessions = Calendar();
            var data = new MarketDataset { Sessions = sessions, Bars = [Bar("AAA", sessions[1]), Bar("BBB", sessions[1])] };
            var strategy = new Observer("AAA", "BBB");
            new BacktestEngine().Run(strategy, data, new());
            Require(strategy.Closes.Count == 1 && strategy.Closes[0] == (sessions[1].Close, 2), "Close callbacks were duplicated or escaped the run window.");
            Require(strategy.Before.Count == 1 && strategy.Before[0] == (sessions[1].Close.AddMinutes(-1), 0), "Pre-close callbacks escaped the run window.");
        });
        test("Every strategy rejects partial-day perpetual and partial-session equity bars", () =>
        {
            var session = Calendar()[0];
            var perpetual = Bar("AAA", session) with { Instrument = new("fixture", AssetClass.LinearPerpetual, "AAA") };
            Throws<InvalidDataException>(() => new BacktestEngine().Run(new Observer("AAA"),
                new() { Bars = [perpetual] }, new()));
            var bars = Enumerable.Range(0, 2).Select(i => Bar("AAA", session) with
            {
                OpenTime = session.Open.AddHours(i), CloseTime = session.Open.AddHours(i + 1), SessionOpen = i == 0, SessionClose = false
            }).ToList();
            Throws<InvalidDataException>(() => new BacktestEngine().Run(new Observer("AAA"),
                new() { Interval = BarInterval.Daily, Sessions = [session], Bars = bars }, new()));
        });
    }

    /// <summary>Provides three market sessions, including an early close.</summary>
    private static List<MarketSession> Calendar()
    {
        var start = DateTimeOffset.Parse("2024-11-27T14:30:00Z");
        return [new(start, start.AddHours(6.5)), new(start.AddDays(2), start.AddDays(2).AddHours(3.5)),
            new(start.AddDays(5), start.AddDays(5).AddHours(6.5))];
    }

    /// <summary>Creates one full-session equity bar without any special clock instrument.</summary>
    private static Bar Bar(string symbol, MarketSession session) => new(new("fixture", AssetClass.Equity, symbol),
        session.Open, session.Close, 100, 101, 99, 100, 1000, true, true);

    /// <summary>Observes callback times and the completed-history counts visible to a portfolio strategy.</summary>
    private sealed class Observer(params string[] symbols) : Strategy
    {
        public List<(DateTimeOffset Time, int Bars)> Closes { get; } = [];
        public List<(DateTimeOffset Time, int Bars)> Before { get; } = [];
        /// <summary>Clears observations before every run.</summary>
        protected override void Initialize() { Closes.Clear(); Before.Clear(); }
        /// <summary>Records the history visible after the market closes.</summary>
        protected override void OnClose() => Closes.Add((Time, symbols.Sum(symbol => Context.History(symbol, 100).Count)));
        /// <summary>Records the history visible before the market closes.</summary>
        protected override void BeforeClose() => Before.Add((Time, symbols.Sum(symbol => Context.History(symbol, 100).Count)));
    }

    /// <summary>Fails with the violated calendar behavior.</summary>
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    /// <summary>Requires the expected validation failure.</summary>
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
