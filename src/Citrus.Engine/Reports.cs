using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Citrus.Contracts;

namespace Citrus.Engine;

public sealed record Performance(decimal? TotalReturn, decimal MaximumDrawdown, double? AnnualizedSharpe, int DailyObservations);
public static class Reports
{
    public static Performance Metrics(IEnumerable<(DateTimeOffset Time, decimal Equity)> observations, decimal riskFreeRate, int annualDays)
    {
        var values = observations.ToArray();
        if (values.Length == 0) return new(null, 0, null, 0);
        var peak = values[0].Equity; var drawdown = 0m;
        foreach (var value in values)
        {
            peak = Math.Max(peak, value.Equity);
            if (peak > 0) drawdown = Math.Max(drawdown, (peak - value.Equity) / peak);
        }
        var days = values.Skip(1).GroupBy(v => v.Time.UtcDateTime.Date).Select(g => g.Last().Equity).ToArray();
        var returns = new List<double>();
        var previous = values[0].Equity;
        foreach (var day in days)
        {
            if (previous > 0) returns.Add((double)(day / previous - 1 - riskFreeRate / annualDays));
            previous = day;
        }
        double? sharpe = null;
        if (returns.Count >= 2)
        {
            var mean = returns.Average();
            var variance = returns.Sum(r => Math.Pow(r - mean, 2)) / (returns.Count - 1);
            if (variance > 0) sharpe = mean / Math.Sqrt(variance) * Math.Sqrt(annualDays);
        }
        return new(values[0].Equity > 0 ? values[^1].Equity / values[0].Equity - 1 : null, drawdown, sharpe, returns.Count);
    }

    public static void Export(string directory, BacktestResult result, RunConfiguration configuration, MarketDataset data,
        string strategyPath, string dataPath, IReadOnlyDictionary<string, string> dependencyHashes)
    {
        Directory.CreateDirectory(directory);
        var annualDays = data.Bars.Any(b => b.Instrument.AssetClass == AssetClass.LinearPerpetual) ? 365 : 252;
        var summary = new
        {
            schemaVersion = 1,
            portfolio = Metrics(result.Equity.Select(p => (p.Time, p.Equity)), configuration.RiskFreeRate, annualDays),
            substrategies = result.Equity.SelectMany(p => p.Substrategies.Keys).Distinct().ToDictionary(s => s,
                s => Metrics(result.Equity.Select(p => (p.Time, p.Substrategies.GetValueOrDefault(s))), configuration.RiskFreeRate, annualDays)),
            result.Final,
            annualizationDays = annualDays
        };
        Json.Write(Path.Combine(directory, "summary.json"), summary);
        Json.Write(Path.Combine(directory, "orders.json"), result.Orders);
        Json.Write(Path.Combine(directory, "fills.json"), result.Fills);
        Json.Write(Path.Combine(directory, "positions.json"), result.Final.Positions);
        Json.Write(Path.Combine(directory, "costs.json"), result.Costs);
        Json.Write(Path.Combine(directory, "equity.json"), result.Equity);
        Json.Write(Path.Combine(directory, "instrument-attribution.json"), result.Attribution);
        Csv(Path.Combine(directory, "instrument-attribution.csv"), ["substrategy", "instrument", "quantity", "markPrice", "realizedPnl", "unrealizedPnl", "incomeAndFees", "netPnl"],
            result.Attribution.Select(p => new object?[] { p.Substrategy, p.Instrument.Key, p.Quantity, p.MarkPrice, p.RealizedPnl, p.UnrealizedPnl, p.IncomeAndFees, p.NetPnl }));
        Csv(Path.Combine(directory, "orders.csv"), ["orderId", "time", "substrategy", "instrument", "quantity", "type", "limit", "timeInForce", "status", "reason"],
            result.Orders.Select(o => new object?[] { o.OrderId, o.Time, o.Request.Substrategy, o.Request.Instrument.Key, o.Request.Quantity, o.Request.Type, o.Request.LimitPrice, o.Request.TimeInForce, o.Status, o.Reason }));
        Csv(Path.Combine(directory, "fills.csv"), ["orderId", "time", "substrategy", "instrument", "quantity", "price", "commission", "executionCost", "internal", "liquidation"],
            result.Fills.Select(f => new object?[] { f.OrderId, f.Time, f.Substrategy, f.Instrument.Key, f.Quantity, f.Price, f.Commission, f.ExecutionCost, f.Internal, f.Liquidation }));
        Csv(Path.Combine(directory, "positions.csv"), ["substrategy", "instrument", "quantity", "averagePrice", "realizedPnl"],
            result.Final.Positions.Select(p => new object?[] { p.Substrategy, p.Instrument.Key, p.Quantity, p.AveragePrice, p.RealizedPnl }));
        Csv(Path.Combine(directory, "costs.csv"), ["time", "substrategy", "instrument", "kind", "amount"],
            result.Costs.Select(c => new object?[] { c.Time, c.Substrategy, c.Instrument.Key, c.Kind, c.Amount }));
        var peak = configuration.InitialCash; var prior = configuration.InitialCash;
        var equityRows = new List<object?[]>();
        foreach (var point in result.Equity)
        {
            peak = Math.Max(peak, point.Equity);
            equityRows.Add([point.Time, point.Cash, point.Equity, point.GrossExposure, prior > 0 ? point.Equity / prior - 1 : null, peak > 0 ? (peak - point.Equity) / peak : null]);
            prior = point.Equity;
        }
        Csv(Path.Combine(directory, "equity.csv"), ["time", "cash", "equity", "grossExposure", "return", "drawdown"], equityRows);
        Csv(Path.Combine(directory, "attribution.csv"), ["time", "substrategy", "equity"], result.Equity.SelectMany(p =>
            p.Substrategies.Select(s => new object?[] { p.Time, s.Key, s.Value })));
        var replay = Path.Combine(directory, "inputs"); Directory.CreateDirectory(replay);
        CopyInput(strategyPath, Path.Combine(replay, "strategy.cs")); CopyInput(dataPath, Path.Combine(replay, "data.json"));
        var references = new List<string>(); var index = 0;
        foreach (var path in dependencyHashes.Keys)
        {
            var name = $"dependency-{index++}-{Path.GetFileName(path)}";
            CopyInput(path, Path.Combine(replay, name)); references.Add(name);
        }
        Json.Write(Path.Combine(replay, "run.json"), configuration with { Strategy = "strategy.cs", Data = "data.json", Output = "../replay", References = references.ToArray(), ReplaySnapshots = "snapshots.json" });
        Json.Write(Path.Combine(replay, "snapshots.json"), result.ExternalSnapshots);
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Json.Write(Path.Combine(directory, "manifest.json"), new
        {
            schemaVersion = 1, configuration, configuration.Seed,
            engineVersion = typeof(BacktestEngine).Assembly.GetName().Version?.ToString(),
            engineHash = Hash(typeof(BacktestEngine).Assembly.Location),
            componentHashes = new[] { typeof(BacktestEngine).Assembly, typeof(Citrus.Simulation.Ledger).Assembly,
                typeof(Citrus.Data.DatasetValidator).Assembly, typeof(IStrategy).Assembly, typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly }
                .ToDictionary(a => a.GetName().Name!, a => Hash(a.Location)),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            strategyHash = Hash(strategyPath), dataHash = Hash(dataPath), dependencyHashes,
            calendarHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data.Sessions, Json.Options))),
            snapshotHashes = result.ExternalSnapshots.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(p.Value))),
            inputHashes = references.Concat(["strategy.cs", "data.json", "run.json", "snapshots.json"])
                .OrderBy(name => name, StringComparer.Ordinal).ToDictionary(name => name, name => Hash(Path.Combine(replay, name))),
            reproducibility = "Guaranteed only for captured inputs on the same engine/runtime. Trusted scripts may perform untracked external I/O or randomness.",
            data.Provider, data.Version, data.Notes
        });
    }
    private static void CopyInput(string source, string destination)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), comparison))
            File.Copy(source, destination, overwrite: true);
    }
    private static void Csv(string path, string[] headers, IEnumerable<object?[]> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        string Format(object? value)
        {
            var text = value switch { null => "", DateTimeOffset t => t.ToString("O"), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString()! };
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        writer.WriteLine(string.Join(',', headers.Select(Format)));
        foreach (var row in rows) writer.WriteLine(string.Join(',', row.Select(Format)));
    }
}
