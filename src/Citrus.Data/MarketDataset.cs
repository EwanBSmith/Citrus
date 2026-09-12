using Citrus.Trading;

namespace Citrus.Data;

/// <summary>Contains normalized bars, explicit sessions, supplementary events, and provenance for a single interval.</summary>
public sealed record MarketDataset
{
    /// <summary>Gets the explicit tradable universe; an empty list preserves legacy dataset behavior.</summary>
    public List<Instrument> Universe { get; init; } = [];
    /// <summary>Gets the serialized format version; the current supported version is one.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the source identifier used for provenance.</summary>
    public string Provider { get; init; } = "import";
    /// <summary>Gets the explicit data revision used for provenance and provider cache requests.</summary>
    public string Version { get; init; } = "1";
    /// <summary>Gets the bar interval shared by the dataset.</summary>
    public BarInterval Interval { get; init; } = BarInterval.Daily;
    /// <summary>Gets bars ordered chronologically within each instrument.</summary>
    public List<Bar> Bars { get; init; } = [];
    /// <summary>Gets ordered, nonoverlapping UTC sessions required for equity validation.</summary>
    public List<MarketSession> Sessions { get; init; } = [];
    /// <summary>Gets normalized equity events with explicit settlement terms.</summary>
    public List<CorporateAction> CorporateActions { get; init; } = [];
    /// <summary>Gets perpetual funding events with rates and valuation marks.</summary>
    public List<FundingEvent> Funding { get; init; } = [];
    /// <summary>Gets provenance notes and data approximations retained in reports.</summary>
    public List<string> Notes { get; init; } = [];
}
