using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using Citrus.Trading;

/// <summary>Combines ten calendar, reversion, volatility, and risk-premia sleeves from the supplied source.</summary>
public sealed class SeasonalityRiskPremia : IStrategy
{
    private const decimal PaydayAllocation = 810m;
    private const decimal TreasuryAllocation = 2608m;
    private const decimal EquityBondReversionAllocation = 3018m;
    private const decimal RiskPremiaAllocation = 3000m;
    private const decimal RiskPremiaTrackingErrorThreshold = 0.10m;
    private const decimal InitialCapital = 17000m;
    private Sleeve[] sleeves = [];
    private Dictionary<DateOnly, decimal> vix = new(), vix3m = new();
    private readonly List<decimal> pair = new();
    private decimal? previousPair, previousEma;
    private HashSet<DateOnly> sessionDates = new();
    private DateOnly calendarFirst, calendarLast;
    private DateTimeOffset? lastDecision;

    private static readonly Instrument Schb = new("US", AssetClass.Equity, "SCHB");
    private static readonly Instrument Tlt = new("US", AssetClass.Equity, "TLT");
    private static readonly Instrument Gldm = new("US", AssetClass.Equity, "GLDM");

    private Sleeve payday = null!, treasury = null!, reversion = null!, riskPremia = null!;
    private Sleeve gold = null!, oil = null!, vixHedge = null!, vixBasis = null!, equityBondPair = null!, gas = null!;

    /// <summary>Binds one registered account, its fixed allocation, and its instrument contexts.</summary>
    private sealed class Sleeve
    {
        public string Name { get; }
        public decimal Allocation { get; }
        public IReadOnlyDictionary<Instrument, InstrumentContext> Markets { get; }
        public InstrumentContext this[Instrument instrument] => Markets[instrument];

        /// <summary>Registers the account and creates its contexts through the same path for every sleeve.</summary>
        public Sleeve(IStrategyContext context, string name, decimal allocation, params string[] symbols)
        {
            Name = name;
            Allocation = allocation;
            context.Register(name, allocation / InitialCapital);
            Markets = symbols.Select(symbol => new Instrument("US", AssetClass.Equity, symbol))
                .ToDictionary(instrument => instrument, instrument => new InstrumentContext(context, name, instrument));
        }

        /// <summary>Looks up a US equity context by its symbol within this sleeve.</summary>
        public InstrumentContext Market(string symbol) => this[new Instrument("US", AssetClass.Equity, symbol)];
    }

    /// <summary>Creates ten independently attributed accounts using fixed source dollar allocations.</summary>
    public void OnStart(IStrategyContext context)
    {
        payday = new Sleeve(context, "PaydaySeason", PaydayAllocation, "SCHB");
        treasury = new Sleeve(context, "BondSeason", TreasuryAllocation, "TLT");
        reversion = new Sleeve(context, "EqBondReversion", EquityBondReversionAllocation, "SCHB", "TLT");
        riskPremia = new Sleeve(context, "RiskPremia", RiskPremiaAllocation, "SCHB", "TLT", "GLDM");
        gold = new Sleeve(context, "GoldSeason", 1256m, "GLDM");
        oil = new Sleeve(context, "OilSeason", 1160m, "UGA");
        vixHedge = new Sleeve(context, "VIXHedge", 2094m, "UVXY", "VXZ");
        vixBasis = new Sleeve(context, "VIXBasis", 800m, "SVXY", "VIXY");
        equityBondPair = new Sleeve(context, "EqBondPair", 800m, "SCHB", "TLT");
        gas = new Sleeve(context, "ShortGas", 1000m, "BOIL");
        sleeves = [payday, treasury, reversion, riskPremia, gold, oil, vixHedge, vixBasis, equityBondPair, gas];
        pair.Clear(); previousPair = previousEma = null; lastDecision = null;
        vix = LoadIndex(context, "VIX"); vix3m = LoadIndex(context, "VIX3M");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        sessionDates = context.Sessions.Select(s => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Open, zone).DateTime)).ToHashSet();
        if (sessionDates.Count == 0) throw new InvalidOperationException("Complete US exchange calendar required.");
        calendarFirst = sessionDates.Min(); calendarLast = sessionDates.Max();

    }

    /// <summary>Evaluates all sleeves once after each completed daily exchange session.</summary>
    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        if (!bars.Any(bar => bar.Instrument == Schb && bar.SessionClose) || lastDecision == context.Time) return;
        if (bars.Any(bar => bar.SessionClose && !bar.SessionOpen))
            throw new InvalidOperationException("This combined strategy requires daily bars.");
        lastDecision = context.Time;
        foreach (var instrument in sleeves.SelectMany(sleeve => sleeve.Markets.Keys).Distinct())
            if (!bars.Any(b => b.Instrument == instrument && b.SessionClose))
                throw new InvalidDataException("Missing daily close for " + instrument.Key + " at " + context.Time);

        var today = payday[Schb].TradingDay();
        var next = payday[Schb].TradingDay(1);
        if (today is null) return;

        TradePayday(next);
        TradeTreasury(next);
        TradeEquityBondReversion(context, today, next);
        RebalanceRiskPremia(context);
        TradeGold(next);
        TradeOil(next);
        TradeGas(next);
        TradeVixHedge();
        TradeVixBasis();
        TradePair();
    }

    /// <summary>Loads an archived Cboe CSV beside the strategy and caches its bytes for this run.</summary>
    private static Dictionary<DateOnly, decimal> LoadIndex(IStrategyContext context, string symbol, [CallerFilePath] string source = "")
    {
        var bytes = context.ExternalData("CBOE/" + symbol, () => File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "Data", symbol + ".csv")));
        return Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(line => line.Trim().Split(','))
            .ToDictionary(row => DateOnly.Parse(row[0], CultureInfo.InvariantCulture), row => decimal.Parse(row[4], CultureInfo.InvariantCulture));
    }

    /// <summary>Holds gold from Thursday's close through the following session's close.</summary>
    private void TradeGold(TradingDay? next)
    {
        if (next is null) return;
        var marketGold = gold[Gldm];
        if (next.Date.DayOfWeek == DayOfWeek.Thursday)
            marketGold.BuyNotional(gold.Allocation, OrderType.MarketOnClose, TimeInForce.Day);
        else marketGold.ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
    }

    /// <summary>Trades oil around weekday US exchange closures.</summary>
    private void TradeOil(TradingDay? next)
    {
        if (next is null) return;
        var marketOil = oil.Market("UGA");
        if (Holiday(WeekdayOffset(next.Date, 5))) marketOil.BuyNotional(oil.Allocation, OrderType.MarketOnClose, TimeInForce.Day);
        if (Holiday(WeekdayOffset(next.Date, 2))) marketOil.ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
        if (Holiday(WeekdayOffset(next.Date, 1))) marketOil.SellNotional(oil.Allocation, OrderType.MarketOnClose, TimeInForce.Day);
        if (Holiday(WeekdayOffset(next.Date, -1))) marketOil.ExitShort(OrderType.MarketOnClose, TimeInForce.Day);
    }

    /// <summary>Maintains the winter natural-gas short and covers it in March.</summary>
    private void TradeGas(TradingDay? next)
    {
        if (next is null) return;
        var marketGas = gas.Market("BOIL");
        if (next.Date.Month >= 11 || next.Date.Month < 3) Target(marketGas, -gas.Allocation, OrderType.MarketOnClose);
        else if (next.Date.Month == 3) marketGas.ExitShort(OrderType.MarketOnClose, TimeInForce.Day);
    }

    /// <summary>Maintains the source's short-UVXY and long-VXZ hedge proportions.</summary>
    private void TradeVixHedge()
    {
        Target(vixHedge.Market("UVXY"), -vixHedge.Allocation / 3.8m);
        Target(vixHedge.Market("VXZ"), vixHedge.Allocation * 2.8m / 3.8m);
    }

    /// <summary>Shifts Monday-Friday dates without excluding holidays, matching the source's weekday offset.</summary>
    private static DateOnly WeekdayOffset(DateOnly date, int offset)
    {
        var remaining = Math.Abs(offset);
        while (remaining > 0)
        {
            date = date.AddDays(Math.Sign(offset));
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) remaining--;
        }
        return date;
    }

    /// <summary>Identifies weekday exchange closures only inside known calendar coverage.</summary>
    private bool Holiday(DateOnly date) => date >= calendarFirst && date <= calendarLast &&
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !sessionDates.Contains(date);

    /// <summary>Uses prior-session index closes so Cboe closing calculations are known before the decision.</summary>
    private void TradeVixBasis()
    {
        var prior = payday[Schb].TradingDay(-1);
        if (prior is null) return;
        if (!vix.TryGetValue(prior.Date, out var spot) || !vix3m.TryGetValue(prior.Date, out var term) || term <= 0)
            throw new InvalidDataException("Missing Cboe index close for " + prior.Date);
        var values = vix.Where(p => p.Key <= prior.Date).OrderBy(p => p.Key).TakeLast(60).Select(p => p.Value).ToArray();
        if (values.Length < 60) return;
        var mean = values.Average();
        var vol = Math.Sqrt((double)(values.Sum(x => (x - mean) * (x - mean)) / values.Length * 252m));
        var shortThreshold = term < 15m ? .85m : term < 17m ? .90m : term < 20m ? .95m : term < 25m ? 1m : 1.1m;
        var longThreshold = vol < 20 ? shortThreshold : 1.1m;
        var ratio = spot / term;
        var inverse = vixBasis.Market("SVXY"); var direct = vixBasis.Market("VIXY");
        if (ratio < shortThreshold)
        {
            direct.ExitLong(OrderType.MarketOnOpen, TimeInForce.Day); Target(inverse, vixBasis.Allocation);
        }
        else if (ratio > longThreshold)
        {
            inverse.ExitLong(OrderType.MarketOnOpen, TimeInForce.Day); Target(direct, vixBasis.Allocation);
        }
        else
        {
            inverse.ExitLong(OrderType.MarketOnOpen, TimeInForce.Day); direct.ExitLong(OrderType.MarketOnOpen, TimeInForce.Day);
        }
    }

    /// <summary>Switches between SCHB and TLT when their log price ratio crosses its five-session EMA.</summary>
    private void TradePair()
    {
        var equity = equityBondPair[Schb]; var bond = equityBondPair[Tlt];
        if (equity.Close is null || bond.Close is null) return;
        var value = (decimal)Math.Log((double)(equity.Close.Value / bond.Close.Value));
        pair.Add(value);
        var ema = Indicators.Ema(pair, 5);
        if (ema is not null && previousEma is not null && previousPair is not null)
        {
            if (previousPair >= previousEma && value < ema)
            {
                bond.ExitLong(OrderType.MarketOnOpen, TimeInForce.Day); equity.BuyNotional(equityBondPair.Allocation, OrderType.MarketOnOpen, TimeInForce.Day);
            }
            else if (previousPair <= previousEma && value > ema)
            {
                equity.ExitLong(OrderType.MarketOnOpen, TimeInForce.Day); bond.BuyNotional(equityBondPair.Allocation, OrderType.MarketOnOpen, TimeInForce.Day);
            }
        }
        previousPair = value; previousEma = ema;
    }

    /// <summary>Rebalances signed notional by whole shares above the source helper's five-percent drift.</summary>
    private static void Target(InstrumentContext market, decimal notional, OrderType type = OrderType.MarketOnOpen)
    {
        if (market.Close is not > 0m) return;
        var pending = market.Context.OpenOrders.Where(o => o.Request.Substrategy == market.Substrategy && o.Request.Instrument == market.Instrument).Sum(o => o.Request.Quantity);
        var difference = notional - (market.Quantity + pending) * market.Close.Value;
        var quantity = Math.Floor(Math.Abs(difference) / market.Close.Value);
        if (quantity == 0 || Math.Abs(difference) / Math.Abs(notional) <= .05m) return;
        if (difference > 0) market.Buy(quantity, type, TimeInForce.Day);
        else market.Sell(quantity, type, TimeInForce.Day);
    }

    /// <summary>Buys SCHB on sessions 8 and 16, then exits on session 12 and at month end.</summary>
    private void TradePayday(TradingDay? next)
    {
        if (next is null) return;

        if (next.DayOfMonth == 12 || next.IsMonthEnd)
            payday[Schb].ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
        else if (next.DayOfMonth is 8 or 16)
            payday[Schb].BuyNotional(payday.Allocation, OrderType.MarketOnClose, TimeInForce.Day);
    }

    /// <summary>Holds TLT long for the final seven sessions and short from month end through session 7.</summary>
    private void TradeTreasury(TradingDay? next)
    {
        if (next is null) return;

        if (next.DayOfMonth == next.DaysInMonth - 7)
            treasury[Tlt].BuyNotional(treasury.Allocation, OrderType.MarketOnClose, TimeInForce.Day);

        if (next.IsMonthEnd)
        {
            treasury[Tlt].ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
            treasury[Tlt].SellNotional(treasury.Allocation, OrderType.MarketOnClose, TimeInForce.Day);
        }
        else if (next.DayOfMonth == 7)
        {
            treasury[Tlt].ExitShort(OrderType.MarketOnClose, TimeInForce.Day);
        }
    }

    /// <summary>Buys the weaker of SCHB and TLT after session 14 and exits it at that month's close.</summary>
    private void TradeEquityBondReversion(IStrategyContext context, TradingDay today, TradingDay? next)
    {
        if (next?.IsMonthEnd == true)
        {
            reversion[Schb].ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
            reversion[Tlt].ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
        }

        if (today.DayOfMonth != 14) return;

        var firstSession = reversion[Schb].TradingDay(-(today.DayOfMonth - 1));
        if (firstSession is null) return;
        var equityOpen = SessionOpen(context, Schb, firstSession.Session.Open);
        var bondOpen = SessionOpen(context, Tlt, firstSession.Session.Open);
        var equityClose = reversion[Schb].Close;
        var bondClose = reversion[Tlt].Close;
        if (equityOpen is null or <= 0m || bondOpen is null or <= 0m || equityClose is null || bondClose is null) return;

        var equityPerformance = (equityClose.Value - equityOpen.Value) / equityOpen.Value;
        var bondPerformance = (bondClose.Value - bondOpen.Value) / bondOpen.Value;
        if (equityPerformance > bondPerformance)
            reversion[Tlt].BuyNotional(reversion.Allocation, OrderType.MarketOnOpen, TimeInForce.Day);
        else if (equityPerformance < bondPerformance)
            reversion[Schb].BuyNotional(reversion.Allocation, OrderType.MarketOnOpen, TimeInForce.Day);
    }

    /// <summary>Targets five-percent annual volatility per asset, scaling the three weights to at most 100%.</summary>
    private void RebalanceRiskPremia(IStrategyContext context)
    {
        var theoreticalWeights = new Dictionary<Instrument, decimal>();
        foreach (var instrument in riskPremia.Markets.Keys)
        {
            var history = context.History(instrument, 91);
            if (history.Count < 91) return;
            var returns = history.Zip(history.Skip(1), (previous, current) =>
                (current.Close - previous.Close) / previous.Close).ToArray();
            var mean = returns.Average();
            var variance = returns.Sum(value => (value - mean) * (value - mean)) / returns.Length;
            if (variance <= 0m) return;
            var annualVolatility = (decimal)Math.Sqrt((double)(variance * 252m));
            theoreticalWeights[instrument] = 0.05m / annualVolatility;
        }

        var scale = Math.Min(1m, 1m / theoreticalWeights.Values.Sum());
        foreach (var (instrument, weight) in theoreticalWeights)
            RebalanceLongNotional(riskPremia[instrument], riskPremia.Allocation * weight * scale);
    }

    /// <summary>Moves a risk-premia holding toward a whole-share notional target when drift exceeds ten percent.</summary>
    private static void RebalanceLongNotional(InstrumentContext market, decimal targetNotional)
    {
        var context = market.Context;
        var instrument = market.Instrument;
        var price = context.History(instrument, 1).Single().Close;
        if (price <= 0m || targetNotional <= 0m) return;

        var held = context.Portfolio.Positions
            .Where(position => position.Substrategy == market.Substrategy && position.Instrument == instrument)
            .Sum(position => position.Quantity);
        var pending = context.OpenOrders
            .Where(order => order.Request.Substrategy == market.Substrategy && order.Request.Instrument == instrument)
            .Sum(order => order.Request.Quantity);
        var effectiveQuantity = held + pending;
        var targetQuantity = Math.Floor(targetNotional / price);
        var drift = Math.Abs(effectiveQuantity * price - targetNotional) / targetNotional;
        var difference = targetQuantity - effectiveQuantity;
        if (difference == 0m || drift <= RiskPremiaTrackingErrorThreshold) return;

        if (difference > 0m)
            context.BuyOnOpen(market.Substrategy, instrument, difference, TimeInForce.Day);
        else
            context.SellOnOpen(market.Substrategy, instrument, -difference, TimeInForce.Day);
    }

    /// <summary>Returns the opening price of a specified session from completed history.</summary>
    private static decimal? SessionOpen(IStrategyContext context, Instrument instrument, DateTimeOffset sessionOpen)
        => context.History(instrument, int.MaxValue).FirstOrDefault(bar => bar.OpenTime == sessionOpen)?.Open;
}
