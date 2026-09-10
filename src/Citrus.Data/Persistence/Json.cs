using System.Text.Json;
using System.Text.Json.Serialization;

namespace Citrus.Data;

/// <summary>Reads and writes the shared JSON format with camel-case properties and string enums.</summary>
public static class Json
{
    /// <summary>Gets shared serialization settings; unknown input properties are rejected.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    /// <summary>Deserializes a file using shared settings and rejects a null document.</summary>
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty JSON document: {path}");
    /// <summary>Serializes a value using shared settings, overwriting the destination file.</summary>
    public static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}
