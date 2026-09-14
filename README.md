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

These commands work on Windows, macOS, and Linux. The automated test executable returns a nonzero exit code on failure. It uses no external test packages or network calls. `dotnet test` is not the test entry point. Strategies are compiled with the application; there is no runtime compiler or external strategy loader. The SDK is required to build; the .NET 10 runtime can run the built CLI.

## Windows desktop workbench

The GUI uses WPF with XAML menus, a workspace tree, resizable panes, tabbed configuration and results views, sortable result tables, and a ScottPlot WPF equity chart with UTC dates, mouse pan/zoom, and right-click image export. The engine and CLI remain cross-platform; the desktop application requires Windows 10 version 2004 or later and the .NET 10 Desktop Runtime. Install the .NET 10 SDK to build it.

```sh
dotnet restore Citrus.slnx --configfile NuGet.Config
dotnet build Citrus.slnx -c Release --no-restore
dotnet run --project src/Citrus.Desktop -c Release --no-build --no-restore
```

The workbench lists runnable classes built into Citrus.Strategies. Select a strategy to load its saved settings, then press **F5** to run. **Backtest → Select backtest** lists JSON files beside the current configuration that select the same strategy. **Copy backtest** creates another standalone configuration. **Save configuration** (Ctrl+S) saves settings; **Validate** (F6) reads and validates the selected strategy's declarations.

**Open in IDE** opens Citrus.slnx when running from a checkout. Rebuild Citrus and restart the application after changing strategy code. **Refresh** refreshes the list from the running build; it does not compile or reload code. **Create example** creates a standalone configuration for the built-in DemoHold strategy and synthetic BTC history in the main cache.

Run the Windows integration check with dotnet run --project src/Citrus.Desktop -c Release --no-build --no-restore -- --smoke-test artifacts/desktop-smoke. It verifies built-in discovery, configuration, background execution, result bindings and WPF rendering.

## Strategy development

Strategies live in this repository and the main `Citrus.slnx` solution. Edit `src/Citrus.Strategies/ZorroPortfolio.cs` or add another class to that project. Strategy code references `Citrus.Trading`; the desktop and CLI reference that project through `Citrus.Engine` and execute its built-in types. There is no strategy submodule or separate repository to update.

Open `Citrus.slnx` in Visual Studio or Rider and edit the strategies in `Citrus.Strategies`. Use the desktop or CLI to execute backtests; no additional runner or strategy test project is required.

Launch the workbench with `dotnet run --project src/Citrus.Desktop -c Release` and select a strategy from the list. Default, Demo and Comparison configurations live in `Backtests/`. Installed desktops without a checkout use a per-user settings directory. Rebuild and restart after editing source; F5 runs the selected built-in strategy. Results go to `Results/<backtest-name>` and are excluded from Git.

```powershell
# Run ZorroPortfolio using the configured historical cache.
dotnet run --project src/Citrus.Cli -c Release -- backtest Backtests/Default.json
```

Default requires the portfolio ETF history in the configured cache. Demo requires BTC history; use the existing CLI data-generation command for synthetic data if needed. Commit strategy changes, backtest configurations and any engine changes together in this repository. The previously created standalone strategy repository is superseded; it is not required for this workflow.

### Settings in strategy code

Declare authoritative run settings in `Configure`. For `DailyStrategy` and `InstrumentStrategy`, override the protected method; direct `IStrategy` implementations use `public void Configure(StrategyOptions options)`.

```csharp
protected override void Configure(StrategyOptions options)
{
    options.InitialCash = 17_000m;
    options.Interval = BarInterval.Daily;
    options.AnnualBorrowRate = 0m;
    options.ShortsAvailable = true;
}
```

Only explicitly assigned properties override JSON and GUI settings. Assigning zero, false, or null is explicit too: for example, `options.Start = null` requires all available starting history even if JSON specifies a start date. Unassigned settings remain configurable per backtest. The available declarations cover start/end dates, interval, capital, rejection seed, risk-free rate, commissions, spread, slippage, rejection probability, borrowing, short availability, and equity/perpetual margins. Strategy-specific thresholds and allocations remain ordinary C# fields.

The GUI reads built-in declarations when opening a configuration, displays effective values, and disables the fields controlled by C#. Rebuild and restart after source changes, then use **Validate** to refresh; **Run** always reloads the declarations. Strategy selection loads the matching declarations. Configuration occurs before cache selection, account creation, or `OnStart`/`Initialize`. Keep `Configure` deterministic and independent of market/context state. Declarations are frozen once per loaded strategy instance.

CLI runs, GUI runs, and direct engine calls apply the same precedence rules. Reports record the effective configuration and declared options. Paths, strategy selection, output locations, historical-cache locations and credentials remain outside strategy options. ZorroPortfolio defines its $17,000 capital, daily interval, zero borrowing and short availability in C#; its dates remain per-backtest settings.

A standalone JSON configuration selects a built-in strategy by its full type name:

```json
{
  "schemaVersion": 1,
  "strategyType": "ZorroPortfolio",
  "output": "../Results/Hold"
}
```

Output paths are relative to the configuration file, regardless of the directory name. There is no special strategy-folder layout. Each strategy must be a public, concrete IStrategy with a public parameterless constructor in Citrus.Strategies. Use the CLI strategies command to list available names. Add dependencies and required content to Citrus.Strategies at build time. Loose source files, external projects, DLL selection, and strategy/reference/solution path settings are no longer supported; old JSON fields are rejected. Move strategy code into Citrus.Strategies and replace those fields with strategyType.

Results contain the effective configuration in `run.json`, the selected market data in `historical-data.json`, and build/input hashes in `manifest.json` as an audit record. Backtests always read from the configured historical cache. Repeatability requires unchanged strategy code, data, engine/runtime, external inputs and deterministic strategy behavior.

To edit the interface visually, open `src/Citrus.Desktop/MainWindow.xaml` in Visual Studio's XAML Designer after restoring and building the solution. Each window and reusable panel has its own `.xaml` layout and matching `.xaml.cs` behavior file. See [desktop development and designer guidance](docs/desktop-development.md).

## Global settings

Open **Data → Historical data** to download Alpaca equities or Hyperliquid perpetual history at hourly or daily intervals. Choose a symbol, venue, and UTC date range; the end date is exclusive. Alpaca uses Global settings credentials and the selected IEX/SIP feed. Downloads fetch missing history through the existing validated cache and can be cancelled.

The default library is `%LOCALAPPDATA%\Citrus\HistoricalData`. Set the main cache in **Global settings**; the `CITRUS_HISTORICAL_DATA` environment variable overrides it for automation. **Refresh** lists each JSON dataset's instruments, coverage bounds, bar count, size and structural validation status. Bounds do not guarantee gap-free coverage. Select a dataset to inspect its provider notes, export a normalized copy, or delete it after confirmation. Do not run another cache writer against the same folder while downloading.

Open **Settings → Global settings** in the Windows workbench to edit Alpaca API credentials and choose the main historical cache. **Save** creates or replaces the per-user file; **Cancel** discards edits. Credentials are masked by default and can be revealed explicitly. Clear a credential and save to remove its stored value.

Both the desktop and CLI use `Citrus/config.json` under the operating system's application-data directory (`%APPDATA%\Citrus\config.json` on Windows). The dialog displays the full path. A missing file uses empty defaults; malformed or unsupported files produce an error and are not overwritten automatically. Credentials are stored as plain text, so keep this file private and outside source control. Global settings are not included in run configurations or result exports.

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

Rerunning a backtest overwrites its result files in the configured output directory. Data commands also overwrite their output file, so the example commands can be rerun as written. Other files in the result directory are retained. Configuration paths resolve relative to the configuration file; data command output paths resolve relative to the working directory. JSON configuration rejects unknown properties so spelling errors are visible.

The Demo backtest runs `Citrus.Strategies.DemoHold` against seeded synthetic BTC prices and records the built-in strategy identity in its audit manifest. Generated prices have zero funding unless supplementary funding events are added. The regression suite separately checks equity session timing, weekends and daylight-saving transitions.

## Concise strategy API

For daily equity portfolios, derive from `DailyStrategy`. Create accounts in `Initialize`, and write rules in `OnClose` or `BeforeClose`. Each strategy runs against a single market. The dataset supplies that market's session calendar, which drives callbacks independently of any instrument. The base class handles symbol binding, session scheduling and callback dispatch. For example:

```csharp
using System;
using Citrus.Trading;

public sealed class ThursdayGold : DailyStrategy
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

Daily strategies can call `TradingDayOfMonth()` for the current session's one-based ordinal and `TradingDaysInMonth()` for its month's session count. These use actual market sessions, excluding exchange holidays, and do not depend on an instrument's bars. `TradingDay(offset)` provides the session date, ordinal and month-end flag. Supply complete calendar months even when the backtest covers only part of a month. Dates default to New York; override `ExchangeTimeZone` for another market. Counts require an opened session; before the first calendar session, `TradingDay()` returns null and the count functions throw.

The public `TradingCalendar` provides the same functions for any supplied time and exchange zone. `InstrumentContext.TradingDay()` delegates to this shared calendar. ZorroPortfolio uses these market-session counts instead of its former weekday approximation, so entries around exchange holidays can change.
`OnClose` runs once at each market session close within the run, after all bars closing at that time are available in history; market orders fill at a subsequent open. `BeforeClose` runs one minute before each supplied session close, including early closes, and can submit an explicit `MarketOnClose` order (day-only by default). Neither hook can see an unfinished bar. `Time` and `Date` are UTC. `DailyStrategy` requires daily equity session bars; for other intervals or instruments use the APIs below. Optional protected `OnFill`, `OnOrderUpdate`, `OnScheduled` and `OnStop` hooks retain notifications and access to `Context`. Reset strategy-owned state in `Initialize` because a strategy instance can be reused.

Derive from `InstrumentStrategy` for one account/instrument. Its constructor registers capital and the host forwards only that instrument's bars to `OnBar(InstrumentContext market, Bar bar)`. For other multi-instrument workflows, use `IStrategy` and construct an `InstrumentContext` for each registered account/instrument during startup.

```csharp
var next = market.TradingDay(1);
if (next?.DayOfMonth is 8 or 16)
    market.BuyNotional(810m, OrderType.MarketOnClose, TimeInForce.Day);
```

`TradingDay()` returns the latest opened session; positive offsets advance sessions, negative offsets go backwards. Its `Date`, `DayOfMonth`, `DaysInMonth`, and `IsMonthEnd` use the exchange calendar (New York by default; a custom zone can be passed to `InstrumentContext`). It returns null outside calendar coverage or before the first session opens, and throws if no calendar exists. Future session times are available, but future prices never are.

`Buy(quantity)` and `Sell(quantity)` remain direct additional-unit orders. Both accept execution type, time-in-force, and an optional limit price. `BuyNotional` and `SellNotional` size additional orders from completed prices; `LotsForNotional` exposes the calculation and accepts a lot size (default one, or e.g. `0.001m` for fractional units). Missing history or an unaffordable lot produces no notional order. `Quantity` reads actual holdings and `Close` reads the latest completed price.

`ExitLong` and `ExitShort` target flat only when the projected holding is on the requested side. They replace matching pending intent with a delta from actual holdings, while preserving an existing exit or opposite-side intent. Repeated or opposite-side exit calls cannot cancel an already pending close. `TargetQuantity(0)` unconditionally targets flat. `CancelOrders` is also available explicitly. Orders use the existing execution, margin, costs, rejection, and attribution rules. For scheduling, portfolio access, or advanced orders, use `market.Context`; the original `IStrategy` API remains available.

## Market datasets

Backtests read the user-wide main historical cache, never a dataset path in the run JSON. `interval` is required by the model (daily by default); optional `start` and exclusive `end` fields select a slice. Identical overlapping bars are deduplicated; conflicting bars fail validation. Citrus combines matching cached files, validates interior coverage, and writes the exact assembled input to `historical-data.json` in the result directory before execution. Invalid cache files fail visibly. No provider requests occur during a backtest. Old dataset files with a `version` property remain readable; new files omit it. Remove obsolete `dataVersion` fields from run configurations and `request.version` from download configurations.

Prepare the main cache with **Data → Historical data**, or place normalized outputs from `data generate`, `data download`, or `data import` in the directory selected by Global settings. Strategies can observe and trade any instrument in the assembled cache slice, using exact, case-sensitive identities. Orders for instruments without cached bars fail with a missing-data error.

## Strategies

Add public, concrete `IStrategy` classes with public parameterless constructors to `Citrus.Strategies`. Declare dependencies in that project and rebuild Citrus. Select each strategy by its full type name in the run configuration.

```csharp
using System.Collections.Generic;
using Citrus.Trading;

public sealed class BuyAndHold : IStrategy
{
    public void OnStart(IStrategyContext c) => c.Register("hold", 1m);
    public void OnBar(IStrategyContext c, IReadOnlyList<Bar> bars)
    {
        if (c.History(bars[0].Instrument, 2).Count == 1)
            c.Buy("hold", bars[0].Instrument, 10);
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

These calls illustrate alternatives, not a sequence to submit together. All buy/sell functions take a **positive quantity in instrument units**, allow fractional units, and return an order ID. Selling can reduce a long or establish/increase a short; buying can cover a short. Each call places an additional order and does not automatically cancel existing orders. The optional `timeInForce` defaults to `GoodTillCancelled`. They use the same validation, netting, fills, margin checks, and attribution as `Submit(new OrderRequest(...))`, which remains available for explicit signed-quantity requests. Equity opening/closing orders must be submitted before their execution event.

Register substrategies during startup with positive capital weights summing to at most one; remaining cash stays unallocated. These weights allocate starting capital, not order sizes. A fixed limit of 100 substrategies is enforced in code and cannot be configured. When percentage targeting is useful, `Rebalance` remains available: its dictionary describes the complete target portfolio for that substrategy, and omitted holdings target zero. Targets use its current equity and completed prices, account for pending quantities, and submit market orders. Batch all desired symbols into one dictionary for a multi-symbol rebalance.

Callbacks run sequentially. `History` exposes completed bars only and returns copies. `OnScheduled`, `OnOrderUpdate`, `OnFill`, and `OnStop` are optional. Schedules require future UTC times within the run to execute. Orders may be cancelled by ID. SMA and EMA return `null` until their warmup period is available. `Mode` exposes backtest/live context through the same contract; this release does not connect to a live trading venue.

Strategies run as **trusted local code**, with normal process permissions, libraries, network access, and filesystem access. They are not sandboxed. Use `ExternalData(key, fetch)` to fetch arbitrary binary API responses once per key during a run. Other I/O, wall-clock reads, and script-owned random sources cannot be monitored or guaranteed reproducible by this engine.

## Market data

Use the desktop historical-data dialog, or supply your own provider download configuration to the CLI (the paths below are placeholders).

```sh
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data download path/to/alpaca-download.json artifacts/alpaca-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data download path/to/hyperliquid-download.json artifacts/hyperliquid-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data import artifacts/alpaca-data.json artifacts/combined.json supplement.json
```

Configure Alpaca credentials through **Settings → Global settings**, or set `APCA_API_KEY_ID` and `APCA_API_SECRET_KEY` in the environment. Keep credentials out of run and download JSON files; the private per-user global file is the only configuration intended to store them. Hyperliquid public historical data requires no key. Download commands are opt-in network operations; the normal tests do not invoke them. Use a date range within available completed history.

Alpaca supplies exchange calendar sessions, adjusted minute bars (`adjustment=all`) aggregated into regular-session hourly/daily bars. Hourly buckets start at the session open, with a shorter final bucket where necessary. Missing no-trade minutes are not synthesized, but every expected hourly/daily bucket must contain data. IEX is the default feed; configure `feed` for your account's entitlement. Some instruments have entire sessions without IEX bars. These gaps fail coverage validation and the error lists the missing dates; an entitled SIP feed or imported history may supply the missing coverage. The calendar includes holidays, daylight-saving changes, and early closes. User-imported equity datasets must provide explicit UTC sessions and correct opening/closing flags.

Hyperliquid downloads hourly/daily candles and paginated funding rates. Its API supplies only the latest 5,000 candles; missing history fails with a coverage error. Import older data and merge datasets instead of silently shortening the requested period. Its funding history does not include historical mark prices: the adapter uses the latest completed candle close, or the first open at dataset start. This approximation is recorded in dataset notes and the manifest; import funding events with explicit historical marks for exact funding notionals.

The JSON dataset format is defined by `MarketDataset` in `Citrus.Data`. It contains `provider`, `interval`, `bars`, `sessions`, `funding`, and `notes`. Generated files are complete examples. Supplement files may contain only events and an empty bar array; import validates the combined dataset. Duplicate bars/events fail. Equity imports must supply consistently adjusted OHLC prices, including an adjusted close; raw prices are unsupported. Corporate-action fields are rejected. Existing raw equity history must be replaced with adjusted prices.

Historical data is looked up by **symbol and interval**, case-insensitively. Provider/feed, venue and asset class remain provenance and simulation metadata, not separate lookup keys. Strategies can bind `new InstrumentContext(context, "account", "SCHB")` or call `context.History("SCHB", 90)` without specifying a venue. Legacy venue-qualified requests also resolve by symbol, with asset-class checks. Missing symbols fail explicitly instead of silently remaining in warmup. Conflicting instrument definitions for one symbol are rejected; use distinct symbols for genuinely different instruments.

New cache entries use readable names such as `symbol-SCHB-1440.json`; existing hashed files are discovered and reused without renaming or redownloading. Complete existing snapshots are reused. Perpetual history fetches only missing ranges. Adjusted equity history cannot be incrementally extended because later adjustments can rewrite earlier prices: move its cache file outside the cache, then download the full desired range as one snapshot. Repeat this refresh when updated provider adjustments are needed. A different provider can reuse complete cached history, but cannot silently append to another source's history; explicitly replace it or extend using the original source. Multiple cache files for the same symbol/interval must be consolidated before downloading. Writes replace cache files atomically. Cache use is single-writer; do not run concurrent downloads into the same cache. The engine validates interior coverage for each instrument's supplied range; imports do not establish unavailable history outside those boundaries.

Citrus uses provider-adjusted prices and does not fetch or simulate dividends, splits, mergers, symbol changes, or delistings. Adjusted series do not establish merger settlement or eliminate survivorship bias. See [Alpaca historical bars](https://docs.alpaca.markets/us/reference/stockbars) and [Hyperliquid info API](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint).

## Results

Each successful output directory contains orders, fills, positions, cash movements/costs, equity observations, substrategy equity attribution, final instrument P&L attribution, and a performance summary. All tabular exports have JSON and/or CSV representations; timestamps use UTC and CSV numbers use invariant formatting. Internal fills are explicitly marked. Per-instrument net P&L includes realized/unrealized P&L, fees, borrow, and funding; its sum plus starting capital is checked against portfolio equity.

The summary reports total return, maximum observed drawdown, and sample-standard-deviation Sharpe on UTC daily closing equity. Annualization uses 252 observations for equities-only runs and 365 if perpetuals are included, with risk-free rate defaulting to zero. Undefined returns or Sharpe are `null`; symbol-level returns are not manufactured without an allocated symbol capital base. Execution cost is already embedded in fill prices and is separately reported as a diagnostic, never deducted twice.

`manifest.json` includes configuration, seed, strategy/data/dependency/component hashes, the calendar hash, runtime and engine versions, provider metadata, and repeatability limitations. Run the same configuration again to repeat a result; repeatability assumes unchanged inputs and engine/runtime plus a deterministic trusted strategy. There are no checkpoints; interrupted runs restart. Only completed runs are exported.

## Design and verification

Read [simulation rules](docs/simulation.md) and [requirement traceability](docs/requirements-traceability.md) before interpreting results. The implementation is organised around trading and backtesting responsibilities:

- **Citrus.Trading** contains instruments, market data events, orders, portfolio records, and the strategy API. Its folders are Instruments, MarketData, Orders, Portfolio, and Strategies; all public types share the `Citrus.Trading` namespace so strategies need one import. This assembly has no dependency on storage, Roslyn, or simulation.
- **Citrus.Data** owns datasets, providers, validation, caching, and JSON persistence.
- **Citrus.Simulation** owns order execution, portfolio accounting, and `SimulationOptions`.
- **Citrus.Engine** owns `RunConfiguration`, built-in strategy selection, backtest coordination, and reports. `BacktestRunner.Run` shares strategy loading, effective configuration, cache selection, execution and export between the CLI and desktop. It returns a `CompletedBacktest` containing results, portfolio metrics and the absolute output directory; the desktop dispatches this synchronous work to a background thread.
- **Citrus.Cli** handles commands and paths; **Citrus.Tests** verifies behaviour.

Live adapters and optimisation/walk-forward remain future releases. **Citrus.Desktop** provides the initial Windows GUI.

Run benchmarks explicitly:

```sh
dotnet run --project tests/Citrus.Tests -c Release --no-build --no-restore -- --benchmark
```

The benchmark uses 100 symbols, 20 substrategies, SMA calculations, and 2,000 initial orders. It measures engine execution, excluding data generation. Initial Windows/.NET 10 measurements: 365,000 daily bars in 15.978 seconds (205 MiB process peak), and 1,752,000 hourly bars in 70.822 seconds (764 MiB process peak). Memory is the process high-water mark, including dataset generation; these are observations, not hardware-independent limits.

GitHub Actions is configured to build, run offline tests, and generate data, validate and execute the Demo backtest on Windows, macOS, and Linux. Local verification on this workspace is Windows only; the CI matrix must run on your remote repository to verify the other hosts.
