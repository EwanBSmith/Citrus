namespace Citrus.Trading;

/// <summary>Identifies an instrument by venue, asset class, and symbol using record value equality.</summary>
public sealed record Instrument(string Venue, AssetClass AssetClass, string Symbol)
{
    /// <summary>Gets the venue, asset class, and symbol joined by colons for reporting and deterministic ordering.</summary>
    public string Key => $"{Venue}:{AssetClass}:{Symbol}";
}
