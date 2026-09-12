using System.Diagnostics;
using Citrus.Data;
using Citrus.Trading;

namespace Citrus.Desktop;

/// <summary>Downloads provider history and manages normalized datasets in a chosen local library.</summary>
internal sealed class HistoricalDataForm : Form
{
    private readonly TextBox folder = new() { Dock = DockStyle.Fill, AccessibleName = "Historical data folder" };
    private readonly ComboBox provider = Choice("Provider", "Alpaca", "Hyperliquid");
    private readonly ComboBox feed = Choice("Alpaca feed", "iex", "sip");
    private readonly ComboBox interval = Choice("Bar interval", "1d", "1h");
    private readonly TextBox symbol = new() { Text = "SPY", Dock = DockStyle.Fill, AccessibleName = "Symbol" };
    private readonly TextBox venue = new() { Text = "US", Dock = DockStyle.Fill, AccessibleName = "Venue" };
    private readonly TextBox version = new() { Text = "1", Dock = DockStyle.Fill, AccessibleName = "Data version" };
    private readonly DateTimePicker start = DateField("Start date UTC", DateTime.UtcNow.Date.AddMonths(-1));
    private readonly DateTimePicker end = DateField("End date UTC exclusive", DateTime.UtcNow.Date);
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, BackgroundColor = SystemColors.Window, AccessibleName = "Local historical datasets" };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "Ready" };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Dataset details" };
    private readonly Button cancel = new() { Text = "Cancel download", AutoSize = true, Enabled = false };
    private readonly ProgressBar progress = new() { Width = 140, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly List<Control> commands = [];
    private CancellationTokenSource? operation;

    /// <summary>Builds the download fields and library table using native Windows controls.</summary>
    internal HistoricalDataForm(string? directory = null)
    {
        Text = "Historical data"; Font = new Font("Segoe UI", 9F); AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1100, 760); MinimumSize = new Size(900, 700); StartPosition = FormStartPosition.CenterParent;
        folder.Text = directory ?? HistoricalDataLibrary.DefaultDirectory;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 164));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        var location = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        location.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95)); location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); location.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        location.Controls.Add(new Label { Text = "Local folder", AutoSize = true }, 0, 0); location.Controls.Add(folder, 1, 0);
        location.Controls.Add(Button("Browse...", Browse), 2, 0); layout.Controls.Add(location, 0, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddField(fields, "Provider", provider, 0, 0); AddField(fields, "Alpaca feed", feed, 2, 0);
        AddField(fields, "Symbol", symbol, 0, 1); AddField(fields, "Venue", venue, 2, 1);
        AddField(fields, "Bar interval", interval, 0, 2); AddField(fields, "Data version", version, 2, 2);
        AddField(fields, "Start date (UTC)", start, 0, 3); AddField(fields, "End date (exclusive)", end, 2, 3);
        layout.Controls.Add(fields, 0, 1);
        var download = new FlowLayoutPanel { Dock = DockStyle.Fill };
        download.Controls.Add(Button("Download", () => _ = DownloadAsync())); download.Controls.Add(cancel); download.Controls.Add(progress);
        download.Controls.Add(new Label { AutoSize = true, Text = "UTC midnight boundaries. Existing bars are reused; missing history is fetched.", Padding = new Padding(6, 6, 0, 0) });
        cancel.Click += (_, _) => { operation?.Cancel(); cancel.Enabled = false; status.Text = "Cancelling download..."; };
        layout.Controls.Add(download, 0, 2); layout.Controls.Add(grid, 0, 3); layout.Controls.Add(details, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        actions.Controls.Add(Button("Refresh", () => _ = RefreshAsync()));
        actions.Controls.Add(Button("Open folder", () => { Directory.CreateDirectory(LibraryPath()); Process.Start(new ProcessStartInfo(LibraryPath()) { UseShellExecute = true }); }));
        actions.Controls.Add(Button("Export selected...", Export)); actions.Controls.Add(Button("Copy path", () => Clipboard.SetText(Selected().Path)));
        actions.Controls.Add(Button("Delete selected...", Delete));
        layout.Controls.Add(actions, 0, 5); layout.Controls.Add(status, 0, 6); Controls.Add(layout);
        commands.Add(location); commands.Add(fields);
        provider.SelectedIndexChanged += (_, _) => { feed.Enabled = provider.SelectedIndex == 0; venue.Text = provider.SelectedIndex == 0 ? "US" : "hyperliquid"; symbol.Text = provider.SelectedIndex == 0 ? "SPY" : "BTC"; };
        grid.SelectionChanged += (_, _) => details.Text = grid.CurrentRow?.DataBoundItem is HistoricalDataEntry entry ? entry.Path + Environment.NewLine + entry.Notes : "";
        Shown += async (_, _) => await RefreshAsync();
        FormClosing += (_, e) => { if (operation is not null) { e.Cancel = true; status.Text = "Wait for the operation to finish, or cancel the download."; } };
    }

    /// <summary>Creates a fixed provider choice with an accessible label.</summary>
    private static ComboBox Choice(string name, params string[] values)
    {
        var control = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name };
        control.Items.AddRange(values); control.SelectedIndex = 0; return control;
    }

    /// <summary>Creates a date-only field whose value is interpreted as UTC midnight.</summary>
    private static DateTimePicker DateField(string name, DateTime value) => new() { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Value = value, AccessibleName = name };

    /// <summary>Places a caption and its input in the download settings grid.</summary>
    private static void AddField(TableLayoutPanel panel, string caption, Control control, int column, int row)
    {
        panel.Controls.Add(new Label { Text = caption, AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, column, row);
        panel.Controls.Add(control, column + 1, row);
    }

    /// <summary>Registers an action that is disabled during background work and contains UI errors.</summary>
    private Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => { try { action(); } catch (Exception error) { ShowError(error); } };
        commands.Add(button); return button;
    }

    /// <summary>Resolves the current folder, rejecting an empty input.</summary>
    private string LibraryPath() => string.IsNullOrWhiteSpace(folder.Text) ? throw new InvalidOperationException("Choose a local data folder.") : Path.GetFullPath(folder.Text.Trim());

    /// <summary>Chooses a library folder and refreshes its contents.</summary>
    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose historical data library", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { folder.Text = dialog.SelectedPath; _ = RefreshAsync(); }
    }

    /// <summary>Returns the selected dataset or explains why an action cannot proceed.</summary>
    private HistoricalDataEntry Selected() => grid.CurrentRow?.DataBoundItem as HistoricalDataEntry ?? throw new InvalidOperationException("Select a local dataset first.");

    /// <summary>Refreshes metadata off the UI thread and leaves malformed files visible.</summary>
    internal async Task RefreshAsync()
    {
        if (operation is not null) return;
        try { SetBusy(true); await ScanAsync(LibraryPath()); }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    /// <summary>Binds metadata without exposing long file paths as table columns.</summary>
    private async Task ScanAsync(string directory)
    {
        var entries = await Task.Run(() => HistoricalDataLibrary.Scan(directory));
        grid.DataSource = entries;
        grid.Columns[nameof(HistoricalDataEntry.Path)]!.Visible = false;
        grid.Columns[nameof(HistoricalDataEntry.Notes)]!.Visible = false;
        grid.Columns[nameof(HistoricalDataEntry.File)]!.DisplayIndex = 0;
        grid.Columns[nameof(HistoricalDataEntry.File)]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        grid.Columns[nameof(HistoricalDataEntry.File)]!.Width = 140;
        grid.Columns[nameof(HistoricalDataEntry.StartUtc)]!.HeaderText = "Start (UTC)";
        grid.Columns[nameof(HistoricalDataEntry.EndUtc)]!.HeaderText = "End (UTC)";
        foreach (var name in new[] { nameof(HistoricalDataEntry.StartUtc), nameof(HistoricalDataEntry.EndUtc) }) grid.Columns[name]!.DefaultCellStyle.Format = "yyyy-MM-dd HH:mm";
        status.Text = entries.Count == 0 ? "No local datasets. Download history to populate this folder." : $"{entries.Count} dataset(s). Coverage bounds are UTC; Valid indicates structural validation, not gap-free coverage.";
    }

    /// <summary>Validates fields and downloads with cancellation while keeping native controls responsive.</summary>
    internal async Task DownloadAsync()
    {
        if (operation is not null) return;
        try
        {
            var directory = LibraryPath();
            if (string.IsNullOrWhiteSpace(symbol.Text) || string.IsNullOrWhiteSpace(venue.Text) || string.IsNullOrWhiteSpace(version.Text)) throw new InvalidOperationException("Symbol, venue and data version are required.");
            var request = new DataRequest(new Instrument(venue.Text.Trim(), provider.SelectedIndex == 0 ? AssetClass.Equity : AssetClass.LinearPerpetual, symbol.Text.Trim().ToUpperInvariant()),
                interval.SelectedIndex == 0 ? BarInterval.Daily : BarInterval.Hourly, new DateTimeOffset(start.Value.Date, TimeSpan.Zero), new DateTimeOffset(end.Value.Date, TimeSpan.Zero), version.Text.Trim());
            if (request.Start >= request.End || request.End > DateTimeOffset.UtcNow) throw new InvalidOperationException("Choose a start before the exclusive end, with the end no later than today.");
            var providerName = provider.Text; var feedName = feed.Text;
            SetBusy(true); cancel.Enabled = true; status.Text = "Downloading and validating history...";
            var token = operation!.Token;
            await Task.Run(() => HistoricalDataLibrary.DownloadAsync(directory, providerName, feedName, request, token));
            await ScanAsync(directory); status.Text = "Download complete. Select the dataset to export it or copy its path into a run configuration.";
        }
        catch (OperationCanceledException) { status.Text = "Download cancelled."; }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    /// <summary>Exports the selected normalized dataset using the native overwrite prompt.</summary>
    private void Export()
    {
        var entry = Selected();
        using var dialog = new SaveFileDialog { Filter = "Citrus dataset (*.json)|*.json", FileName = "historical-data.json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { HistoricalDataLibrary.Export(entry.Path, dialog.FileName); status.Text = "Exported " + dialog.FileName; }
    }

    /// <summary>Removes only the selected file after showing its exact path for confirmation.</summary>
    private void Delete()
    {
        var entry = Selected();
        if (MessageBox.Show(this, "Permanently delete this local dataset? Runs using it will need another data file.\n\n" + entry.Path, "Delete historical data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        File.Delete(entry.Path); _ = RefreshAsync();
    }

    /// <summary>Disables conflicting commands for the lifetime of a background operation.</summary>
    private void SetBusy(bool value)
    {
        if (value) operation = new CancellationTokenSource();
        else { operation?.Dispose(); operation = null; }
        foreach (var command in commands) command.Enabled = !value;
        progress.Visible = value; cancel.Enabled = false;
    }

    /// <summary>Shows actionable local validation errors without echoing arbitrary provider diagnostics.</summary>
    private void ShowError(Exception error)
    {
        var message = error is InvalidOperationException ? error.Message : "Operation failed. Check the folder permissions, dataset format, network connection and provider access. No completed download was reported.";
        status.Text = message;
        MessageBox.Show(this, message, "Historical data", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

