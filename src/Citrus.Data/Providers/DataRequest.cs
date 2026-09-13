using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Specifies an instrument, interval, UTC coverage range, for a provider.</summary>
public sealed record DataRequest(Instrument Instrument, BarInterval Interval, DateTimeOffset Start, DateTimeOffset End);
