using System.Net.Http.Json;
using System.Text.Json;
using Citrus.Trading;

namespace Citrus.Data;

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
