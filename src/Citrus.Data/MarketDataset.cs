using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Contains normalized bars, explicit sessions, supplementary events, and provenance for a single interval.</summary>
public sealed record MarketDataset
{
    /// <summary>Gets the source identifier used for provenance.</summary>
    public string Provider { get; init; } = "import";
    /// <summary>Gets the bar interval shared by the dataset.</summary>
    public BarInterval Interval { get; init; } = BarInterval.Daily;
    /// <summary>Gets bars ordered chronologically within each instrument; imported equity OHLC must be provider-adjusted on one consistent basis.</summary>
    public List<Bar> Bars { get; init; } = [];
    /// <summary>Gets ordered, nonoverlapping UTC sessions required for equity validation.</summary>
    public List<MarketSession> Sessions { get; init; } = [];
    /// <summary>Gets perpetual funding events with rates and valuation marks.</summary>
    public List<FundingEvent> Funding { get; init; } = [];
    /// <summary>Gets provenance notes and data approximations retained in reports.</summary>
    public List<string> Notes { get; init; } = [];
}
