using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Citrus.Trading;
using Citrus.Data;

namespace Citrus.Engine;

/// <summary>Reports fractional return and drawdown, annualized Sharpe, and the number of usable daily returns; undefined metrics are null.</summary>
public sealed record Performance(decimal? TotalReturn, decimal MaximumDrawdown, double? AnnualizedSharpe, int DailyObservations);
/// <summary>Calculates performance and exports results with provenance hashes.</summary>
public static class Reports
{
    /// <summary>Computes drawdown over ordered observations and sample-deviation Sharpe from UTC daily closing equity after the initial observation.</summary>
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
        // The first observation is the capital baseline, not a daily close; intraday points still affect drawdown.
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

    /// <summary>Writes JSON/CSV results, replacing matching output files while retaining unrelated files.</summary>
    public static void Export(string directory, BacktestResult result, RunConfiguration configuration, MarketDataset data,
        string strategyPath, string dataPath, IReadOnlyDictionary<string, string> dependencyHashes, IStrategy? strategy = null)
    {
        Directory.CreateDirectory(directory);
        if (strategy is not null) configuration = StrategyConfiguration.Resolve(strategy, configuration);
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
        // Compute the file SHA-256 digest used to identify captured inputs and runtime components.
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Json.Write(Path.Combine(directory, "run.json"), configuration);
        Json.Write(Path.Combine(directory, "manifest.json"), new
        {
            schemaVersion = 1, configuration, configuration.Seed,
            engineVersion = typeof(BacktestEngine).Assembly.GetName().Version?.ToString(),
            engineBuild = typeof(BacktestEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            strategyBuild = strategy?.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            engineHash = Hash(typeof(BacktestEngine).Assembly.Location),
            componentHashes = BuiltInStrategies.ComponentHashes(),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            strategyHash = Hash(strategyPath), dataHash = Hash(dataPath), dependencyHashes,

            strategyOptions = strategy is null ? null : StrategyConfiguration.Declarations(strategy),
            calendarHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data.Sessions, Json.Options))),
            reproducibility = "Repeatability requires unchanged inputs, engine/runtime, and a deterministic trusted strategy. External I/O and script-owned randomness are untracked.",
            data.Provider, data.Notes
        });
    }
    /// <summary>Writes a UTF-8 CSV with quoted headers and values, invariant numbers, and round-trip timestamps.</summary>
    private static void Csv(string path, string[] headers, IEnumerable<object?[]> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        // Format one CSV field, doubling embedded quotes and representing null as an empty field.
        string Format(object? value)
        {
            var text = value switch { null => "", DateTimeOffset t => t.ToString("O"), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString()! };
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        writer.WriteLine(string.Join(',', headers.Select(Format)));
        foreach (var row in rows) writer.WriteLine(string.Join(',', row.Select(Format)));
    }
}
