using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Checks normalized market structure and explicit expected bar coverage.</summary>
public static class DatasetValidator
{
    /// <summary>Rejects malformed bars, sessions, and supplementary events; coverage is checked separately.</summary>
    public static void Validate(MarketDataset data)
    {
        if (data.Interval != BarInterval.Daily || data.Bars.Count == 0)
            throw new InvalidDataException("Dataset requires daily bars; other intervals are unsupported.");
        foreach (var symbol in data.Bars.Select(b => b.Instrument).Distinct().GroupBy(i => i.Symbol, StringComparer.OrdinalIgnoreCase))
            if (symbol.Count() > 1)
                throw new InvalidDataException($"Conflicting historical datasets for symbol '{symbol.Key}'. Keep one instrument definition per symbol and interval; provider and venue are metadata, not separate history keys.");
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
                if (group.Key.AssetClass == AssetClass.Equity && (!bar.SessionOpen || !bar.SessionClose ||
                    !data.Sessions.Any(s => s.Open == bar.OpenTime && s.Close == bar.CloseTime)))
                    throw new InvalidDataException("Daily equity bars must span an entire exchange session with both boundary flags.");
                if (group.Key.AssetClass == AssetClass.LinearPerpetual &&
                    (bar.OpenTime.TimeOfDay != TimeSpan.Zero || bar.CloseTime != bar.OpenTime.AddDays(1)))
                    throw new InvalidDataException("Daily perpetual bars must span midnight to midnight UTC.");
            }
        }
        foreach (var f in data.Funding)
            if (f.Instrument.AssetClass != AssetClass.LinearPerpetual || f.Time.Offset != TimeSpan.Zero || f.MarkPrice <= 0)
                throw new InvalidDataException("Invalid funding event.");
        if (data.Funding.GroupBy(f => (f.Instrument, f.Time)).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate funding event.");
    }

    /// <summary>Builds complete daily boundaries: exchange sessions for equities and UTC days for perpetuals.</summary>
    public static IReadOnlyList<(DateTimeOffset Open, DateTimeOffset Close)> Expected(Instrument instrument, BarInterval interval,
        DateTimeOffset start, DateTimeOffset end, IReadOnlyList<MarketSession> sessions)
    {
        if (interval != BarInterval.Daily) throw new ArgumentException("Only daily bars are supported.");
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        if (instrument.AssetClass == AssetClass.Equity)
        {
            foreach (var session in sessions.Where(s => s.Open >= start && s.Close <= end).OrderBy(s => s.Open))
                result.Add((session.Open, session.Close));
        }
        else
        {
            var first = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero);
            if (first < start) first = first.AddDays(1);
            for (var t = first; t.AddDays(1) <= end; t = t.AddDays(1)) result.Add((t, t.AddDays(1)));
        }
        return result;
    }
    /// <summary>Throws when any expected boundary pair is missing for the requested instrument and range.</summary>
    public static void RequireCoverage(MarketDataset data, Instrument instrument, DateTimeOffset start, DateTimeOffset end)
    {
        var actual = data.Bars.Where(b => b.Instrument == instrument).Select(b => (b.OpenTime, b.CloseTime)).ToHashSet();
        var missing = Expected(instrument, data.Interval, start, end, data.Sessions).Where(x => !actual.Contains(x)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"Missing {missing.Length} {data.Interval.Name} bars for {instrument.Key} from {data.Provider}. " +
            $"Missing UTC bar starts: {string.Join(", ", missing.Take(12).Select(b => b.Open.ToString("yyyy-MM-dd HH:mm")))}. " +
            "The selected data has no bars for these sessions. Import the missing history or use another available feed.");
    }
}
