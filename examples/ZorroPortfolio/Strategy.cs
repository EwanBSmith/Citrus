using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using Citrus.Trading;

/// <summary>Adapts the source portfolio to Citrus closing-auction calendar orders and next-open price signals.</summary>
public sealed class ZorroPortfolio : IStrategy
{
    private sealed class Sleeve
    {
        public readonly decimal Allocation;
        public readonly Dictionary<string, InstrumentContext> Markets;
        /// <summary>Registers a fixed source account and its instrument contexts.</summary>
        public Sleeve(IStrategyContext context, string name, decimal allocation, params string[] symbols)
        {
            Allocation = allocation;
            context.Register(name, allocation / 17000m);
            Markets = symbols.ToDictionary(s => s, s => new InstrumentContext(context, name, s));
        }
        public InstrumentContext this[string symbol] => Markets[symbol];
    }
    private Sleeve payday = null!, gold = null!, bond = null!, reversion = null!, hedge = null!, basis = null!, risk = null!, oil = null!, pair = null!;
    private (double Date, double Close)[] vix = [], vix3m = [];
    private readonly List<double> volatility = [], ratios = [];
    private double? ema, previousRatio, previousEma;
    private DateOnly? processedDate;

    /// <summary>Uses the source account and asset ordering, with natural gas disabled.</summary>
    public void OnStart(IStrategyContext context)
    {
        payday = new(context, "PaydaySeason", 810, "SCHB");
        gold = new(context, "GoldSeason", 1256, "GLDM");
        bond = new(context, "BondSeason", 2608, "TLT");
        reversion = new(context, "EqBondReversion", 3018, "SCHB", "TLT");
        hedge = new(context, "VIXHedge", 2094, "UVXY", "VXZ");
        basis = new(context, "VIXBasis", 800, "SVXY", "VIXY");
        risk = new(context, "RiskPremia", 3000, "SCHB", "GLDM", "TLT");
        oil = new(context, "OilSeason", 1160, "UGA");
        pair = new(context, "EqBondPair", 800, "SCHB", "TLT");
        vix = LoadIndex(context, "VIX"); vix3m = LoadIndex(context, "VIX3M");
        volatility.Clear(); ratios.Clear(); ema = previousRatio = previousEma = null;
        processedDate = null;
        foreach (var session in context.Sessions.Where(s => s.Close > context.Time))
            context.Schedule(session.Close.AddMinutes(-1), "calendar");
    }

    /// <summary>Evaluates completed daily-bar signals; market orders become eligible at the next open.</summary>
    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        var date = DateOnly.FromDateTime(context.Time.UtcDateTime);
        if (!bars.Any(b => b.Instrument == payday["SCHB"].Instrument && b.SessionClose) || processedDate == date) return;
        if (bars.Any(b => b.SessionClose && !b.SessionOpen)) throw new InvalidOperationException("ZorroPortfolio requires daily session bars.");
        processedDate = date;
        var tdm = Enumerable.Range(1, date.Day).Count(d => CalendarDay(new(date.Year, date.Month, d)));
        var spot = IndexClose(vix, context.Time); var term = IndexClose(vix3m, context.Time);
        volatility.Add(spot);
        var eq = MeanPrice(context, pair["SCHB"]); var treas = MeanPrice(context, pair["TLT"]);
        if (eq is > 0 && treas is > 0)
        {
            var ratio = Math.Log((double)(eq.Value / treas.Value));
            ratios.Add(ratio);
            ema = ema is null ? ratio : ema + (ratio - ema) / 3;
        }
        var warm = context.History(payday["SCHB"].Instrument, 90).Count == 90;
        if (warm)
        {
            if (tdm == 14)
            {
                var eh = context.History(reversion["SCHB"].Instrument, tdm + 1);
                var bh = context.History(reversion["TLT"].Instrument, tdm + 1);
                if (eh.Count == tdm + 1 && bh.Count == tdm + 1)
                {
                    var diff = (eh[^1].Close - eh[0].Open) / eh[0].Open - (bh[^1].Close - bh[0].Open) / bh[0].Open;
                    if (diff > 0) Enter(reversion, "TLT", 1); else if (diff < 0) Enter(reversion, "SCHB", 1);
                }
            }
            Target(hedge["UVXY"], -551.052978515625m);
            // lite-C constant-folds 2094*(2.8/(1.+2.8)) to this float; retaining it matters at the 5% boundary.
            Target(hedge["VXZ"], 1542.949951171875m);
            if (term > 0 && volatility.Count >= 60)
            {
                var sample = volatility.TakeLast(60).ToArray(); var mean = sample.Average();
                var vol = Math.Sqrt(sample.Sum(x => (x - mean) * (x - mean)) / 60 * 252);
                var low = term < 15 ? .85 : term < 17 ? .9 : term < 20 ? .95 : term < 25 ? 1 : 1.1;
                var high = vol < 20 ? low : 1.1;
                if (spot / term < low) { Exit(basis["VIXY"], 1); Target(basis["SVXY"], 800); }
                else if (spot / term > high) { Exit(basis["SVXY"], 1); Target(basis["VIXY"], 800); }
                else { Exit(basis["VIXY"], 1); Exit(basis["SVXY"], 1); }
            }
            RiskPremia(context);
            if (ema is not null && previousEma is not null && ratios.Count > 0)
            {
                var ratio = ratios[^1];
                if (previousRatio >= previousEma && ratio < ema) { Enter(pair, "SCHB", 1); Exit(pair["TLT"], 1); }
                else if (previousRatio <= previousEma && ratio > ema) { Enter(pair, "TLT", 1); Exit(pair["SCHB"], 1); }
            }
        }
        previousRatio = ratios.Count == 0 ? null : ratios[^1]; previousEma = ema;
    }

    /// <summary>Preserves the source's skipped volatility slot and Assets ordering; missing slot is explicitly zero.</summary>
    private void RiskPremia(IStrategyContext context)
    {
        var weights = new double[3];
        foreach (var i in new[] { 0, 2 })
        {
            var market = risk[i == 0 ? "SCHB" : "GLDM"];
            var h = context.History(market.Instrument, 91);
            if (h.Count == 0) return;
            var returns = h.Zip(h.Skip(1), (a, b) => (double)(b.Close / a.Close - 1)).ToList();
            if (h.Count < 91) returns.Insert(0, (double)(h[0].Close / h[0].Open - 1));
            while (returns.Count < 90) returns.Insert(0, 0);
            var mean = returns.Average(); var variance = returns.Sum(x => (x - mean) * (x - mean)) / 89;
            if (variance <= 0) return;
            weights[i] = .05 / Math.Sqrt(variance * 252);
        }
        var factor = Math.Min(1, 1 / weights.Sum());
        var symbols = new[] { "SCHB", "GLDM", "TLT" };
        for (var i = 0; i < 3; i++) if (weights[i] > 0) Target(risk[symbols[i]], (decimal)(weights[i] * factor) * risk.Allocation);
    }

    /// <summary>Submits calendar-known trades one minute before the auction using only previously completed prices.</summary>
    public void OnScheduled(IStrategyContext context, string name)
    {
        if (name != "calendar" || context.History(payday["SCHB"].Instrument, 90).Count < 90) return;
        var date = DateOnly.FromDateTime(context.Time.UtcDateTime);
        var tdm = Enumerable.Range(1, date.Day).Count(d => CalendarDay(new(date.Year, date.Month, d)));
        var tom = Enumerable.Range(1, DateTime.DaysInMonth(date.Year, date.Month)).Count(d => CalendarDay(new(date.Year, date.Month, d)));
        const OrderType close = OrderType.MarketOnClose;
        if (tdm is 8 or 16) Enter(payday, "SCHB", 1, close);
        if (tdm == 12 || tdm == tom) Exit(payday["SCHB"], 1, close);
        if (date.DayOfWeek == DayOfWeek.Thursday) Enter(gold, "GLDM", 1, close); else Exit(gold["GLDM"], 1, close);
        if (tdm == tom - 7) Enter(bond, "TLT", 1, close);
        if (tdm == tom) Enter(bond, "TLT", -1, close);
        if (tdm == 7) Exit(bond["TLT"], -1, close);
        if (tdm == tom) { Exit(reversion["SCHB"], 1, close); Exit(reversion["TLT"], 1, close); }
        if (Holiday(WeekdayOffset(date, 5))) Enter(oil, "UGA", 1, close);
        if (Holiday(WeekdayOffset(date, 2))) Exit(oil["UGA"], 1, close);
        if (Holiday(WeekdayOffset(date, 1))) Enter(oil, "UGA", -1, close);
        if (Holiday(WeekdayOffset(date, -1))) Exit(oil["UGA"], -1, close);
    }

    /// <summary>Adds source-sized lots on the same side or reverses to the requested side with one net order.</summary>
    private static void Enter(Sleeve sleeve, string symbol, int side, OrderType type = OrderType.Market)
    {
        var market = sleeve[symbol];
        if (market.Context.History(market.Instrument, 91).Count < 91 || market.Close is not > 0) return;
        var quantity = Math.Floor(sleeve.Allocation / market.Close.Value);
        if (quantity <= 0) return;
        var projected = Projected(market);
        SetQuantity(market, (Math.Sign(projected) == side ? projected : 0) + side * quantity, type);
    }

    /// <summary>Includes accepted unfilled orders when evaluating this account's intended holding.</summary>
    private static decimal Projected(InstrumentContext market) => market.Quantity + market.Context.OpenOrders
        .Where(o => o.Request.Substrategy == market.Substrategy && o.Request.Instrument == market.Instrument).Sum(o => o.Request.Quantity);

    /// <summary>Replaces this market's outstanding intent with a single delta from its actual filled position.</summary>
    private static void SetQuantity(InstrumentContext market, decimal desired, OrderType type)
    {
        market.CancelOrders();
        var delta = desired - market.Quantity;
        var duration = type == OrderType.MarketOnClose ? TimeInForce.Day : TimeInForce.GoodTillCancelled;
        if (delta > 0) market.Buy(delta, type, duration); else if (delta < 0) market.Sell(-delta, type, duration);
    }

    /// <summary>Closes only the specified projected side without cancelling an exit already moving it to flat.</summary>
    private static void Exit(InstrumentContext market, int side, OrderType type = OrderType.Market)
    {
        if (Math.Sign(Projected(market)) == side) SetQuantity(market, 0, type);
    }

    /// <summary>Applies five-percent notional drift using completed prices and pending quantities.</summary>
    private static void Target(InstrumentContext market, decimal target)
    {
        if (market.Context.History(market.Instrument, 91).Count < 91 || market.Close is not > 0) return;
        var projected = Projected(market);
        var diff = target - projected * market.Close.Value;
        var qty = Math.Floor(Math.Abs(diff) / market.Close.Value);
        if (qty <= 0 || Math.Abs(diff) / Math.Abs(target) <= .05m) return;
        SetQuantity(market, projected + Math.Sign(diff) * qty, OrderType.Market);
    }

    /// <summary>Uses Zorro's unconfigured global Holidays array, separately from the oil helper.</summary>
    private static bool CalendarDay(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !(d.Month == 1 && d.Day == 1 || d.Month == 12 && d.Day == 25);

    /// <summary>Reconstructs the daily high/low midpoint used by price() from the source bar.</summary>
    private static decimal? MeanPrice(IStrategyContext context, InstrumentContext market)
    {
        var b = context.History(market.Instrument, 1).LastOrDefault();
        return b is null ? null : (b.High + b.Low) / 2;
    }

    /// <summary>Shifts dates by weekdays, counting holidays as weekdays.</summary>
    private static DateOnly WeekdayOffset(DateOnly d, int n)
    {
        for (var left = Math.Abs(n); left > 0;) { d = d.AddDays(Math.Sign(n)); if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) left--; }
        return d;
    }

    /// <summary>Ports holidays.c, including its omission of exceptional exchange closures.</summary>
    private static bool Holiday(DateOnly d)
    {
        var y = d.Year;
        // Find an observed fixed holiday and an ordinal Monday without modifying global bar-day counting.
        DateOnly Observed(int m, int day) { var x = new DateOnly(y, m, day); return x.DayOfWeek == DayOfWeek.Saturday ? x.AddDays(-1) : x.DayOfWeek == DayOfWeek.Sunday ? x.AddDays(1) : x; }
        DateOnly Nth(int m, DayOfWeek dow, int n) { var x = new DateOnly(y, m, 1); return x.AddDays(((int)dow - (int)x.DayOfWeek + 7) % 7 + 7 * (n - 1)); }
        var a = y % 19; var b = y / 100; var c = y % 100; var h = (19 * a + b - b / 4 - (b - (b + 8) / 25 + 1) / 3 + 15) % 30;
        var l = (32 + 2 * (b % 4) + 2 * (c / 4) - h - c % 4) % 7; var m = (a + 11 * h + 22 * l) / 451;
        var easter = new DateOnly(y, (h + l - 7 * m + 114) / 31, (h + l - 7 * m + 114) % 31 + 1);
        var memorial = new DateOnly(y, 5, 31); memorial = memorial.AddDays(-((int)memorial.DayOfWeek + 6) % 7);
        return new DateOnly(y, 1, 1).DayOfWeek != DayOfWeek.Saturday && d == Observed(1, 1) ||
            d == Nth(1, DayOfWeek.Monday, 3) || d == Nth(2, DayOfWeek.Monday, 3) || d == easter.AddDays(-2) || d == memorial ||
            y >= 2022 && d == Observed(6, 19) || d == Observed(7, 4) || d == Nth(9, DayOfWeek.Monday, 1) ||
            d == Nth(11, DayOfWeek.Thursday, 4) || d == Observed(12, 25);
    }

    /// <summary>Loads source index records from snapshots beside the strategy, retaining their original timestamps.</summary>
    private static (double Date, double Close)[] LoadIndex(IStrategyContext context, string symbol, [CallerFilePath] string source = "")
    {
        var bytes = context.ExternalData(symbol, () => File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "Data", symbol + ".t6")));
        return Enumerable.Range(0, bytes.Length / 32).Select(i => (BitConverter.ToDouble(bytes, i * 32), (double)BitConverter.ToSingle(bytes, i * 32 + 20))).OrderBy(r => r.Item1).ToArray();
    }

    /// <summary>Matches cboeClose's sixteen-hour subtraction and last-known-record lookup.</summary>
    private static double IndexClose((double Date, double Close)[] rows, DateTimeOffset time)
    {
        var date = time.UtcDateTime.ToOADate() - 16.0 / 24;
        var low = 0; var high = rows.Length - 1;
        while (low <= high) { var mid = (low + high) / 2; if (rows[mid].Date <= date) low = mid + 1; else high = mid - 1; }
        return high >= 0 ? rows[high].Close : 0;
    }
}
