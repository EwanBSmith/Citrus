using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Citrus.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Citrus.Engine;

public sealed class CompiledStrategy : IDisposable
{
    private sealed class StrategyLoadContext(IReadOnlyList<string> references) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(IStrategy).Assembly.GetName().Name) return typeof(IStrategy).Assembly;
            var path = references.FirstOrDefault(p => AssemblyName.GetAssemblyName(p).Name == assemblyName.Name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
    private readonly AssemblyLoadContext context;
    public IStrategy Strategy { get; }
    public IReadOnlyDictionary<string, string> DependencyHashes { get; }
    private CompiledStrategy(AssemblyLoadContext context, IStrategy strategy, IReadOnlyDictionary<string, string> hashes)
    { this.context = context; Strategy = strategy; DependencyHashes = hashes; }
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
    public void Dispose() => context.Unload();
}
