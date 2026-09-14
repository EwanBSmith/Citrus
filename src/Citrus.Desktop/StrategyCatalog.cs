using Citrus.Engine;

namespace Citrus.Desktop;

/// <summary>Finds saved settings for the built-in strategies.</summary>
internal static class StrategyCatalog
{
    /// <summary>Finds the main solution for the optional Open in IDE command.</summary>
    internal static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Citrus.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Open Citrus.slnx from your source checkout to edit strategies.");
    }

    /// <summary>Uses checkout settings during development and per-user settings for an installed application.</summary>
    internal static string ConfigurationDirectory()
    {
        try { return Path.Combine(FindRoot(), "Backtests"); }
        catch (DirectoryNotFoundException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Citrus", "Backtests");
        }
    }

    /// <summary>Lists JSON files in the selected settings directory that select the same strategy.</summary>
    internal static string[] Backtests(string directory, string type) => !Directory.Exists(directory) ? []
        : Directory.GetFiles(directory, "*.json").Where(path => Selects(path, type))
            .OrderBy(path => Path.GetFileName(path) == "Default.json" ? 0 : 1).ThenBy(path => path, StringComparer.Ordinal).ToArray();

    /// <summary>Skips unrelated or malformed JSON when listing sibling configurations.</summary>
    private static bool Selects(string path, string type)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property => property.Name.Equals("strategyType", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.String && property.Value.GetString() == type);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>Creates separate settings for a built-in type without overwriting existing files.</summary>
    internal static string Configuration(string directory, string type)
    {
        if (!BuiltInStrategies.Names().Contains(type)) throw new InvalidDataException("Unknown built-in strategy: " + type);
        var existing = Backtests(directory, type);
        if (existing.Length > 0) return existing[0];
        Directory.CreateDirectory(directory);
        var name = string.Concat(type.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(directory, name + ".json");
        for (var suffix = 2; File.Exists(path); suffix++) path = Path.Combine(directory, name + suffix + ".json");
        Citrus.Data.Json.Write(path, new RunConfiguration { StrategyType = type,
            Output = "../Results/" + Path.GetFileNameWithoutExtension(path) });
        return path;
    }
}
