using System.ComponentModel;
using Citrus.Data;

namespace Citrus.Desktop;

/// <summary>Edits user-wide settings with masked credentials and explicit save or cancel.</summary>
public partial class GlobalSettingsWindow : Window
{
    private readonly string? configurationPath;
    private bool loadFailed;

    /// <summary>Builds designable settings fields without reading the user profile.</summary>
    public GlobalSettingsWindow() : this(null) { }

    /// <summary>Loads the selected settings file only after a runtime window is opened.</summary>
    internal GlobalSettingsWindow(string? configurationPath)
    {
        InitializeComponent();
        this.configurationPath = configurationPath;
        Loaded += LoadSettings;
    }

    /// <summary>Loads saved credentials and prevents overwriting a damaged or unreadable file.</summary>
    private void LoadSettings(object sender, RoutedEventArgs e)
    {
        if (DesignerProperties.GetIsInDesignMode(this)) return;
        try
        {
            location.Text = configurationPath ?? GlobalConfiguration.DefaultPath;
            var settings = GlobalConfiguration.Load(location.Text);
            key.Password = settings.AlpacaApiKeyId;
            secret.Password = settings.AlpacaApiSecretKey;
            historicalData.Text = settings.ResolveHistoricalDataDirectory(_ => null);
        }
        catch (Exception error)
        {
            loadFailed = true;
            errorText.Text = "Settings could not be loaded. The file will not be overwritten. " + ErrorDialog.Redact(error.Message);
        }
    }

    /// <summary>Synchronizes masked and visible fields when the user explicitly reveals credentials.</summary>
    private void RevealCredentials(object sender, RoutedEventArgs e)
    {
        var show = reveal.IsChecked == true;
        if (show) { visibleKey.Text = key.Password; visibleSecret.Text = secret.Password; }
        else { key.Password = visibleKey.Text; secret.Password = visibleSecret.Text; visibleKey.Clear(); visibleSecret.Clear(); }
        key.Visibility = secret.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        visibleKey.Visibility = visibleSecret.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Saves values only after a successful initial read, without changing the file on cancel.</summary>
    internal bool SaveSettings()
    {
        if (loadFailed) return false;
        try
        {
            new GlobalConfiguration
            {
                AlpacaApiKeyId = (reveal.IsChecked == true ? visibleKey.Text : key.Password).Trim(),
                AlpacaApiSecretKey = (reveal.IsChecked == true ? visibleSecret.Text : secret.Password).Trim(),
                HistoricalDataDirectory = historicalData.Text.Trim()
            }.Save(configurationPath ?? GlobalConfiguration.DefaultPath);
            return true;
        }
        catch (Exception error)
        {
            errorText.Text = "Could not save global settings. " + ErrorDialog.Redact(error.Message);
            return false;
        }
    }

    /// <summary>Closes the dialog after the explicit save succeeds.</summary>
    private void SaveClicked(object sender, RoutedEventArgs e) { if (SaveSettings()) Close(); }

    /// <summary>Closes the window without persisting edited fields.</summary>
    private void CancelClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>Selects the shared historical-data cache.</summary>
    private void BrowseHistoricalData(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the main historical data cache", InitialDirectory = historicalData.Text };
        if (dialog.ShowDialog(this) == true) historicalData.Text = dialog.FolderName;
    }
}
