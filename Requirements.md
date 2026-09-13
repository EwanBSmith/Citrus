# Citrus Backtesting and Live Trading Engine Requirements

## 1 Purpose

Citrus is a cross-platform backtesting and live trading engine. It provides a graphical interface for creating strategies and a command-line interface for deployment. A strategy is defined in a script-like file and may contain multiple substrategies that operate together as a portfolio.

The same strategy definition is intended to run in both backtesting and live trading, subject to explicitly identified environment-specific behaviour.

## 2 Concept of Operations

### 2.1 Release progression

The first release focuses on the backtesting engine and file-based C# strategy development. Optimisation, live trading, and the graphical interface are subsequent capabilities. The first graphical interface release will support script editing, backtest execution, and results analysis.

### 2.2 Strategy development

A user writes a strategy as C# source code. A strategy may contain one or more substrategies and may operate across one or more symbols. The initial implementation may compile strategy source code before execution. Its strategy-facing interfaces shall not prevent interpreted C# execution from being added later.

The GUI uses WPF and XAML on Windows, with AvalonEdit for C# strategy editing and a classic desktop workbench layout. A user can edit strategy scripts, configure and start backtests, and analyse results. Views shall support the Visual Studio XAML Designer without loading user settings, historical datasets, or strategy assemblies. The cross-platform requirement applies to the engine and CLI, not the GUI.

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
- **UR-PLAT-003** Strategies shall live in the main Citrus repository and solution as a C# class-library project. Citrus shall support project builds and prebuilt strategy assemblies; existing file-based scripts remain compatible.
- **UR-PLAT-003E** The desktop shall discover runnable classes from Citrus.Strategies and offer strategy selection without folder browsing. Selection shall load matching named backtests or create separate settings for a new strategy; refreshing shall rebuild the catalog.
- **UR-PLAT-003A** Strategies shall be written in C# against Citrus.Trading.
- **UR-PLAT-003B** Citrus shall build selected strategy projects in Release before execution, or load explicitly selected prebuilt assemblies.
- **UR-PLAT-003C** The strategy execution contract shall be independent of the authoring IDE and repository; strategy code shall reference Citrus.Trading.
- **UR-PLAT-003D** Strategies may declare authoritative run settings in C#. Explicit assignments shall override JSON and GUI settings before data selection and account creation; undeclared settings remain editable. The GUI shall display declared settings read-only and reports shall capture the effective configuration.
- **UR-PLAT-004** A strategy definition shall run in backtesting and live trading without modification, except for behaviour explicitly selected through runtime context.
- **UR-PLAT-005** Citrus shall open externally maintained strategy solutions or projects in the associated IDE. Strategy authoring, debugging and source control belong to the external development environment.
- **UR-PLAT-006** Citrus shall provide a command-line interface for deployment and execution.
- **UR-PLAT-007** The Citrus engine and CLI shall run on Windows, macOS, and Linux. The GUI is exempt from this requirement and shall use WPF on Windows, with XAML views for configuration, execution and analysis.

### 3.2 Release scope

- **UR-SCOPE-001** The first release shall provide the backtesting engine and file-based C# strategy execution.
- **UR-SCOPE-002** Optimisation is not required for the first release but shall remain a planned capability.
- **UR-SCOPE-003** Live trading is not required for the first release but shall remain a planned capability.
- **UR-SCOPE-004** The graphical interface is not required for the first release but shall remain a planned capability.
- **UR-SCOPE-005** The graphical interface shall select external strategy projects, assemblies and concrete strategy types, and open their solutions in an IDE. It shall not contain a source editor.
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
- **UR-DATA-006** Citrus shall initially support daily and hourly data intervals.
- **UR-DATA-007** The market-data architecture shall permit additional data intervals to be added later.
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
- **UR-LIVE-003** Citrus shall support immediate, externally scheduled execution, including invocation by a cron-compatible scheduler for cryptocurrency rebalancing.

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

## 4 Clarifications Required

The following decisions are needed before the requirements can be made fully testable.

1. Define whether strategy scripts are trusted local code and whether they may load arbitrary libraries or access the network and filesystem.
2. Define the strategy event model, including startup, data arrival, scheduled execution, order updates, fills, shutdown, and recovery after interruption.
3. Define the order-netting rules, including how compatible orders are combined, how opposing orders interact, whether order types and limits may be combined, and how fills and costs are attributed to their originating substrategies.
4. Define backtest execution assumptions for each order type, including bar timing, fill price, limit-order fills, commissions, liquidity, partial fills, rejected orders, and look-ahead prevention.
5. Resolved: equity data uses adjusted OHLC prices. Corporate actions are not simulated separately; holdings and order quantities change only through execution.
6. Define the portfolio accounting model for cash, buying power, margin, leverage, borrow availability and fees, funding payments, and cryptocurrency perpetual liquidation.
7. Define the optimisation objective, parameter types and ranges, constraints, search methods, parallelism, and the exact walk-forward train and test procedure.
8. Define live-trading reliability requirements, including persistent state, idempotent order submission, reconciliation with the broker, restart behaviour, stale-data detection, retries, logging, alerts, and emergency shutdown.
9. Define supported .NET and operating-system versions and whether the GUI must be native or may use a cross-platform web or desktop framework.
10. Define secrets management and the security boundary for broker credentials and third-party API keys.
11. Define the required reports and metrics, including trades, orders, equity curve, drawdown, returns, risk measures, and attribution by substrategy and symbol.
12. Define reproducibility requirements, including captured market-data snapshots, configuration capture, random seeds, engine version, and exportable run manifests.
13. Define measurable performance targets for backtest speed, supported history length, symbol count, strategy count, optimisation scale, and live execution latency.

## 5 Terminology to Standardise

- Choose one term for the composable unit currently called both an **algo** and a **substrategy**.
- Distinguish a **strategy definition** from a configured **strategy instance** and a running **strategy process**.
- Define whether **symbol** identifies only an instrument or an instrument plus venue and asset class.
- Define the exact compatibility and attribution rules used when **netting** individual substrategy orders.
- Define whether **instant running** means immediate one-shot execution, a continuously running service, or both.
