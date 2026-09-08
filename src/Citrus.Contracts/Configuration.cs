using System.Text.Json;
using System.Text.Json.Serialization;

namespace Citrus.Contracts;

public sealed record SimulationOptions
{
    public decimal CommissionFixed { get; init; }
    public decimal CommissionPerUnit { get; init; }
    public decimal SpreadBps { get; init; }
    public decimal SlippageBps { get; init; }
    public double RejectionProbability { get; init; }
    public decimal EquityInitialMargin { get; init; } = 0.5m;
    public decimal EquityMaintenanceMargin { get; init; } = 0.25m;
    public decimal PerpetualInitialMargin { get; init; } = 0.1m;
    public decimal PerpetualMaintenanceMargin { get; init; } = 0.05m;
    public decimal AnnualBorrowRate { get; init; } = 0.03m;
    public bool ShortsAvailable { get; init; } = true;
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

public sealed record RunConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public string Strategy { get; init; } = "";
    public string[] References { get; init; } = [];
    public string Data { get; init; } = "";
    public string Output { get; init; } = "results";
    public string? ReplaySnapshots { get; init; }
    public decimal InitialCash { get; init; } = 100_000;
    public int Seed { get; init; } = 42;
    public decimal RiskFreeRate { get; init; }
    public int MaximumSubstrategies { get; init; } = 1000;
    public SimulationOptions Simulation { get; init; } = new();
}

public sealed record MarketDataset
{
    public int SchemaVersion { get; init; } = 1;
    public string Provider { get; init; } = "import";
    public string Version { get; init; } = "1";
    public BarInterval Interval { get; init; } = BarInterval.Daily;
    public List<Bar> Bars { get; init; } = [];
    public List<MarketSession> Sessions { get; init; } = [];
    public List<CorporateAction> CorporateActions { get; init; } = [];
    public List<FundingEvent> Funding { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

public static class Json
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty JSON document: {path}");
    public static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}
