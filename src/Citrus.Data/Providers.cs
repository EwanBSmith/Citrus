using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Citrus.Contracts;

namespace Citrus.Data;

internal static class ProviderHttp
{
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
            throw new HttpRequestException($"Market data request failed with HTTP {(int)response.StatusCode}.");
        }
    }
    public static decimal Number(JsonElement item, string name) => item.GetProperty(name).ValueKind == JsonValueKind.String
        ? decimal.Parse(item.GetProperty(name).GetString()!, CultureInfo.InvariantCulture) : item.GetProperty(name).GetDecimal();
}

public sealed class HyperliquidProvider(HttpClient client) : IMarketDataProvider
{
    public string Name => "hyperliquid";
    private Task<JsonDocument> Post(object payload, CancellationToken token) => ProviderHttp.SendAsync(client,
        () => new(HttpMethod.Post, "https://api.hyperliquid.xyz/info") { Content = JsonContent.Create(payload) }, token);
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
        var data = new MarketDataset { Provider = Name, Version = request.Version, Interval = request.Interval, Bars = bars };
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

public sealed class AlpacaProvider(HttpClient client, string keyId, string secret, IReadOnlyList<MarketSession> sessions, string feed = "iex") : IMarketDataProvider
{
    public string Name => "alpaca-" + feed;
    private Task<JsonDocument> Get(string url, CancellationToken token) => ProviderHttp.SendAsync(client, () =>
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Add("APCA-API-KEY-ID", keyId); message.Headers.Add("APCA-API-SECRET-KEY", secret);
        return message;
    }, token);
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
            var url = $"https://data.alpaca.markets/v2/stocks/bars?symbols={Uri.EscapeDataString(request.Instrument.Symbol)}&timeframe=1Min&adjustment=raw&feed={Uri.EscapeDataString(feed)}&start={Uri.EscapeDataString(request.Start.ToString("O"))}&end={Uri.EscapeDataString(request.End.ToString("O"))}&limit=10000";
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
        var actions = await CorporateActionsAsync(request, cancellationToken);
        return new() { Provider = Name, Version = request.Version, Interval = request.Interval, Bars = bars, Sessions = sessions.ToList(), CorporateActions = actions,
            Notes = ["Regular-session aggregation of raw minute bars; missing trade minutes are not synthesized. Corporate action API filters process dates; verify effective-date coverage with supplementary events."] };
    }
    public async Task<List<CorporateAction>> CorporateActionsAsync(DataRequest request, CancellationToken token = default)
    {
        var result = new List<CorporateAction>(); string? page = null;
        var tokens = new HashSet<string>();
        do
        {
            var url = $"https://data.alpaca.markets/v1/corporate-actions?symbols={Uri.EscapeDataString(request.Instrument.Symbol)}&start={request.Start:yyyy-MM-dd}&end={request.End:yyyy-MM-dd}&limit=1000&data_quality=all";
            if (page is not null) url += "&page_token=" + Uri.EscapeDataString(page);
            using var doc = await Get(url, token);
            foreach (var group in doc.RootElement.GetProperty("corporate_actions").EnumerateObject())
                foreach (var row in group.Value.EnumerateArray())
                {
                    string S(string name) => row.GetProperty(name).GetString() ?? throw new InvalidDataException($"Corporate action missing {name}.");
                    decimal N(string name) => ProviderHttp.Number(row, name);
                    var sourceField = group.Name is "cash_mergers" or "stock_mergers" or "stock_and_cash_mergers" ? "acquiree_symbol"
                        : group.Name == "name_changes" ? "old_symbol" : "symbol";
                    if (row.TryGetProperty(sourceField, out var sourceSymbol) && sourceSymbol.GetString() != request.Instrument.Symbol) continue;
                    var dateField = row.TryGetProperty("ex_date", out _) ? "ex_date" : row.TryGetProperty("effective_date", out _) ? "effective_date" : "process_date";
                    var date = DateOnly.Parse(S(dateField), CultureInfo.InvariantCulture);
                    var session = sessions.FirstOrDefault(s => DateOnly.FromDateTime(s.Open.UtcDateTime) == date)
                        ?? throw new InvalidDataException("Corporate action effective date lacks a session; supply normalized action data.");
                    var id = S("id");
                    var instrument = request.Instrument;
                    CorporateAction action = group.Name switch
                    {
                        "forward_splits" or "reverse_splits" => new(id, instrument, session.Open, ActionType.Split, Ratio: N("new_rate") / N("old_rate")),
                        "cash_dividends" => new(id, instrument, session.Open, ActionType.Dividend, Amount: N("rate")),
                        "cash_mergers" => new(id, instrument, session.Open, ActionType.Merger, Amount: N("rate")),
                        "stock_mergers" or "stock_and_cash_mergers" => new(id, instrument, session.Open, ActionType.Merger,
                            Amount: group.Name == "stock_and_cash_mergers" ? N("cash_rate") : 0, Ratio: N("acquirer_rate") / N("acquiree_rate"), Successor: instrument with { Symbol = S("acquirer_symbol") }),
                        "name_changes" => new(id, instrument, session.Open, ActionType.SymbolChange, Successor: instrument with { Symbol = S("new_symbol") }),
                        "worthless_removals" => new(id, instrument, session.Open, ActionType.Delisting, Amount: 0),
                        _ => throw new InvalidDataException($"Unsupported corporate action {group.Name}/{id}; supply explicit normalized events.")
                    };
                    if (action.Time >= request.Start && action.Time <= request.End) result.Add(action);
                }
            page = doc.RootElement.TryGetProperty("next_page_token", out var next) ? next.GetString() : null;
            if (page is not null && !tokens.Add(page)) throw new InvalidDataException("Repeated corporate-action page token.");
        } while (page is not null);
        return result;
    }
    public static async Task<List<MarketSession>> CalendarAsync(HttpClient client, string keyId, string secret, DateOnly start, DateOnly end, CancellationToken token = default)
    {
        var provider = new AlpacaProvider(client, keyId, secret, []);
        using var doc = await provider.Get($"https://paper-api.alpaca.markets/v2/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}", token);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        return doc.RootElement.EnumerateArray().Select(row =>
        {
            DateTimeOffset ConvertTime(string field)
            {
                var local = DateTime.Parse(row.GetProperty("date").GetString() + "T" + row.GetProperty(field).GetString(), CultureInfo.InvariantCulture);
                return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            }
            return new MarketSession(ConvertTime("open"), ConvertTime("close"));
        }).ToList();
    }
}
