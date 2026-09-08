using Citrus.Contracts;

namespace Citrus.Simulation;

public sealed class Ledger(decimal initialCash)
{
    private sealed class Account(decimal cash)
    {
        public decimal Cash = cash;
        public readonly Dictionary<Instrument, Position> Positions = [];
    }
    private readonly Dictionary<string, Account> accounts = [];
    private readonly Dictionary<Instrument, decimal> marks = [];
    private decimal unallocated = initialCash;
    private readonly decimal startingCash = initialCash;
    public List<CashMovement> Movements { get; } = [];
    public IReadOnlyDictionary<Instrument, decimal> Marks => marks;
    public void Register(string name, decimal weight)
    {
        if (string.IsNullOrWhiteSpace(name) || weight <= 0 || accounts.ContainsKey(name) || startingCash * weight > unallocated)
            throw new ArgumentException("Substrategy names must be unique and capital weights positive with total <= 1.");
        accounts.Add(name, new Account(startingCash * weight));
        unallocated -= startingCash * weight;
    }
    public bool Contains(string name) => accounts.ContainsKey(name);
    public int Count => accounts.Count;
    public void Mark(Instrument instrument, decimal price) => marks[instrument] = price;
    public decimal Quantity(string name, Instrument instrument) => accounts[name].Positions.GetValueOrDefault(instrument)?.Quantity ?? 0;
    public decimal NetQuantity(Instrument instrument) => accounts.Values.Sum(a => a.Positions.GetValueOrDefault(instrument)?.Quantity ?? 0);
    public decimal SubstrategyEquity(string name) => Equity(accounts[name]);
    private decimal Equity(Account account) => account.Cash + account.Positions.Values.Sum(p =>
        p.Instrument.AssetClass == AssetClass.Equity ? p.Quantity * marks.GetValueOrDefault(p.Instrument, p.AveragePrice)
        : p.Quantity * (marks.GetValueOrDefault(p.Instrument, p.AveragePrice) - p.AveragePrice));
    public PortfolioSnapshot Snapshot(DateTimeOffset time) => new(time,
        unallocated + accounts.Values.Sum(a => a.Cash), unallocated + accounts.Values.Sum(Equity),
        accounts.Values.SelectMany(a => a.Positions.Values).GroupBy(p => p.Instrument)
            .Sum(g => Math.Abs(g.Sum(p => p.Quantity)) * marks.GetValueOrDefault(g.Key)),
        accounts.Values.SelectMany(a => a.Positions.Values).OrderBy(p => p.Substrategy, StringComparer.Ordinal)
            .ThenBy(p => p.Instrument.Key, StringComparer.Ordinal).ToArray());
    public IReadOnlyDictionary<string, decimal> AttributedEquity() => accounts.ToDictionary(a => a.Key, a => Equity(a.Value));
    public IReadOnlyList<InstrumentAttribution> Attribution()
    {
        var income = Movements.Where(c => c.Kind is "Commission" or "Dividend" or "Funding" or "Borrow")
            .GroupBy(c => (c.Substrategy, c.Instrument)).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));
        return accounts.Values.SelectMany(a => a.Positions.Values).Select(p =>
        {
            var mark = marks.GetValueOrDefault(p.Instrument, p.AveragePrice);
            var unrealized = p.Quantity * (mark - p.AveragePrice);
            var fees = income.GetValueOrDefault((p.Substrategy, p.Instrument));
            return new InstrumentAttribution(p.Substrategy, p.Instrument, p.Quantity, mark, p.RealizedPnl, unrealized, fees, p.RealizedPnl + unrealized + fees);
        }).OrderBy(p => p.Substrategy, StringComparer.Ordinal).ThenBy(p => p.Instrument.Key, StringComparer.Ordinal).ToArray();
    }
    public void Reconcile(DateTimeOffset time)
    {
        var expected = startingCash + Attribution().Sum(p => p.NetPnl);
        if (Math.Abs(Snapshot(time).Equity - expected) > 0.00000001m)
            throw new InvalidOperationException("Ledger attribution does not reconcile to portfolio equity.");
    }
    public decimal Margin(SimulationOptions options, bool maintenance)
    {
        return accounts.Values.SelectMany(a => a.Positions.Values).GroupBy(p => p.Instrument).Sum(g =>
            Math.Abs(g.Sum(p => p.Quantity)) * marks.GetValueOrDefault(g.Key) * MarginRate(g.Key, options, maintenance));
    }
    public static decimal MarginRate(Instrument i, SimulationOptions o, bool maintenance) => i.AssetClass == AssetClass.Equity
        ? maintenance ? o.EquityMaintenanceMargin : o.EquityInitialMargin
        : maintenance ? o.PerpetualMaintenanceMargin : o.PerpetualInitialMargin;
    public void Apply(Fill fill)
    {
        var account = accounts[fill.Substrategy];
        var prior = account.Positions.GetValueOrDefault(fill.Instrument) ?? new(fill.Substrategy, fill.Instrument, 0, 0, 0);
        var closed = Math.Sign(prior.Quantity) == Math.Sign(fill.Quantity) ? 0 : Math.Min(Math.Abs(prior.Quantity), Math.Abs(fill.Quantity));
        var realized = closed * Math.Sign(prior.Quantity) * (fill.Price - prior.AveragePrice);
        var quantity = prior.Quantity + fill.Quantity;
        var average = quantity == 0 ? 0 : prior.Quantity == 0 || Math.Sign(quantity) != Math.Sign(prior.Quantity) ? fill.Price
            : Math.Sign(prior.Quantity) == Math.Sign(fill.Quantity)
                ? (Math.Abs(prior.Quantity) * prior.AveragePrice + Math.Abs(fill.Quantity) * fill.Price) / Math.Abs(quantity)
                : prior.AveragePrice;
        account.Cash += fill.Instrument.AssetClass == AssetClass.Equity ? -fill.Quantity * fill.Price - fill.Commission : realized - fill.Commission;
        account.Positions[fill.Instrument] = new(fill.Substrategy, fill.Instrument, quantity, average, prior.RealizedPnl + realized);
        if (fill.Commission != 0) Movements.Add(new(fill.Substrategy, fill.Instrument, fill.Time, "Commission", -fill.Commission));
        if (fill.ExecutionCost != 0) Movements.Add(new(fill.Substrategy, fill.Instrument, fill.Time, "ExecutionCost", -fill.ExecutionCost));
    }
    private void Cash(string sub, Instrument instrument, DateTimeOffset time, string kind, decimal amount)
    {
        accounts[sub].Cash += amount;
        Movements.Add(new(sub, instrument, time, kind, amount));
    }
    public void ChargeBorrow(DateTimeOffset time, TimeSpan elapsed, decimal annualRate)
    {
        // Charge physical net short exposure, proportionally to virtual short owners.
        foreach (var instrument in marks.Keys.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            var net = NetQuantity(instrument);
            if (instrument.AssetClass != AssetClass.Equity || net >= 0) continue;
            var shorts = accounts.Where(a => Quantity(a.Key, instrument) < 0).ToArray();
            var total = shorts.Sum(a => -Quantity(a.Key, instrument));
            var charge = -net * marks[instrument] * annualRate * (decimal)elapsed.TotalSeconds / (365m * 86400);
            var remaining = charge;
            for (var index = 0; index < shorts.Length; index++)
            {
                var amount = index == shorts.Length - 1 ? remaining : charge * -Quantity(shorts[index].Key, instrument) / total;
                remaining -= amount;
                Cash(shorts[index].Key, instrument, time, "Borrow", -amount);
            }
        }
    }
    public void Fund(FundingEvent funding)
    {
        foreach (var sub in accounts.Keys)
            Cash(sub, funding.Instrument, funding.Time, "Funding", -Quantity(sub, funding.Instrument) * funding.MarkPrice * funding.Rate);
    }
    public void CorporateAction(CorporateAction action)
    {
        foreach (var (sub, account) in accounts)
        {
            if (!account.Positions.TryGetValue(action.Instrument, out var p)) continue;
            switch (action.Type)
            {
                case ActionType.Dividend:
                    Cash(sub, action.Instrument, action.Time, "Dividend", p.Quantity * action.Amount!.Value);
                    break;
                case ActionType.Split:
                    account.Positions[action.Instrument] = p with { Quantity = p.Quantity * action.Ratio!.Value, AveragePrice = p.AveragePrice / action.Ratio.Value };
                    break;
                case ActionType.SymbolChange:
                case ActionType.Merger:
                case ActionType.Delisting:
                    var ratio = action.Type == ActionType.SymbolChange ? 1m : action.Ratio ?? 0;
                    if (p.Quantity == 0) break;
                    var successorMark = action.Successor is not null && ratio > 0 ? marks.GetValueOrDefault(action.Successor,
                        Math.Max(0, marks.GetValueOrDefault(action.Instrument, p.AveragePrice) - (action.Amount ?? 0)) / ratio) : 0;
                    // Book a fair-value exchange, including an already-held or opposing successor position.
                    Apply(new(0, sub, action.Instrument, action.Time, -p.Quantity, (action.Amount ?? 0) + ratio * successorMark, 0, true));
                    if (action.Successor is not null && ratio > 0)
                    {
                        marks.TryAdd(action.Successor, successorMark);
                        Apply(new(0, sub, action.Successor, action.Time, p.Quantity * ratio, successorMark, 0, true));
                    }
                    Movements.Add(new(sub, action.Instrument, action.Time, action.Type.ToString(), p.Quantity * (action.Amount ?? 0)));
                    break;
            }
        }
        if (action.Type == ActionType.Split && marks.TryGetValue(action.Instrument, out var mark)) marks[action.Instrument] = mark / action.Ratio!.Value;
        if (action.Successor is not null && marks.TryGetValue(action.Instrument, out var old))
            marks.TryAdd(action.Successor, Math.Max(0, old - (action.Amount ?? 0)) / (action.Ratio ?? 1));
    }
}
