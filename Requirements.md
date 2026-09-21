# Citrus Backtesting and Live Trading Engine Requirements

## 1 Purpose

Citrus is a cross-platform backtesting and live trading engine. It provides a graphical interface for creating strategies and a command-line interface for deployment. A strategy is built into Citrus.Strategies and may contain multiple substrategies that operate together as a portfolio.

The same strategy definition is intended to run in both backtesting and live trading, subject to explicitly identified environment-specific behaviour.

## 2 Concept of Operations

### 2.1 Release progression

The first release focuses on the backtesting engine and C# strategy development in Citrus.Strategies. Optimisation, live trading, and the graphical interface are subsequent capabilities. The first graphical interface release will support built-in strategy selection, backtest execution, and results analysis.

### 2.2 Strategy development

A user writes a strategy as C# source code. A strategy may contain one or more substrategies and may operate across one or more symbols in a single market. Daily strategy callbacks shall follow that market's shared session calendar without selecting a clock instrument. Strategies are compiled as part of Citrus.Strategies when building Citrus; runtime project, assembly, and source-file loading are unsupported.

The GUI uses WPF and XAML on Windows, with a classic desktop workbench layout. A user selects a built-in strategy, configures and starts backtests, and analyses results. Strategy editing takes place in the main solution in an IDE. Views shall support the Visual Studio XAML Designer without loading user settings, historical datasets, or strategy assemblies. The cross-platform requirement applies to the engine and CLI, not the GUI.

### 2.3 Backtesting

A user runs a strategy against historical or simulated market data. Each substrategy produces individual orders. Citrus nets those orders before routing the resulting orders to an appropriate simulated broker. The user may configure the simulated spread, slippage, and probability that an order is rejected. Citrus records results so performance can be analysed by strategy component and symbol.

### 2.4 Optimisation

A user optimises strategy parameters and can evaluate them using walk-forward optimisation.

### 2.5 Live deployment

A user deploys the same strategy definition through the command-line interface. Citrus connects to a supported live broker or exchange, schedules or immediately triggers strategy execution, nets the desired positions of substrategies, and submits the resulting orders.

## 3 User Requirements

### 3.1 Platform and interfaces

- **UR-PLAT-001** Citrus shall be implemented using .NET.
- **UR-PLAT-002** Citrus shall support backtesting and live trading.
- **UR-PLAT-003** Strategies shall live in the main Citrus repository and solution as a C# class-library project. Strategies shall be built into Citrus.Strategies with the application; external projects, prebuilt assembly selection, loose scripts, and conventional strategy-folder discovery are unsupported.
- **UR-PLAT-003E** The desktop shall discover runnable classes from Citrus.Strategies and offer strategy selection without folder browsing. Selection shall load matching named backtests or create separate settings for a new strategy; refreshing shall list the running build; code changes require rebuilding and restarting Citrus.
- **UR-PLAT-003A** Strategies shall be written in C# against Citrus.Trading.
- **UR-PLAT-003B** Citrus shall instantiate the named built-in strategy with fresh state for each run. Standalone JSON configurations shall select strategyType and resolve output paths relative to the configuration file.
- **UR-PLAT-003C** The strategy execution contract shall be independent of the authoring IDE and repository; strategy code shall reference Citrus.Trading.
- **UR-PLAT-003D** Strategies may declare authoritative run settings in C#. Explicit assignments shall override JSON and GUI settings before data selection and account creation; undeclared settings remain editable. The GUI shall display declared settings read-only and reports shall capture the effective configuration.
- **UR-PLAT-004** A strategy definition shall run in backtesting and live trading without modification, except for behaviour explicitly selected through runtime context.
- **UR-PLAT-005** Citrus shall open the main Citrus solution in the associated IDE when the checkout is available. Strategy authoring, debugging and source control belong to the external development environment.
- **UR-PLAT-006** Citrus shall provide a command-line interface for deployment and execution.
- **UR-PLAT-007** The Citrus engine and CLI shall run on Windows, macOS, and Linux. The GUI is exempt from this requirement and shall use WPF on Windows, with XAML views for configuration, execution and analysis.

### 3.2 Release scope

- **UR-SCOPE-001** The first release shall provide the backtesting engine and built-in C# strategy execution.
- **UR-SCOPE-002** Optimisation is not required for the first release but shall remain a planned capability.
- **UR-SCOPE-003** Live trading is not required for the first release but shall remain a planned capability.
- **UR-SCOPE-004** The graphical interface is not required for the first release but shall remain a planned capability.
- **UR-SCOPE-005** The graphical interface shall select concrete built-in strategy types and open the main Citrus solution in an IDE. It shall not contain a source editor.
- **UR-SCOPE-006** The first graphical interface release shall allow users to configure and execute backtests.
- **UR-SCOPE-007** The first graphical interface release shall present backtest results for analysis.

### 3.3 Brokers and exchanges

- **UR-BRK-001** Citrus shall support live equities trading through Alpaca Markets.
- **UR-BRK-002** Citrus shall support live cryptocurrency perpetual-futures trading through Hyperliquid.
- **UR-BRK-003** Citrus shall provide a simulated equities broker for backtesting.
- **UR-BRK-004** Citrus shall provide a simulated cryptocurrency perpetual-futures exchange for backtesting.
- **UR-BRK-005** A user shall be able to configure simulated trading costs in a backtest.
- **UR-BRK-006** A user shall be able to configure the spread applied by a simulated broker.
- **UR-BRK-007** A user shall be able to configure the slippage applied by a simulated broker.
- **UR-BRK-008** A user shall be able to configure the probability that a simulated broker rejects an order.
- **UR-BRK-009** The simulated broker shall apply the configured rejection probability independently to each submitted order using the backtest's reproducible random-number source.

### 3.4 Market data

- **UR-DATA-001** Citrus shall obtain historical market data from the Alpaca API.
- **UR-DATA-002** Citrus shall obtain historical market data from the Hyperliquid API.
- **UR-DATA-003** Citrus shall cache downloaded historical data and shall not redownload data already present and valid in the cache.
- **UR-DATA-004** Citrus shall import provider-adjusted equity OHLC prices, including adjusted close, without separate dividend, split, merger, symbol-change, or delisting processing.
- **UR-DATA-005** Citrus shall provide a geometric or arithmetic Brownian-motion data generator, as selected in the detailed design, for simulated prices.
- **UR-DATA-006** Citrus shall support end-of-day trading only, using one full-session equity bar or one UTC-day perpetual bar. All strategies shall derive from one Strategy base class; non-daily intervals and arbitrary-time strategy scheduling are unsupported.
- **UR-DATA-007** The market-data pipeline shall reject non-daily intervals and partial-session bars before strategy execution.
- **UR-DATA-008** Backtests shall assemble a MarketDataset from matching entries in the user-wide historical data cache. Strategies shall select the instruments they trade and may access history and place orders for any instrument in that assembled dataset. Orders for instruments without cached market bars shall fail with a missing-data error.
- **UR-DATA-009** Data download commands shall cache historical coverage from Alpaca for equities or Hyperliquid for perpetuals for reuse. Unavailable coverage shall fail rather than shorten it silently.
- **UR-DATA-010** Backtests shall read prepared history from the main cache without provider network access and shall capture the assembled dataset with their results.

### 3.5 Backtesting and results

- **UR-BT-001** A strategy shall support multiple algorithms or substrategies.
- **UR-BT-002** A strategy shall run against multiple symbols.
- **UR-BT-003** Backtest results shall be attributable by substrategy and symbol.
- **UR-BT-004** Backtest execution shall use the same strategy-facing trading abstractions used in live trading.
- **UR-BT-005** Each substrategy shall produce individual orders before portfolio-level netting occurs.
- **UR-BT-006** Citrus shall preserve the originating substrategy and symbol attribution needed to report results after orders have been netted.

### 3.6 Optimisation

- **UR-OPT-001** Citrus shall support optimisation of strategy parameters.
- **UR-OPT-002** Citrus shall support walk-forward optimisation.

### 3.7 Live trading and scheduling

- **UR-LIVE-001** For equities, Citrus shall support strategy execution a configurable number of minutes before market close.
- **UR-LIVE-002** Equity market-session timing shall be determined using Alpaca's market clock or calendar service.
- **UR-LIVE-003** Future live execution shall use the same EOD decision lifecycle, including external invocation after a daily close.

### 3.8 Equities trading

- **UR-EQ-001** The equities backtest and live-trading implementations shall support market orders.
- **UR-EQ-002** The equities backtest and live-trading implementations shall support limit orders.
- **UR-EQ-003** The equities backtest and live-trading implementations shall support market-on-open orders.
- **UR-EQ-004** The equities backtest and live-trading implementations shall support market-on-close orders.
- **UR-EQ-005** The equities backtest and live-trading implementations shall support short positions.

### 3.9 Strategy authoring and execution

- **UR-STRAT-001** Strategy scripts shall use C# with a concise strategy API inspired by Zorro Trader scripts.
- **UR-STRAT-002** Strategy scripts shall provide concise operations for defining and executing portfolio rebalancing.
- **UR-STRAT-003** Strategy scripts shall be able to ingest data from third-party APIs.
- **UR-STRAT-004** Strategy scripts shall be able to conditionally execute code according to whether the runtime is backtesting or live trading.
- **UR-STRAT-005** Strategy scripts shall have access to a library of technical indicators, initially including moving averages.
- **UR-STRAT-006** A strategy shall support up to 100 substrategies, enforced by a fixed code limit that is not configurable.
- **UR-STRAT-007** Citrus shall net the individual orders produced by substrategies in both backtesting and live trading according to one defined and consistent netting model.

### 3.10 Verification

- **UR-TEST-001** Citrus shall include automated unit tests for deterministic calculation and execution components.
- **UR-TEST-002** Automated tests shall validate equivalent strategy decisions across backtest and live execution contexts where the same inputs and execution assumptions apply.

## 4 Decisions and open questions

The current backtesting decisions are documented here:

- Strategy trust and external access: [strategy API](README.md#direct-orders-and-portfolio-access).
- Daily callbacks and session calendars: [strategy lifecycle](README.md#accounts-and-daily-callbacks). Fill timing, limits, liquidity assumptions, and cancellation: [event timing](docs/simulation.md#event-timing).
- Order compatibility, crossing, commission allocation, and rejection: [netting and costs](docs/simulation.md#netting-and-costs).
- Cash, margin, borrow, funding, and liquidation: [portfolio accounting](docs/simulation.md#portfolio-accounting).
- Equity price adjustments and corporate-action limits: [adjusted equity prices](docs/simulation.md#adjusted-equity-prices).
- Runtime and desktop platform: [build instructions](README.md#build-and-verify) and [Windows desktop](README.md#windows-desktop-workbench).
- Credential storage and overrides: [global settings](README.md#global-settings).
- Reports, captured inputs, and reproducibility: [results](README.md#results).
- Implemented requirements and verification limits: [traceability](docs/requirements-traceability.md).

Open decisions for later work:

1. Optimisation objectives, parameter ranges, constraints, search methods, parallelism, and walk-forward procedures.
2. Live deployment and recovery: one-shot versus continuous execution, persistent state, idempotency, broker reconciliation, restarts, stale data, retries, alerts, emergency shutdown, and production credential protection.
3. Measurable performance and capacity targets: history length, symbol and strategy counts, optimisation scale, and live latency.
4. Minimum supported OS versions for the cross-platform engine and CLI; the Windows desktop baseline is documented above.

## 5 Terminology

- **Strategy:** a built-in C# class whose instance runs a configured backtest.
- **Substrategy:** a named account within a strategy, with allocated capital and separately attributed positions.
- **Symbol:** the case-insensitive historical lookup name within an interval. Venue, provider, and asset class remain metadata; conflicting instrument definitions fail.
- **Netting:** compatible substrategy orders cross internally; residual exposure is routed externally. See [netting rules](docs/simulation.md#netting-and-costs).
