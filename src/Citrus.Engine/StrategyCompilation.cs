using Citrus.Trading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Citrus.Engine;

/// <summary>Shares C# parsing, references, and compiler options between execution and strategy authoring.</summary>
public static class StrategyCompilation
{
    /// <summary>Creates a compilation without emitting, loading, or executing any strategy code.</summary>
    public static CSharpCompilation Create(string source, string sourcePath, IEnumerable<string>? references = null, string assemblyName = "CitrusStrategy")
    {
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ?? [];
        var metadata = platform.Concat((references ?? []).Select(Path.GetFullPath)).Append(typeof(IStrategy).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => MetadataReference.CreateFromFile(p));
        var tree = CSharpSyntaxTree.ParseText(source, path: sourcePath);
        return CSharpCompilation.Create(assemblyName, [tree], metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));
    }
}
