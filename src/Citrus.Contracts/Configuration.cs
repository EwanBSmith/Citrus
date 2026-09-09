using System.Text.Json;
using System.Text.Json.Serialization;

namespace Citrus.Contracts;

/// <summary>Configures execution costs, rejection probability, margin fractions, and equity borrowing.</summary>
public sealed record SimulationOptions
{
    /// <summary>Gets the fixed commission per external net order, allocated across its attributed fills.</summary>
    public decimal CommissionFixed { get; init; }
    /// <summary>Gets the commission per absolute unit externally executed.</summary>
    public decimal CommissionPerUnit { get; init; }
    /// <summary>Gets the full bid/ask spread in basis points; execution applies half on each side.</summary>
    public decimal SpreadBps { get; init; }
    /// <summary>Gets additional adverse execution movement in basis points.</summary>
    public decimal SlippageBps { get; init; }
    /// <summary>Gets the probability from zero to one that an external submission is rejected.</summary>
    public double RejectionProbability { get; init; }
    /// <summary>Gets the equity notional fraction required when increasing exposure.</summary>
    public decimal EquityInitialMargin { get; init; } = 0.5m;
    /// <summary>Gets the equity notional fraction required to avoid liquidation.</summary>
    public decimal EquityMaintenanceMargin { get; init; } = 0.25m;
    /// <summary>Gets the perpetual notional fraction required when increasing exposure.</summary>
    public decimal PerpetualInitialMargin { get; init; } = 0.1m;
    /// <summary>Gets the perpetual notional fraction required to avoid liquidation.</summary>
    public decimal PerpetualMaintenanceMargin { get; init; } = 0.05m;
    /// <summary>Gets the annual fractional borrow rate charged on net short equity exposure using elapsed time and a 365-day year.</summary>
    public decimal AnnualBorrowRate { get; init; } = 0.03m;
    /// <summary>Gets whether external orders may increase net short equity exposure.</summary>
    public bool ShortsAvailable { get; init; } = true;
    /// <summary>Rejects invalid probabilities, negative costs, nonpositive execution prices implied by costs, and inconsistent margin fractions.</summary>
    public void Validate()
    {
        if (!double.IsFinite(RejectionProbability) || RejectionProbability is < 0 or > 1)
            throw new ArgumentException("RejectionProbability must be between zero and one.");
        if (CommissionFixed < 0 || CommissionPerUnit < 0 || SpreadBps < 0 || SlippageBps < 0 || AnnualBorrowRate < 0)
            throw new ArgumentException("Costs must be nonnegative.");
        if (SpreadBps / 2 + SlippageBps >= 10000) throw new ArgumentException("Combined execution cost must be less than 100 percent.");
        if (EquityMaintenanceMargin <= 0 || EquityInitialMargin < EquityMaintenanceMargin || EquityInitialMargin > 1 ||
            PerpetualMaintenanceMargin <= 0 || PerpetualInitialMargin < PerpetualMaintenanceMargin || PerpetualInitialMargin > 1)
            throw new ArgumentException("Margin fractions must satisfy 0 < maintenance <= initial <= 1.");
    }
}

/// <summary>Defines strategy inputs, reproducibility settings, output paths, and simulation parameters for a run.</summary>
public sealed record RunConfiguration
{
    /// <summary>Gets the serialized format version; the current supported version is one.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Gets the strategy source path, resolved relative to the run configuration by the CLI.</summary>
    public string Strategy { get; init; } = "";
    /// <summary>Gets additional assembly paths, resolved relative to the run configuration by the CLI.</summary>
    public string[] References { get; init; } = [];
    /// <summary>Gets the market dataset path, resolved relative to the run configuration by the CLI.</summary>
    public string Data { get; init; } = "";
    /// <summary>Gets the results directory, resolved relative to the run configuration by the CLI.</summary>
    public string Output { get; init; } = "results";
    /// <summary>Gets an optional captured snapshot file; null enables fetching and capture during the run.</summary>
    public string? ReplaySnapshots { get; init; }
    /// <summary>Gets starting portfolio cash, including capital not allocated to a substrategy.</summary>
    public decimal InitialCash { get; init; } = 100_000;
    /// <summary>Gets the seed used for simulated external order rejection draws.</summary>
    public int Seed { get; init; } = 42;
    /// <summary>Gets the annual fractional risk-free rate used in reported Sharpe calculations.</summary>
    public decimal RiskFreeRate { get; init; }
    /// <summary>Gets the maximum number of substrategies that may register during startup.</summary>
    public int MaximumSubstrategies { get; init; } = 1000;
    /// <summary>Gets execution and accounting settings for the simulated venue.</summary>
    public SimulationOptions Simulation { get; init; } = new();
}

/// <summary>Contains normalized bars, explicit sessions, supplementary events, and provenance for a single interval.</summary>
public sealed record MarketDataset
{
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

/// <summary>Reads and writes the shared JSON format with camel-case properties and string enums.</summary>
public static class Json
{
    /// <summary>Gets shared serialization settings; unknown input properties are rejected.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    /// <summary>Deserializes a file using shared settings and rejects a null document.</summary>
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty JSON document: {path}");
    /// <summary>Serializes a value using shared settings, overwriting the destination file.</summary>
    public static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}
