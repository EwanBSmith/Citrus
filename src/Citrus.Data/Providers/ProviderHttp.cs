using System.Globalization;
using System.Net;
using System.Text.Json;

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
