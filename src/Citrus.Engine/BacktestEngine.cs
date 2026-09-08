using Citrus.Contracts;
using Citrus.Data;
using Citrus.Simulation;

namespace Citrus.Engine;

public sealed record EquityPoint(DateTimeOffset Time, decimal Cash, decimal Equity, decimal GrossExposure,
    IReadOnlyDictionary<string, decimal> Substrategies);
public sealed record BacktestResult(IReadOnlyList<OrderUpdate> Orders, IReadOnlyList<Fill> Fills,
    IReadOnlyList<CashMovement> Costs, IReadOnlyList<EquityPoint> Equity, PortfolioSnapshot Final,
    IReadOnlyDictionary<string, byte[]> ExternalSnapshots, IReadOnlyList<InstrumentAttribution> Attribution);

public sealed class BacktestEngine
{
    public BacktestResult Run(IStrategy strategy, MarketDataset data, RunConfiguration configuration,
        ExecutionMode mode = ExecutionMode.Backtest, IReadOnlyDictionary<string, byte[]>? snapshots = null)
    {
        DatasetValidator.Validate(data);
        foreach (var action in data.CorporateActions.Where(a => a.Successor is not null))
            if (!data.Bars.Any(b => b.Instrument == action.Successor && b.CloseTime >= action.Time))
                throw new InvalidDataException($"Corporate action {action.Id} requires successor price history; merge a supplementary dataset.");
        configuration.Simulation.Validate();
        if (configuration.SchemaVersion != 1 || configuration.InitialCash <= 0 || configuration.MaximumSubstrategies <= 0)
            throw new ArgumentException("Invalid run configuration.");
        foreach (var group in data.Bars.GroupBy(b => b.Instrument))
            DatasetValidator.RequireCoverage(data, group.Key, group.Min(b => b.OpenTime), group.Max(b => b.CloseTime));
        var ledger = new Ledger(configuration.InitialCash);
        var book = new OrderBook(ledger, configuration.Simulation, configuration.Seed);
        var context = new Context(ledger, book, configuration.MaximumSubstrategies, mode, snapshots);
        var opens = data.Bars.GroupBy(b => b.OpenTime).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Instrument.Key, StringComparer.Ordinal).ToArray());
        var closes = data.Bars.GroupBy(b => b.CloseTime).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Instrument.Key, StringComparer.Ordinal).ToArray());
        var actions = data.CorporateActions.GroupBy(a => a.Time).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray());
        var funding = data.Funding.GroupBy(f => f.Time).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Instrument.Key, StringComparer.Ordinal).ToArray());
        var first = opens.Keys.Min(); var last = closes.Keys.Max();
        var times = new SortedSet<DateTimeOffset>(opens.Keys.Concat(closes.Keys).Concat(actions.Keys).Concat(funding.Keys).Where(t => t >= first && t <= last));
        context.Time = first.AddTicks(-1);
        var points = new List<EquityPoint>();
        var notificationIndex = 0;
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
        return new(book.Updates, book.Fills, ledger.Movements, points, ledger.Snapshot(context.Time), context.Snapshots, ledger.Attribution());
    }

    private sealed class Context(Ledger ledger, OrderBook book, int maximumSubstrategies, ExecutionMode mode,
        IReadOnlyDictionary<string, byte[]>? snapshots) : IStrategyContext
    {
        private readonly Dictionary<Instrument, List<Bar>> history = [];
        public readonly SortedDictionary<DateTimeOffset, List<string>> Scheduled = [];
        public readonly Dictionary<string, byte[]> Snapshots = snapshots?.ToDictionary(p => p.Key, p => p.Value.ToArray()) ?? [];
        public bool Started;
        public bool Stopping;
        public ExecutionMode Mode => mode;
        public DateTimeOffset Time { get; set; }
        public PortfolioSnapshot Portfolio => ledger.Snapshot(Time);
        public IReadOnlyList<OrderUpdate> OpenOrders => book.Pending.Select(o => new OrderUpdate(o.Id,
            o.Request with { Quantity = o.Remaining }, OrderStatus.Accepted, o.Submitted)).ToArray();
        public void Register(string substrategy, decimal capitalWeight)
        {
            if (Started || ledger.Count >= maximumSubstrategies) throw new InvalidOperationException("Register substrategies during startup within the configured limit.");
            ledger.Register(substrategy, capitalWeight);
        }
        public IReadOnlyList<Bar> History(Instrument instrument, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            return history.TryGetValue(instrument, out var values) ? values.TakeLast(count).ToArray() : [];
        }
        public void Add(Bar bar)
        {
            if (!history.TryGetValue(bar.Instrument, out var values)) history.Add(bar.Instrument, values = []);
            values.Add(bar);
        }
        public long Submit(OrderRequest order) => !Stopping ? book.Submit(order, Time) : throw new InvalidOperationException("Run is stopping.");
        public bool Cancel(long orderId) => book.Cancel(orderId, Time);
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
                var delta = desired - ledger.Quantity(substrategy, instrument) - pending;
                if (Math.Abs(delta) > 0.00000001m) Submit(new(substrategy, instrument, delta));
            }
        }
        public void Schedule(DateTimeOffset time, string name)
        {
            if (time.Offset != TimeSpan.Zero || time <= Time || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Scheduled events require a future UTC time and name.");
            if (!Scheduled.TryGetValue(time, out var names)) Scheduled.Add(time, names = []);
            names.Add(name);
        }
        public byte[] ExternalData(string key, Func<byte[]> fetch)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Snapshot key required.");
            if (!Snapshots.TryGetValue(key, out var data))
            {
                if (snapshots is not null) throw new InvalidDataException($"Missing replay snapshot: {key}");
                Snapshots.Add(key, data = fetch().ToArray());
            }
            return data.ToArray();
        }
    }
}
