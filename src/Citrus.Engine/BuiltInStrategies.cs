using System.Security.Cryptography;
using Citrus.Strategies;
using Citrus.Trading;

namespace Citrus.Engine;

/// <summary>Discovers and constructs the strategy classes shipped with this Citrus build.</summary>
public static class BuiltInStrategies
{
    /// <summary>Gets public, concrete strategies with parameterless constructors in stable name order.</summary>
    private static Type[] Types() => typeof(DemoHold).Assembly.GetTypes()
        .Where(t => typeof(IStrategy).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false, IsVisible: true, ContainsGenericParameters: false }
            && t.GetConstructor(Type.EmptyTypes) is not null).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();

    /// <summary>Lists built-in strategy names without constructing strategy instances.</summary>
    public static string[] Names() => Types().Select(t => t.FullName!).ToArray();

    /// <summary>Creates a fresh instance of the explicitly selected built-in strategy.</summary>
    public static IStrategy Create(string? name)
    {
        var type = Types().SingleOrDefault(t => t.FullName == name)
            ?? throw new InvalidDataException("Select a strategyType from Citrus.Strategies: " + string.Join(", ", Names()));
        return (IStrategy)Activator.CreateInstance(type)!;
    }

    /// <summary>Hashes the application components whose code determines a backtest result.</summary>
    public static IReadOnlyDictionary<string, string> ComponentHashes() => new[]
    {
        typeof(DemoHold).Assembly, typeof(BacktestEngine).Assembly, typeof(Citrus.Simulation.Ledger).Assembly,
        typeof(Citrus.Data.DatasetValidator).Assembly, typeof(IStrategy).Assembly
    }.ToDictionary(a => a.GetName().Name!, a => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))));
}
