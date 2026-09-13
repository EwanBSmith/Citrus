using Citrus.Trading;

namespace Citrus.Simulation;

/// <summary>Tracks an accepted request and its mutable signed unfilled quantity.</summary>
public sealed class PendingOrder(long id, OrderRequest request, DateTimeOffset submitted)
{
    /// <summary>Gets the simulation-assigned order identifier.</summary>
    public long Id { get; } = id;
    /// <summary>Gets or sets the request, including adjustments made for stock splits.</summary>
    public OrderRequest Request { get; set; } = request;
    /// <summary>Gets the submission time used to determine event eligibility.</summary>
    public DateTimeOffset Submitted { get; } = submitted;
    /// <summary>Gets or sets signed units still awaiting execution.</summary>
    public decimal Remaining { get; set; } = request.Quantity;
}

/// <summary>Simulates eligible order crossing, external residual execution, costs, rejection, and margin liquidation.</summary>
public sealed class OrderBook(Ledger ledger, SimulationOptions options, int seed)
{
    private readonly Random random = new(seed);
    private long nextId;
    private readonly List<PendingOrder> pending = [];
    private readonly HashSet<Instrument> retired = [];
    /// <summary>Gets notification order as indexes into the fill or update lists for sequential callback dispatch.</summary>
    public List<(bool IsFill, int Index)> Notifications { get; } = [];
    /// <summary>Gets order status changes in creation order.</summary>
    public List<OrderUpdate> Updates { get; } = [];
    /// <summary>Gets attributed executions, including internal transfers and forced liquidation fills.</summary>
    public List<Fill> Fills { get; } = [];
    /// <summary>Gets accepted orders that still have an unfilled balance.</summary>
    public IReadOnlyList<PendingOrder> Pending => pending;
    /// <summary>Validates a request and assigns an ID; retired instruments produce a rejection update.</summary>
    public long Submit(OrderRequest request, DateTimeOffset time)
    {
        if (!Enum.IsDefined(request.Type) || !Enum.IsDefined(request.TimeInForce) || !Enum.IsDefined(request.Instrument.AssetClass))
            throw new ArgumentException("Unknown order type, time-in-force, or asset class.");
        if (!ledger.Contains(request.Substrategy) || request.Quantity == 0 || string.IsNullOrWhiteSpace(request.Instrument.Symbol))
            throw new ArgumentException("Order requires a registered substrategy, instrument, and nonzero quantity.");
        if (request.Type == OrderType.Limit ? request.LimitPrice is null or <= 0 : request.LimitPrice is not null)
            throw new ArgumentException("Only limit orders require a positive limit price.");
        if (request.Instrument.AssetClass == AssetClass.LinearPerpetual && request.Type is OrderType.MarketOnOpen or OrderType.MarketOnClose)
            throw new ArgumentException("Perpetuals do not have opening or closing auctions.");
        var id = ++nextId;
        if (retired.Contains(request.Instrument))
        {
            Update(new(id, request, OrderStatus.Rejected, time, "Instrument retired by corporate action"));
            return id;
        }
        pending.Add(new(id, request, time));
        Update(new(id, request, OrderStatus.Accepted, time));
        return id;
    }
    /// <summary>Appends a status update and its notification index, preserving callback order relative to fills.</summary>
    private void Update(OrderUpdate update) { Notifications.Add((false, Updates.Count)); Updates.Add(update); }

    /// <summary>Executes one just-submitted market order at a known completed close in explicit legacy compatibility mode.</summary>
    public void ExecuteCompletedClose(long orderId, Bar bar, Bar? nextBar = null, Bar? followingBar = null)
    {
        if (!options.ExecuteMarketOrdersAtCompletedClose) throw new InvalidOperationException("Completed-close execution is disabled.");
        var order = pending.SingleOrDefault(o => o.Id == orderId);
        if (order is null) return;
        if (order.Request.Type != OrderType.Market || order.Request.Instrument != bar.Instrument || order.Submitted != bar.CloseTime)
            throw new InvalidOperationException("Completed-close execution requires a current market order and matching completed bar.");
        var reducing = ledger.Quantity(order.Request.Substrategy, bar.Instrument) * order.Remaining < 0;
        var raw = options.CompletedCloseTickSize > 0 && !reducing
            ? Math.Round(bar.Close / options.CompletedCloseTickSize, MidpointRounding.AwayFromZero) * options.CompletedCloseTickSize : bar.Close;
        var legacySlippage = 0m;
        if (options.ZorroDailySlippageSeconds > 0 && nextBar is not null)
        {
            var move = nextBar.Close > nextBar.Open ? nextBar.High - nextBar.Open
                : nextBar.Close == nextBar.Open && followingBar is not null ? Math.Max(0, followingBar.Open - nextBar.Close) : 0;
            legacySlippage = move * (decimal)(options.ZorroDailySlippageSeconds / Math.Sqrt(86400));
            raw += legacySlippage;
        }
        var price = options.CompletedCloseTickSize > 0
            ? Math.Round(raw / options.CompletedCloseTickSize, MidpointRounding.AwayFromZero) * options.CompletedCloseTickSize : raw;
        Route([order], price, bar.CloseTime, null, legacySlippage);
        if (order.Remaining == 0 && pending.Contains(order)) Finish(order, OrderStatus.Filled, bar.CloseTime);
    }
    /// <summary>Removes a pending order and records cancellation, returning false when the ID is not pending.</summary>
    public bool Cancel(long id, DateTimeOffset time, string reason = "Cancelled")
    {
        var order = pending.Find(o => o.Id == id);
        if (order is null) return false;
        Finish(order, OrderStatus.Cancelled, time, reason);
        return true;
    }
    /// <summary>Records a terminal status and removes the order from the pending book.</summary>
    private void Finish(PendingOrder order, OrderStatus status, DateTimeOffset time, string? reason = null)
    {
        Update(new(order.Id, order.Request, status, time, reason));
        pending.Remove(order);
    }
    /// <summary>Adjusts split orders or cancels and retires instruments that undergo conversion or delisting.</summary>
    public void ApplyAction(CorporateAction action)
    {
        if (action.Type is ActionType.Merger or ActionType.Delisting or ActionType.SymbolChange) retired.Add(action.Instrument);
        foreach (var order in pending.Where(o => o.Request.Instrument == action.Instrument).ToArray())
        {
            if (action.Type == ActionType.Split)
            {
                order.Remaining *= action.Ratio!.Value;
                order.Request = order.Request with { Quantity = order.Request.Quantity * action.Ratio.Value, LimitPrice = order.Request.LimitPrice / action.Ratio.Value };
            }
            else if (action.Type is ActionType.Merger or ActionType.Delisting or ActionType.SymbolChange)
                Cancel(order.Id, action.Time, "Corporate action: resubmit on successor instrument");
        }
    }
    /// <summary>Executes orders eligible at the bar open or close and expires day orders at their applicable boundary.</summary>
    public void Execute(Bar bar, bool atClose)
    {
        var time = atClose ? bar.CloseTime : bar.OpenTime;
        var groups = pending.Where(o => o.Request.Instrument == bar.Instrument && (atClose || o.Request.Type == OrderType.MarketOnOpen ? o.Submitted < time : o.Submitted <= time) &&
            (atClose ? o.Request.Type is OrderType.Limit || o.Request.Type == OrderType.MarketOnClose && bar.SessionClose
                : o.Request.Type == OrderType.Market || o.Request.Type == OrderType.MarketOnOpen && bar.SessionOpen))
            // Limits use a full subsequent bar, never a bar already underway when submitted.
            .Where(o => o.Request.Type != OrderType.Limit || o.Submitted <= bar.OpenTime)
            .GroupBy(o => (o.Request.Type, o.Request.LimitPrice, o.Request.TimeInForce)).ToArray();
        foreach (var group in groups)
        {
            var orders = group.OrderBy(o => o.Id).ToArray();
            if (group.Key.Type == OrderType.Limit)
            {
                // Crossing requires a reference price satisfying both sides. Equal-limit orders cross at the limit if touched.
                var limit = group.Key.LimitPrice!.Value;
                var bothSides = orders.Any(o => o.Remaining > 0) && orders.Any(o => o.Remaining < 0);
                if (bothSides && bar.Low <= limit && bar.High >= limit) Cross(orders, limit, time);
                foreach (var side in new[] { 1, -1 })
                {
                    var eligible = orders.Where(o => o.Remaining * side > 0 && (side > 0 ? bar.Low <= limit : bar.High >= limit)).ToArray();
                    if (eligible.Length == 0) continue;
                    var reference = side > 0 ? Math.Min(bar.Open, limit) : Math.Max(bar.Open, limit);
                    Route(eligible, reference, time, limit);
                }
            }
            else
            {
                var reference = atClose ? bar.Close : bar.Open;
                Cross(orders, reference, time);
                Route(orders.Where(o => o.Remaining != 0).ToArray(), reference, time, null);
            }
            foreach (var order in orders.Where(o => o.Remaining == 0 && pending.Contains(o))) Finish(order, OrderStatus.Filled, time);
        }
        if (atClose && (bar.SessionClose || bar.Instrument.AssetClass == AssetClass.LinearPerpetual && bar.CloseTime.TimeOfDay == TimeSpan.Zero))
            foreach (var order in pending.Where(o => o.Request.Instrument == bar.Instrument && o.Request.TimeInForce == TimeInForce.Day && o.Submitted < time).ToArray())
                Cancel(order.Id, time, "Day order expired");
    }
    /// <summary>Matches opposing virtual quantities in array order at a common price without external commissions.</summary>
    private void Cross(PendingOrder[] orders, decimal price, DateTimeOffset time)
    {
        var buys = orders.Where(o => o.Remaining > 0).ToArray();
        var sells = orders.Where(o => o.Remaining < 0).ToArray();
        var b = 0; var s = 0;
        while (b < buys.Length && s < sells.Length)
        {
            var quantity = Math.Min(buys[b].Remaining, -sells[s].Remaining);
            FillOrder(buys[b], quantity, price, 0, true, time);
            FillOrder(sells[s], -quantity, price, 0, true, time);
            if (buys[b].Remaining == 0) b++;
            if (sells[s].Remaining == 0) s++;
        }
    }
    /// <summary>Prices and risk-checks the net residual, then rejects it or allocates external fills and commission across its orders.</summary>
    private void Route(PendingOrder[] orders, decimal reference, DateTimeOffset time, decimal? limit, decimal signedSlippagePerUnit = 0)
    {
        if (orders.Length == 0) return;
        var quantity = orders.Sum(o => o.Remaining);
        if (quantity == 0) return;
        var price = reference * (1 + Math.Sign(quantity) * (options.SpreadBps / 2 + options.SlippageBps) / 10000);
        if (price <= 0) throw new InvalidOperationException("Configured costs produce a nonpositive execution price.");
        if (limit.HasValue) price = quantity > 0 ? Math.Min(price, limit.Value) : Math.Max(price, limit.Value);
        var commission = options.CommissionFixed + Math.Abs(quantity) * options.CommissionPerUnit;
        // A draw is consumed per external submission even if its risk check fails.
        var rejected = random.NextDouble() < options.RejectionProbability;
        var instrument = orders[0].Request.Instrument;
        var prior = ledger.NetQuantity(instrument);
        var newQuantity = prior + quantity;
        var increased = Math.Abs(newQuantity) > Math.Abs(prior) || prior != 0 && newQuantity != 0 && Math.Sign(newQuantity) != Math.Sign(prior);
        var margin = ledger.Margin(options, false) + (Math.Abs(newQuantity) - Math.Abs(prior)) * ledger.Marks[instrument] * Ledger.MarginRate(instrument, options, false);
        var equityAfter = ledger.Snapshot(time).Equity - commission - quantity * (price - ledger.Marks[instrument]);
        var reason = rejected ? "Simulated rejection" :
            instrument.AssetClass == AssetClass.Equity && newQuantity < 0 && newQuantity < prior && !options.ShortsAvailable ? "Shorts unavailable" :
            increased && equityAfter < margin ? "Insufficient initial margin" : null;
        if (reason is not null)
        {
            foreach (var order in orders) Finish(order, OrderStatus.Rejected, time, reason);
            return;
        }
        // Give the final fill the decimal remainder so attributed commissions sum exactly to the net charge.
        var remainingCost = commission;
        for (var index = 0; index < orders.Length; index++)
        {
            var order = orders[index];
            var cost = index == orders.Length - 1 ? remainingCost : commission * Math.Abs(order.Remaining / quantity);
            remainingCost -= cost;
            FillOrder(order, order.Remaining, price, cost, false, time,
                Math.Abs(order.Remaining) * Math.Abs(price - reference) + order.Remaining * signedSlippagePerUnit);
        }
    }
    /// <summary>Books an attributed fill, queues its notification, and reduces the signed unfilled quantity.</summary>
    private void FillOrder(PendingOrder order, decimal quantity, decimal price, decimal cost, bool internalFill, DateTimeOffset time, decimal executionCost = 0)
    {
        var fill = new Fill(order.Id, order.Request.Substrategy, order.Request.Instrument, time, quantity, price, cost, internalFill, ExecutionCost: executionCost);
        ledger.Apply(fill); Notifications.Add((true, Fills.Count)); Fills.Add(fill); order.Remaining -= quantity;
    }
    /// <summary>On a maintenance breach, cancels pending orders and closes all positions with internal crossing and costed external residuals.</summary>
    public void Liquidate(DateTimeOffset time)
    {
        if (ledger.Snapshot(time).Equity >= ledger.Margin(options, true)) return;
        foreach (var order in pending.ToArray()) Cancel(order.Id, time, "Maintenance margin breach");
        foreach (var group in ledger.Snapshot(time).Positions.Where(p => p.Quantity != 0).GroupBy(p => p.Instrument))
        {
            var temporary = group.Select(p => new PendingOrder(++nextId, new(p.Substrategy, p.Instrument, -p.Quantity), time)).ToArray();
            var start = Fills.Count;
            Cross(temporary, ledger.Marks[group.Key], time);
            var residual = temporary.Where(o => o.Remaining != 0).ToArray();
            var quantity = residual.Sum(o => o.Remaining);
            var price = ledger.Marks[group.Key] * (1 + Math.Sign(quantity) * (options.SpreadBps / 2 + options.SlippageBps) / 10000);
            var total = quantity == 0 ? 0 : options.CommissionFixed + Math.Abs(quantity) * options.CommissionPerUnit;
            var left = total;
            for (var index = 0; index < residual.Length; index++)
            {
                var o = residual[index];
                var cost = index == residual.Length - 1 ? left : total * Math.Abs(o.Remaining / quantity);
                left -= cost;
                FillOrder(o, o.Remaining, price, cost, false, time, Math.Abs(o.Remaining * (price - ledger.Marks[group.Key])));
            }
            for (var index = start; index < Fills.Count; index++) Fills[index] = Fills[index] with { Liquidation = true };
            foreach (var o in temporary) Update(new(o.Id, o.Request, OrderStatus.Filled, time, "Forced liquidation"));
        }
    }
}
