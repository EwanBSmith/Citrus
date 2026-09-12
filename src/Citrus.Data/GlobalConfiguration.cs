using System.Text.Json;

namespace Citrus.Data;

/// <summary>Stores user-wide provider settings separately from exported backtest inputs.</summary>
public sealed class GlobalConfiguration
{
    /// <summary>Gets or sets the supported file format version.</summary>
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Gets or sets the Alpaca API key identifier.</summary>
    public string AlpacaApiKeyId { get; set; } = "";
    /// <summary>Gets or sets the Alpaca API secret.</summary>
    public string AlpacaApiSecretKey { get; set; } = "";

    /// <summary>Gets the per-user file location shared by the CLI and desktop.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Citrus", "config.json");

    /// <summary>Loads settings, returning defaults only when the file does not exist.</summary>
    public static GlobalConfiguration Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new();
        try
        {
            var result = Json.Read<GlobalConfiguration>(path);
            result.Validate();
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            // Parser diagnostics can contain user-supplied property names; do not expose secrets.
            throw new InvalidDataException("Global configuration is invalid. Expected schemaVersion 1 and string credential fields.");
        }
    }

    /// <summary>Validates the document before use or persistence.</summary>
    private void Validate()
    {
        if (SchemaVersion != 1 || AlpacaApiKeyId is null || AlpacaApiSecretKey is null)
            throw new InvalidDataException("Unsupported global configuration.");
    }

    /// <summary>Replaces the settings atomically so interrupted writes preserve the previous file.</summary>
    public void Save(string? path = null)
    {
        Validate();
        path = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
                JsonSerializer.Serialize(stream, this, Json.Options);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Resolves credentials with nonempty environment values taking precedence over saved values.</summary>
    public (string KeyId, string Secret) ResolveAlpacaCredentials(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var key = environment("APCA_API_KEY_ID");
        var secret = environment("APCA_API_SECRET_KEY");
        key = string.IsNullOrWhiteSpace(key) ? AlpacaApiKeyId : key;
        secret = string.IsNullOrWhiteSpace(secret) ? AlpacaApiSecretKey : secret;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Configure Alpaca credentials in Global settings or set APCA_API_KEY_ID and APCA_API_SECRET_KEY.");
        return (key, secret);
    }
}
