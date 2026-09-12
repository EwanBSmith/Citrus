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

    /// <summary>Shows the actual failure and copyable details in a resizable modal dialog.</summary>
    internal static void Show(IWin32Window owner, string operation, Exception error)
    {
        var hint = error switch
        {
            UnauthorizedAccessException => "Check access to the displayed file or folder.",
            HttpRequestException => "Check the endpoint and HTTP status below. Authentication, feed access, and rate limits are different failures.",
            OperationCanceledException => "The request was cancelled or timed out. Retry a smaller date range if it timed out.",
            _ => "Review the details below before retrying."
        };
        var details = Redact(operation + Environment.NewLine + error);
        using var dialog = new Form { Text = operation, Size = new Size(820, 480), MinimumSize = new Size(600, 360),
            StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 9F) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = Redact(error.Message) + Environment.NewLine + hint, AutoSize = true,
            MaximumSize = new Size(740, 0), Padding = new Padding(0, 0, 0, 12) });
        layout.Controls.Add(new TextBox { Text = details, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill, AccessibleName = "Error details" });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK };
        var copy = new Button { Text = "Copy details", AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(details);
        buttons.Controls.Add(close); buttons.Controls.Add(copy); layout.Controls.Add(buttons);
        dialog.Controls.Add(layout); dialog.AcceptButton = close; dialog.CancelButton = close;
        dialog.ShowDialog(owner);
    }
}
