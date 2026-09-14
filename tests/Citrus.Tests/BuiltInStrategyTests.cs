using System.Text.Json;
using Citrus.Data;
using Citrus.Engine;
using Citrus.Strategies;

/// <summary>Checks built-in discovery, fresh instances and rejection of removed external inputs.</summary>
internal static class BuiltInStrategyTests
{
    /// <summary>Registers strategy selection regressions with the offline runner.</summary>
    internal static void Register(Action<string, Action> test)
    {
        test("Built-in discovery selects only shipped strategies and creates fresh instances", () =>
        {
            var names = BuiltInStrategies.Names();
            Require(names.Contains(typeof(DemoHold).FullName!) && names.Contains(typeof(ZorroPortfolio).FullName!), "Built-in strategies are missing.");
            Require(names.SequenceEqual(names.Order(StringComparer.Ordinal)), "Discovery order is unstable.");
            var first = BuiltInStrategies.Create(typeof(DemoHold).FullName);
            Require(first is DemoHold && !ReferenceEquals(first, BuiltInStrategies.Create(typeof(DemoHold).FullName)), "Runs shared mutable strategy state.");
            foreach (var name in new string?[] { null, "", "Missing", "DemoHold", "System.String", typeof(BuiltInStrategyTests).FullName })
                Throws<InvalidDataException>(() => BuiltInStrategies.Create(name));
        });
        test("Run files reject every removed external strategy setting", () =>
        {
            foreach (var property in new[] { "strategy", "strategyProject", "strategyAssembly", "strategySolution", "references" })
                Throws<JsonException>(() => JsonSerializer.Deserialize<RunConfiguration>("{\"" + property + "\":null}", Json.Options));
            var root = Path.Combine(Path.GetTempPath(), "citrus-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "arbitrary.json");
            Json.Write(path, new RunConfiguration { StrategyType = typeof(DemoHold).FullName });
            Require(RunConfiguration.Read(path).StrategyType == typeof(DemoHold).FullName, "Standalone configuration was not read.");
            Json.Write(path, new RunConfiguration { SchemaVersion = 99 });
            Throws<InvalidDataException>(() => RunConfiguration.Read(path));
        });
    }

    /// <summary>Fails when a built-in selection contract is violated.</summary>
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    /// <summary>Requires the expected exception category.</summary>
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
