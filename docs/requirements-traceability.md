# First-release requirement traceability

The source requirements remain unchanged. This table maps first-release requirements to implementations and executable evidence. Later capabilities are explicitly deferred rather than represented as working live features.

| Requirements | Implementation | Verification |
| --- | --- | --- |
| UR-PLAT-001, 003/003A/B/C, 006/007; UR-SCOPE-001 | .NET 10 projects, CLI, built-in Citrus.Strategies discovery and selection, independent strategy contract | Build, built-in selection and removed-field rejection tests, CLI examples, cross-platform CI |
| UR-PLAT-003D | StrategyOptions and shared StrategyConfiguration resolution before data/account initialization; GUI field locks | Explicit zero/false/null precedence, early validation, direct engine data bounds, effective manifests, WPF lock/refresh checks |
| UR-PLAT-003E | Main-project strategy discovery and desktop catalog with matching backtest selection | Discovery filtering and rebuild regression; WPF strategy switching, option loading, and new-configuration checks |
| UR-PLAT-004; UR-BT-004; UR-STRAT-004; UR-TEST-002 | Shared context with explicit runtime mode | Backtest/fake live-context decision equivalence test; no real live adapter |
| UR-BRK-003–009 | Equity/perpetual ledgers, execution prices, fees, independent seeded residual rejection | Margin, borrow, funding, liquidation, costs and seeded outcome tests |
| UR-DATA-001/002 | Alpaca raw bars/actions/calendar; Hyperliquid candles/funding | Offline HTTP pagination, normalization, calendar and missing-history fixtures |
| UR-DATA-003 | Coverage cache with atomic file replacement | Missing-range and cache reuse tests |
| UR-DATA-004 | Provider-adjusted equity OHLC; no separate corporate-action processing | Adjusted Alpaca request/aggregation, dataset round trips and removed-field rejection, adjusted-price P&L and cache snapshot tests |
| UR-DATA-005–007 | Seeded geometric Brownian OHLC generator; extensible interval record | Deterministic generation and OHLC validation tests |
| UR-DATA-008–010 | Main-cache runs with strategy-selected instruments; separate cached data downloads and captured assembled input | Cache assembly/slicing, multi-instrument orders and history, missing instrument data rejection, and cache reuse tests |
| UR-BT-001–003, 005/006; UR-STRAT-006/007 | Registered substrategies, full instrument identity, compatible netting, virtual ledgers and exports | Crossing, residual rejection, attribution, reconciliation and exports tests |
| UR-EQ-001–005 | Market, limit, opening, closing, and equity shorts | Timing, gap fills, session/DST/holiday, limit, short-availability tests |
| UR-STRAT-001–003/005 | C# API, rebalance helper, per-run external-data cache, SMA/EMA | Pending-aware rebalance, external-data cache and indicator tests |
| UR-TEST-001 | Dependency-free automated unit/integration executable | `dotnet run --project tests/Citrus.Tests` |
| UR-PLAT-005/007; UR-SCOPE-004–007 | Windows WPF/XAML workbench: built-in strategy selection and main-solution IDE launch, configuration controls, background backtests, ScottPlot equity chart and result tables | WPF desktop smoke check: built-in discovery and standalone settings, offline example, repeated runs/export, invalid inputs, configuration precision, displayed result bindings, normal/compact/high-DPI rendering |
| UR-SCOPE-002/003; UR-OPT-001/002 | Planned optimisation and walk-forward | Deferred; no implementation claim |
| UR-PLAT-002; UR-BRK-001/002; UR-LIVE-001–003 | Live execution remains a later release | Deferred; only market-data connections implemented |

## Acceptance evidence and limits

The tests cover the daily strategy API, pending-aware position intents, pre-close scheduling, deterministic next-bar decisions, compatible and incompatible netting, exact commission allocation, accounting and adjusted equity prices, validation failures, cache reuse, provider pagination, credential-safe provider errors, invalid strategy selection, and export totals. CI additionally executes CLI generation and backtests. The performance harness runs the research-scale daily and hourly cases with no timing threshold.

HTTP tests use realistic offline fixtures, not credentials. Credentialed acceptance is opt-in: execute the documented `data download` commands with an entitled account and an available date range. Provider coverage/entitlements and historical adjustment completeness cannot be established by offline tests. Funding mark approximation, common settlement currency, full fills, and bar-based liquidation are documented in simulation rules and dataset metadata.
