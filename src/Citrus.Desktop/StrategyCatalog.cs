using Citrus.Engine;

namespace Citrus.Desktop;

/// <summary>Locates the main strategy project and its saved backtests.</summary>
internal static class StrategyCatalog
{
    internal const string Project = "src/Citrus.Strategies/Citrus.Strategies.csproj";

    /// <summary>Finds the checkout from the executable or working directory without a folder picker.</summary>
    internal static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, Project))) return directory.FullName;
        throw new DirectoryNotFoundException("Citrus.Strategies was not found. Run the desktop from your Citrus checkout.");
    }

    /// <summary>Matches configurations by project and full strategy type, preferring Default.</summary>
    internal static string[] Backtests(string root, string type) =>
        !Directory.Exists(Path.Combine(root, "Backtests")) ? [] : StrategyFolder.Backtests(root)
            .Where(path =>
            {
                var config = StrategyFolder.Read(path);
                return config.StrategyType == type && config.StrategyProject is not null
                    && string.Equals(StrategyFolder.Input(path, config), Path.GetFullPath(Project, root), StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => Path.GetFileName(path) == "Default.json" ? 0 : 1).ThenBy(path => path, StringComparer.Ordinal).ToArray();

    /// <summary>Creates separate settings for a newly discovered type without overwriting existing files.</summary>
    internal static string Configuration(string root, string type)
    {
        var existing = Backtests(root, type);
        if (existing.Length > 0) return existing[0];
        Directory.CreateDirectory(Path.Combine(root, "Backtests"));
        var name = string.Concat(type.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(root, "Backtests", name + ".json");
        for (var suffix = 2; File.Exists(path); suffix++) path = Path.Combine(root, "Backtests", name + suffix + ".json");
        Citrus.Data.Json.Write(path, new RunConfiguration { StrategyProject = Project, StrategyType = type,
            StrategySolution = "Citrus.slnx", Output = "Results/" + Path.GetFileNameWithoutExtension(path) });
        return path;
    }
}
