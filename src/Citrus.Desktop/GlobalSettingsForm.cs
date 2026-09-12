using Citrus.Data;

namespace Citrus.Desktop;

/// <summary>Edits user-wide settings without changing the current backtest document.</summary>
internal sealed class GlobalSettingsForm : Form
{
    private readonly TextBox key = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, AccessibleName = "Alpaca API key ID" };
    private readonly TextBox secret = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, AccessibleName = "Alpaca API secret key" };
    private readonly string path;

    /// <summary>Loads saved values and builds labelled fields with explicit Save and Cancel actions.</summary>
    internal GlobalSettingsForm(string? configurationPath = null)
    {
        path = configurationPath ?? GlobalConfiguration.DefaultPath;
        var settings = GlobalConfiguration.Load(path);
        Text = "Global settings";
        ClientSize = new Size(640, 320);
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimizeBox = MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var location = new TextBox { Text = path, ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "Global configuration file" };
        layout.Controls.Add(new Label { Text = "Configuration file", AutoSize = true }, 0, 0);
        layout.Controls.Add(location, 1, 0);
        layout.Controls.Add(new Label { Text = "Alpaca API key ID", AutoSize = true }, 0, 1);
        layout.Controls.Add(key, 1, 1);
        layout.Controls.Add(new Label { Text = "Alpaca API secret key", AutoSize = true }, 0, 2);
        layout.Controls.Add(secret, 1, 2);
        key.Text = settings.AlpacaApiKeyId;
        secret.Text = settings.AlpacaApiSecretKey;
        var reveal = new CheckBox { Text = "Show credentials", AutoSize = true };
        reveal.CheckedChanged += (_, _) => key.UseSystemPasswordChar = secret.UseSystemPasswordChar = !reveal.Checked;
        layout.Controls.Add(reveal, 1, 3);
        var hint = new Label { Text = "Shared by all Citrus workspaces and the CLI. Credentials are stored as plain text in your user profile. Environment variables override saved values. Clear a field and save to remove its stored value.", Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(580, 0), Padding = new Padding(0, 10, 0, 10) };
        layout.Controls.Add(hint, 0, 4);
        layout.SetColumnSpan(hint, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save" };
        save.Click += (_, _) => SaveSettings();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    /// <summary>Persists edited values and keeps the dialog open when saving fails.</summary>
    private void SaveSettings()
    {
        try
        {
            new GlobalConfiguration { AlpacaApiKeyId = key.Text.Trim(), AlpacaApiSecretKey = secret.Text.Trim() }.Save(path);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not save global settings. Check that the configuration location is writable.", "Global settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
