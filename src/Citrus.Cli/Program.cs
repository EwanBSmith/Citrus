using System.Globalization;
using Citrus.Trading;
using Citrus.Data;
using Citrus.Engine;

var configuredSecrets = new List<string>();
try
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
        Citrus: deterministic backtesting with trusted C# strategies
          validate <strategy.cs> [reference.dll ...]
          backtest <run.json>
          data generate <generation.json> <output.json>
          data import <dataset.json> <output.json> [supplement.json ...]
          data download <download.json> <output.json>
        See README.md and examples for configuration and simulation assumptions.
        """);
        return 0;
    }
    if (args[0] == "validate" && args.Length >= 2)
    {
        using var compiled = CompiledStrategy.Load(args[1], args.Skip(2));
        Console.WriteLine($"Valid strategy: {compiled.Strategy.GetType().Name}"); return 0;
    }
    if (args[0] == "backtest" && args.Length == 2)
    {
        var configPath = Path.GetFullPath(args[1]);
        var root = Path.GetDirectoryName(configPath)!;
        // Return an absolute path using the configuration directory as the base for relative paths.
        string Resolve(string path) => Path.GetFullPath(path, root);
        var config = Json.Read<RunConfiguration>(configPath);
        var strategy = Resolve(config.Strategy); var output = Resolve(config.Output);
        if (string.IsNullOrWhiteSpace(config.Data)) throw new ArgumentException("A market dataset path is required.");
        var dataPath = Resolve(config.Data);
        var data = Json.Read<MarketDataset>(dataPath);
        using var compiled = CompiledStrategy.Load(strategy, config.References.Select(Resolve));
        var result = new BacktestEngine().Run(compiled.Strategy, data, config);
        Reports.Export(output, result, config, data, strategy, dataPath, compiled.DependencyHashes);
        Console.WriteLine($"Completed: {result.Fills.Count} attributed fills; final equity {result.Final.Equity.ToString("F2", CultureInfo.InvariantCulture)}. Results: {output}"); return 0;
    }
    if (args.Length >= 4 && args[0] == "data")
    {
        var configRoot = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
        MarketDataset dataset;
        switch (args[1])
        {
            case "generate":
                var generation = Json.Read<Generation>(args[2]);
                dataset = BrownianGenerator.Generate(generation.Instrument, generation.Interval, generation.Start, generation.Count, generation.Seed,
                    generation.InitialPrice, generation.AnnualDrift, generation.AnnualVolatility, generation.Sessions);
                break;
            case "import":
                dataset = Json.Read<MarketDataset>(args[2]);
                foreach (var path in args.Skip(4))
                {
                    var supplement = Json.Read<MarketDataset>(path);
                    if (dataset.Interval != supplement.Interval) throw new InvalidDataException("Imported intervals must match.");
                    dataset = dataset with { Bars = dataset.Bars.Concat(supplement.Bars).OrderBy(b => b.OpenTime).ToList(),
                        Sessions = dataset.Sessions.Concat(supplement.Sessions).Distinct().OrderBy(s => s.Open).ToList(),
                        CorporateActions = dataset.CorporateActions.Concat(supplement.CorporateActions).ToList(),
                        Funding = dataset.Funding.Concat(supplement.Funding).ToList(), Notes = dataset.Notes.Concat(supplement.Notes).ToList() };
                }
                break;
            case "download":
                var download = Json.Read<Download>(args[2]);
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    IMarketDataProvider provider;
                    var sessions = download.Sessions;
                    if (download.Provider == "alpaca")
                    {
                        var (key, secret) = GlobalConfiguration.Load().ResolveAlpacaCredentials();
                        configuredSecrets.AddRange([key, secret]);
                        if (sessions.Count == 0) sessions = await AlpacaProvider.CalendarAsync(http, key, secret, DateOnly.FromDateTime(download.Request.Start.UtcDateTime), DateOnly.FromDateTime(download.Request.End.UtcDateTime));
                        provider = new AlpacaProvider(http, key, secret, sessions, download.Feed);
                    }
                    else if (download.Provider == "hyperliquid") provider = new HyperliquidProvider(http);
                    else throw new ArgumentException("Provider must be alpaca or hyperliquid.");
                    dataset = await new DataCache(Path.GetFullPath(download.Cache, configRoot)).GetAsync(provider, download.Request, sessions);
                }
                break;
            default: throw new ArgumentException("Unknown data command.");
        }
        DatasetValidator.Validate(dataset);
        foreach (var group in dataset.Bars.GroupBy(b => b.Instrument)) DatasetValidator.RequireCoverage(dataset, group.Key, group.Min(b => b.OpenTime), group.Max(b => b.CloseTime));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
        Json.Write(args[3], dataset); Console.WriteLine($"Saved {dataset.Bars.Count} bars to {args[3]}"); return 0;
    }
    throw new ArgumentException("Invalid arguments. Run with --help.");
}
catch (Exception exception)
{
    // Strategies are trusted and may throw arbitrary text; redact configured credentials before printing.
    var message = exception.Message;
    foreach (var value in configuredSecrets.Where(value => !string.IsNullOrEmpty(value)).OrderByDescending(value => value.Length))
        message = message.Replace(value, "[REDACTED]", StringComparison.Ordinal);
    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        if (entry.Key.ToString() is { } key && (key.Contains("KEY", StringComparison.OrdinalIgnoreCase) || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)) && entry.Value?.ToString() is { Length: > 3 } value)
            message = message.Replace(value, "[REDACTED]", StringComparison.Ordinal);
    Console.Error.WriteLine("Citrus: " + message); return 1;
}

/// <summary>Defines synthetic price generation inputs, including explicit sessions for equities.</summary>
internal sealed record Generation(Instrument Instrument, BarInterval Interval, DateTimeOffset Start, int Count, int Seed = 42,
    decimal InitialPrice = 100, double AnnualDrift = 0.05, double AnnualVolatility = 0.2, List<MarketSession>? Sessions = null);
/// <summary>Defines provider download settings and a cache path relative to the configuration file.</summary>
internal sealed record Download(string Provider, DataRequest Request, string Cache = ".cache", string Feed = "iex")
{
    public List<MarketSession> Sessions { get; init; } = [];
}
