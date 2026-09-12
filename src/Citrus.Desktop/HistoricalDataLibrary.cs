using Citrus.Data;
using Citrus.Trading;

namespace Citrus.Desktop;

/// <summary>Inspects normalized local datasets and downloads history through the shared provider cache.</summary>
internal static class HistoricalDataLibrary
{
    /// <summary>Gets a persistent user-local library independent of individual backtest workspaces.</summary>
    internal static string DefaultDirectory => GlobalConfiguration.Load().ResolveHistoricalDataDirectory();

    /// <summary>Reads each JSON file independently so damaged datasets remain visible for management.</summary>
    internal static List<HistoricalDataEntry> Scan(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json").Order().Select(path =>
        {
            try
            {
                var data = Json.Read<MarketDataset>(path);
                DatasetValidator.Validate(data);
                return new HistoricalDataEntry(path, data.Provider, string.Join(", ", data.Bars.Select(b => b.Instrument.Key).Distinct()),
                    data.Interval.Name, data.Bars.Min(b => b.OpenTime).UtcDateTime, data.Bars.Max(b => b.CloseTime).UtcDateTime,
                    data.Bars.Count, new FileInfo(path).Length, "Valid", string.Join(Environment.NewLine, data.Notes));
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NullReferenceException)
            {
                return new HistoricalDataEntry(path, "", "", "", null, null, 0, 0, "Unreadable or invalid", "Repair or replace this dataset before using it.");
            }
        }).ToList();
    }

    /// <summary>Downloads missing bars with an exclusive library writer lock and the existing coverage checks.</summary>
    internal static async Task DownloadAsync(string directory, string providerName, string feed, DataRequest request, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using var writer = new FileStream(Path.Combine(directory, ".download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var client = new HttpClient();
        List<MarketSession> sessions = [];
        IMarketDataProvider provider;
        if (providerName == "Alpaca")
        {
            var (key, secret) = GlobalConfiguration.Load().ResolveAlpacaCredentials();
            sessions = await AlpacaProvider.CalendarAsync(client, key, secret, DateOnly.FromDateTime(request.Start.UtcDateTime), DateOnly.FromDateTime(request.End.UtcDateTime), token);
            if (DatasetValidator.Expected(request.Instrument, request.Interval, request.Start, request.End, sessions).Count == 0)
                throw new InvalidOperationException("The selected range contains no exchange sessions.");
            provider = new AlpacaProvider(client, key, secret, sessions, feed);
        }
        else provider = new HyperliquidProvider(client);
        try { await new DataCache(directory).GetAsync(provider, request, sessions, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Could not download {request.Instrument.Key} ({request.Interval.Name}, {provider.Name}) " +
                $"from {request.Start:yyyy-MM-dd} to {request.End:yyyy-MM-dd} UTC. {error.Message}", error);
        }
    }

    /// <summary>Validates and atomically exports a dataset for use as a backtest configuration's data file.</summary>
    internal static void Export(string source, string destination)
    {
        var data = Json.Read<MarketDataset>(source);
        DatasetValidator.Validate(data);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { Json.Write(temporary, data); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Describes an on-disk dataset without retaining its bars in the UI.</summary>
internal sealed record HistoricalDataEntry(string Path, string Provider, string Instruments, string Interval,
    DateTime? StartUtc, DateTime? EndUtc, int Bars, long Bytes, string Status, string Notes)
{
    /// <summary>Gets the recognizable file name even when dataset metadata is damaged.</summary>
    public string File => System.IO.Path.GetFileName(Path);
}

