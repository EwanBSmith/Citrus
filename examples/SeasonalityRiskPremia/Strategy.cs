using System;
using System.Collections.Generic;
using System.Linq;
using Citrus.Trading;

/// <summary>Combines payday, Treasury, equity-bond reversion, and volatility-targeted risk-premia sleeves.</summary>
public sealed class SeasonalityRiskPremia : IStrategy
{
    private const decimal PaydayAllocation = 810m;
    private const decimal TreasuryAllocation = 2608m;
    private const decimal EquityBondReversionAllocation = 3018m;
    private const decimal RiskPremiaAllocation = 3000m;
    private const decimal RiskPremiaTrackingErrorThreshold = 0.10m;
    private const decimal InitialCapital = 10000m;

    private const string Payday = "PaydaySeason";
    private const string Treasury = "BondSeason";
    private const string EquityBondReversion = "EqBondReversion";
    private const string RiskPremia = "RiskPremia";

    private static readonly Instrument Schb = new("US", AssetClass.Equity, "SCHB");
    private static readonly Instrument Tlt = new("US", AssetClass.Equity, "TLT");
    private static readonly Instrument Gldm = new("US", AssetClass.Equity, "GLDM");
    private static readonly Instrument[] RiskPremiaInstruments = [Schb, Tlt, Gldm];

    private InstrumentContext? payday;
    private InstrumentContext? treasury;
    private InstrumentContext? reversionEquity;
    private InstrumentContext? reversionBond;

    /// <summary>Creates four independently attributed accounts using the source strategy's dollar allocations.</summary>
    public void OnStart(IStrategyContext context)
    {
        context.Register(Payday, PaydayAllocation / InitialCapital);
        context.Register(Treasury, TreasuryAllocation / InitialCapital);
        context.Register(EquityBondReversion, EquityBondReversionAllocation / InitialCapital);
        context.Register(RiskPremia, RiskPremiaAllocation / InitialCapital);

        payday = new InstrumentContext(context, Payday, Schb);
        treasury = new InstrumentContext(context, Treasury, Tlt);
        reversionEquity = new InstrumentContext(context, EquityBondReversion, Schb);
        reversionBond = new InstrumentContext(context, EquityBondReversion, Tlt);
    }

    /// <summary>Evaluates the four sleeves once after each completed exchange session.</summary>
    public void OnBar(IStrategyContext context, IReadOnlyList<Bar> bars)
    {
        if (!bars.Any(bar => bar.SessionClose)) return;

        var today = payday!.TradingDay();
        var next = payday.TradingDay(1);
        if (today is null) return;

        TradePayday(next);
        TradeTreasury(next);
        TradeEquityBondReversion(context, today, next);
        RebalanceRiskPremia(context);
    }

    /// <summary>Buys SCHB on sessions 8 and 16, then exits on session 12 and at month end.</summary>
    private void TradePayday(TradingDay? next)
    {
        if (next is null) return;

        if (next.DayOfMonth == 12 || next.IsMonthEnd)
            payday!.ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
        else if (next.DayOfMonth is 8 or 16)
            payday!.BuyNotional(PaydayAllocation, OrderType.MarketOnClose, TimeInForce.Day);
    }

    /// <summary>Holds TLT long for the final seven sessions and short from month end through session 7.</summary>
    private void TradeTreasury(TradingDay? next)
    {
        if (next is null) return;

        if (next.DayOfMonth == next.DaysInMonth - 7)
            treasury!.BuyNotional(TreasuryAllocation, OrderType.MarketOnClose, TimeInForce.Day);

        if (next.IsMonthEnd)
        {
            treasury!.ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
            treasury.SellNotional(TreasuryAllocation, OrderType.MarketOnClose, TimeInForce.Day);
        }
        else if (next.DayOfMonth == 7)
        {
            treasury!.ExitShort(OrderType.MarketOnClose, TimeInForce.Day);
        }
    }

    /// <summary>Buys the weaker of SCHB and TLT after session 14 and exits it at that month's close.</summary>
    private void TradeEquityBondReversion(IStrategyContext context, TradingDay today, TradingDay? next)
    {
        if (next?.IsMonthEnd == true)
        {
            reversionEquity!.ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
            reversionBond!.ExitLong(OrderType.MarketOnClose, TimeInForce.Day);
        }

        if (today.DayOfMonth != 14) return;

        var firstSession = reversionEquity!.TradingDay(-(today.DayOfMonth - 1));
        if (firstSession is null) return;
        var equityOpen = SessionOpen(context, Schb, firstSession.Session.Open);
        var bondOpen = SessionOpen(context, Tlt, firstSession.Session.Open);
        var equityClose = reversionEquity.Close;
        var bondClose = reversionBond!.Close;
        if (equityOpen is null or <= 0m || bondOpen is null or <= 0m || equityClose is null || bondClose is null) return;

        var equityPerformance = (equityClose.Value - equityOpen.Value) / equityOpen.Value;
        var bondPerformance = (bondClose.Value - bondOpen.Value) / bondOpen.Value;
        if (equityPerformance > bondPerformance)
            reversionBond.BuyNotional(EquityBondReversionAllocation, OrderType.MarketOnOpen, TimeInForce.Day);
        else if (equityPerformance < bondPerformance)
            reversionEquity.BuyNotional(EquityBondReversionAllocation, OrderType.MarketOnOpen, TimeInForce.Day);
    }

    /// <summary>Targets five-percent annual volatility per asset, scaling the three weights to at most 100%.</summary>
    private static void RebalanceRiskPremia(IStrategyContext context)
    {
        var theoreticalWeights = new Dictionary<Instrument, decimal>();
        foreach (var instrument in RiskPremiaInstruments)
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
            RebalanceLongNotional(context, instrument, RiskPremiaAllocation * weight * scale);
    }

    /// <summary>Moves a risk-premia holding toward a whole-share notional target when drift exceeds ten percent.</summary>
    private static void RebalanceLongNotional(IStrategyContext context, Instrument instrument, decimal targetNotional)
    {
        var price = context.History(instrument, 1).Single().Close;
        if (price <= 0m || targetNotional <= 0m) return;

        var held = context.Portfolio.Positions
            .Where(position => position.Substrategy == RiskPremia && position.Instrument == instrument)
            .Sum(position => position.Quantity);
        var pending = context.OpenOrders
            .Where(order => order.Request.Substrategy == RiskPremia && order.Request.Instrument == instrument)
            .Sum(order => order.Request.Quantity);
        var effectiveQuantity = held + pending;
        var targetQuantity = Math.Floor(targetNotional / price);
        var drift = Math.Abs(effectiveQuantity * price - targetNotional) / targetNotional;
        var difference = targetQuantity - effectiveQuantity;
        if (difference == 0m || drift <= RiskPremiaTrackingErrorThreshold) return;

        if (difference > 0m)
            context.BuyOnOpen(RiskPremia, instrument, difference, TimeInForce.Day);
        else
            context.SellOnOpen(RiskPremia, instrument, -difference, TimeInForce.Day);
    }

    /// <summary>Returns the opening price of a specified session from completed history.</summary>
    private static decimal? SessionOpen(IStrategyContext context, Instrument instrument, DateTimeOffset sessionOpen)
        => context.History(instrument, int.MaxValue).FirstOrDefault(bar => bar.OpenTime == sessionOpen)?.Open;
}
