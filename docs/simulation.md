# Simulation rules, version 1

## Event timing

The engine merges instrument event times into one UTC timeline. At each time it accrues net equity borrow since the previous event, marks completed bars and evaluates their limit/closing orders, applies funding marks/payments, checks maintenance margin, delivers notifications, exposes completed bars, invokes scheduled callbacks, and finally processes new bar opens and their orders. Opening marks are not visible to a decision on the preceding close. Instruments are sorted by their full keys for deterministic processing.

Market orders submitted on a completed bar can fill at the next bar's open, including when that open has the same timestamp as the previous close. Opening-auction orders require submission strictly before the opening timestamp. Closing-auction orders require submission strictly before the close. A closing-bar callback can therefore never trade at that close. Callback order notifications preserve acceptance, fills, and terminal status ordering. No parallel strategy execution occurs.

Limit orders use a complete subsequent bar. They fill at its open when favorable, otherwise at the limit if touched, and are timestamped at the bar's close because intrabar chronology is unknown. A limit submitted during a bar waits for a whole following bar. Orders cannot depend on a synthetic intrabar path. Market and auction orders fill fully; limit orders fill fully when eligible. There are no volume limits or partial external fills. Equity day orders expire at session close; perpetual day orders expire at UTC midnight. Pending orders cancel at run end.

## Netting and costs

Compatibility requires identical instrument, order type, time-in-force, and limit price. Evaluation at the same eligible event supplies the execution-event boundary. Compatible opposing orders cross FIFO by order ID; market/auction reference prices are the eligible open/close. Equal-limit opposing orders cross at the limit only when that price lies in the subsequent bar. Both virtual sides receive equal and opposite internal fills. Residual orders retain their origin and are routed once per compatible side/group.

Each residual submission consumes one deterministic random rejection draw, independent of other submissions. Pure internal crosses consume none. A residual rejection leaves completed internal fills intact and rejects only remaining external quantity. This can produce fills followed by a rejected terminal order update; fills are authoritative for executed quantity.

External buy prices add half the configured spread plus slippage, and sells subtract them, in basis points. Limit prices cap the result. Commission equals a fixed amount plus an amount per absolute unit. Allocate it proportionally to residual order sizes; assign the final arithmetic remainder to the last contributor. Internal fills have no external fees or slippage. Reproducibility requires the same engine/runtime, event order, and seed.

## Portfolio accounting

Equities exchange cash for shares, support long and short quantities, and use average cost for realized P&L. Linear perpetuals post realized P&L and fees to cash; unrealized P&L is quantity times mark-minus-entry. This is a common settlement-currency model: imported instruments must share the configured account's currency (USD or USD-equivalent stablecoin). There is no FX conversion or inverse-contract support.

Substrategy ledgers preserve virtual positions, including offsetting internal positions. Portfolio equity and cash aggregate these ledgers plus unallocated cash. Perpetual virtual realized/unrealized splits can differ from a net venue account; equity and net quantity are the comparison invariants. End-of-run reconciliation independently checks starting capital plus all attributed P&L against final equity. Per-substrategy capital is used for rebalance targets, while margin is shared at portfolio level.

Initial and maintenance requirements are absolute net instrument exposure times configured fractions. A risk-increasing trade is rejected if estimated post-cost equity is below initial margin. Reductions may proceed; turning through zero is checked as increasing risk. Equity short-availability applies to the physical net position. Borrow is charged on net short equity notional with ACT/365 elapsed time, allocated proportionally among virtual short owners. There is no interest on cash or short proceeds.

Funding events debit positive positions and credit negative positions by quantity × supplied mark × funding rate. They also supply observable marks for margin checks. Absent imported/generated funding events mean zero funding; the Hyperliquid downloader checks hourly rate coverage. Funding rates can be negative.

A maintenance breach cancels open orders and flattens the portfolio at observable marks with configured trading costs. Opposing virtual positions cross internally first; residuals close externally. Forced liquidation is guaranteed and does not use probabilistic rejection. It may leave negative cash after a gap. The engine observes opens, closes, and explicit funding marks, not intrabar highs/lows as a sequenced path, and cannot model an exchange's intrabar liquidation or liquidation auctions exactly.

## Adjusted equity prices

Equity history uses consistently provider-adjusted open, high, low, and close prices. Execution and valuation retain the OHLC timing rules above. Provider adjustments are reflected in price returns; the engine does not separately credit dividends, rescale holdings or limits, convert merger positions, or retire symbols. Quantities change only through fills. These are adjusted price units, not historical share counts or cash-dividend accounting.

Imports must supply adjusted history; Citrus cannot infer the adjustment basis from numeric bars. Corporate-action fields are rejected. Replace raw history with provider-adjusted prices. Keep each symbol's history on one adjustment snapshot: to extend or refresh adjusted equity data, move its existing cache file outside the cache and download the full required range. Provider adjustments do not model merger proceeds, delisting losses, or survivorship bias.
