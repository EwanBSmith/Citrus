# Seasonality and risk premia

For results matching the supplied Zorro trade log, use [ZorroPortfolio](../ZorroPortfolio/README.md). This example keeps the cleaned-up ten-sleeve interpretation; it is not the Zorro-parity baseline.

This strategy ports all ten sleeves from the supplied Zorro source, including the originally commented-out natural-gas short:

- `PaydaySeason`: $810 of SCHB on trading sessions 8 and 16, exited on session 12 and month end.
- `BondSeason`: $2,608 of TLT long for the final seven trading sessions, then short through session 7 of the next month.
- `EqBondReversion`: $3,018 in whichever of SCHB or TLT has underperformed from the first session's open through session 14, exited at month end.
- `RiskPremia`: a $3,000 SCHB/TLT/GLDM portfolio targeting 5% annualized volatility per asset over 90 daily returns, scaled to no more than 100% total exposure and rebalanced at 10% tracking error.
- `GoldSeason`: $1,256 GLDM long at Thursday's close, exited at the following session's close.
- `OilSeason`: $1,160 UGA long five weekdays before a US exchange holiday, exited two weekdays before; short one weekday before and covered one weekday after. Weekday offsets exclude weekends but include holidays. Weekday exchange closures are the user-approved replacement for the missing `holidays.c`.
- `VIXHedge`: $2,094 divided between short UVXY (1/3.8) and long VXZ (2.8/3.8).
- `VIXBasis`: $800 in SVXY below the source's VIX/VIX3M threshold, VIXY above its upper threshold, or cash between thresholds. Uses the source's 60-observation VIX level population standard deviation times sqrt(252) and piecewise thresholds.
- `EqBondPair`: $800 alternating between SCHB and TLT on crossings of log(SCHB/TLT) and its five-session EMA (SMA-seeded by Citrus).
- `ShortGas`: $1,000 BOIL short from November through February, covered in March.

All ten use the same `Sleeve` structure: a registered account name, fixed allocation, and instrument contexts. Named sleeve fields are initialized together in `OnStart`, and each sleeve has its own decision method. The shared sleeve collection supplies the required-instrument coverage check.

The default backtest uses $17,000: allocations total $16,546 with $454 unallocated. Dollar targets do not compound. Changing starting capital changes attributed account cash but not fixed trading notionals. It requires daily SCHB, TLT, GLDM, UGA, UVXY, VXZ, SVXY, VIXY, and BOIL history plus complete US exchange-calendar months in the main Citrus historical-data cache. Missing daily ETF closes fail visibly. Hourly input is rejected. Oil events outside calendar coverage are skipped, so supply calendar padding for holiday offsets at run boundaries.

The port uses completed data only. Calendar-known payday and Treasury orders are submitted one session early for the intended closing auction. The equity-bond signal becomes known after session 14 closes and therefore fills at the next session's open. Risk-premia changes also fill at the next open.

Gold, oil, and gas also submit for the next scheduled closing auction. VIX hedge, VIX basis, and equity-bond pair orders fill at the next open. VIX basis conservatively uses the previous exchange session's index closes, because the final Cboe closing values may not be available at the ETF closing callback. Missing index dates fail visibly. Sixty archived observations are required. Added notional rebalances use the source helper's actual 5% drift rule, including VIX basis (the source's declared 20% constant is unused). Whole shares are used; reverse splits can leave fractional holdings handled by the ledger. No borrow-availability history is modeled; shorts are enabled and the default borrow charge remains zero.

Archived [Cboe historical index data](https://www.cboe.com/tradable-products/vix/vix-historical-data) is in `Data/VIX.csv` and `Data/VIX3M.csv`, downloaded on 2026-09-13 from `https://cdn.cboe.com/api/global/us_indices/daily_prices/VIX_History.csv` and the corresponding `VIX3M_History.csv`. Keep these files beside `Strategy.cs` when copying the strategy. Reads use `ExternalData` to cache bytes within a run; Citrus does not include external bytes in its result manifest, so these archives must be retained for reproducibility. The strategy never downloads during a backtest.

The existing raw-price history semantics remain: corporate actions affect holdings, but historical close series are not retrospectively adjusted for indicator calculations. Splits can therefore affect volatility and pair signals. These results are an implementation trial, not exact Zorro equivalence.

The supplied risk-premia loop increments its index twice and later refers to `Assets` instead of `RPAssets`. This port implements the apparent intended basket—SCHB, TLT, and GLDM—and uses the named 10% risk-premia tracking-error constant rather than the generic helper's hard-coded 5% threshold.

The `Downloads` directory contains matching Alpaca SIP requests for all nine instruments. Each request writes through the configured main historical-data cache. Run with `dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- backtest examples/SeasonalityRiskPremia`. Reports are retained in `Results/Default`. Existing tracked reports are updated; the local ignore rule prevents additional generated result files from being added accidentally.
