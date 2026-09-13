# Seasonality and risk premia

This strategy ports four sleeves from the supplied Zorro source:

- `PaydaySeason`: $810 of SCHB on trading sessions 8 and 16, exited on session 12 and month end.
- `BondSeason`: $2,608 of TLT long for the final seven trading sessions, then short through session 7 of the next month.
- `EqBondReversion`: $3,018 in whichever of SCHB or TLT has underperformed from the first session's open through session 14, exited at month end.
- `RiskPremia`: a $3,000 SCHB/TLT/GLDM portfolio targeting 5% annualized volatility per asset over 90 daily returns, scaled to no more than 100% total exposure and rebalanced at 10% tracking error.

The default backtest uses $10,000 so the source allocations correspond to substrategy capital weights; $564 remains unallocated. It requires daily SCHB, TLT, and GLDM history plus complete US exchange-calendar months in the main Citrus historical-data cache.

The port uses completed data only. Calendar-known payday and Treasury orders are submitted one session early for the intended closing auction. The equity-bond signal becomes known after session 14 closes and therefore fills at the next session's open. Risk-premia changes also fill at the next open.

The supplied risk-premia loop increments its index twice and later refers to `Assets` instead of `RPAssets`. This port implements the apparent intended basket—SCHB, TLT, and GLDM—and uses the named 10% risk-premia tracking-error constant rather than the generic helper's hard-coded 5% threshold.

The `Downloads` directory contains matching Alpaca SIP requests for all three instruments. Each request writes through the configured main historical-data cache.
