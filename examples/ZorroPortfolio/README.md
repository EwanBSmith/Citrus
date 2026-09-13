# Zorro portfolio using Citrus execution

This strategy adapts the nine enabled sleeves from `cq_Alpaca_Portfolio.c` to the normal Citrus fill model. `SeasonalityRiskPremia` remains a separate example with different trading rules. No Zorro execution toggle, immediate completed-close fill, tick-rounding override, or future-candle extrapolation remains in the engine.

## Strategy API

The strategy now derives from `DailyStrategy`: `Initialize` declares its accounts, `OnClose` evaluates completed-price signals, and `BeforeClose` handles calendar trades. Account-bound calls such as `gold["GLDM"].EnterLong(OrderType.MarketOnClose)` and `hedge["UVXY"].TargetNotional(amount, tolerance: .05m)` replace its custom sleeve and position-management helpers. `WarmupBars = 91` preserves its entry warmup; the source's separate 90-bar signal condition remains explicit.

The API refactor reproduced all 2,637 fills, all order events and the complete summary exactly on the comparison dataset below. Signal mathematics, source-specific calendar rules and index-file interpretation remain in the strategy.

## Execution

Calendar-known trades (payday, gold, bond, oil, and the reversion sleeve's month-end exit) are submitted one minute before the scheduled session close as day-only market-on-close orders. Sizing uses the preceding completed daily close. The session calendar supplies auction times, including early closes; it does not supply prices.

Price-dependent signals (equity/bond reversion entry, VIX hedge and basis, risk premia, and equity/bond pair) use completed daily bars and submit market orders for the next available open. A decision that needs today's close cannot obtain that same close under this model.

The strategy includes pending quantities when computing its intended position. Reversals replace outstanding intent with one net order from the actual holding to the desired holding, so a later exit cannot cancel an earlier unfilled reversal. All orders go through normal netting, costs, rejection, margin checks and notifications. Rejected orders do not create assumed holdings; subsequent signals use actual positions. There is no special end-of-run liquidation: pending orders are cancelled by the engine and remaining holdings are marked to the final observed prices.

The source's calendar-day counting, signal thresholds, fixed allocations, whole-unit sizing, risk-premia slot/asset ordering, midpoint pair signal and original CBOE snapshots remain. They were not tuned to recover the old profit. Natural gas remains disabled. Index snapshots have the source's stale tail; refreshing them changes the experiment.

## Same-data comparison

Verified locally on 13 September 2026 with adjusted Alpaca SIP OHLC, 1,471 daily observations from 2 November 2020 through 11 September 2026, and $17,000 starting capital. Both runs used identical ETF and index data and zero configured spread, commission, borrow and ordinary slippage. The old compatibility run additionally used its five-second Zorro extrapolation and cent rounding; the adapted run uses ordinary unrounded OHLC execution.

| Metric | Old compatibility mode | Native Citrus strategy |
| --- | ---: | ---: |
| Final marked equity | $23,334.31 | $22,995.39 |
| Net P&L | $6,334.31 | $5,995.39 |
| Total return | 37.26% | 35.27% |
| Maximum observed drawdown | 5.10% | 4.85% |
| Annualized daily Sharpe | 1.380 | 1.302 |
| Attributed fills | 2,671 | 2,637 |

Daily-return correlation is 0.9836. Final equity is $338.92 lower (1.45% of the old final equity; net profit is 5.35% lower). The native result includes unrealized P&L on open final holdings; the old run forced them closed. This demonstrates similar behaviour on this history, not exact fills or a prediction of future performance. Standard spread/slippage/commission settings remain available for cost assumptions.

The former $38,381.34 parity figure used a different March 2010–September 2026 Zorro T6 dataset and compatibility execution. It is not the benchmark for this shorter Alpaca run.

## Running

Run `backtest examples/ZorroPortfolio Comparison` for the fixed comparison dates, or `backtest examples/ZorroPortfolio` for the available history. Both use native Citrus fills. The comparison's `Results/Comparison` directory is overwritten on reruns.

Use adjusted daily histories for SCHB, GLDM, TLT, UGA, UVXY, VXZ, SVXY and VIXY in the main historical cache. The missing ETFs were downloaded and installed there for this comparison; BOIL is also available for the separate seasonality example. The strategy loads VIX and VIX3M from its adjacent `Data` directory. Keep index and ETF coverage aligned when extending the analysis.

The regression suite compiles this strategy and checks auction timing, preceding-close sizing, next-open signals, reversals, repeatability and unchanged pre-close decisions after an unseen close is altered.
