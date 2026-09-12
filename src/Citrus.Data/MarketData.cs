using System.Security.Cryptography;
using System.Text;
using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Checks normalized market structure and explicit expected bar coverage.</summary>
public static class DatasetValidator
{
    /// <summary>Rejects malformed bars, sessions, and supplementary events; coverage is checked separately.</summary>
    public static void Validate(MarketDataset data)
    {
        if (data.SchemaVersion != 1 || data.Interval.Minutes <= 0 || data.Bars.Count == 0)
            throw new InvalidDataException("Dataset requires schemaVersion 1, a positive interval, and bars.");
        DateTimeOffset? sessionClose = null;
        foreach (var session in data.Sessions)
        {
            if (session.Open.Offset != TimeSpan.Zero || session.Close.Offset != TimeSpan.Zero || session.Open >= session.Close || session.Open < sessionClose)
                throw new InvalidDataException("Sessions must be ordered, nonoverlapping UTC intervals.");
            sessionClose = session.Close;
        }
        foreach (var group in data.Bars.GroupBy(b => b.Instrument))
        {
            if (string.IsNullOrWhiteSpace(group.Key.Venue) || string.IsNullOrWhiteSpace(group.Key.Symbol) || !Enum.IsDefined(group.Key.AssetClass))
                throw new InvalidDataException("Instrument requires venue, asset class, and symbol.");
            DateTimeOffset? previous = null;
            foreach (var bar in group)
            {
                if (bar.OpenTime.Offset != TimeSpan.Zero || bar.CloseTime.Offset != TimeSpan.Zero || bar.OpenTime >= bar.CloseTime ||
                    bar.Open <= 0 || bar.Close <= 0 || bar.Low <= 0 || bar.High < Math.Max(bar.Open, bar.Close) ||
                    bar.Low > Math.Min(bar.Open, bar.Close) || bar.Volume < 0 || previous > bar.OpenTime)
                    throw new InvalidDataException($"Invalid or unordered bar for {group.Key.Key} at {bar.OpenTime:O}.");
                if (previous == bar.CloseTime) throw new InvalidDataException("Duplicate bar.");
                previous = bar.CloseTime;
                if (group.Key.AssetClass == AssetClass.Equity && !data.Sessions.Any(s => s.Open <= bar.OpenTime && s.Close >= bar.CloseTime &&
                    bar.SessionOpen == (bar.OpenTime == s.Open) && bar.SessionClose == (bar.CloseTime == s.Close)))
                    throw new InvalidDataException("Equity bars must match explicit exchange sessions and boundary flags.");
            }
        }
        if (data.CorporateActions.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != data.CorporateActions.Count)
            throw new InvalidDataException("Duplicate corporate action IDs.");
        foreach (var a in data.CorporateActions)
        {
            if (!Enum.IsDefined(a.Type) || a.Instrument.AssetClass != AssetClass.Equity || a.Time.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(a.Id) ||
                a.Type == ActionType.Split && a.Ratio is not > 0 || a.Type == ActionType.Dividend && a.Amount is null or < 0 ||
                a.Type == ActionType.SymbolChange && (a.Successor is null || a.Ratio is not (null or 1)) ||
                a.Type == ActionType.Merger && (a.Amount is null && a.Successor is null || a.Successor is not null && a.Ratio is not > 0) ||
                a.Type == ActionType.Delisting && a.Amount is null || a.Amount < 0)
                throw new InvalidDataException($"Unresolved corporate action {a.Id}: explicit terms required.");
            if (a.Successor is not null && (a.Successor == a.Instrument || a.Successor.AssetClass != AssetClass.Equity))
                throw new InvalidDataException($"Corporate action {a.Id} requires a different successor equity.");
            if (a.Type == ActionType.Merger && a.Successor is null && a.Ratio is not null ||
                a.Type == ActionType.Delisting && (a.Successor is not null || a.Ratio is not null))
                throw new InvalidDataException($"Corporate action {a.Id} contains inconsistent settlement terms.");
        }
        foreach (var f in data.Funding)
            if (f.Instrument.AssetClass != AssetClass.LinearPerpetual || f.Time.Offset != TimeSpan.Zero || f.MarkPrice <= 0)
                throw new InvalidDataException("Invalid funding event.");
        if (data.Funding.GroupBy(f => (f.Instrument, f.Time)).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate funding event.");
    }

    /// <summary>Builds bar boundaries for a range, clipping equities to supplied sessions and perpetuals to the range end.</summary>
    public static IReadOnlyList<(DateTimeOffset Open, DateTimeOffset Close)> Expected(Instrument instrument, BarInterval interval,
        DateTimeOffset start, DateTimeOffset end, IReadOnlyList<MarketSession> sessions)
    {
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        if (instrument.AssetClass == AssetClass.Equity)
        {
            foreach (var session in sessions.Where(s => s.Close > start && s.Open < end).OrderBy(s => s.Open))
                for (var t = session.Open; t < session.Close; t = t.AddMinutes(interval.Minutes))
                {
                    var close = interval.Minutes >= 1440 ? session.Close : Min(t.AddMinutes(interval.Minutes), session.Close);
                    if (t >= start && close <= end) result.Add((t, close));
                }
        }
        else
            for (var t = start; t < end; t = t.AddMinutes(interval.Minutes)) result.Add((t, Min(t.AddMinutes(interval.Minutes), end)));
        return result;
    }
    /// <summary>Returns the earlier instant when clipping a bar to a session or requested range.</summary>
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    /// <summary>Throws when any expected boundary pair is missing for the requested instrument and range.</summary>
    public static void RequireCoverage(MarketDataset data, Instrument instrument, DateTimeOffset start, DateTimeOffset end)
    {
        var actual = data.Bars.Where(b => b.Instrument == instrument).Select(b => (b.OpenTime, b.CloseTime)).ToHashSet();
        var missing = Expected(instrument, data.Interval, start, end, data.Sessions).Where(x => !actual.Contains(x)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"Missing {missing.Length} bars for {instrument.Key}; first gap {missing[0].Open:O}. Import the missing history.");
    }
}

/// <summary>Generates seeded synthetic geometric Brownian motion bars without funding or corporate actions.</summary>
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
            instrument.AssetClass == AssetClass.Equity ? sessions!.Max(s => s.Close) : start.AddMinutes((long)count * interval.Minutes), sessions ?? []).Take(count).ToArray();
        if (boundaries.Length < count) throw new ArgumentException("Insufficient sessions for generated bar count.");
        var price = (double)initialPrice;
        var bars = new List<Bar>();
        foreach (var (open, close) in boundaries)
        {
            var first = price; var high = price; var low = price;
            var dt = instrument.AssetClass == AssetClass.Equity
                ? interval.Minutes >= 1440 ? 1.0 / 252 / 4 : (close - open).TotalMinutes / (252 * 390 * 4)
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
        return new() { Provider = "gbm", Version = $"gbm-v1-seed-{seed}", Interval = interval, Bars = bars, Sessions = sessions?.ToList() ?? [],
            Notes = ["Synthetic GBM prices. No corporate actions or funding generated; funding is zero unless supplementary events are imported."] };
    }
}

/// <summary>Specifies an instrument, interval, UTC coverage range, and explicit data revision for a provider.</summary>
public sealed record DataRequest(Instrument Instrument, BarInterval Interval, DateTimeOffset Start, DateTimeOffset End, string Version = "1");
/// <summary>Supplies normalized historical market data for an explicit instrument and coverage request.</summary>
public interface IMarketDataProvider
{
    /// <summary>Gets the provider identity, including feed where applicable, used in cache keys.</summary>
    string Name { get; }
    /// <summary>Fetches normalized data for the requested range with cooperative cancellation.</summary>
    Task<MarketDataset> FetchAsync(DataRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Caches versioned provider data on disk, fetching missing ranges; callers must ensure a single writer.</summary>
public sealed class DataCache(string directory)
{
    /// <summary>Reuses validated cached bars, fetches missing ranges, requires coverage, and atomically replaces the cache before returning the requested slice.</summary>
    public async Task<MarketDataset> GetAsync(IMarketDataProvider provider, DataRequest request, IReadOnlyList<MarketSession> sessions,
        CancellationToken cancellationToken = default)
    {
        if (request.Start >= request.End || request.Start.Offset != TimeSpan.Zero || request.End.Offset != TimeSpan.Zero || request.Interval.Minutes <= 0)
            throw new ArgumentException("Coverage must be a nonempty UTC range.");
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider.Name}|{request.Instrument.Key}|{request.Interval}|{request.Version}")));
        var path = Path.Combine(directory, key + ".json");
        var data = File.Exists(path) ? Json.Read<MarketDataset>(path) : new MarketDataset { Provider = provider.Name, Version = request.Version, Interval = request.Interval, Sessions = sessions.ToList() };
        if (data.Bars.Count > 0) DatasetValidator.Validate(data);
        if (data.Interval != request.Interval || data.Bars.Any(b => b.Instrument != request.Instrument))
            throw new InvalidDataException("Cached instrument or interval does not match the request.");
        var expected = DatasetValidator.Expected(request.Instrument, request.Interval, request.Start, request.End, sessions);
        var actual = data.Bars.Select(b => (b.OpenTime, b.CloseTime)).ToHashSet();
        // Coalesce adjacent missing bars to avoid a request per bar.
        var ranges = new List<(DateTimeOffset Open, DateTimeOffset Close)>();
        var previousMissing = false;
        foreach (var gap in expected)
        {
            if (actual.Contains(gap)) { previousMissing = false; continue; }
            if (previousMissing) ranges[^1] = (ranges[^1].Open, gap.Close);
            else ranges.Add(gap);
            previousMissing = true;
        }
        foreach (var gap in ranges)
        {
            var fetched = await provider.FetchAsync(request with { Start = gap.Open, End = gap.Close }, cancellationToken);
            DatasetValidator.Validate(fetched);
            if (fetched.Interval != request.Interval || fetched.Bars.Any(b => b.Instrument != request.Instrument))
                throw new InvalidDataException("Provider returned a different instrument or interval.");
            data = data with
            {
                Bars = data.Bars.Concat(fetched.Bars).DistinctBy(b => (b.Instrument, b.OpenTime)).OrderBy(b => b.OpenTime).ToList(),
                CorporateActions = data.CorporateActions.Concat(fetched.CorporateActions).DistinctBy(a => a.Id).OrderBy(a => a.Time).ToList(),
                Funding = data.Funding.Concat(fetched.Funding).DistinctBy(f => (f.Instrument, f.Time)).OrderBy(f => f.Time).ToList(),
                Notes = data.Notes.Concat(fetched.Notes).Distinct().ToList(),
                Sessions = data.Sessions.Concat(sessions).Concat(fetched.Sessions).Distinct().OrderBy(s => s.Open).ToList()
            };
        }
        DatasetValidator.Validate(data);
        DatasetValidator.RequireCoverage(data, request.Instrument, request.Start, request.End);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Json.Write(temporary, data); File.Move(temporary, path, true);
        return data with
        {
            Bars = data.Bars.Where(b => b.OpenTime >= request.Start && b.CloseTime <= request.End).ToList(),
            CorporateActions = data.CorporateActions.Where(a => a.Time >= request.Start && a.Time <= request.End).ToList(),
            Funding = data.Funding.Where(f => f.Time >= request.Start && f.Time <= request.End).ToList()
        };
    }
}
