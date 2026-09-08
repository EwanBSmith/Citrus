# First-release requirement traceability

The source requirements remain unchanged. This table maps first-release requirements to implementations and executable evidence. Later capabilities are explicitly deferred rather than represented as working live features.

| Requirements | Implementation | Verification |
| --- | --- | --- |
| UR-PLAT-001, 003/003A/B/C, 006/007; UR-SCOPE-001 | .NET 10 projects, CLI, Roslyn source loader, independent strategy contract | Build, compiler diagnostics test, CLI examples, cross-platform CI |
| UR-PLAT-004; UR-BT-004; UR-STRAT-004; UR-TEST-002 | Shared context with explicit runtime mode | Backtest/fake live-context decision equivalence test; no real live adapter |
| UR-BRK-003–009 | Equity/perpetual ledgers, execution prices, fees, independent seeded residual rejection | Margin, borrow, funding, liquidation, costs and seeded outcome tests |
| UR-DATA-001/002 | Alpaca raw bars/actions/calendar; Hyperliquid candles/funding | Offline HTTP pagination, normalization, calendar and missing-history fixtures |
| UR-DATA-003 | Versioned coverage cache with atomic file replacement | Missing-range and cache reuse tests |
| UR-DATA-004 | Explicit split/dividend/merger/symbol-change/delisting events | Corporate-action tests, successor aggregation and unresolved-event rejection |
| UR-DATA-005–007 | Seeded geometric Brownian OHLC generator; extensible interval record | Deterministic generation and OHLC validation tests |
| UR-BT-001–003, 005/006; UR-STRAT-006/007 | Registered substrategies, full instrument identity, compatible netting, virtual ledgers and exports | Crossing, residual rejection, attribution, reconciliation and exports tests |
| UR-EQ-001–005 | Market, limit, opening, closing, and equity shorts | Timing, gap fills, session/DST/holiday, limit, short-availability tests |
| UR-STRAT-001–003/005 | C# API, rebalance helper, external-data snapshots, SMA/EMA | Pending-aware rebalance, snapshot replay and indicator tests |
| UR-TEST-001 | Dependency-free automated unit/integration executable | `dotnet run --project tests/Citrus.Tests` |
| UR-SCOPE-002–007; UR-OPT-001/002 | Planned optimisation, walk-forward, GUI editing/execution/analysis | Deferred; no implementation claim |
| UR-PLAT-002/005; UR-BRK-001/002; UR-LIVE-001–003 | Live execution and GUI remain later releases | Deferred; only market-data connections implemented |

## Acceptance evidence and limits

The tests cover deterministic next-bar decisions, compatible and incompatible netting, exact commission allocation, accounting and corporate actions, validation failures, cache reuse, provider pagination, credential-safe provider errors, compiler errors, export totals, and replay snapshots. CI additionally executes CLI generation/backtests and portable replay. The performance harness runs the research-scale daily and hourly cases with no timing threshold.

HTTP tests use realistic offline fixtures, not credentials. Credentialed acceptance is opt-in: execute the documented `data download` commands with an entitled account and an available date range. Provider coverage/entitlements and historical action completeness cannot be established by offline tests. Funding mark approximation, daily dividend recognition, common settlement currency, full fills, and bar-based liquidation are documented in simulation rules and dataset metadata.
