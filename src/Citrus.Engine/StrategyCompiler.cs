using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Citrus.Trading;
using Microsoft.CodeAnalysis;

namespace Citrus.Engine;

/// <summary>Owns a trusted compiled strategy and its collectible assembly load context.</summary>
public sealed class CompiledStrategy : IDisposable
{
    /// <summary>Loads explicit strategy dependencies in a collectible context while sharing the host contract assembly.</summary>
    private sealed class StrategyLoadContext(IReadOnlyList<string> references, string? assemblyPath = null) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver? resolver = assemblyPath is null ? null : new(assemblyPath);
        /// <summary>Resolves the shared strategy contract or an explicit dependency, deferring other assemblies to default resolution.</summary>
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share the host contract assembly so strategy instances retain the same IStrategy type identity.
            if (assemblyName.Name == typeof(IStrategy).Assembly.GetName().Name) return typeof(IStrategy).Assembly;
            var path = references.FirstOrDefault(p => AssemblyName.GetAssemblyName(p).Name == assemblyName.Name)
                ?? resolver?.ResolveAssemblyToPath(assemblyName);
            if (path is null && assemblyPath is not null && Path.Combine(Path.GetDirectoryName(assemblyPath)!, assemblyName.Name + ".dll") is string sibling && File.Exists(sibling))
                path = sibling;
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        /// <summary>Resolves native assets from the strategy's dependency manifest.</summary>
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) => resolver?.ResolveUnmanagedDllToPath(unmanagedDllName) is string path
            ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
    private readonly AssemblyLoadContext context;
    /// <summary>Gets the instantiated strategy defined by the source file.</summary>
    public IStrategy Strategy { get; }
    /// <summary>Gets frozen authoritative settings declared by this strategy.</summary>
    public IReadOnlyDictionary<string, object?> Options => StrategyConfiguration.Declarations(Strategy);
    /// <summary>Gets the validated effective configuration for this loaded strategy.</summary>
    public RunConfiguration EffectiveConfiguration(RunConfiguration requested) => StrategyConfiguration.Apply(requested, Options);
    /// <summary>Gets SHA-256 hashes keyed by absolute explicit dependency paths for provenance.</summary>
    public IReadOnlyDictionary<string, string> DependencyHashes { get; }
    private string? snapshotDirectory;
    /// <summary>Gets the immutable assembly or source input used by this loaded strategy.</summary>
    public string InputPath { get; private set; } = "";
    /// <summary>Gets the strategy checkout state captured before loading user code.</summary>
    public GitRevision? StrategyRevision { get; private set; }
    /// <summary>Gets the Citrus checkout state captured before loading user code.</summary>
    public GitRevision? CitrusRevision { get; private set; }
    /// <summary>Retains the load context, strategy instance, and dependency hashes for execution and disposal.</summary>
    private CompiledStrategy(AssemblyLoadContext context, IStrategy strategy, IReadOnlyDictionary<string, string> hashes)
    { this.context = context; Strategy = strategy; DependencyHashes = hashes; }
    /// <summary>Compiles source and loads exactly one concrete strategy with a public parameterless constructor; compilation and loading failures propagate.</summary>
    public static CompiledStrategy Load(string sourcePath, IEnumerable<string>? references = null)
    {
        var paths = (references ?? []).Select(Path.GetFullPath).ToArray();
        var compilation = StrategyCompilation.Create(File.ReadAllText(sourcePath), sourcePath, paths,
            "CitrusStrategy_" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)))[..16]);
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
            return new(context, strategy, paths.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))) { InputPath = sourcePath };
        }
        catch { context.Unload(); throw; }
    }
    /// <summary>Requests unloading of the strategy assembly context; collection requires outstanding references to be released.</summary>
    public void Dispose() => context.Unload();

    /// <summary>Lists runnable strategy types without constructing them or locking their build output.</summary>
    public static string[] DiscoverAssembly(string assemblyPath)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        var context = new StrategyLoadContext([], assemblyPath);
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(assemblyPath));
            var assembly = context.LoadFromStream(stream);
            return assembly.GetTypes().Where(t => typeof(IStrategy).IsAssignableFrom(t)
                && t is { IsClass: true, IsAbstract: false, IsVisible: true, ContainsGenericParameters: false }
                && t.GetConstructor(Type.EmptyTypes) is not null)
                .Select(t => t.FullName!).Order(StringComparer.Ordinal).ToArray();
        }
        finally { context.Unload(); }
    }

    /// <summary>Builds or loads a configured strategy through the same path for desktop and CLI callers.</summary>
    public static CompiledStrategy LoadConfiguration(string configurationPath, RunConfiguration configuration)
    {
        StrategyFolder.Validate(configuration);
        var input = StrategyFolder.Input(configurationPath, configuration);
        var revision = StrategyProject.Revision(input);
        var citrus = StrategyProject.Revision(typeof(CompiledStrategy).Assembly.Location);
        var references = configuration.References.Select(p => StrategyFolder.Resolve(configurationPath, p)).ToArray();
        var compiled = !string.IsNullOrWhiteSpace(configuration.StrategyProject)
            ? LoadAssembly(StrategyProject.Build(input), configuration.StrategyType, references)
            : !string.IsNullOrWhiteSpace(configuration.StrategyAssembly)
                ? LoadAssembly(input, configuration.StrategyType, references) : Load(input, references);
        compiled.StrategyRevision = revision;
        compiled.CitrusRevision = citrus;
        return compiled;
    }

    /// <summary>Snapshots build output, resolves its dependencies and selects a concrete strategy without locking the build directory.</summary>
    public static CompiledStrategy LoadAssembly(string assemblyPath, string? typeName = null, IEnumerable<string>? references = null)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(assemblyPath)) throw new FileNotFoundException("Build the strategy assembly before loading it.", assemblyPath);
        var snapshot = Path.Combine(Path.GetTempPath(), "citrus-strategies", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshot);
        var root = Path.GetDirectoryName(assemblyPath)!;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(snapshot, Path.GetRelativePath(root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var paths = new List<string>();
        foreach (var reference in references ?? [])
        {
            var target = Path.Combine(snapshot, Path.GetFileName(reference));
            if (File.Exists(target) && !File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(reference)))
                throw new InvalidDataException("Conflicting strategy dependency: " + reference);
            File.Copy(reference, target, true);
            paths.Add(target);
        }
        var contract = Path.Combine(snapshot, "Citrus.Trading.dll");
        if (File.Exists(contract) && !CompatibleContract(contract))
            throw new InvalidDataException("The strategy was built against a different Citrus.Trading assembly. Rebuild Citrus and the strategy from the same pinned Citrus revision.");
        var input = Path.Combine(snapshot, Path.GetFileName(assemblyPath));
        var context = new StrategyLoadContext(paths, input);
        try
        {
            var assembly = context.LoadFromAssemblyPath(input);
            var types = assembly.GetTypes().Where(t => typeof(IStrategy).IsAssignableFrom(t) && t is { IsAbstract: false, IsClass: true, ContainsGenericParameters: false }
                && (string.IsNullOrWhiteSpace(typeName) || t.FullName == typeName)).ToArray();
            if (types.Length != 1 || !types[0].IsPublic || types[0].GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException("Set strategyType to the full name of one public concrete IStrategy with a public parameterless constructor.");
            var hashes = Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(snapshot, p),
                p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            return new(context, (IStrategy)Activator.CreateInstance(types[0])!, hashes) { snapshotDirectory = snapshot, InputPath = input };
        }
        catch (ReflectionTypeLoadException error)
        {
            context.Unload();
            throw new InvalidDataException("Could not load strategy dependencies: " + string.Join("; ", error.LoaderExceptions.Select(e => e?.Message)), error);
        }
        catch { context.Unload(); throw; }
    }

    /// <summary>Preserves the exact loaded build output and writes a configuration that selects it without rebuilding.</summary>
    public string? ExportArtifacts(string directory, RunConfiguration configuration)
    {
        if (snapshotDirectory is null) return null;
        var relative = Path.Combine("strategy-artifacts", Path.GetFileName(snapshotDirectory));
        var target = Path.Combine(directory, relative);
        foreach (var file in Directory.GetFiles(snapshotDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(snapshotDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
        Citrus.Data.Json.Write(Path.Combine(directory, "run.json"), configuration with { Strategy = null, StrategyProject = null,
            StrategyAssembly = Path.Combine(relative, Path.GetFileName(InputPath)), StrategyType = Strategy.GetType().FullName,
            StrategySolution = null, References = [], Output = "replay-results" });
        return relative;
    }

    /// <summary>Accepts identical API artifacts or the same Git-versioned API identity, allowing checkout/debug metadata differences.</summary>
    private static bool CompatibleContract(string path)
    {
        var host = typeof(IStrategy).Assembly;
        if (File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(host.Location))) return true;
        try
        {
            if (AssemblyName.GetAssemblyName(path).FullName != host.GetName().FullName) return false;
            var version = host.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (version is null || !version.Contains('+')) return false;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                if (metadata.GetString(type.Namespace) != "System.Reflection" || metadata.GetString(type.Name) != nameof(AssemblyInformationalVersionAttribute)) continue;
                var blob = metadata.GetBlobReader(attribute.Value);
                return blob.ReadUInt16() == 1 && blob.ReadSerializedString() == version;
            }
        }
        catch (BadImageFormatException) { return false; }
        return false;
    }
}
