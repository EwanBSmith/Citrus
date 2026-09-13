# Citrus

Citrus is a .NET 10 backtesting engine for trusted C# strategy files. It runs on Windows, macOS, and Linux, with simulated equities and linear perpetuals, portfolio order netting, separate substrategy accounting, and JSON/CSV results.

## Build and verify

Install the .NET 10 SDK, then run from the repository root:

```sh
dotnet restore src/Citrus.Cli/Citrus.Cli.csproj --configfile NuGet.Config
dotnet restore tests/Citrus.Tests/Citrus.Tests.csproj --configfile NuGet.Config
dotnet build src/Citrus.Cli/Citrus.Cli.csproj --no-restore -c Release
dotnet build tests/Citrus.Tests/Citrus.Tests.csproj --no-restore -c Release
dotnet run --project tests/Citrus.Tests -c Release --no-build --no-restore
```

These commands work on Windows, macOS, and Linux. The automated test executable returns a nonzero exit code on failure. It uses no external test packages or network calls. `dotnet test` is not the test entry point. Strategy compilation uses the Microsoft.CodeAnalysis.CSharp NuGet package, copied into the application output. Package restore needs network access on the first build. The SDK is required to build; the .NET 10 runtime can run the built CLI.

## Windows desktop workbench

The GUI uses WPF with XAML menus, a workspace tree, resizable panes, tabbed editors, sortable result tables, and a ScottPlot WPF equity chart with UTC dates, mouse pan/zoom, and right-click image export. The engine and CLI remain cross-platform; the desktop application requires Windows 10 version 2004 or later and the .NET 10 Desktop Runtime. Install the .NET 10 SDK to build it.

```sh
dotnet restore Citrus.slnx --configfile NuGet.Config
dotnet build Citrus.slnx -c Release --no-restore
dotnet run --project src/Citrus.Desktop -c Release --no-build --no-restore
```

Choose **File → Create example** to create an offline strategy folder and seed example prices into the main cache, or **Open** a strategy folder. Each folder contains `Strategy.cs` and `Backtests/Default.json`. Select a named backtest from **Backtest → Select backtest**; **Backtest → Copy backtest** creates another configuration with its own results directory. Edit C# on the Strategy tab and settings in the Configuration form. **Save all** (`Ctrl+S`) saves both; **Validate** (`F6`) saves and compiles; **Run backtest** (`F5`) saves, executes in the background, and exports reports. Switching configurations prompts to save outstanding edits. The application also accepts a strategy folder or legacy run JSON as its first argument.

The Strategy tab uses [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) with Roslyn C# language services, bundled through NuGet. It provides syntax colours, line numbers, folding, indentation guides, matching and closing brackets, automatic indentation, undo/redo, completion for Citrus APIs and configured assemblies, hover information, and method signatures. Click the gutter to fold a block; **Edit** also offers collapse/expand commands. Editing works offline after restore, without a browser runtime or language server.

Compiler errors and warnings update after a short typing pause, with squiggles and a **Problems** list. Double-click a problem or press Enter on its row to navigate to the source. Live checks analyze unsaved text without saving or executing it. **Validate** additionally loads and constructs the strategy, checking the executable strategy contract. Edited assembly references resolve relative to the run file. Missing or invalid references appear as a code assistance error in the editor status strip.

| Editor command | Shortcut |
| --- | --- |
| Complete code; accept selected completion | `Ctrl+Space`; `Tab` or `Enter` |
| Method parameter information | `Ctrl+Shift+Space` |
| Find / replace | `Ctrl+F` / `Ctrl+H` |
| Next / previous match (wraps) | `F3` / `Shift+F3` |
| Go to line / definition in this file | `Ctrl+G` / `F12` |
| Format document | `Ctrl+Shift+F` |
| Toggle selected line comments | `Ctrl+/` |
| Indent / unindent selected lines | `Tab` / `Shift+Tab` |
| Undo / redo | `Ctrl+Z` / `Ctrl+Y` |
| Dismiss completion or information | `Esc` |

Find/replace supports match case, whole words, and regular expressions. Regex replacements support capture groups such as `$1`; literal mode inserts replacement text exactly. Replace all, formatting, and comment commands each form one undo step. Formatting uses four spaces and preserves LF or CRLF line endings. Source editing is locked during validation and execution; opening another source resets the undo history.

Configuration paths resolve relative to the strategy folder for named backtests, or to the run JSON for legacy standalone configurations. Numeric fields use a decimal point and preserve untouched precision. Optional date bounds accept ISO 8601 timestamps, such as `2024-01-01T00:00:00Z`; leave a field blank to remove its boundary. Strategies select the instruments they trade. A run selects an interval plus optional UTC bounds; Citrus assembles every matching instrument from the main historical cache without provider access.

Overview reports portfolio performance and equity. Result tabs expose orders, fills, positions, costs, equity (including substrategy balances), and instrument attribution; click a column to sort or use `Ctrl+C` to copy selected rows. Tables show the first 5,000 records; **Results folder** opens the complete JSON/CSV exports. Runs replace matching report files. Prior results are cleared when a new run starts so a failed run cannot appear successful.

Saved-result import and run cancellation are not yet provided. The window stays responsive during execution but must wait for the current operation before closing. Strategies remain trusted local code with normal process permissions.

Windows-only offline integration and rendering checks:

```sh
dotnet run --project src/Citrus.Desktop -c Release --no-build --no-restore -- --smoke-test artifacts/desktop-smoke
```

The smoke check writes its outcome and WPF layout PNGs to the specified folder, and returns a nonzero exit code on failure. It includes real Roslyn completion/diagnostic checks, AvalonEdit text input, replacement, undo, formatting, reference changes, settings masking/save/cancel, configuration precision, displayed result cells, and save/run integration. Normal, compact, and high-DPI rendering checks run on the WPF dispatcher. `Citrus.slnx` contains the complete product and is built as a solution on Windows. On macOS and Linux, build the CLI and test projects directly as shown above so the Windows desktop project is excluded.

To edit the interface visually, open `src/Citrus.Desktop/MainWindow.xaml` in Visual Studio's XAML Designer after restoring and building the solution. Each window and reusable panel has its own `.xaml` layout and matching `.xaml.cs` behavior file. See [desktop development and designer guidance](docs/desktop-development.md).

## Global settings

Open **Data → Historical data** to download Alpaca equities or Hyperliquid perpetual history at hourly or daily intervals. Choose a symbol, venue, and UTC date range; the end date is exclusive. Alpaca uses Global settings credentials and the selected IEX/SIP feed. Downloads fetch missing history through the existing validated cache and can be cancelled.

The default library is `%LOCALAPPDATA%\Citrus\HistoricalData`. Set the main cache in **Global settings**; the `CITRUS_HISTORICAL_DATA` environment variable overrides it for automation. **Refresh** lists each JSON dataset's instruments, coverage bounds, bar count, size and structural validation status. Bounds do not guarantee gap-free coverage. Select a dataset to inspect its provider notes, export a normalized copy, or delete it after confirmation. Do not run another cache writer against the same folder while downloading.

Open **Settings → Global settings** in the Windows workbench to edit Alpaca API credentials and choose the main historical cache. **Save** creates or replaces the per-user file; **Cancel** discards edits. Credentials are masked by default and can be revealed explicitly. Clear a credential and save to remove its stored value.

Both the desktop and CLI use `Citrus/config.json` under the operating system's application-data directory (`%APPDATA%\Citrus\config.json` on Windows). The dialog displays the full path. A missing file uses empty defaults; malformed or unsupported files produce an error and are not overwritten automatically. Credentials are stored as plain text, so keep this file private and outside source control. Global settings are not included in run configurations or replay exports.

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
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data generate examples/perpetual-generation.json artifacts/cache/perpetual-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- validate examples/PerpetualTrend/Strategy.cs
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- backtest examples/PerpetualTrend
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data generate examples/equity-generation.json artifacts/cache/equity-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- backtest examples/EquityHold
```

Rerunning a backtest overwrites its result files in the configured output directory. Data commands also overwrite their output file, so the example commands can be rerun as written. Other files in the result directory are retained. Configuration paths resolve relative to the configuration file; data command output paths resolve relative to the working directory. JSON configuration rejects unknown properties so spelling errors are visible.

The perpetual example has a trend substrategy and a holding substrategy with 70/30 capital allocation. The equity example demonstrates next-session opening orders across a weekend and the US daylight-saving transition. Generated prices have zero funding unless supplementary funding events are added.

## Payday seasonality

`examples/PaydaySeasonality/Strategy.cs` ports the supplied Zorro `PaydaySeason` rules: buy SCHB on trading sessions 8 and 16, sell its entire long holding on session 12 and the last session of each month. Each entry buys `floor(810 / preceding session close)` whole shares; the $810 notional is fixed, not compounded. Edit the strategy constructor or `BuyNotional` amount to change the instrument or allocation. The run starts with $1,000 to leave a cash buffer for price movement and costs.

```sh
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data download examples/payday-download.json artifacts/payday-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- backtest examples/PaydaySeasonality
```

The download configuration writes through `artifacts/cache`; point `CITRUS_HISTORICAL_DATA` there for this example, or remove its `cache` field to use the configured main cache. The download must finish before opening the run. Supply **complete exchange-calendar months** in `sessions`: trading-day counts use that calendar, not observed bar counts or weekdays. Missing calendar sessions silently change the strategy dates; the strategy cannot establish calendar completeness itself. The example requests daily bars for November and December 2024. A partial bar range can use a full-month calendar, but must not truncate that calendar. Only one shared exchange calendar is currently supported by the framework.

The strategy runs once at each session close and inspects `market.TradingDay(1)`, the next exchange session. Signals on days 7, 11, 15 and the penultimate session submit day-only market-on-close orders for days 8, 12, 16 and month end. The engine has already processed the signal day's auction and expiry before `OnBar`, so these orders remain eligible for the following auction, including early closes. Daily data is sufficient: dates match the intended Zorro windows, but sizing uses the preceding close instead of the original 15:30 price. Hourly datasets still work and use only their final daily bars. No 90-bar warmup is needed. Start before a required signal day; starting on an entry date does not retroactively submit its order. Entries without an affordable share are skipped; rejected entries cannot create a short exit. Rejected orders are not retried. A run ending before an exit date leaves the position open.

## Concise strategy API

Derive from `InstrumentStrategy` for one account/instrument. Its constructor registers capital and the host forwards only that instrument's bars to `OnBar(InstrumentContext market, Bar bar)`. For several instruments or substrategies, use `IStrategy` and construct an `InstrumentContext` for each registered account/instrument during startup.

```csharp
var next = market.TradingDay(1);
if (next?.DayOfMonth is 8 or 16)
    market.BuyNotional(810m, OrderType.MarketOnClose, TimeInForce.Day);
```

`TradingDay()` returns the latest opened session; positive offsets advance sessions, negative offsets go backwards. Its `Date`, `DayOfMonth`, `DaysInMonth`, and `IsMonthEnd` use the exchange calendar (New York by default; a custom zone can be passed to `InstrumentContext`). It returns null outside calendar coverage or before the first session opens, and throws if no calendar exists. Future session times are available, but future prices never are.

`Buy(quantity)` and `Sell(quantity)` remain direct additional-unit orders. Both accept execution type, time-in-force, and an optional limit price. `BuyNotional` and `SellNotional` size additional orders from completed prices; `LotsForNotional` exposes the calculation and accepts a lot size (default one, or e.g. `0.001m` for fractional units). Missing history or an unaffordable lot produces no notional order. `Quantity` reads actual holdings and `Close` reads the latest completed price.

`ExitLong` and `ExitShort` cancel pending orders for that account/instrument and close only its actual holding on the requested side; calling them while flat cannot open an opposite position. Repeated exits replace the pending exit, rather than duplicating its quantity. `CancelOrders` is also available explicitly. Orders use the existing execution, margin, costs, rejection, and attribution rules. For scheduling, portfolio access, or advanced orders, use `market.Context`; the original `IStrategy` API remains available.

## Market datasets

Backtests read the user-wide main historical cache, never a dataset path in the run JSON. `interval` is required by the model (daily by default); optional `start` and exclusive `end` fields select a slice. Identical overlapping bars are deduplicated; conflicting bars fail validation. Citrus combines matching cached files, validates interior coverage, and writes the exact assembled input to `historical-data.json` in the result directory before execution. Invalid cache files fail visibly. No provider requests occur during a backtest. Old dataset files with a `version` property remain readable; new files omit it. Remove obsolete `dataVersion` fields from run configurations and `request.version` from download configurations.

Prepare the main cache with **Data → Historical data**, or place normalized outputs from `data generate`, `data download`, or `data import` in the directory selected by Global settings. Strategies can observe and trade any instrument in the assembled cache slice, using exact, case-sensitive identities. Orders for instruments without cached bars fail with a missing-data error.

## Strategies

A source file defines exactly one concrete `IStrategy` with a public parameterless constructor. Specify extra assembly paths in the run configuration's `references` array, or after the source filename with `validate`.

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

```sh
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data download examples/alpaca-download.json artifacts/alpaca-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data download examples/hyperliquid-download.json artifacts/hyperliquid-data.json
dotnet run --project src/Citrus.Cli -c Release --no-build --no-restore -- data import artifacts/alpaca-data.json artifacts/combined.json supplement.json
```

Configure Alpaca credentials through **Settings → Global settings**, or set `APCA_API_KEY_ID` and `APCA_API_SECRET_KEY` in the environment. Keep credentials out of run and download JSON files; the private per-user global file is the only configuration intended to store them. Hyperliquid public historical data requires no key. Download commands are opt-in network operations; the normal tests do not invoke them. Update the Hyperliquid example's date range to available completed history.

Alpaca supplies exchange calendar sessions, adjusted minute bars (`adjustment=all`) aggregated into regular-session hourly/daily bars. Hourly buckets start at the session open, with a shorter final bucket where necessary. Missing no-trade minutes are not synthesized, but every expected hourly/daily bucket must contain data. IEX is the example feed; configure `feed` for your account's entitlement. Some instruments have entire sessions without IEX bars. These gaps fail coverage validation and the error lists the missing dates; an entitled SIP feed or imported history may supply the missing coverage. The calendar includes holidays, daylight-saving changes, and early closes. User-imported equity datasets must provide explicit UTC sessions and correct opening/closing flags.

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
- **Citrus.Engine** owns `RunConfiguration`, strategy compilation, backtest coordination, and reports.
- **Citrus.Cli** handles commands and paths; **Citrus.Tests** verifies behaviour.

When updating an existing strategy, replace `using Citrus.Contracts;` with `using Citrus.Trading;`. External projects must update their project/assembly reference to `Citrus.Trading` and rebuild. Consumers of `MarketDataset` or `Json` now import `Citrus.Data`, consumers of `SimulationOptions` import `Citrus.Simulation`, and consumers of `RunConfiguration` import `Citrus.Engine`. Equity datasets require adjusted prices and omit corporate actions. Previously captured strategy sources also need the import updated before recompilation; existing manifests retain their original hashes.

Live adapters and optimisation/walk-forward remain future releases. **Citrus.Desktop** provides the initial Windows GUI.

Run benchmarks explicitly:

```sh
dotnet run --project tests/Citrus.Tests -c Release --no-build --no-restore -- --benchmark
```

The benchmark uses 100 symbols, 20 substrategies, SMA calculations, and 2,000 initial orders. It measures engine execution, excluding data generation. Initial Windows/.NET 10 measurements: 365,000 daily bars in 15.978 seconds (205 MiB process peak), and 1,752,000 hourly bars in 70.822 seconds (764 MiB process peak). Memory is the process high-water mark, including dataset generation; these are observations, not hardware-independent limits.

GitHub Actions is configured to build, run offline tests, and execute both examples on Windows, macOS, and Linux. Local verification on this workspace is Windows only; the CI matrix must run on your remote repository to verify the other hosts.

## Strategy folders

A strategy folder contains `Strategy.cs` and one or more `Backtests/<name>.json` files. Folder backtests omit the `strategy` property: Citrus discovers the source automatically. Output and assembly-reference paths are relative to the strategy folder. Prefer `Results/<name>` for separate outputs; copying a backtest in the app sets this automatically.

`backtest <folder>` selects `Default.json`, or the only configuration when there is just one. With multiple configurations and no Default, specify a name: `backtest <folder> HigherCosts`. The name is the filename without `.json`. Existing standalone run JSON files remain supported, with their original paths relative to the run file.

To migrate an existing strategy, create a folder, move its C# source to `Strategy.cs`, move the run JSON to `Backtests/Default.json`, remove the JSON `strategy` property, and adjust output/reference paths to be relative to the new strategy folder. Historical data stays in the main cache.
