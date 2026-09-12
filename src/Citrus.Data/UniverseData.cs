using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Defines the instruments that a universe-driven backtest may trade.</summary>
public sealed record TradingUniverse
{
    public List<Instrument> Instruments { get; init; } = [];

    /// <summary>Rejects empty, duplicate, malformed, or unsupported instrument definitions before any downloads.</summary>
    public void Validate()
    {
        if (Instruments.Count == 0 || Instruments.Distinct().Count() != Instruments.Count ||
            Instruments.Any(i => i is null || string.IsNullOrWhiteSpace(i.Venue) || string.IsNullOrWhiteSpace(i.Symbol) ||
                !Enum.IsDefined(i.AssetClass)))
            throw new ArgumentException("Universe requires distinct instruments with venue, assetClass, and symbol.");
    }
}

/// <summary>Resolves a universe from the local provider cache and downloads missing history.</summary>
public sealed class UniverseData(HttpClient client, string cacheDirectory)
{
    /// <summary>Loads complete hourly or daily history and retains the universe in the reproducible dataset.</summary>
    public async Task<MarketDataset> LoadAsync(TradingUniverse universe, DateTimeOffset start, DateTimeOffset end,
        BarInterval interval, string version = "1", string feed = "iex", CancellationToken token = default)
    {
        universe.Validate();
        if (start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero || start >= end ||
            interval != BarInterval.Hourly && interval != BarInterval.Daily || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(feed))
            throw new ArgumentException("Universe backtests require a nonempty UTC range, 1h or 1d interval, version, and feed.");
        if (universe.Instruments.Any(i => i.AssetClass == AssetClass.LinearPerpetual) &&
            (start.Ticks % TimeSpan.FromMinutes(interval.Minutes).Ticks != 0 || end.Ticks % TimeSpan.FromMinutes(interval.Minutes).Ticks != 0))
            throw new ArgumentException("Perpetual dates must align to complete UTC bars.");
        Directory.CreateDirectory(cacheDirectory);
        var sessions = universe.Instruments.Any(i => i.AssetClass == AssetClass.Equity)
            ? await CalendarAsync(start, end, token) : [];
        var datasets = new List<MarketDataset>();
        foreach (var instrument in universe.Instruments.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            if (DatasetValidator.Expected(instrument, interval, start, end, sessions).Count == 0)
                throw new InvalidDataException($"No complete trading bars in the requested range for {instrument.Key}.");
            IMarketDataProvider provider = instrument.AssetClass == AssetClass.Equity
                ? new DeferredAlpaca(client, sessions, feed) : new HyperliquidProvider(client);
            datasets.Add(await new DataCache(cacheDirectory).GetAsync(provider, new(instrument, interval, start, end, version), sessions, token));
        }
        var result = new MarketDataset
        {
            Provider = string.Join("+", datasets.Select(d => d.Provider).Distinct().Order()), Version = version, Interval = interval,
            Universe = universe.Instruments.ToList(), Sessions = sessions,
            Bars = datasets.SelectMany(d => d.Bars).OrderBy(b => b.OpenTime).ThenBy(b => b.Instrument.Key, StringComparer.Ordinal).ToList(),
            CorporateActions = datasets.SelectMany(d => d.CorporateActions).DistinctBy(a => a.Id).OrderBy(a => a.Time).ToList(),
            Funding = datasets.SelectMany(d => d.Funding).OrderBy(f => f.Time).ToList(),
            Notes = datasets.SelectMany(d => d.Notes).Distinct().ToList()
        };
        DatasetValidator.Validate(result);
        return result;
    }

    /// <summary>Caches calendar responses by covered UTC date ranges, including holidays with no sessions.</summary>
    private async Task<List<MarketSession>> CalendarAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var path = Path.Combine(cacheDirectory, "alpaca-calendar.json");
        var entries = File.Exists(path) ? Json.Read<List<CalendarCoverage>>(path) : [];
        var first = DateOnly.FromDateTime(start.UtcDateTime);
        var last = DateOnly.FromDateTime(end.UtcDateTime);
        var entry = entries.FirstOrDefault(e => e.Start <= first && e.End >= last);
        if (entry is null)
        {
            var (key, secret) = Credentials();
            entry = new(first, last, await AlpacaProvider.CalendarAsync(client, key, secret, first, last, token));
            entries.Add(entry);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Json.Write(temporary, entries);
            File.Move(temporary, path, true);
        }
        return entry.Sessions.Where(s => s.Close > start && s.Open < end).ToList();
    }

    /// <summary>Reads authentication only when an Alpaca network request is required.</summary>
    private static (string Key, string Secret) Credentials() => (
        Environment.GetEnvironmentVariable("APCA_API_KEY_ID") ?? throw new InvalidOperationException("Set APCA_API_KEY_ID to download missing Alpaca data."),
        Environment.GetEnvironmentVariable("APCA_API_SECRET_KEY") ?? throw new InvalidOperationException("Set APCA_API_SECRET_KEY to download missing Alpaca data."));

    /// <summary>Records authoritative calendar coverage even across weekends and holidays.</summary>
    public sealed record CalendarCoverage(DateOnly Start, DateOnly End, List<MarketSession> Sessions);

    /// <summary>Exposes the Alpaca cache identity without requiring credentials on cache hits.</summary>
    private sealed class DeferredAlpaca(HttpClient client, IReadOnlyList<MarketSession> sessions, string feed) : IMarketDataProvider
    {
        public string Name => "alpaca-" + feed;
        /// <summary>Creates the authenticated provider only for missing history.</summary>
        public Task<MarketDataset> FetchAsync(DataRequest request, CancellationToken cancellationToken = default)
        {
            var (key, secret) = Credentials();
            return new AlpacaProvider(client, key, secret, sessions, feed).FetchAsync(request, cancellationToken);
        }
    }
}
