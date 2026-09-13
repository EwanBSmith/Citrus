using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using Citrus.Trading;

/// <summary>Adapts the source portfolio to Citrus closing-auction calendar orders and next-open price signals.</summary>
public sealed class ZorroPortfolio : DailyStrategy
{
    private StrategyAccount payday = null!, gold = null!, bond = null!, reversion = null!, hedge = null!, basis = null!, risk = null!, oil = null!, pair = null!;
    private (double Date, double Close)[] vix = [], vix3m = [];
    private readonly List<double> volatility = [], ratios = [];
    private double? ema, previousRatio, previousEma;
    protected override string ClockSymbol => "SCHB";
    protected override int WarmupBars => 91;

    /// <summary>Uses the source account and asset ordering, with natural gas disabled.</summary>
    protected override void Initialize()
    {
        payday = Account("PaydaySeason", 810, "SCHB");
        gold = Account("GoldSeason", 1256, "GLDM");
        bond = Account("BondSeason", 2608, "TLT");
        reversion = Account("EqBondReversion", 3018, "SCHB", "TLT");
        hedge = Account("VIXHedge", 2094, "UVXY", "VXZ");
        basis = Account("VIXBasis", 800, "SVXY", "VIXY");
        risk = Account("RiskPremia", 3000, "SCHB", "GLDM", "TLT");
        oil = Account("OilSeason", 1160, "UGA");
        pair = Account("EqBondPair", 800, "SCHB", "TLT");
        vix = LoadIndex("VIX"); vix3m = LoadIndex("VIX3M");
        volatility.Clear(); ratios.Clear(); ema = previousRatio = previousEma = null;
    }

    /// <summary>Evaluates completed daily-bar signals; market orders become eligible at the next open.</summary>
    protected override void OnClose()
    {
        var date = Date;
        var tdm = Enumerable.Range(1, date.Day).Count(d => CalendarDay(new(date.Year, date.Month, d)));
        var tom = Enumerable.Range(1, DateTime.DaysInMonth(date.Year, date.Month)).Count(d => CalendarDay(new(date.Year, date.Month, d)));
        var spot = IndexClose(vix, Time); var term = IndexClose(vix3m, Time);
        EqBondReversion(tdm, tom, beforeClose: false);
        VIXHedge();
        VIXBasis(spot, term);
        RiskPremia();
        EqBondPair();
    }

    /// <summary>Trades the monthly equity-versus-bond reversion signal after the completed close.</summary>
    private void EqBondReversion(int tdm, int tom, bool beforeClose)
    {
        if (beforeClose)
        {
            if (tdm == tom)
            {
                reversion["SCHB"].ExitLong(OrderType.MarketOnClose);
                reversion["TLT"].ExitLong(OrderType.MarketOnClose);
            }
            return;
        }
        if (!payday["SCHB"].HasHistory(90) || tdm != 14) return;
        var equities = reversion["SCHB"].History(tdm + 1);
        var bonds = reversion["TLT"].History(tdm + 1);
        if (equities.Count != tdm + 1 || bonds.Count != tdm + 1) return;
        var difference = (equities[^1].Close - equities[0].Open) / equities[0].Open
            - (bonds[^1].Close - bonds[0].Open) / bonds[0].Open;
        if (difference > 0) reversion["TLT"].EnterLong();
        else if (difference < 0) reversion["SCHB"].EnterLong();
    }

    /// <summary>Maintains the fixed short-volatility and medium-term-volatility hedge notionals.</summary>
    private void VIXHedge()
    {
        if (!payday["SCHB"].HasHistory(90)) return;
        hedge["UVXY"].TargetNotional(-551.052978515625m, tolerance: .05m);
        // lite-C constant-folds 2094*(2.8/(1.+2.8)) to this float; retaining it matters at the 5% boundary.
        hedge["VXZ"].TargetNotional(1542.949951171875m, tolerance: .05m);
    }

    /// <summary>Switches the volatility-basis allocation from the completed VIX term-structure signal.</summary>
    private void VIXBasis(double spot, double term)
    {
        volatility.Add(spot);
        if (!payday["SCHB"].HasHistory(90) || term <= 0 || volatility.Count < 60) return;
        var sample = volatility.TakeLast(60).ToArray(); var mean = sample.Average();
        var vol = Math.Sqrt(sample.Sum(x => (x - mean) * (x - mean)) / 60 * 252);
        var low = term < 15 ? .85 : term < 17 ? .9 : term < 20 ? .95 : term < 25 ? 1 : 1.1;
        var high = vol < 20 ? low : 1.1;
        if (spot / term < low) { basis["VIXY"].ExitLong(); basis["SVXY"].TargetNotional(800, tolerance: .05m); }
        else if (spot / term > high) { basis["SVXY"].ExitLong(); basis["VIXY"].TargetNotional(800, tolerance: .05m); }
        else { basis["VIXY"].ExitLong(); basis["SVXY"].ExitLong(); }
    }

    /// <summary>Preserves the source's skipped volatility slot and Assets ordering; missing slot is explicitly zero.</summary>
    private void RiskPremia()
    {
        if (!payday["SCHB"].HasHistory(90)) return;
        var weights = new double[3];
        foreach (var i in new[] { 0, 2 })
        {
            var market = risk[i == 0 ? "SCHB" : "GLDM"];
            var h = market.History(91);
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
        for (var i = 0; i < 3; i++) if (weights[i] > 0) risk[symbols[i]].TargetNotional((decimal)(weights[i] * factor) * risk.Allocation, tolerance: .05m);
    }

    /// <summary>Updates and trades the equity-versus-bond midpoint crossover after the completed close.</summary>
    private void EqBondPair()
    {
        var equities = pair["SCHB"].Midpoint; var bonds = pair["TLT"].Midpoint;
        if (equities is > 0 && bonds is > 0)
        {
            var ratio = Math.Log((double)(equities.Value / bonds.Value));
            ratios.Add(ratio);
            ema = ema is null ? ratio : ema + (ratio - ema) / 3;
        }
        if (payday["SCHB"].HasHistory(90) && ema is not null && previousEma is not null && ratios.Count > 0)
        {
            var ratio = ratios[^1];
            if (previousRatio >= previousEma && ratio < ema) { pair["SCHB"].EnterLong(); pair["TLT"].ExitLong(); }
            else if (previousRatio <= previousEma && ratio > ema) { pair["TLT"].EnterLong(); pair["SCHB"].ExitLong(); }
        }
        previousRatio = ratios.Count == 0 ? null : ratios[^1]; previousEma = ema;
    }

    /// <summary>Submits calendar-known trades one minute before the auction using only previously completed prices.</summary>
    protected override void BeforeClose()
    {
        if (!payday["SCHB"].HasHistory(90)) return;
        var date = Date;
        var tdm = Enumerable.Range(1, date.Day).Count(d => CalendarDay(new(date.Year, date.Month, d)));
        var tom = Enumerable.Range(1, DateTime.DaysInMonth(date.Year, date.Month)).Count(d => CalendarDay(new(date.Year, date.Month, d)));
        PaydaySeason(tdm, tom);
        GoldSeason(date);
        BondSeason(tdm, tom);
        EqBondReversion(tdm, tom, beforeClose: true);
        OilSeason(date);
    }

    /// <summary>Trades the source payday entry and exit dates at the closing auction.</summary>
    private void PaydaySeason(int tdm, int tom)
    {
        if (tdm is 8 or 16) payday["SCHB"].EnterLong(OrderType.MarketOnClose);
        if (tdm == 12 || tdm == tom) payday["SCHB"].ExitLong(OrderType.MarketOnClose);
    }

    /// <summary>Holds gold from each Thursday close until the following session close.</summary>
    private void GoldSeason(DateOnly date)
    {
        if (date.DayOfWeek == DayOfWeek.Thursday) gold["GLDM"].EnterLong(OrderType.MarketOnClose);
        else gold["GLDM"].ExitLong(OrderType.MarketOnClose);
    }

    /// <summary>Runs the month-end bond long/short sequence at closing auctions.</summary>
    private void BondSeason(int tdm, int tom)
    {
        if (tdm == tom - 7) bond["TLT"].EnterLong(OrderType.MarketOnClose);
        if (tdm == tom) bond["TLT"].EnterShort(OrderType.MarketOnClose);
        if (tdm == 7) bond["TLT"].ExitShort(OrderType.MarketOnClose);
    }

    /// <summary>Trades the long and short oil holiday windows at closing auctions.</summary>
    private void OilSeason(DateOnly date)
    {
        if (Holiday(WeekdayOffset(date, 5))) oil["UGA"].EnterLong(OrderType.MarketOnClose);
        if (Holiday(WeekdayOffset(date, 2))) oil["UGA"].ExitLong(OrderType.MarketOnClose);
        if (Holiday(WeekdayOffset(date, 1))) oil["UGA"].EnterShort(OrderType.MarketOnClose);
        if (Holiday(WeekdayOffset(date, -1))) oil["UGA"].ExitShort(OrderType.MarketOnClose);
    }

    /// <summary>Uses Zorro's unconfigured global Holidays array, separately from the oil helper.</summary>
    private static bool CalendarDay(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !(d.Month == 1 && d.Day == 1 || d.Month == 12 && d.Day == 25);

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
    private (double Date, double Close)[] LoadIndex(string symbol, [CallerFilePath] string source = "")
    {
        var bytes = Context.ExternalData(symbol, () => File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "Data", symbol + ".t6")));
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
