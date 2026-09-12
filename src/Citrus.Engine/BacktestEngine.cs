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
    public BacktestResult Run(IStrategy strategy, MarketDataset data, RunConfiguration configuration,
        ExecutionMode mode = ExecutionMode.Backtest)
    {
        DatasetValidator.Validate(data);
        foreach (var action in data.CorporateActions.Where(a => a.Successor is not null))
            if (!data.Bars.Any(b => b.Instrument == action.Successor && b.CloseTime >= action.Time))
                throw new InvalidDataException($"Corporate action {action.Id} requires successor price history; merge a supplementary dataset.");
        configuration.Simulation.Validate();
        if (configuration.SchemaVersion != 1 || configuration.InitialCash <= 0)
            throw new ArgumentException("Invalid run configuration.");
        foreach (var group in data.Bars.GroupBy(b => b.Instrument))
            DatasetValidator.RequireCoverage(data, group.Key, group.Min(b => b.OpenTime), group.Max(b => b.CloseTime));
        var available = data.Bars.Select(b => b.Instrument).ToHashSet();
        var ledger = new Ledger(configuration.InitialCash);
        var book = new OrderBook(ledger, configuration.Simulation, configuration.Seed);
        var context = new Context(ledger, book, mode, available, data.Sessions);
        var opens = data.Bars.GroupBy(b => b.OpenTime).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Instrument.Key, StringComparer.Ordinal).ToArray());
        var closes = data.Bars.GroupBy(b => b.CloseTime).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Instrument.Key, StringComparer.Ordinal).ToArray());
        var actions = data.CorporateActions.GroupBy(a => a.Time).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray());
        var funding = data.Funding.GroupBy(f => f.Time).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Instrument.Key, StringComparer.Ordinal).ToArray());
        var first = opens.Keys.Min(); var last = closes.Keys.Max();
        var times = new SortedSet<DateTimeOffset>(opens.Keys.Concat(closes.Keys).Concat(actions.Keys).Concat(funding.Keys).Where(t => t >= first && t <= last));
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
                if (notification.IsFill) strategy.OnFill(context, book.Fills[notification.Index]);
                else strategy.OnOrderUpdate(context, book.Updates[notification.Index]);
            }
        }
        // Append portfolio and substrategy valuations at the current event time.
        void Snapshot() { var p = ledger.Snapshot(context.Time); points.Add(new(p.Time, p.Cash, p.Equity, p.GrossExposure, ledger.AttributedEquity())); }
        strategy.OnStart(context);
        context.Started = true;
        Dispatch(); Snapshot();
        var previous = first;
        try
        {
            while (times.Count > 0)
            {
                foreach (var scheduled in context.Scheduled.Keys.Where(t => t <= last)) times.Add(scheduled);
                var time = times.Min; times.Remove(time); context.Time = time;
                ledger.ChargeBorrow(time, time - previous, configuration.Simulation.AnnualBorrowRate); previous = time;
                // Settle closing bars before actions and callbacks; same-time opens run only after decisions.
                if (closes.TryGetValue(time, out var closing))
                {
                    foreach (var bar in closing) ledger.Mark(bar.Instrument, bar.Close);
                    foreach (var bar in closing) book.Execute(bar, true);
                }
                if (actions.TryGetValue(time, out var events)) foreach (var action in events) { ledger.CorporateAction(action); book.ApplyAction(action); }
                if (funding.TryGetValue(time, out var payments)) foreach (var payment in payments) { ledger.Mark(payment.Instrument, payment.MarkPrice); ledger.Fund(payment); }
                book.Liquidate(time);
                Dispatch();
                if (closing is not null)
                {
                    // Publish history only once the entire closing batch has completed execution and accounting.
                    foreach (var bar in closing) context.Add(bar);
                    strategy.OnBar(context, closing);
                }
                if (context.Scheduled.Remove(time, out var names)) foreach (var name in names) strategy.OnScheduled(context, name);
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
            Dispatch(); strategy.OnStop(context);
        }
        ledger.Reconcile(context.Time);
        return new(book.Updates, book.Fills, ledger.Movements, points, ledger.Snapshot(context.Time), ledger.Attribution());
    }

    /// <summary>Implements strategy operations against the ledger and order book while retaining completed history.</summary>
    private sealed class Context(Ledger ledger, OrderBook book, ExecutionMode mode, HashSet<Instrument> available, IReadOnlyList<MarketSession> sessions) : IStrategyContext
    {
        private readonly Dictionary<Instrument, List<Bar>> history = [];
        public readonly SortedDictionary<DateTimeOffset, List<string>> Scheduled = [];
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
        /// <summary>Returns a copy of up to count completed bars for an instrument, rejecting negative counts.</summary>
        public IReadOnlyList<Bar> History(Instrument instrument, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
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
            if (!available.Contains(order.Instrument))
                throw new ArgumentException($"Instrument {order.Instrument.Key} has no bars in the market dataset.");
            return book.Submit(order, Time);
        }
        /// <summary>Cancels a pending order at the current simulation time and reports whether it was found.</summary>
        public bool Cancel(long orderId) => book.Cancel(orderId, Time);
        /// <summary>Submits market deltas toward a complete target portfolio using completed prices and accounting for pending units.</summary>
        public void Rebalance(string substrategy, IReadOnlyDictionary<Instrument, decimal> weights)
        {
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
        /// <summary>Queues a named callback at a strictly future UTC time, preserving insertion order for equal times.</summary>
        public void Schedule(DateTimeOffset time, string name)
        {
            if (time.Offset != TimeSpan.Zero || time <= Time || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Scheduled events require a future UTC time and name.");
            if (!Scheduled.TryGetValue(time, out var names)) Scheduled.Add(time, names = []);
            names.Add(name);
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
