# Citrus

Citrus is a .NET 10 backtesting engine for trusted C# strategies, with strategies built into Citrus.Strategies as part of the main solution. It runs on Windows, macOS, and Linux, with simulated equities and linear perpetuals, portfolio order netting, separate substrategy accounting, and JSON/CSV results.

## Build and verify

Install the .NET 10 SDK, then run from the repository root:

```sh
dotnet restore src/Citrus.Cli/Citrus.Cli.csproj --configfile NuGet.Config
dotnet restore tests/Citrus.Tests/Citrus.Tests.csproj --configfile NuGet.Config
dotnet build src/Citrus.Cli/Citrus.Cli.csproj --no-restore -c Release
dotnet build tests/Citrus.Tests/Citrus.Tests.csproj --no-restore -c Release
dotnet run --project tests/Citrus.Tests -c Release --no-build --no-restore
```

Tests run offline without external test packages and return a nonzero exit code on failure. Use the executable above, not `dotnet test`. Building requires the SDK; the built CLI requires the .NET 10 runtime.

## Windows desktop workbench

The desktop provides strategy selection, configuration, sortable results, and a ScottPlot equity chart with UTC dates, pan/zoom, and image export. It requires Windows 10 version 2004 or later and the .NET 10 Desktop Runtime.

```sh
dotnet restore Citrus.slnx --configfile NuGet.Config
dotnet build Citrus.slnx -c Release --no-restore
dotnet run --project src/Citrus.Desktop -c Release --no-build --no-restore
```

Select a strategy, then press **F5** to run. **Backtest → Select backtest** lists matching JSON configurations; **Copy backtest** creates another. **Save configuration** (Ctrl+S) saves settings; **Validate** (F6) checks strategy declarations.

**Open in IDE** opens the checkout solution. After source changes, rebuild and restart Citrus; **Refresh** only lists the running build. **Create example** creates DemoHold settings and synthetic BTC history in the main cache.

See [desktop development](docs/desktop-development.md) for XAML Designer guidance and the Windows smoke test.

## Strategy development

Add a public, concrete `Strategy` with a public parameterless constructor to `src/Citrus.Strategies`. Edit it in `Citrus.slnx` using Visual Studio or Rider, and declare dependencies and content in that project. Strategies reference `Citrus.Trading` and compile with the application.

Select the built-in type in the workbench or run its JSON configuration through the CLI. `Backtests/` contains Default, Demo, and Comparison settings. Installed desktops without a checkout use per-user settings. Commit strategy and configuration changes together; generated `Results/` files are ignored by Git.

```powershell
# Run ZorroPortfolio using the configured historical cache.
dotnet run --project src/Citrus.Cli -c Release -- backtest Backtests/Default.json
```

Default needs the portfolio ETF history in the configured cache. Demo needs BTC history; the [CLI example](#cli-examples) generates it offline.

### Settings in strategy code

Declare authoritative run settings by overriding the protected `Configure` method on `Strategy`. Citrus supports end-of-day trading only.

```csharp
protected override void Configure(StrategyOptions options)
{
    options.InitialCash = 17_000m;
    options.AnnualBorrowRate = 0m;
    options.ShortsAvailable = true;
}
```

Explicit assignments override JSON and GUI settings, including zero, false, and null. For example, `options.Start = null` removes a configured start bound. Unassigned settings remain editable. Options cover dates, capital, seed, risk-free rate, execution costs, rejection, borrowing, short availability, and margins. Keep strategy thresholds and allocations in ordinary C# fields.

The GUI loads effective values and locks C#-controlled fields when opening, validating, or running a backtest. `Configure` runs before data selection and `Initialize`; keep it deterministic and independent of market/context state. Declarations are frozen per strategy instance.

The CLI, GUI, and direct engine calls use this precedence. Strategy selection, paths, and credentials stay outside strategy options. ZorroPortfolio declares $17,000 capital, zero borrowing, and short availability; dates remain per-backtest settings.

A standalone JSON configuration selects a built-in strategy by its full type name:

```json
{
  "schemaVersion": 1,
  "strategyType": "ZorroPortfolio",
  "output": "../Results/Hold"
}
```

Select the full type name reported by the CLI `strategies` command. Output paths resolve relative to the configuration file. Unknown JSON fields are rejected; see [existing files](#existing-files) for migration notes.

### Accounts and daily callbacks

Create accounts in `Initialize` and trade in `OnClose` or `BeforeClose`. Each strategy uses one market calendar, independent of any single instrument:

```csharp
using System;
using Citrus.Trading;

public sealed class ThursdayGold : Strategy
{
    private StrategyAccount gold = null!;

    protected override void Initialize()
        => gold = Account("GoldSeason", 1256, "GLDM");

    protected override void BeforeClose()
    {
        var market = gold["GLDM"];
        if (Date.DayOfWeek == DayOfWeek.Thursday)
            market.EnterLong(OrderType.MarketOnClose);
        else
            market.ExitLong(OrderType.MarketOnClose);
    }
}
```

`Account` reserves the specified amount of starting capital and uses that fixed amount as each instrument's default entry notional; allocations must fit within starting cash. Account symbols are case-insensitive. `EnterLong`/`EnterShort` add whole lots on the same side or reverse an opposing projected holding with one net order. They use completed prices; they do not immediately change actual holdings. Repeated entries deliberately add exposure. Use `TargetQuantity` or `TargetNotional` for position targets instead. `TargetNotional(800, tolerance: .05m)` trades only when notional drift exceeds 5%, counting pending orders; a zero target flattens. Notional helpers accept a lot size, while `Buy` and `Sell` remain direct quantity operations.

Override `WarmupBars` to delay entry and nonzero notional-target helpers until that instrument has enough completed bars. Callbacks still run during warmup so indicators can initialize; exits and explicit quantity orders remain available. `market.History(count)`, `market.HasHistory(count)`, `market.Close` and `market.Midpoint` expose completed data without repeating context/instrument arguments.

`TradingDayOfMonth()` and `TradingDaysInMonth()` count actual exchange sessions, excluding exchange holidays and including early-close sessions. Supply complete calendar months even for a sliced backtest. Session dates use New York for equities and UTC for perpetuals; override `ExchangeTimeZone` for another market. Before the first session opens, `TradingDay()` returns null and the count helpers throw.

`TradingCalendar` supports the same queries at a supplied time and zone; `InstrumentContext.TradingDay()` uses it too.
`OnClose` runs once at each market session close within the run, after all bars closing at that time are available in history; market orders fill at a subsequent open. `BeforeClose` runs one minute before each supplied session close, including early closes, and can submit an explicit `MarketOnClose` order (day-only by default). Neither hook can see an unfinished bar. `Time` and `Date` are UTC. Equities require full-session bars and an explicit exchange calendar. Perpetuals use complete midnight-to-midnight UTC bars and a generated UTC calendar. Run the two market types separately. Optional protected `OnFill`, `OnOrderUpdate` and `OnStop` hooks retain notifications and access to `Context`. Reset strategy-owned state in `Initialize` because a strategy instance can be reused.

Use `Account` for portfolio sleeves, or bind an `InstrumentContext` after registering an account directly:

```csharp
var next = market.TradingDay(1);
if (next?.DayOfMonth is 8 or 16)
    market.BuyNotional(810m, OrderType.MarketOnClose, TimeInForce.Day);
```

`TradingDay(offset)` returns the latest opened session shifted by signed session count, including its date, ordinal, month total, and month-end flag. It returns null outside coverage and throws without a calendar. Instrument contexts accept a custom zone. Future session times are visible; future prices are not.

`Buy(quantity)` and `Sell(quantity)` remain direct additional-unit orders. Both accept execution type, time-in-force, and an optional limit price. `BuyNotional` and `SellNotional` size additional orders from completed prices; `LotsForNotional` exposes the calculation and accepts a lot size (default one, or e.g. `0.001m` for fractional units). Missing history or an unaffordable lot produces no notional order. `Quantity` reads actual holdings and `Close` reads the latest completed price.

`ExitLong`/`ExitShort` target flat only on the matching projected side. They replace pending intent with a delta from actual holdings, preserving an existing exit or opposite-side intent. Repeated exits cannot cancel a pending close. `TargetQuantity(0)` always targets flat; `CancelOrders` explicitly cancels matching orders. Use `market.Context` for advanced operations.

### Direct orders and portfolio access

For direct quantity orders, use `Context`:

```csharp
using System.Collections.Generic;
using Citrus.Trading;

public sealed class BuyAndHold : Strategy
{
    protected override void Initialize() => Context.Register("hold", 1m);
    protected override void OnClose()
    {
        foreach (var bar in CompletedBars)
            if (Context.History(bar.Instrument, 2).Count == 1)
                Context.Buy("hold", bar.Instrument, 10);
    }
}
```

Use quantity-based order functions directly; rebalancing is optional:

```csharp
long buyId = context.Buy("trend", instrument, 10);       // buy 10 shares/contracts
context.Sell("trend", instrument, 5);                   // sell 5; can also open a short
long limitId = context.BuyLimit("trend", instrument, 10, 95m);
context.SellLimit("trend", instrument, 5, 110m, TimeInForce.Day);
context.BuyOnOpen("trend", instrument, 10);             // equity opening auction
context.SellOnOpen("trend", instrument, 5);
context.BuyOnClose("trend", instrument, 10);            // equity closing auction
context.SellOnClose("trend", instrument, 5);
context.Cancel(limitId);                                // cancel by returned order ID
```

These are alternatives, not a sequence. Buy/sell helpers accept **positive quantities**, including fractional units, and return order IDs. Each places an additional order without cancelling existing orders; sells can open shorts and buys can cover them. Time-in-force defaults to `GoodTillCancelled`. `Submit(new OrderRequest(...))` accepts signed quantities. Auction orders must precede their execution event.

Register substrategies during startup with positive capital weights summing to at most one; remaining cash stays unallocated. These weights allocate starting capital, not order sizes. A fixed limit of 100 substrategies is enforced in code and cannot be configured. When percentage targeting is useful, `Rebalance` remains available: its dictionary describes the complete target portfolio for that substrategy, and omitted holdings target zero. Targets use its current equity and completed prices, account for pending quantities, and submit market orders. Batch all desired symbols into one dictionary for a multi-symbol rebalance.

Callbacks run sequentially; `History` returns copies of completed bars. SMA/EMA return null before warmup. `Mode` distinguishes backtest/live contexts, but live venue execution is not implemented.

Strategies are **trusted local code**, with process permissions and unrestricted library, network, and filesystem access. `ExternalData(key, fetch)` caches response bytes once per key per run. Other I/O, wall-clock reads, and strategy-owned randomness are untracked.

## Global settings

Use **Data → Historical data** to download daily Alpaca equities or Hyperliquid perpetuals. Choose a symbol, venue, and UTC range with an exclusive end. Alpaca uses the selected IEX/SIP feed. Downloads validate coverage and support cancellation.

The cache defaults to `%LOCALAPPDATA%\Citrus\HistoricalData`. Set it in **Settings → Global settings** or override it with `CITRUS_HISTORICAL_DATA`. The history dialog lists datasets, coverage bounds, structural validation, and provider notes, with export and delete actions. Bounds alone do not prove gap-free coverage.

**Global settings** also stores Alpaca credentials. Save persists changes; Cancel discards them. Credentials are masked, can be revealed, and can be cleared.

The CLI and desktop share `Citrus/config.json` in the OS application-data directory (`%APPDATA%\Citrus\config.json` on Windows). Missing files use defaults; invalid files fail without automatic replacement. Credentials are plain text: keep this file private and outside source control. It is excluded from result exports.

```json
{
  "schemaVersion": 1,
  "alpacaApiKeyId": "",
  "alpacaApiSecretKey": ""
}
```

Alpaca downloads read saved credentials on each invocation. Nonempty `APCA_API_KEY_ID` and `APCA_API_SECRET_KEY` environment variables override their respective saved fields. Hyperliquid requires no credentials.

## CLI examples

```sh
# Point automated/example runs at an isolated main cache (PowerShell: $env:CITRUS_HISTORICAL_DATA="artifacts/cache")
export CITRUS_HISTORICAL_DATA="artifacts/cache"
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data generate tests/Citrus.Tests/Fixtures/DemoGeneration.json artifacts/cache/demo-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- validate Backtests/Demo.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- backtest Backtests/Demo.json
```

Reruns overwrite matching result files and retain unrelated files. Data commands overwrite their output, resolving output paths from the working directory.

Demo runs `Citrus.Strategies.DemoHold` on seeded BTC prices. Generated history has zero funding unless supplemented.

## Market data

Backtests combine cached datasets without provider requests. Optional `start` and exclusive `end` select a slice; `interval` must be `{"name":"1d","minutes":1440}`. Identical overlapping bars are deduplicated; conflicts, invalid files, and interior gaps fail. The assembled input is captured as `historical-data.json` before execution.

Populate the cache through the history dialog or place normalized `data generate`, `data download`, or `data import` outputs there. Strategies can trade any instrument in the selected history; missing symbols fail explicitly.

### Providers and cache

Use the desktop historical-data dialog, or supply your own provider download configuration to the CLI (the paths below are placeholders).

```sh
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data download path/to/alpaca-download.json artifacts/alpaca-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data download path/to/hyperliquid-download.json artifacts/hyperliquid-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data import artifacts/alpaca-data.json artifacts/combined.json supplement.json
```

Downloads use [global settings](#global-settings) and require network access. Choose completed history within provider coverage.

Alpaca supplies exchange sessions and adjusted daily bars (`timeframe=1Day`, `adjustment=all`), mapped through daylight-saving changes and early closes. IEX is the default feed; select another feed according to entitlement. Missing bars fail with the affected dates. Imported equity bars require full sessions and both boundary flags. See [Alpaca bar rules](https://docs.alpaca.markets/us/docs/market-data-faq).

Hyperliquid downloads daily candles and paginated funding rates. Its API supplies only the latest 5,000 candles; missing history fails with a coverage error. Import older data and merge datasets instead of silently shortening the requested period. Its funding history does not include historical mark prices: the adapter uses the latest completed candle close, or the first open at dataset start. This approximation is recorded in dataset notes and the manifest; import funding events with explicit historical marks for exact funding notionals.

`MarketDataset` contains `provider`, `interval`, `bars`, `sessions`, `funding`, and `notes`. Generated files are examples. Supplements may contain events with no bars; validation applies to the combined import. Duplicate bars/events fail. Equity OHLC must be consistently adjusted; raw prices and corporate-action fields are unsupported.

History lookup is case-insensitive by **symbol and interval**. Provider/feed, venue, and asset class remain metadata. Bind by symbol with `new InstrumentContext(context, "account", "SCHB")` or `context.History("SCHB", 90)`. Instrument-based requests also resolve by symbol, with asset-class checks. Conflicting definitions fail; use distinct symbols for distinct instruments.

Complete snapshots are reused; perpetual downloads fetch missing ranges. To extend or refresh adjusted equities, move the existing cache file aside and download the full range as one snapshot: adjustments can rewrite earlier prices. Extend history with its original provider or explicitly replace it. Consolidate multiple files for one symbol/interval before downloading. Writes are atomic, but the cache requires one writer. Coverage checks cannot establish history beyond the supplied range.

Adjusted prices do not establish merger settlements or eliminate survivorship bias. See [simulation rules](docs/simulation.md#adjusted-equity-prices), [Alpaca historical bars](https://docs.alpaca.markets/us/reference/stockbars), and [Hyperliquid info API](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint).

### Existing files

For older configurations, move external strategy code and dependencies into `Citrus.Strategies`, select `strategyType`, and remove strategy/reference/solution paths, `dataVersion`, and `request.version`. Unknown fields fail validation. Dataset `version` fields remain readable but are omitted on write. Hashed cache filenames are still discovered and reused; new entries use names such as `symbol-SCHB-1440.json`. Replace raw equity history with adjusted prices.

## Results

`run.json` records effective settings; `historical-data.json` captures the selected input; `manifest.json` records build and input provenance.

Each successful output directory contains orders, fills, positions, cash movements/costs, equity observations, substrategy equity attribution, final instrument P&L attribution, and a performance summary. All tabular exports have JSON and/or CSV representations; timestamps use UTC and CSV numbers use invariant formatting. Internal fills are explicitly marked. Per-instrument net P&L includes realized/unrealized P&L, fees, borrow, and funding; its sum plus starting capital is checked against portfolio equity.

The summary reports total return, maximum observed drawdown, and sample-standard-deviation Sharpe on UTC daily closing equity. Annualization uses 252 observations for equities-only runs and 365 if perpetuals are included, with risk-free rate defaulting to zero. Undefined returns or Sharpe are `null`; symbol-level returns are not manufactured without an allocated symbol capital base. Execution cost is already embedded in fill prices and is separately reported as a diagnostic, never deducted twice.

`manifest.json` includes configuration, seed, strategy/data/dependency/component hashes, the calendar hash, runtime and engine versions, provider metadata, and repeatability limitations. Run the same configuration again to repeat a result; repeatability assumes unchanged inputs and engine/runtime plus a deterministic trusted strategy. There are no checkpoints; interrupted runs restart. Only completed runs are exported.

## Design and verification

Read [simulation rules](docs/simulation.md) and [requirement traceability](docs/requirements-traceability.md) before interpreting results. The implementation is organised around trading and backtesting responsibilities:

- **Citrus.Trading** contains instruments, market data events, orders, portfolio records, and the strategy API. Its folders are Instruments, MarketData, Orders, Portfolio, and Strategies; all public types share the `Citrus.Trading` namespace so strategies need one import. This assembly has no dependency on storage, Roslyn, or simulation.
- **Citrus.Data** owns datasets, providers, validation, caching, and JSON persistence.
- **Citrus.Simulation** owns order execution, portfolio accounting, and `SimulationOptions`.
- **Citrus.Engine** owns `RunConfiguration`, built-in strategy selection, backtest coordination, and reports. `BacktestRunner.Run` shares strategy loading, effective configuration, cache selection, execution and export between the CLI and desktop. `Reports.Export` returns the portfolio metrics written to `summary.json`. `BacktestRunner.Run` reuses those metrics in a `CompletedBacktest` containing results and the absolute output directory. The desktop dispatches this synchronous work to a background thread.
- **Citrus.Cli** handles commands and paths; **Citrus.Tests** verifies behaviour.

Live adapters and optimisation/walk-forward remain future releases. **Citrus.Desktop** provides the initial Windows GUI.

Run benchmarks explicitly:

```sh
dotnet run --project tests/Citrus.Tests -c Release --no-build --no-restore -- --benchmark
```

The benchmark uses 100 symbols, 20 substrategies, SMA calculations, and 2,000 initial orders. It measures engine execution, excluding data generation. Run it locally for current timing and process peak-memory measurements.

GitHub Actions builds, tests, and runs the Demo on Windows, macOS, and Linux; Windows also runs the desktop smoke suite. Check CI results for platform verification.
