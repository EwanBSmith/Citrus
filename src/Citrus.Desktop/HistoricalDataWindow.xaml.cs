using System.ComponentModel;
using System.Diagnostics;
using Citrus.Data;
using Citrus.Trading;

namespace Citrus.Desktop;

/// <summary>Downloads provider history and manages the local library through designable WPF views.</summary>
public partial class HistoricalDataWindow : Window
{
    private CancellationTokenSource? operation;

    /// <summary>Creates an empty, designer-safe historical-data view.</summary>
    public HistoricalDataWindow() : this(null) { }

    /// <summary>Configures provider choices and defers cache access until runtime loading.</summary>
    internal HistoricalDataWindow(string? directory)
    {
        InitializeComponent();
        provider.ItemsSource = new[] { "Alpaca", "Hyperliquid" };
        feed.ItemsSource = new[] { "iex", "sip" };
        interval.ItemsSource = new[] { "1d", "1h" };
        provider.SelectedIndex = feed.SelectedIndex = interval.SelectedIndex = 0;
        start.SelectedDate = DateTime.UtcNow.Date.AddMonths(-1);
        end.SelectedDate = DateTime.UtcNow.Date;
        Loaded += async (_, _) =>
        {
            if (DesignerProperties.GetIsInDesignMode(this)) return;
            try { folder.Text = directory ?? HistoricalDataLibrary.DefaultDirectory; await RefreshAsync(); }
            catch (Exception error) { ShowError(error); }
        };
        Closing += (_, e) => { if (operation is not null) { e.Cancel = true; status.Text = "Wait for the operation to finish, or cancel the download."; } };
    }

    /// <summary>Updates the provider-specific venue, symbol, and feed controls.</summary>
    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (feed is null) return;
        feed.IsEnabled = provider.SelectedIndex == 0;
        venue.Text = provider.SelectedIndex == 0 ? "US" : "hyperliquid";
        symbol.Text = provider.SelectedIndex == 0 ? "SPY" : "BTC";
    }

    /// <summary>Displays full metadata for the selected dataset outside the results table.</summary>
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        details.Text = grid.SelectedItem is HistoricalDataEntry entry ? entry.Path + Environment.NewLine + entry.Notes : "";

    /// <summary>Runs data commands with a shared error boundary and cancellation support.</summary>
    private void CommandClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (((FrameworkElement)sender).Tag as string)
            {
                case "Download": _ = DownloadAsync(); break;
                case "Cancel": operation?.Cancel(); cancel.IsEnabled = false; status.Text = "Cancelling download…"; break;
                case "Refresh": _ = RefreshAsync(); break;
                case "Open": Directory.CreateDirectory(LibraryPath()); Process.Start(new ProcessStartInfo(LibraryPath()) { UseShellExecute = true }); break;
                case "Export": Export(); break;
                case "Delete": Delete(); break;
            }
        }
        catch (Exception error) { ShowError(error); }
    }

    /// <summary>Resolves the current folder, rejecting an empty input.</summary>
    private string LibraryPath() => string.IsNullOrWhiteSpace(folder.Text) ? throw new InvalidOperationException("Choose a local data folder.") : Path.GetFullPath(folder.Text.Trim());

    /// <summary>Returns the selected dataset or explains why an action cannot proceed.</summary>
    private HistoricalDataEntry Selected() => grid.SelectedItem as HistoricalDataEntry ?? throw new InvalidOperationException("Select a local dataset first.");

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
        grid.ItemsSource = entries;
        status.Text = entries.Count == 0 ? "No local datasets. Download history to populate this folder." : $"{entries.Count} dataset(s). Coverage bounds are UTC; Valid indicates structural validation, not gap-free coverage.";
    }

    /// <summary>Validates fields and downloads with cancellation while keeping native controls responsive.</summary>
    internal async Task DownloadAsync()
    {
        if (operation is not null) return;
        try
        {
            var directory = LibraryPath();
            if (string.IsNullOrWhiteSpace(symbol.Text) || string.IsNullOrWhiteSpace(venue.Text)) throw new InvalidOperationException("Symbol and venue are required.");
            var request = new DataRequest(new Instrument(venue.Text.Trim(), provider.SelectedIndex == 0 ? AssetClass.Equity : AssetClass.LinearPerpetual, symbol.Text.Trim().ToUpperInvariant()),
                interval.SelectedIndex == 0 ? BarInterval.Daily : BarInterval.Hourly, new DateTimeOffset((start.SelectedDate ?? throw new InvalidOperationException("Select a start date.")).Date, TimeSpan.Zero), new DateTimeOffset((end.SelectedDate ?? throw new InvalidOperationException("Select an end date.")).Date, TimeSpan.Zero));
            if (request.Start >= request.End || request.End > DateTimeOffset.UtcNow) throw new InvalidOperationException("Choose a start before the exclusive end, with the end no later than today.");
            var providerName = (string)provider.SelectedItem; var feedName = (string)feed.SelectedItem;
            SetBusy(true); cancel.IsEnabled = true; status.Text = "Downloading and validating history...";
            var token = operation!.Token;
            await Task.Run(() => HistoricalDataLibrary.DownloadAsync(directory, providerName, feedName, request, token));
            await ScanAsync(directory); status.Text = "Download complete. Backtests now read matching history directly from the main cache.";
        }
        catch (OperationCanceledException) when (operation?.IsCancellationRequested == true) { status.Text = "Download cancelled."; }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    /// <summary>Exports the selected normalized dataset using the native overwrite prompt.</summary>
    private void Export()
    {
        var entry = Selected();
        var dialog = new SaveFileDialog { Filter = "Citrus dataset (*.json)|*.json", FileName = "historical-data.json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true) { HistoricalDataLibrary.Export(entry.Path, dialog.FileName); status.Text = "Exported " + dialog.FileName; }
    }

    /// <summary>Removes only the selected file after showing its exact path for confirmation.</summary>
    private void Delete()
    {
        var entry = Selected();
        if (MessageBox.Show(this, "Permanently delete this cached dataset? Backtests requiring it will fail until matching history is restored.\n\n" + entry.Path, "Delete historical data", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        File.Delete(entry.Path); _ = RefreshAsync();
    }

    /// <summary>Disables conflicting commands for the lifetime of a background operation.</summary>
    private void SetBusy(bool value)
    {
        if (value) operation = new CancellationTokenSource();
        else { operation?.Dispose(); operation = null; }
        fields.IsEnabled = actions.IsEnabled = downloadButton.IsEnabled = !value;
        progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed; cancel.IsEnabled = false;
    }

    /// <summary>Shows actionable local validation errors without echoing arbitrary provider diagnostics.</summary>
    private void ShowError(Exception error)
    {
        status.Text = ErrorDialog.Redact(error.Message);
        ErrorDialog.Show(this, "Historical data operation failed", error);
    }
}
