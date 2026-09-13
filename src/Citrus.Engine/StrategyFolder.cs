using Citrus.Data;

namespace Citrus.Engine;

/// <summary>Discovers named backtests and resolves strategy-folder or legacy run-file inputs.</summary>
public static class StrategyFolder
{
    /// <summary>Returns named backtests in stable order from a strategy folder.</summary>
    public static string[] Backtests(string folder) => Directory.GetFiles(Path.Combine(Path.GetFullPath(folder), "Backtests"), "*.json")
        .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();

    /// <summary>Chooses Default, a sole configuration, or an explicitly named backtest without guessing between alternatives.</summary>
    public static string ConfigurationPath(string input, string? name = null)
    {
        var path = Path.GetFullPath(input);
        if (!Directory.Exists(path))
        {
            if (name is not null) throw new ArgumentException("A backtest name requires a strategy folder.");
            return path;
        }
        if (!Directory.Exists(Path.Combine(path, "Backtests"))) throw new FileNotFoundException("The strategy folder must contain Backtests/*.json.");
        var files = Backtests(path);
        if (name is not null)
            return files.SingleOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.Ordinal))
                ?? throw new FileNotFoundException($"Backtest '{name}' was not found.");
        return files.FirstOrDefault(p => Path.GetFileName(p) == "Default.json")
            ?? (files.Length == 1 ? files[0] : throw new InvalidDataException("Choose a named backtest, or create Backtests/Default.json."));
    }

    /// <summary>Identifies folder configurations by their Backtests directory and conventional source.</summary>
    public static string? Root(string configurationPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))!;
        var parent = Path.GetDirectoryName(directory);
        return Path.GetFileName(directory) == "Backtests" && parent is not null ? parent : null;
    }

    /// <summary>Resolves output and references relative to the strategy folder, or a legacy run file's directory.</summary>
    public static string Resolve(string configurationPath, string path) => Path.GetFullPath(path,
        Root(configurationPath) ?? Path.GetDirectoryName(Path.GetFullPath(configurationPath))!);

    /// <summary>Uses conventional source for folder strategies and explicit source for legacy configurations.</summary>
    public static string Source(string configurationPath, RunConfiguration configuration) => Root(configurationPath) is string root
        ? Path.Combine(root, "Strategy.cs") : Resolve(configurationPath, string.IsNullOrWhiteSpace(configuration.Strategy) ? throw new InvalidDataException("Legacy runs require a strategy path.") : configuration.Strategy);

    /// <summary>Loads settings, rejecting unsupported versions and conflicting folder source paths.</summary>
    public static RunConfiguration Read(string path)
    {
        var configuration = Json.Read<RunConfiguration>(path);
        if (configuration.SchemaVersion != 1) throw new InvalidDataException("Unsupported run configuration schema version.");
        Validate(configuration);
        if (Root(path) is not null && !string.IsNullOrEmpty(configuration.Strategy))
            throw new InvalidDataException("Folder backtests discover Strategy.cs automatically; remove the strategy property.");
        return configuration;
    }

    /// <summary>Rejects ambiguous strategy inputs before compiling or executing any user code.</summary>
    public static void Validate(RunConfiguration configuration)
    {
        if (new[] { configuration.Strategy, configuration.StrategyProject, configuration.StrategyAssembly }.Count(p => !string.IsNullOrWhiteSpace(p)) > 1)
            throw new InvalidDataException("Specify only one of strategy, strategyProject, or strategyAssembly.");
    }

    /// <summary>Resolves the external project, assembly, or legacy source selected by a backtest.</summary>
    public static string Input(string path, RunConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.StrategyProject) ? Resolve(path, configuration.StrategyProject) :
        !string.IsNullOrWhiteSpace(configuration.StrategyAssembly) ? Resolve(path, configuration.StrategyAssembly) : Source(path, configuration);

    /// <summary>Resolves the user's explicit solution or falls back to the configured project or source.</summary>
    public static string DevelopmentPath(string path, RunConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.StrategySolution) ? Resolve(path, configuration.StrategySolution) : Input(path, configuration);
}
