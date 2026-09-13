using Citrus.Data;
using Citrus.Engine;
using Citrus.Trading;

/// <summary>Checks external assembly selection, transitive assets, immutable captures and invalid configurations.</summary>
internal static class ExternalStrategyTests
{
    /// <summary>Registers integration cases with the offline regression runner.</summary>
    internal static void Register(Action<string, Action> test)
    {
        test("External assemblies select types and capture transitive dependencies and content", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "citrus-external-tests-" + Guid.NewGuid().ToString("N"));
            var build = Path.Combine(root, "build");
            Directory.CreateDirectory(build);
            var helper = Path.Combine(build, "Helper.dll");
            Emit(helper, "public static class Helper { public static string Value => System.IO.File.ReadAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Helper).Assembly.Location)!, \"value.txt\")); }");
            var assembly = Path.Combine(build, "Strategies.dll");
            Emit(assembly, """
                using Citrus.Trading;
                public class First : IStrategy {
                    public First() { if (Helper.Value != "fixture") throw new System.Exception("Dependency content was not captured."); }
                    public void OnStart(IStrategyContext c) { }
                    public void OnBar(IStrategyContext c, System.Collections.Generic.IReadOnlyList<Bar> b) { }
                }
                public class Second : First { }
                """, helper);
            File.WriteAllText(Path.Combine(build, "value.txt"), "fixture");
            ExpectFailure(() => CompiledStrategy.LoadAssembly(assembly));
            ExpectFailure(() => CompiledStrategy.LoadAssembly(assembly, "Missing"));
            Directory.CreateDirectory(Path.Combine(root, "Backtests"));
            var configPath = Path.Combine(root, "Backtests", "Default.json");
            var config = new RunConfiguration { StrategyAssembly = "build/Strategies.dll", StrategyType = "Second" };
            Json.Write(configPath, config);
            if (StrategyFolder.ConfigurationPath(root) != configPath) throw new Exception("Project-only workspace was not discovered.");
            using var strategy = CompiledStrategy.LoadConfiguration(configPath, config);
            if (strategy.Strategy.GetType().Name != "Second" || !strategy.DependencyHashes.ContainsKey("Helper.dll") || !strategy.DependencyHashes.ContainsKey("value.txt"))
                throw new Exception("Strategy selection or dependency capture failed.");
            var output = Path.Combine(root, "results");
            strategy.ExportArtifacts(output, config);
            // Overwriting the developer's output must not change the loaded run or its exported capture.
            File.WriteAllText(assembly, "replaced by a subsequent build");
            File.WriteAllText(Path.Combine(build, "value.txt"), "changed");
            var replayPath = Path.Combine(output, "run.json");
            using var replay = CompiledStrategy.LoadConfiguration(replayPath, StrategyFolder.Read(replayPath));
            if (replay.Strategy.GetType().Name != "Second") throw new Exception("Captured build was not replayable.");
        });
        test("External strategy settings reject conflicting inputs and mismatched Citrus API binaries", () =>
        {
            ExpectFailure(() => StrategyFolder.Validate(new() { StrategyProject = "a.csproj", StrategyAssembly = "a.dll" }));
            var root = Path.Combine(Path.GetTempPath(), "citrus-api-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var assembly = Path.Combine(root, "Strategy.dll");
            File.Copy(typeof(ExternalStrategyTests).Assembly.Location, assembly);
            File.WriteAllText(Path.Combine(root, "Citrus.Trading.dll"), "incompatible API");
            ExpectFailure(() => CompiledStrategy.LoadAssembly(assembly));
        });
        test("API compatibility tolerates debug paths but rejects a different recorded Git revision", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "citrus-version-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var assembly = Path.Combine(root, "Simple.dll");
            Emit(assembly, "public class Simple : Citrus.Trading.IStrategy { public void OnStart(Citrus.Trading.IStrategyContext c) {} public void OnBar(Citrus.Trading.IStrategyContext c, System.Collections.Generic.IReadOnlyList<Citrus.Trading.Bar> b) {} }");
            var api = typeof(IStrategy).Assembly;
            var bytes = File.ReadAllBytes(api.Location);
            var pdb = bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("Citrus.Trading.pdb"));
            if (pdb < 0) throw new Exception("Fixture API must contain portable debug metadata.");
            bytes[pdb] = (byte)'X';
            var contract = Path.Combine(root, "Citrus.Trading.dll");
            File.WriteAllBytes(contract, bytes);
            using var loaded = CompiledStrategy.LoadAssembly(assembly);
            var version = api.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
            var position = bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(version));
            if (position < 0) throw new Exception("Fixture API must record its build version.");
            bytes[position] = (byte)'9';
            File.WriteAllBytes(contract, bytes);
            ExpectFailure(() => CompiledStrategy.LoadAssembly(assembly));
        });
    }

    /// <summary>Emits a real DLL fixture with optional dependency references.</summary>
    private static void Emit(string path, string source, params string[] references)
    {
        using var stream = File.Create(path);
        var result = StrategyCompilation.Create(source, path + ".cs", references, Path.GetFileNameWithoutExtension(path)).Emit(stream);
        if (!result.Success) throw new Exception(string.Join("\n", result.Diagnostics));
    }

    /// <summary>Requires invalid external input to fail with an actionable validation error.</summary>
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid external strategy was accepted.");
    }
}
