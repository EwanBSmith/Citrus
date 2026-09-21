using Citrus.Trading;
using Citrus.Data;
using Citrus.Simulation;

namespace Citrus.Engine;

/// <summary>Records portfolio and substrategy valuation at one simulation observation time.</summary>
public sealed record EquityPoint(DateTimeOffset Time, decimal Cash, decimal Equity, decimal GrossExposure,
    IReadOnlyDictionary<string, decimal> Substrategies);
/// <summary>Collects completed-run execution history, valuations, and final attribution.</summary>
public sealed record BacktestResult(IReadOnlyList<OrderUpdate> Orders, IReadOnlyList<Fill> Fills,
    IReadOnlyList<CashMovement> Costs, IReadOnlyList<EquityPoint> Equity, PortfolioSnapshot Final,
    IReadOnlyList<InstrumentAttribution> Attribution);

/// <summary>Runs a sequential event simulation over validated market data with deterministic event ordering.</summary>
public sealed class BacktestEngine
{
    private const int MaximumSubstrategies = 100;
    /// <summary>Validates inputs and runs a trusted strategy to completion; mode only labels the strategy context.</summary>
    public BacktestResult Run(Strategy strategy, MarketDataset data, RunConfiguration configuration,
        ExecutionMode mode = ExecutionMode.Backtest)
    {
        configuration = StrategyConfiguration.Resolve(strategy, configuration);
        data = StrategyConfiguration.SelectData(strategy, data, configuration);
        DatasetValidator.Validate(data);
        foreach (var group in data.Bars.GroupBy(b => b.Instrument))
            DatasetValidator.RequireCoverage(data, group.Key, group.Min(b => b.OpenTime), group.Max(b => b.CloseTime));
        var available = data.Bars.Select(b => b.Instrument).ToHashSet();
        var ledger = new Ledger(configuration.InitialCash);
        var book = new OrderBook(ledger, configuration.Simulation, configuration.Seed);
        var sessions = Sessions(data);
        var context = new Context(ledger, book, mode, available, sessions);
        var opens = data.Bars.GroupBy(b => b.OpenTime).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Instrument.Key, StringComparer.Ordinal).ToArray());
        var closes = data.Bars.GroupBy(b => b.CloseTime).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Instrument.Key, StringComparer.Ordinal).ToArray());
        var funding = data.Funding.GroupBy(f => f.Time).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Instrument.Key, StringComparer.Ordinal).ToArray());
        var first = opens.Keys.Min(); var last = closes.Keys.Max();
        var times = new SortedSet<DateTimeOffset>(opens.Keys.Concat(closes.Keys).Concat(funding.Keys).Where(t => t >= first && t <= last));
        var sessionCloses = sessions.Select(s => s.Close).Where(t => t > first && t <= last).ToHashSet();
        var auctions = sessions.Select(s => s.Close.AddMinutes(-1)).Where(t => t >= first && t <= last).ToHashSet();
        times.UnionWith(sessionCloses); times.UnionWith(auctions);
        context.Time = first.AddTicks(-1);
        var points = new List<EquityPoint>();
        var notificationIndex = 0;
        // Deliver queued notifications in order, including callback-generated events, with a per-dispatch loop limit.
        void Dispatch()
        {
            // Bound callback-generated notifications to catch accidental infinite submission loops.
            var delivered = 0;
            while (notificationIndex < book.Notifications.Count)
            {
                if (++delivered > 100_000) throw new InvalidOperationException("Order callback notification limit exceeded.");
                var notification = book.Notifications[notificationIndex++];
                if (notification.IsFill) strategy.NotifyFill(book.Fills[notification.Index]);
                else strategy.NotifyOrder(book.Updates[notification.Index]);
            }
        }
        // Append portfolio and substrategy valuations at the current event time.
        void Snapshot() { var p = ledger.Snapshot(context.Time); points.Add(new(p.Time, p.Cash, p.Equity, p.GrossExposure, ledger.AttributedEquity())); }
        strategy.Start(context);
        context.Started = true;
        Dispatch(); Snapshot();
        var previous = first;
        try
        {
            foreach (var time in times)
            {
                context.Time = time;
                ledger.ChargeBorrow(time, time - previous, configuration.Simulation.AnnualBorrowRate); previous = time;
                // Settle closing bars before funding and callbacks; same-time opens run only after decisions.
                if (closes.TryGetValue(time, out var closing))
                {
                    foreach (var bar in closing) ledger.Mark(bar.Instrument, bar.Close);
                    foreach (var bar in closing) book.Execute(bar, true);
                }
                if (funding.TryGetValue(time, out var payments)) foreach (var payment in payments) { ledger.Mark(payment.Instrument, payment.MarkPrice); ledger.Fund(payment); }
                book.Liquidate(time);
                Dispatch();
                if (closing is not null)
                {
                    // Publish history only once the entire closing batch has completed execution and accounting.
                    foreach (var bar in closing) context.Add(bar);
                }
                if (sessionCloses.Contains(time)) strategy.Close(closing ?? []);
                if (auctions.Contains(time)) strategy.PrepareClose();
                Dispatch();
                if (opens.TryGetValue(time, out var opening))
                {
                    foreach (var bar in opening) ledger.Mark(bar.Instrument, bar.Open);
                    book.Liquidate(time);
                    foreach (var bar in opening) book.Execute(bar, false);
                    book.Liquidate(time);
                    Dispatch();
                }
                Snapshot();
            }
        }
        finally
        {
            context.Stopping = true;
            foreach (var order in book.Pending.ToArray()) book.Cancel(order.Id, context.Time, "End of run");
            Dispatch(); strategy.Stop();
        }
        ledger.Reconcile(context.Time);
        return new(book.Updates, book.Fills, ledger.Movements, points, ledger.Snapshot(context.Time), ledger.Attribution());
    }

    /// <summary>Uses the exchange calendar or creates complete UTC calendar months for a daily perpetual market.</summary>
    private static IReadOnlyList<MarketSession> Sessions(MarketDataset data)
    {
        if (data.Bars.Select(b => b.Instrument.AssetClass).Distinct().Count() != 1)
            throw new InvalidDataException("An EOD strategy requires one market calendar; run equities and perpetuals separately.");
        if (data.Bars[0].Instrument.AssetClass == AssetClass.Equity) return data.Sessions;
        var first = data.Bars.Min(b => b.OpenTime);
        var last = data.Bars.Max(b => b.OpenTime);
        var start = new DateTimeOffset(first.Year, first.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(last.Year, last.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var sessions = new List<MarketSession>();
        for (var day = start; day < end; day = day.AddDays(1)) sessions.Add(new(day, day.AddDays(1)));
        return sessions;
    }

    /// <summary>Implements strategy operations against the ledger and order book while retaining completed history.</summary>
    private sealed class Context(Ledger ledger, OrderBook book, ExecutionMode mode, HashSet<Instrument> available, IReadOnlyList<MarketSession> sessions) : IStrategyContext
    {
        private readonly Dictionary<Instrument, List<Bar>> history = [];
        private readonly Dictionary<string, Instrument> symbols = available.ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> externalData = [];
        public bool Started;
        public bool Stopping;
        public ExecutionMode Mode => mode;
        public IReadOnlyList<MarketSession> Sessions { get; } = Array.AsReadOnly(sessions.ToArray());
        public DateTimeOffset Time { get; set; }
        public PortfolioSnapshot Portfolio => ledger.Snapshot(Time);
        public IReadOnlyList<OrderUpdate> OpenOrders => book.Pending.Select(o => new OrderUpdate(o.Id,
            o.Request with { Quantity = o.Remaining }, OrderStatus.Accepted, o.Submitted)).ToArray();
        /// <summary>Registers initial capital allocation during startup, up to the fixed limit of 100 substrategies.</summary>
        public void Register(string substrategy, decimal capitalWeight)
        {
            if (Started) throw new InvalidOperationException("Register substrategies during startup.");
            if (ledger.Count >= MaximumSubstrategies) throw new InvalidOperationException("A strategy may register at most 100 substrategies.");
            ledger.Register(substrategy, capitalWeight);
        }
        /// <summary>Resolves only instrument metadata, never future prices, using the symbol as the lookup key.</summary>
        public Instrument ResolveInstrument(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol) || !symbols.TryGetValue(symbol, out var instrument))
                throw new ArgumentException($"No historical data for symbol '{symbol}'. Import its history or select a range that includes it.");
            return instrument;
        }
        /// <summary>Resolves legacy venue-qualified requests while retaining asset-class safety.</summary>
        private Instrument Resolve(Instrument instrument)
        {
            var resolved = ResolveInstrument(instrument.Symbol);
            if (resolved.AssetClass != instrument.AssetClass) throw new ArgumentException($"Asset class does not match history for {instrument.Symbol}.");
            return resolved;
        }
        /// <summary>Returns a copy of up to count completed bars for an instrument, rejecting negative counts.</summary>
        public IReadOnlyList<Bar> History(Instrument instrument, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            instrument = Resolve(instrument);
            return history.TryGetValue(instrument, out var values) ? values.TakeLast(count).ToArray() : [];
        }
        /// <summary>Appends a completed bar to the history exposed to strategy callbacks.</summary>
        public void Add(Bar bar)
        {
            if (!history.TryGetValue(bar.Instrument, out var values)) history.Add(bar.Instrument, values = []);
            values.Add(bar);
        }
        /// <summary>Submits an order at the current simulation time, rejecting submissions once shutdown begins.</summary>
        public long Submit(OrderRequest order)
        {
            if (Stopping) throw new InvalidOperationException("Run is stopping.");
            order = order with { Instrument = Resolve(order.Instrument) };
            var id = book.Submit(order, Time);
            return id;
        }
        /// <summary>Cancels a pending order at the current simulation time and reports whether it was found.</summary>
        public bool Cancel(long orderId) => book.Cancel(orderId, Time);
        /// <summary>Submits market deltas toward a complete target portfolio using completed prices and accounting for pending units.</summary>
        public void Rebalance(string substrategy, IReadOnlyDictionary<Instrument, decimal> weights)
        {
            weights = weights.ToDictionary(p => Resolve(p.Key), p => p.Value);
            var equity = ledger.SubstrategyEquity(substrategy);
            var instruments = weights.Keys.Concat(Portfolio.Positions.Where(p => p.Substrategy == substrategy).Select(p => p.Instrument))
                .Concat(book.Pending.Where(o => o.Request.Substrategy == substrategy).Select(o => o.Request.Instrument)).Distinct().OrderBy(i => i.Key, StringComparer.Ordinal);
            foreach (var instrument in instruments)
            {
                if (!history.TryGetValue(instrument, out var bars) || bars.Count == 0) throw new InvalidOperationException("Rebalancing requires an observed completed price.");
                var desired = equity * weights.GetValueOrDefault(instrument) / bars[^1].Close;
                var pending = book.Pending.Where(o => o.Request.Substrategy == substrategy && o.Request.Instrument == instrument).Sum(o => o.Remaining);
                // Pending units already move toward the target, so repeated rebalances must not duplicate them.
                var delta = desired - ledger.Quantity(substrategy, instrument) - pending;
                if (Math.Abs(delta) > 0.00000001m) Submit(new(substrategy, instrument, delta));
            }
        }
        /// <summary>Fetches bytes once per key during a run and returns a defensive copy.</summary>
        public byte[] ExternalData(string key, Func<byte[]> fetch)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("External data key required.");
            if (!externalData.TryGetValue(key, out var data)) externalData.Add(key, data = fetch().ToArray());
            return data.ToArray();
        }
    }
}
