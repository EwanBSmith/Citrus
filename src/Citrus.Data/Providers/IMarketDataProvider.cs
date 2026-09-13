namespace Citrus.Data;

/// <summary>Supplies normalized historical market data for an explicit instrument and coverage request.</summary>
public interface IMarketDataProvider
{
    /// <summary>Gets the source identity, including feed where applicable, retained as provenance metadata.</summary>
    string Name { get; }
    /// <summary>Fetches normalized data for the requested range with cooperative cancellation.</summary>
    Task<MarketDataset> FetchAsync(DataRequest request, CancellationToken cancellationToken = default);
}
