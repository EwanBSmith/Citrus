using System.Globalization;
using System.Text.Json;
using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Downloads adjusted daily equity bars and binds them to explicit exchange sessions.</summary>
public sealed class AlpacaProvider(HttpClient client, string keyId, string secret, IReadOnlyList<MarketSession> sessions, string feed = "iex") : IMarketDataProvider
{
    /// <summary>Gets the provider identity used for provenance and cache partitioning.</summary>
    public string Name => "alpaca-" + feed;
    /// <summary>Creates an authenticated Alpaca GET request for each retry; the caller owns the returned document.</summary>
    private Task<JsonDocument> Get(string url, CancellationToken token) => ProviderHttp.SendAsync(client, () =>
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Add("APCA-API-KEY-ID", keyId); message.Headers.Add("APCA-API-SECRET-KEY", secret);
        return message;
    }, token);
    /// <summary>Fetches daily history and normalizes provider records for the requested asset class.</summary>
    public async Task<MarketDataset> FetchAsync(DataRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Instrument.AssetClass != AssetClass.Equity) throw new ArgumentException("Alpaca adapter requires equities.");
        if (request.Interval != BarInterval.Daily) throw new ArgumentException("Only daily bars are supported.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        // Daily records are timestamped at midnight in New York, before the exchange opens.
        var from = TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTime(request.Start, zone).Date, zone);
        var rows = new List<(DateTimeOffset Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume)>();
        string? page = null;
        var tokens = new HashSet<string>();
        do
        {
            var url = $"https://data.alpaca.markets/v2/stocks/bars?symbols={Uri.EscapeDataString(request.Instrument.Symbol)}&timeframe=1Day&adjustment=all&feed={Uri.EscapeDataString(feed)}&start={Uri.EscapeDataString(from.ToString("O"))}&end={Uri.EscapeDataString(request.End.ToString("O"))}&limit=10000";
            if (page is not null) url += "&page_token=" + Uri.EscapeDataString(page);
            using var document = await Get(url, cancellationToken);
            if (document.RootElement.GetProperty("bars").TryGetProperty(request.Instrument.Symbol, out var values))
                foreach (var row in values.EnumerateArray()) rows.Add((row.GetProperty("t").GetDateTimeOffset().ToUniversalTime(), ProviderHttp.Number(row, "o"),
                    ProviderHttp.Number(row, "h"), ProviderHttp.Number(row, "l"), ProviderHttp.Number(row, "c"), ProviderHttp.Number(row, "v")));
            page = document.RootElement.TryGetProperty("next_page_token", out var next) ? next.GetString() : null;
            if (page is not null && !tokens.Add(page)) throw new InvalidDataException("Repeated Alpaca page token.");
        } while (page is not null);
        if (rows.GroupBy(r => r.Time).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate provider timestamps.");
        var bars = new List<Bar>();
        foreach (var (open, close) in DatasetValidator.Expected(request.Instrument, request.Interval, request.Start, request.End, sessions))
        {
            var date = TimeZoneInfo.ConvertTime(open, zone).Date;
            var day = rows.Where(r => TimeZoneInfo.ConvertTime(r.Time, zone).Date == date).ToArray();
            if (day.Length == 0) continue;
            if (day.Length != 1) throw new InvalidDataException("Expected one provider bar per exchange date.");
            var row = day[0];
            bars.Add(new(request.Instrument, open, close, row.Open, row.High, row.Low, row.Close, row.Volume, true, true));
        }
        return new() { Provider = Name, Interval = request.Interval, Bars = bars, Sessions = sessions.ToList(),
            Notes = ["Provider daily bars (adjustment=all) mapped to exchange sessions. Returns include provider price adjustments, with no separate corporate-action accounting."] };
    }
    /// <summary>Fetches exchange sessions and converts New York local boundaries to UTC with daylight-saving rules.</summary>
    public static async Task<List<MarketSession>> CalendarAsync(HttpClient client, string keyId, string secret, DateOnly start, DateOnly end, CancellationToken token = default)
    {
        var provider = new AlpacaProvider(client, keyId, secret, []);
        using var doc = await provider.Get($"https://paper-api.alpaca.markets/v2/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}", token);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        return doc.RootElement.EnumerateArray().Select(row =>
        {
            // Combine the calendar date and local session time, then apply New York daylight-saving rules to obtain UTC.
            DateTimeOffset ConvertTime(string field)
            {
                var local = DateTime.Parse(row.GetProperty("date").GetString() + "T" + row.GetProperty(field).GetString(), CultureInfo.InvariantCulture);
                return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            }
            return new MarketSession(ConvertTime("open"), ConvertTime("close"));
        }).ToList();
    }
}
