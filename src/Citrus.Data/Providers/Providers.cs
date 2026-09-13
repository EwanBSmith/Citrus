using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Shares bounded retries and invariant numeric parsing across market data adapters.</summary>
internal static class ProviderHttp
{
    /// <summary>Sends a fresh request on each attempt, retrying throttling and server errors up to three times; the caller disposes the returned document.</summary>
    public static async Task<JsonDocument> SendAsync(HttpClient client, Func<HttpRequestMessage> request, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var message = request();
            using var response = await client.SendAsync(message, token);
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (attempt < 3 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
            { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token); continue; }
            // Never echo provider response bodies or authentication headers.
            throw new HttpRequestException($"Market data request to {message.RequestUri!.Host}{message.RequestUri.AbsolutePath} failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
        }
    }
    /// <summary>Reads a provider decimal supplied either as a JSON number or an invariant numeric string.</summary>
    public static decimal Number(JsonElement item, string name) => item.GetProperty(name).ValueKind == JsonValueKind.String
        ? decimal.Parse(item.GetProperty(name).GetString()!, CultureInfo.InvariantCulture) : item.GetProperty(name).GetDecimal();
}

/// <summary>Downloads perpetual candles and funding, approximating funding marks from observable candle prices.</summary>
public sealed class HyperliquidProvider(HttpClient client) : IMarketDataProvider
{
    /// <summary>Gets the provider identity used for provenance and cache partitioning.</summary>
    public string Name => "hyperliquid";
    /// <summary>Posts a JSON info request using shared retry handling; the caller owns the returned document.</summary>
    private Task<JsonDocument> Post(object payload, CancellationToken token) => ProviderHttp.SendAsync(client,
        () => new(HttpMethod.Post, "https://api.hyperliquid.xyz/info") { Content = JsonContent.Create(payload) }, token);
    /// <summary>Fetches supported hourly or daily history and normalizes provider records for the requested asset class.</summary>
    public async Task<MarketDataset> FetchAsync(DataRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Instrument.AssetClass != AssetClass.LinearPerpetual) throw new ArgumentException("Hyperliquid adapter requires linear perpetuals.");
        if (request.Interval != BarInterval.Daily && request.Interval != BarInterval.Hourly) throw new ArgumentException("Provider supports 1h and 1d.");
        using var candles = await Post(new { type = "candleSnapshot", req = new { coin = request.Instrument.Symbol, interval = request.Interval.Name,
            startTime = request.Start.ToUnixTimeMilliseconds(), endTime = request.End.ToUnixTimeMilliseconds() - 1 } }, cancellationToken);
        var bars = candles.RootElement.EnumerateArray().Select(c =>
        {
            var open = DateTimeOffset.FromUnixTimeMilliseconds(c.GetProperty("t").GetInt64());
            return new Bar(request.Instrument, open, open.AddMinutes(request.Interval.Minutes), ProviderHttp.Number(c, "o"),
                ProviderHttp.Number(c, "h"), ProviderHttp.Number(c, "l"), ProviderHttp.Number(c, "c"), ProviderHttp.Number(c, "v"));
        }).Where(b => b.OpenTime >= request.Start && b.CloseTime <= request.End).OrderBy(b => b.OpenTime).ToList();
        var data = new MarketDataset { Provider = Name, Interval = request.Interval, Bars = bars };
        DatasetValidator.RequireCoverage(data, request.Instrument, request.Start, request.End);
        // Funding history has rates but no historical mark. Use the latest observable bar price and record this approximation.
        var funding = new List<FundingEvent>();
        var cursor = request.Start.ToUnixTimeMilliseconds();
        while (cursor <= request.End.ToUnixTimeMilliseconds())
        {
            using var page = await Post(new { type = "fundingHistory", coin = request.Instrument.Symbol,
                startTime = cursor, endTime = request.End.ToUnixTimeMilliseconds() }, cancellationToken);
            var rows = page.RootElement.EnumerateArray().ToArray();
            if (rows.Length == 0) break;
            var maximum = cursor - 1;
            foreach (var row in rows)
            {
                var milliseconds = row.GetProperty("time").GetInt64();
                if (milliseconds < cursor) throw new InvalidDataException("Funding pagination did not advance.");
                maximum = Math.Max(maximum, milliseconds);
                var time = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                var completed = bars.LastOrDefault(b => b.CloseTime <= time);
                var price = completed?.Close ?? bars.First().Open;
                if (time <= request.End) funding.Add(new(request.Instrument, time, ProviderHttp.Number(row, "fundingRate"), price));
            }
            cursor = maximum + 1;
        }
        for (var time = request.Start.AddHours(1); time <= request.End; time = time.AddHours(1))
            if (!funding.Any(f => f.Time == time)) throw new InvalidDataException($"Missing funding at {time:O}; import explicit funding history.");
        return data with { Funding = funding.OrderBy(f => f.Time).ToList(), Notes = ["Funding notionals use latest completed bar close (first open at dataset start); import historical marks for exact notionals."] };
    }
}

/// <summary>Aggregates adjusted equity minute bars within explicit sessions.</summary>
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
    /// <summary>Fetches supported hourly or daily history and normalizes provider records for the requested asset class.</summary>
    public async Task<MarketDataset> FetchAsync(DataRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Instrument.AssetClass != AssetClass.Equity) throw new ArgumentException("Alpaca adapter requires equities.");
        if (request.Interval != BarInterval.Daily && request.Interval != BarInterval.Hourly) throw new ArgumentException("Provider supports 1h and 1d.");
        // Minute bars are aggregated from the regular session open, avoiding extended-hours contamination and hour alignment ambiguity.
        var rows = new List<(DateTimeOffset Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume)>();
        string? page = null;
        var tokens = new HashSet<string>();
        do
        {
            var url = $"https://data.alpaca.markets/v2/stocks/bars?symbols={Uri.EscapeDataString(request.Instrument.Symbol)}&timeframe=1Min&adjustment=all&feed={Uri.EscapeDataString(feed)}&start={Uri.EscapeDataString(request.Start.ToString("O"))}&end={Uri.EscapeDataString(request.End.ToString("O"))}&limit=10000";
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
            var slice = rows.Where(r => r.Time >= open && r.Time < close).OrderBy(r => r.Time).ToArray();
            if (slice.Length == 0) continue;
            bars.Add(new(request.Instrument, open, close, slice[0].Open, slice.Max(r => r.High), slice.Min(r => r.Low), slice[^1].Close,
                slice.Sum(r => r.Volume), sessions.Any(s => s.Open == open), sessions.Any(s => s.Close == close)));
        }
        return new() { Provider = Name, Interval = request.Interval, Bars = bars, Sessions = sessions.ToList(),
            Notes = ["Regular-session aggregation of adjusted minute bars (adjustment=all); missing trade minutes are not synthesized. Returns include provider price adjustments, with no separate corporate-action accounting."] };
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
