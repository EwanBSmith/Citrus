using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Caches provider data on disk, fetching missing ranges; callers must ensure a single writer.</summary>
public sealed class DataCache(string directory)
{
    /// <summary>Gets the persistent user-local cache used by normal CLI and desktop backtests.</summary>
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Citrus", "HistoricalData");

    /// <summary>Loads and combines matching cached datasets without performing provider I/O.</summary>
    public static MarketDataset Load(string directory, BarInterval interval, DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        if (interval != BarInterval.Daily || start is not null && start.Value.Offset != TimeSpan.Zero ||
            end is not null && end.Value.Offset != TimeSpan.Zero || start is not null && end is not null && start >= end)
            throw new ArgumentException("Historical cache selection requires daily bars and a valid UTC range.");
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Historical data cache does not exist: {directory}");
        var datasets = new List<MarketDataset>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            MarketDataset data;
            try { data = Json.Read<MarketDataset>(path); DatasetValidator.Validate(data); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or ArgumentException or NullReferenceException)
            { throw new InvalidDataException($"Historical cache entry is invalid: {path}", error); }
            if (data.Interval != interval) continue;
            var bars = data.Bars.Where(b => (start is null || b.OpenTime >= start) && (end is null || b.CloseTime <= end)).ToList();
            if (bars.Count == 0) continue;
            datasets.Add(data with
            {
                Bars = bars,
                Sessions = data.Sessions,
                Funding = data.Funding.Where(f => (start is null || f.Time >= start) && (end is null || f.Time < end)).ToList()
            });
        }
        if (datasets.Count == 0)
            throw new InvalidDataException($"The main historical cache contains no {interval.Name} data matching the requested range.");
        var result = new MarketDataset
        {
            Provider = string.Join("+", datasets.Select(d => d.Provider).Distinct().Order(StringComparer.Ordinal)),
            Interval = interval,
            Bars = datasets.SelectMany(d => d.Bars).Distinct().OrderBy(b => b.OpenTime).ThenBy(b => b.Instrument.Key, StringComparer.Ordinal).ToList(),
            Sessions = datasets.SelectMany(d => d.Sessions).Distinct().OrderBy(s => s.Open).ToList(),
            Funding = datasets.SelectMany(d => d.Funding).DistinctBy(f => (f.Instrument, f.Time)).OrderBy(f => f.Time).ToList(),
            Notes = datasets.SelectMany(d => d.Notes).Distinct().ToList()
        };
        DatasetValidator.Validate(result);
        foreach (var group in result.Bars.GroupBy(b => b.Instrument))
            DatasetValidator.RequireCoverage(result, group.Key, start ?? group.Min(b => b.OpenTime), end ?? group.Max(b => b.CloseTime));
        return result;
    }

    /// <summary>Reuses validated cached bars, fetches missing ranges, requires coverage, and atomically replaces the cache before returning the requested slice.</summary>
    public async Task<MarketDataset> GetAsync(IMarketDataProvider provider, DataRequest request, IReadOnlyList<MarketSession> sessions,
        CancellationToken cancellationToken = default)
    {
        if (request.Start >= request.End || request.Start.Offset != TimeSpan.Zero || request.End.Offset != TimeSpan.Zero || request.Interval != BarInterval.Daily)
            throw new ArgumentException("Coverage must be a nonempty UTC range.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"symbol-{Uri.EscapeDataString(request.Instrument.Symbol.ToUpperInvariant())}-{request.Interval.Minutes}.json");
        // Discover legacy hashed files as well as readable symbol files without renaming user data.
        {
            var matches = Directory.EnumerateFiles(directory, "*.json").Where(candidate =>
            {
                var entry = Json.Read<MarketDataset>(candidate);
                return entry.Interval == request.Interval && entry.Bars.Any(b => string.Equals(b.Instrument.Symbol, request.Instrument.Symbol, StringComparison.OrdinalIgnoreCase));
            }).ToArray();
            if (matches.Length > 1) throw new InvalidDataException($"Multiple cache files contain symbol '{request.Instrument.Symbol}' at {request.Interval.Name}. Consolidate them before downloading more history.");
            if (matches.Length == 1) path = matches[0];
        }
        var data = File.Exists(path) ? Json.Read<MarketDataset>(path) : new MarketDataset { Provider = provider.Name, Interval = request.Interval, Sessions = sessions.ToList() };
        if (data.Bars.Count > 0) DatasetValidator.Validate(data);
        if (data.Bars.Count > 0)
        {
            var stored = data.Bars[0].Instrument;
            if (!string.Equals(stored.Symbol, request.Instrument.Symbol, StringComparison.OrdinalIgnoreCase) || stored.AssetClass != request.Instrument.AssetClass)
                throw new InvalidDataException("Cached symbol or asset class does not match the request.");
            request = request with { Instrument = stored };
        }
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
        // Adjustments can rewrite all earlier prices; never append a different adjustment basis to an equity snapshot.
        if (ranges.Count > 0 && data.Bars.Count > 0 && request.Instrument.AssetClass == AssetClass.Equity)
            throw new InvalidDataException("Adjusted equity history must be downloaded as one snapshot. Move the existing symbol cache file out of the cache and download the full desired range again.");
        foreach (var gap in ranges)
        {
            if (data.Bars.Count > 0 && data.Provider != provider.Name)
                throw new InvalidDataException($"History for {request.Instrument.Symbol} already uses {data.Provider}. Use that source to extend it or explicitly replace the existing history; sources are not silently mixed.");
            var fetched = await provider.FetchAsync(request with { Start = gap.Open, End = gap.Close }, cancellationToken);
            DatasetValidator.RequireCoverage(fetched with { Provider = provider.Name, Sessions = sessions.ToList() }, request.Instrument, gap.Open, gap.Close);
            DatasetValidator.Validate(fetched);
            if (fetched.Interval != request.Interval || fetched.Bars.Any(b => b.Instrument != request.Instrument))
                throw new InvalidDataException("Provider returned a different instrument or interval.");
            data = data with
            {
                Bars = data.Bars.Concat(fetched.Bars).DistinctBy(b => (b.Instrument, b.OpenTime)).OrderBy(b => b.OpenTime).ToList(),
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
            Funding = data.Funding.Where(f => f.Time >= request.Start && f.Time <= request.End).ToList()
        };
    }
}
