namespace Citrus.Engine;

/// <summary>Retains a completed backtest, portfolio performance, and absolute report directory.</summary>
public sealed record CompletedBacktest(BacktestResult Result, Performance Performance, string Output);
