# Zorro portfolio

This is the matching Citrus version of `cq_Alpaca_Portfolio.c`, tested against the supplied Zorro 3.112 trade export. Use this example for parity, rather than `SeasonalityRiskPremia`, which implements different, cleaned-up trading semantics on Alpaca data.

## Retained strategy and results

The standalone T6 backtest project and launch script have been removed. Run this strategy through the normal GUI or CLI using the main historical library. Instruments bind by symbol (for example `SCHB`), so a venue label such as `US` does not prevent trading. Missing symbols fail explicitly. The strategy, configuration, index snapshots and engine compatibility options remain.

The reference below used adjusted Zorro T6 prices, original date coverage and 15:30 New York timestamps. Running against the currently cached Alpaca data is supported, but different prices, date coverage or timestamps will produce different results; symbol-based lookup does not make distinct datasets numerically identical.

The strategy uses the VIX and VIX3M snapshots beside its source. Updating ETF or index history can change results; keep the inputs aligned when reproducing a particular historical run.

The comparison framework and diagnostic helpers have also been removed; the engine regression tests remain. The figures below describe the previously verified run, not a new comparison performed automatically.

## Verified reference

For the supplied March 2010–September 11, 2026 export:

- 3,571 closed Zorro trade rows recombine into 6,604 execution groups.
- All execution groups match by UTC minute, sleeve, asset, entry/exit, direction, quantity and fill price.
- Zorro exported profit: $21,381.34068028; Citrus: $21,381.34. The sub-cent difference reflects Zorro's floating-point trade accounting and printed precision.
- Starting cash: $17,000; final Citrus equity: $38,381.34; all positions closed.

This verifies executions and profit, not byte-identical trade tickets, chart samples, Sharpe, annual return, drawdown or Monte Carlo statistics. Those reports use platform-specific definitions; Zorro's capital-required return is not Citrus's return on starting cash. Slippage is already embedded in fill prices, not charged again as a cash fee. Citrus's slippage-cost diagnostic measures signed pre-rounding price impact; it is not Zorro's reported trade-slippage statistic.

## Preserved source behavior

All nine enabled strategies use one uniform `Sleeve` structure: PaydaySeason, GoldSeason, BondSeason, EqBondReversion, VIXHedge, VIXBasis, RiskPremia, OilSeason and EqBondPair. Natural gas stays disabled because it is disabled in this Zorro reference.

Parity deliberately preserves source quirks rather than silently repairing them:

- Adjusted T6 OHLC, 15:30 New York daily decisions, original inception/warmup behavior, and no second application of dividends or splits.
- Zorro's unconfigured global trading-day calendar (weekdays except literal January 1 and December 25), separately from the actual US holiday helper used by oil seasonality. The helper omits exceptional exchange closures.
- `price()` is the daily high/low midpoint for the pair signal; its EMA is recursively initialized from the first observation.
- Sample variance for risk premia. The original double increment skips its middle volatility slot, and the asset-list order assigns the gold-derived weight to TLT. This run's unused slot is explicitly zero here; the original uninitialized variable is not portable across arbitrary Zorro builds.
- Zorro's constant-folded hedge allocations, 5% drift checks, whole-share truncation, and terminal cancellation of new entries followed by liquidation.
- Original CBOE index snapshots, including their stale tail, instead of silently substituting fresher data.

The opt-in `executeMarketOrdersAtCompletedClose`, `completedCloseTickSize`, and `zorroDailySlippageSeconds` options reproduce the local daily Fill=1 behavior. Entries round before extrapolation; exits round afterward. The execution model uses future source candles privately for Zorro's five-second price extrapolation, including flat-candle and terminal-cache behavior. It never exposes those candles through strategy history. These details were checked with isolated Zorro diagnostics; the public documentation describes [Fill=1](https://zorro-project.com/manual/en/fill.htm) and [slippage](https://zorro-project.com/manual/en/spread.htm), but not every implementation edge case.

This legacy compatibility mode is not a realistic guarantee of obtaining a known daily close. Normal Citrus next-event execution remains the default. The slippage compatibility model is specific to this daily T6/zero-spread setup; it is not a general emulation of every Zorro fill mode. The strategy closes opposing positions before opening them; do not combine reversals into one order when using it.
