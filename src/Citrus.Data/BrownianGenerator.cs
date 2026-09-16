using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Generates seeded synthetic geometric Brownian motion bars without funding.</summary>
public static class BrownianGenerator
{
    /// <summary>Generates count bars using four price steps per bar; equities require enough explicit sessions.</summary>
    public static MarketDataset Generate(Instrument instrument, BarInterval interval, DateTimeOffset start, int count,
        int seed, decimal initialPrice = 100, double annualDrift = 0.05, double annualVolatility = 0.2,
        IReadOnlyList<MarketSession>? sessions = null)
    {
        if (count <= 0 || initialPrice <= 0 || annualVolatility < 0 || !double.IsFinite(annualVolatility) || !double.IsFinite(annualDrift))
            throw new ArgumentException("Invalid generation parameters.");
        if (instrument.AssetClass == AssetClass.Equity && (sessions is null || sessions.Count == 0)) throw new ArgumentException("Equities generation requires sessions.");
        var rng = new Random(seed);
        var boundaries = DatasetValidator.Expected(instrument, interval, start,
            instrument.AssetClass == AssetClass.Equity ? sessions!.Max(s => s.Close) : start.AddDays(count), sessions ?? []).Take(count).ToArray();
        if (boundaries.Length < count) throw new ArgumentException("Insufficient sessions for generated bar count.");
        var price = (double)initialPrice;
        var bars = new List<Bar>();
        foreach (var (open, close) in boundaries)
        {
            var first = price; var high = price; var low = price;
            var dt = instrument.AssetClass == AssetClass.Equity
                ? 1.0 / 252 / 4
                : (close - open).TotalDays / (365 * 4);
            for (var step = 0; step < 4; step++)
            {
                var normal = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
                price *= Math.Exp((annualDrift - annualVolatility * annualVolatility / 2) * dt + annualVolatility * Math.Sqrt(dt) * normal);
                high = Math.Max(high, price); low = Math.Min(low, price);
            }
            bars.Add(new(instrument, open, close, (decimal)first, (decimal)high, (decimal)low, (decimal)price, 1_000_000,
                sessions?.Any(s => s.Open == open) == true, sessions?.Any(s => s.Close == close) == true));
        }
        return new() { Provider = "gbm", Interval = interval, Bars = bars, Sessions = sessions?.ToList() ?? [],
            Notes = ["Synthetic GBM prices. No funding generated; funding is zero unless supplementary events are imported."] };
    }
}
