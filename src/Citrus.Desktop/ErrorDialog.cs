using Citrus.Data;

namespace Citrus.Desktop;

/// <summary>Presents copyable, credential-redacted errors with their underlying technical details.</summary>
internal static class ErrorDialog
{
    /// <summary>Redacts saved and environment credentials before errors reach controls or the clipboard.</summary>
    internal static string Redact(string text)
    {
        var secrets = new List<string>();
        try
        {
            var settings = GlobalConfiguration.Load();
            secrets.Add(settings.AlpacaApiKeyId);
            secrets.Add(settings.AlpacaApiSecretKey);
        }
        catch { /* A damaged settings file must not prevent reporting the original error. */ }
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key.ToString() is { } key && (key.Contains("KEY", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)))
                secrets.Add(entry.Value?.ToString() ?? "");
        foreach (var secret in secrets.Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length))
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }

    /// <summary>Shows a redacted failure through the designable WPF error dialog.</summary>
    internal static void Show(Window owner, string operation, Exception error)
    {
        if (DesktopSmokeTest.IsRunning) throw new InvalidOperationException(operation, error);
        var dialog = new ErrorWindow { Owner = owner };
        dialog.SetError(operation, error);
        dialog.ShowDialog();
    }
}
