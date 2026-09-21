using Citrus.Trading;

namespace Citrus.Simulation;

/// <summary>Maintains virtual substrategy accounts and marks while measuring risk on net physical exposure.</summary>
public sealed class Ledger(decimal initialCash)
{
    /// <summary>Stores cash and virtual positions for one substrategy, initialized with its allocated capital.</summary>
    private sealed class Account(decimal cash)
    {
        public decimal Cash = cash;
        public readonly Dictionary<Instrument, Position> Positions = [];
    }
    private readonly Dictionary<string, Account> accounts = [];
    private readonly Dictionary<Instrument, decimal> marks = [];
    private decimal unallocated = initialCash;
    private readonly decimal startingCash = initialCash;
    /// <summary>Gets recorded income, fees, settlements, and diagnostic execution costs.</summary>
    public List<CashMovement> Movements { get; } = [];
    /// <summary>Gets the latest assigned valuation price for each instrument.</summary>
    public IReadOnlyDictionary<Instrument, decimal> Marks => marks;
    /// <summary>Allocates a fraction of starting cash to a unique substrategy without exceeding unallocated capital.</summary>
    public void Register(string name, decimal weight)
    {
        if (string.IsNullOrWhiteSpace(name) || weight <= 0 || accounts.ContainsKey(name) || startingCash * weight > unallocated)
            throw new ArgumentException("Substrategy names must be unique and capital weights positive with total <= 1.");
        accounts.Add(name, new Account(startingCash * weight));
        unallocated -= startingCash * weight;
    }
    /// <summary>Checks whether the account is registered.</summary>
    public bool Contains(string name) => accounts.ContainsKey(name);
    /// <summary>Gets the registered account count.</summary>
    public int Count => accounts.Count;
    /// <summary>Sets the price used for subsequent valuation and margin calculations.</summary>
    public void Mark(Instrument instrument, decimal price) => marks[instrument] = price;
    /// <summary>Returns signed units held by a substrategy, or zero for an absent position.</summary>
    public decimal Quantity(string name, Instrument instrument) => accounts[name].Positions.GetValueOrDefault(instrument)?.Quantity ?? 0;
    /// <summary>Returns signed physical exposure after summing all substrategy positions.</summary>
    public decimal NetQuantity(Instrument instrument) => accounts.Values.Sum(a => a.Positions.GetValueOrDefault(instrument)?.Quantity ?? 0);
    /// <summary>Returns account cash plus equity market value or perpetual unrealized profit.</summary>
    public decimal SubstrategyEquity(string name) => Equity(accounts[name]);
    /// <summary>Values account cash plus equity holdings or perpetual unrealized profit, falling back to entry prices for missing marks.</summary>
    private decimal Equity(Account account) => account.Cash + account.Positions.Values.Sum(p =>
        p.Instrument.AssetClass == AssetClass.Equity ? p.Quantity * marks.GetValueOrDefault(p.Instrument, p.AveragePrice)
        : p.Quantity * (marks.GetValueOrDefault(p.Instrument, p.AveragePrice) - p.AveragePrice));
    /// <summary>Builds a portfolio snapshot at the supplied time with positions in stable reporting order.</summary>
    public PortfolioSnapshot Snapshot(DateTimeOffset time) => new(time,
        unallocated + accounts.Values.Sum(a => a.Cash), unallocated + accounts.Values.Sum(Equity),
        accounts.Values.SelectMany(a => a.Positions.Values).GroupBy(p => p.Instrument)
            .Sum(g => Math.Abs(g.Sum(p => p.Quantity)) * marks.GetValueOrDefault(g.Key)),
        accounts.Values.SelectMany(a => a.Positions.Values).OrderBy(p => p.Substrategy, StringComparer.Ordinal)
            .ThenBy(p => p.Instrument.Key, StringComparer.Ordinal).ToArray());
    /// <summary>Returns marked equity for each registered substrategy, excluding unallocated cash.</summary>
    public IReadOnlyDictionary<string, decimal> AttributedEquity() => accounts.ToDictionary(a => a.Key, a => Equity(a.Value));
    /// <summary>Returns instrument profit including income and fees; embedded execution costs are not deducted again.</summary>
    public IReadOnlyList<InstrumentAttribution> Attribution()
    {
        // ExecutionCost is diagnostic: the fill price already includes its effect on trading profit.
        var income = Movements.Where(c => c.Kind is "Commission" or "Funding" or "Borrow")
            .GroupBy(c => (c.Substrategy, c.Instrument)).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));
        return accounts.Values.SelectMany(a => a.Positions.Values).Select(p =>
        {
            var mark = marks.GetValueOrDefault(p.Instrument, p.AveragePrice);
            var unrealized = p.Quantity * (mark - p.AveragePrice);
            var fees = income.GetValueOrDefault((p.Substrategy, p.Instrument));
            return new InstrumentAttribution(p.Substrategy, p.Instrument, p.Quantity, mark, p.RealizedPnl, unrealized, fees, p.RealizedPnl + unrealized + fees);
        }).OrderBy(p => p.Substrategy, StringComparer.Ordinal).ThenBy(p => p.Instrument.Key, StringComparer.Ordinal).ToArray();
    }
    /// <summary>Checks that starting capital plus attributed profit equals marked portfolio equity within decimal tolerance.</summary>
    public void Reconcile(DateTimeOffset time)
    {
        var expected = startingCash + Attribution().Sum(p => p.NetPnl);
        if (Math.Abs(Snapshot(time).Equity - expected) > 0.00000001m)
            throw new InvalidOperationException("Ledger attribution does not reconcile to portfolio equity.");
    }
    /// <summary>Calculates initial or maintenance margin on absolute net notional per instrument.</summary>
    public decimal Margin(SimulationOptions options, bool maintenance)
    {
        return accounts.Values.SelectMany(a => a.Positions.Values).GroupBy(p => p.Instrument).Sum(g =>
            Math.Abs(g.Sum(p => p.Quantity)) * marks.GetValueOrDefault(g.Key) * MarginRate(g.Key, options, maintenance));
    }
    /// <summary>Selects the configured initial or maintenance fraction for the asset class.</summary>
    public static decimal MarginRate(Instrument i, SimulationOptions o, bool maintenance) => i.AssetClass == AssetClass.Equity
        ? maintenance ? o.EquityMaintenanceMargin : o.EquityInitialMargin
        : maintenance ? o.PerpetualMaintenanceMargin : o.PerpetualInitialMargin;
    /// <summary>Books a fill into cash and average-cost positions, realizing profit on closed units.</summary>
    public void Apply(Fill fill)
    {
        var account = accounts[fill.Substrategy];
        var prior = account.Positions.GetValueOrDefault(fill.Instrument) ?? new(fill.Substrategy, fill.Instrument, 0, 0, 0);
        var closed = Math.Sign(prior.Quantity) == Math.Sign(fill.Quantity) ? 0 : Math.Min(Math.Abs(prior.Quantity), Math.Abs(fill.Quantity));
        var realized = closed * Math.Sign(prior.Quantity) * (fill.Price - prior.AveragePrice);
        var quantity = prior.Quantity + fill.Quantity;
        // A reversal opens the residual at this fill's price; a partial close retains the old cost basis.
        var average = quantity == 0 ? 0 : prior.Quantity == 0 || Math.Sign(quantity) != Math.Sign(prior.Quantity) ? fill.Price
            : Math.Sign(prior.Quantity) == Math.Sign(fill.Quantity)
                ? (Math.Abs(prior.Quantity) * prior.AveragePrice + Math.Abs(fill.Quantity) * fill.Price) / Math.Abs(quantity)
                : prior.AveragePrice;
        // Equities exchange full notional cash; perpetuals settle only realized profit and commission.
        account.Cash += fill.Instrument.AssetClass == AssetClass.Equity ? -fill.Quantity * fill.Price - fill.Commission : realized - fill.Commission;
        account.Positions[fill.Instrument] = new(fill.Substrategy, fill.Instrument, quantity, average, prior.RealizedPnl + realized);
        if (fill.Commission != 0) Movements.Add(new(fill.Substrategy, fill.Instrument, fill.Time, "Commission", -fill.Commission));
        if (fill.ExecutionCost != 0) Movements.Add(new(fill.Substrategy, fill.Instrument, fill.Time, "ExecutionCost", -fill.ExecutionCost));
    }
    /// <summary>Applies a signed cash adjustment and records its instrument, time, and attribution category.</summary>
    private void Cash(string sub, Instrument instrument, DateTimeOffset time, string kind, decimal amount)
    {
        accounts[sub].Cash += amount;
        Movements.Add(new(sub, instrument, time, kind, amount));
    }
    /// <summary>Charges elapsed-time borrow on physical net short equities and allocates it to virtual short owners.</summary>
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
    /// <summary>Applies signed perpetual funding to each substrategy at the event mark and rate.</summary>
    public void Fund(FundingEvent funding)
    {
        foreach (var sub in accounts.Keys)
            Cash(sub, funding.Instrument, funding.Time, "Funding", -Quantity(sub, funding.Instrument) * funding.MarkPrice * funding.Rate);
    }
}
