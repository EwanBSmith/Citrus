using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Citrus.Trading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Citrus.Engine;

/// <summary>Owns a trusted compiled strategy and its collectible assembly load context.</summary>
public sealed class CompiledStrategy : IDisposable
{
    /// <summary>Loads explicit strategy dependencies in a collectible context while sharing the host contract assembly.</summary>
    private sealed class StrategyLoadContext(IReadOnlyList<string> references) : AssemblyLoadContext(isCollectible: true)
    {
        /// <summary>Resolves the shared strategy contract or an explicit dependency, deferring other assemblies to default resolution.</summary>
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share the host contract assembly so strategy instances retain the same IStrategy type identity.
            if (assemblyName.Name == typeof(IStrategy).Assembly.GetName().Name) return typeof(IStrategy).Assembly;
            var path = references.FirstOrDefault(p => AssemblyName.GetAssemblyName(p).Name == assemblyName.Name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
    private readonly AssemblyLoadContext context;
    /// <summary>Gets the instantiated strategy defined by the source file.</summary>
    public IStrategy Strategy { get; }
    /// <summary>Gets SHA-256 hashes keyed by absolute explicit dependency paths for replay provenance.</summary>
    public IReadOnlyDictionary<string, string> DependencyHashes { get; }
    /// <summary>Retains the load context, strategy instance, and dependency hashes for execution and disposal.</summary>
    private CompiledStrategy(AssemblyLoadContext context, IStrategy strategy, IReadOnlyDictionary<string, string> hashes)
    { this.context = context; Strategy = strategy; DependencyHashes = hashes; }
    /// <summary>Compiles source and loads exactly one concrete strategy with a public parameterless constructor; compilation and loading failures propagate.</summary>
    public static CompiledStrategy Load(string sourcePath, IEnumerable<string>? references = null)
    {
        var paths = (references ?? []).Select(Path.GetFullPath).ToArray();
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ?? [];
        var metadata = platform.Concat(paths).Append(typeof(IStrategy).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => MetadataReference.CreateFromFile(p));
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath), path: sourcePath);
        var compilation = CSharpCompilation.Create("CitrusStrategy_" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)))[..16],
            [tree], metadata, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success) throw new InvalidDataException(string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var context = new StrategyLoadContext(paths);
        try
        {
            stream.Position = 0;
            var assembly = context.LoadFromStream(stream);
            var types = assembly.GetTypes().Where(t => typeof(IStrategy).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass).ToArray();
            if (types.Length != 1) throw new InvalidDataException("Strategy file must define exactly one concrete IStrategy with a public parameterless constructor.");
            var strategy = (IStrategy?)Activator.CreateInstance(types[0]) ?? throw new InvalidDataException("Cannot construct strategy.");
            return new(context, strategy, paths.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
        }
        catch { context.Unload(); throw; }
    }
    /// <summary>Requests unloading of the strategy assembly context; collection requires outstanding references to be released.</summary>
    public void Dispose() => context.Unload();
}
