using System.Data;
using System.Diagnostics;
using Citrus.Engine;

namespace Citrus.Desktop;

/// <summary>Coordinates XAML workspace views, saved documents, and background backtests.</summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, DataGrid> grids;
    private readonly string? historicalDataDirectory;
    private string? configPath;
    private string? strategyType;
    private string? outputPath;
    private bool configDirty;
    private bool loading;
    private bool busy;
    private string? catalogRoot;
    /// <summary>Tracks initial asynchronous option discovery for the current workspace.</summary>
    internal Task OptionsReady { get; private set; } = Task.CompletedTask;

    /// <summary>Creates an empty workbench without accessing user files in the designer.</summary>
    public MainWindow() : this(null, null) { }

    /// <summary>Creates the workspace and defers opening an optional strategy until the window is loaded.</summary>
    internal MainWindow(string? initialPath, string? historicalDataDirectory)
    {
        InitializeComponent();
        this.historicalDataDirectory = historicalDataDirectory;
        grids = new() { ["Orders"] = orders, ["Fills"] = fills, ["Positions"] = positions,
            ["Costs"] = costs, ["Equity"] = equity, ["Attribution"] = attribution };
        foreach (var grid in grids.Values) grid.AutoGeneratingColumn += BindResultColumn;
        configEditor.ConfigurationChanged += (_, _) =>
        {
            if (!loading) configDirty = true;
            UpdateTitle();
        };
        Closing += OnClosing;
        Closed += (_, _) => { chart.Dispose(); ClearTables(); };
        if (!DesktopSmokeTest.IsRunning)
            Loaded += (_, _) =>
            {
                RefreshStrategies();
                if (initialPath is not null) Guard(() => LoadConfiguration(initialPath));
            };
    }

    /// <summary>Releases table storage after a completed run is replaced or the window closes.</summary>
    private void ClearTables()
    {
        foreach (var grid in grids.Values)
        {
            var table = (grid.ItemsSource as DataView)?.Table;
            grid.ItemsSource = null;
            table?.Dispose();
        }
    }

    /// <summary>Binds literal flattened column names without interpreting periods as nested WPF paths.</summary>
    private void BindResultColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.Column is DataGridBoundColumn column)
            column.Binding = new System.Windows.Data.Binding { Path = new PropertyPath("(0)", e.PropertyDescriptor) };
    }

    /// <summary>Prevents conflicting menu and keyboard commands during execution.</summary>
    private void CanExecuteCommand(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !busy;

    /// <summary>Routes menu and keyboard commands through the UI error boundary.</summary>
    private void ExecuteCommand(object sender, ExecutedRoutedEventArgs e) => Guard(() =>
    {
        switch (e.Parameter as string)
        {
            case "Open": Open(); break;
            case "RefreshStrategies": RefreshStrategies(); break;
            case "IDE": OpenDevelopmentEnvironment(); break;
            case "Example": CreateExample(); break;
            case "Save": SaveAll(); break;
            case "Validate": _ = ValidateAsync(); break;
            case "Run": _ = RunAsync(); break;
            case "Results": OpenResults(); break;
            case "Copy": CopyBacktest(); break;
            case "History": new HistoricalDataWindow(historicalDataDirectory) { Owner = this }.ShowDialog(); break;
            case "Settings": new GlobalSettingsWindow { Owner = this }.ShowDialog(); break;
            case "Exit": Close(); break;
            case "About": MessageBox.Show(this, "Citrus Backtesting Workbench\nWPF • .NET 10\n\nRun built-in C# strategies and analyse deterministic backtests.\nAll result timestamps are UTC.", "About Citrus"); break;
        }
    });

    /// <summary>Loads a selected named backtest and restores the selection when navigation is cancelled.</summary>
    private void BacktestChanged(object sender, RoutedEventArgs e)
    {
        if (loading || configPath is null || sender is not MenuItem { Tag: string name }) return;
        Guard(() =>
        {
            try { LoadConfiguration(name); }
            finally { RefreshBacktests(); }
        });
    }

    /// <summary>Selects the tab represented by a workspace navigation item.</summary>
    private void NavigateWorkspace(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string index }) tabs.SelectedIndex = int.Parse(index);
    }

    /// <summary>Lists sibling backtests and restores the active selection without triggering navigation.</summary>
    private void RefreshBacktests()
    {
        loading = true;
        try
        {
            backtests.Items.Clear();
            var root = configPath is null ? null : Path.GetDirectoryName(configPath);
            backtests.IsEnabled = root is not null;
            if (root is null) return;
            var config = configEditor.ReadConfiguration(validate: false);
            var paths = StrategyCatalog.Backtests(root, config.StrategyType!);
            foreach (var path in paths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var item = new MenuItem
                {
                    Header = new TextBlock { Text = name },
                    Tag = path,
                    IsCheckable = true,
                    IsChecked = string.Equals(name, Path.GetFileNameWithoutExtension(configPath), StringComparison.OrdinalIgnoreCase)
                };
                item.Click += BacktestChanged;
                backtests.Items.Add(item);
            }
        }
        finally { loading = false; }
    }

    /// <summary>Copies current saved settings to a named backtest with its own result directory.</summary>
    private void CopyBacktest()
    {
        if (configPath is null)
            throw new InvalidOperationException("Select a strategy first.");
        var root = Path.GetDirectoryName(configPath)!;
        var dialog = new SaveFileDialog { Title = "Copy backtest", Filter = "Backtest (*.json)|*.json", InitialDirectory = root, FileName = "NewBacktest.json" };
        if (dialog.ShowDialog(this) != true) return;
        SaveAll();
        var configuration = configEditor.ReadConfiguration() with { Output = "Results/" + Path.GetFileNameWithoutExtension(dialog.FileName) };
        Citrus.Data.Json.Write(dialog.FileName, configuration);
        LoadConfiguration(dialog.FileName);
    }

    /// <summary>Contains synchronous command errors at the UI boundary.</summary>
    private void Guard(Action action)
    {
        if (busy) return;
        try { action(); } catch (Exception exception) { ShowError(exception); }
    }

    /// <summary>Shows the strategy list and refreshes it when first opened.</summary>
    private void Open()
    {
        tabs.SelectedIndex = 0;
        strategies.Focus();
        if (strategies.Items.Count == 0) RefreshStrategies();
    }

    /// <summary>Lists the runnable strategy classes included in this Citrus build.</summary>
    internal void RefreshStrategies()
    {
        if (busy) return;
        try
        {
            SetBusy(true, "Loading Citrus.Strategies...");
            catalogRoot = StrategyCatalog.ConfigurationDirectory();
            var names = BuiltInStrategies.Names();
            loading = true;
            try
            {
                strategies.ItemsSource = names;
                strategies.SelectedItem = configPath is null ? null : configEditor.ReadConfiguration(false).StrategyType;
            }
            finally { loading = false; }
            catalogStatus.Text = names.Length == 0 ? "No runnable strategies found. Add a public strategy class, rebuild Citrus and restart."
                : $"{names.Length} strategies — select one to load its backtest settings.";
            status.Text = "Ready — select a strategy";
        }
        catch (Exception exception)
        {
            catalogStatus.Text = "Could not list built-in strategies. See the execution log.";
            ShowError(exception);
        }
        finally { SetBusy(false); }
    }

    /// <summary>Opens saved settings for the selected type, preserving edits if selection is cancelled.</summary>
    private void StrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || busy || catalogRoot is null || strategies.SelectedItem is not string type) return;
        Guard(() =>
        {
            try
            {
                if (!ConfirmEdits()) return;
                configDirty = false;
                LoadConfiguration(StrategyCatalog.Configuration(catalogRoot, type));
            }
            finally
            {
                loading = true;
                try { strategies.SelectedItem = configPath is null ? null : configEditor.ReadConfiguration(false).StrategyType; }
                finally { loading = false; }
            }
        });
    }

    /// <summary>Loads saved settings only after reads succeed and outstanding edits are resolved.</summary>
    internal void LoadConfiguration(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var configuration = RunConfiguration.Read(fullPath);
        var selectedType = configuration.StrategyType;
        if (!BuiltInStrategies.Names().Contains(selectedType)) throw new InvalidDataException("Unknown built-in strategy: " + selectedType);
        if (!ConfirmEdits()) return;
        // Re-read after saving in case the selected run is the currently edited document.
        configuration = RunConfiguration.Read(fullPath);
        selectedType = configuration.StrategyType;
        loading = true;
        try
        {
            configEditor.LoadConfiguration(configuration); configEditor.ConfigurationPath = fullPath;
        }
        finally { loading = false; }
        configPath = fullPath; strategyType = selectedType; RefreshBacktests();
        loading = true;
        try { strategies.SelectedItem = configuration.StrategyType; }
        finally { loading = false; }
        configDirty = false;
        ClearResults(); UpdateTitle();
        status.Text = fullPath;
        AppendLog("Opened " + fullPath);
        OptionsReady = ValidateAsync(save: false);
    }

    /// <summary>Generates an isolated sample workspace and opens it for immediate offline execution.</summary>
    private void CreateExample()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a parent folder for a new self-contained Citrus example" };
        if (dialog.ShowDialog(this) == true) LoadConfiguration(BacktestWorkspace.CreateExample(dialog.FolderName, historicalDataDirectory));
    }

    /// <summary>Saves validated backtest settings; strategy source is built into Citrus.Strategies.</summary>
    private void SaveAll(bool validate = true)
    {
        if (configPath is null || strategyType is null) throw new InvalidOperationException("Open a run configuration or create an example first.");
        var configuration = configEditor.ReadConfiguration(validate);

        if (configDirty) { Citrus.Data.Json.Write(configPath, configuration); configDirty = false; }
        UpdateTitle(); AppendLog("Saved workspace documents.");
    }

    /// <summary>Offers save, discard, or cancel before replacing documents or closing.</summary>
    private bool ConfirmEdits()
    {
        if (!configDirty) return true;
        var choice = MessageBox.Show(this, "Save changes to the run configuration?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) SaveAll();
        return true;
    }

    /// <summary>Captures saved settings and the selected built-in strategy type.</summary>
    private RunConfiguration Prepare()
    {
        SaveAll(validate: false);
        var configuration = configEditor.ReadConfiguration(validate: false);
        strategyType = configuration.StrategyType;
        UpdateTitle();
        return configuration;
    }

    /// <summary>Opens the main Citrus solution using its Windows file association.</summary>
    private void OpenDevelopmentEnvironment()
    {
        var path = Path.Combine(StrategyCatalog.FindRoot(), "Citrus.slnx");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Reads and validates built-in strategy settings away from the UI thread.</summary>
    private async Task ValidateAsync(bool save = true)
    {
        if (busy) return;
        try
        {
            var config = save ? Prepare() : configEditor.ReadConfiguration(validate: false);
            var path = configPath!;
            SetBusy(true, "Validating strategy...");
            var resolved = await Task.Run(() =>
            {
                var strategy = BuiltInStrategies.Create(config.StrategyType);
                return (Name: strategy.GetType().Name, Configuration: StrategyConfiguration.Resolve(strategy, config),
                    Fields: StrategyConfiguration.Declarations(strategy).Keys.Select(StrategyConfiguration.Field).ToArray());
            });
            configEditor.LoadConfiguration(resolved.Configuration, resolved.Fields);
            AppendLog("Valid strategy: " + resolved.Name); status.Text = "Strategy settings loaded — C# assignments are read-only";
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    /// <summary>Executes one saved backtest in the background and binds only its completed results.</summary>
    internal async Task RunAsync()
    {
        if (busy) return;
        try
        {
            var config = Prepare();
            var path = configPath!;
            ClearResults();
            SetBusy(true, "Running backtest — preparing data, simulating and exporting...");
            var watch = Stopwatch.StartNew();
            var completed = await Task.Run(() => BacktestRunner.Run(path, config, historicalDataDirectory,
                (effective, fields) => Dispatcher.Invoke(() => configEditor.LoadConfiguration(effective, fields))));
            Present(completed);
            status.Text = $"Completed in {watch.Elapsed.TotalSeconds:N1}s — {completed.Output}";
            AppendLog(status.Text);
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    /// <summary>Shows portfolio performance, equity, and flattened sortable execution/accounting records.</summary>
    internal void Present(CompletedBacktest completed)
    {
        outputPath = completed.Output;
        var r = completed.Result; var p = completed.Performance;
        metrics.Text = $"Final equity: {r.Final.Equity:N2}     Total return: {p.TotalReturn?.ToString("P2") ?? "N/A"}     Max drawdown: {p.MaximumDrawdown:P2}\n" +
            $"Annualized Sharpe: {p.AnnualizedSharpe?.ToString("N3") ?? "N/A"}     Daily observations: {p.DailyObservations:N0}     Attributed fills: {r.Fills.Count:N0}";
        chart.SetPoints(r.Equity);
        Bind("Orders", r.Orders); Bind("Fills", r.Fills); Bind("Positions", r.Final.Positions);
        Bind("Costs", r.Costs); Bind("Equity", r.Equity); Bind("Attribution", r.Attribution);
        tabs.SelectedIndex = 2;
    }

    /// <summary>Binds a result table and releases the previous table's storage.</summary>
    private void Bind<T>(string name, IEnumerable<T> records)
    {
        var table = ResultTable.Create(records);
        var previous = (grids[name].ItemsSource as DataView)?.Table;
        grids[name].ItemsSource = table.DefaultView; previous?.Dispose();
    }

    /// <summary>Clears prior-run results so failures cannot be mistaken for a successful new run.</summary>
    private void ClearResults()
    {
        outputPath = null; chart.SetPoints([]); metrics.Text = "No completed backtest";
        ClearTables();
    }

    /// <summary>Opens the actual completed report directory in Windows Explorer.</summary>
    private void OpenResults()
    {
        if (outputPath is null) throw new InvalidOperationException("Complete a backtest first.");
        Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
    }

    /// <summary>Prevents concurrent commands while keeping results and logs responsive.</summary>
    private void SetBusy(bool value, string? message = null)
    {
        busy = value; menu.IsEnabled = !value;
        strategyPage.IsEnabled = !value;
        configPage.IsEnabled = !value;
        progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        CommandManager.InvalidateRequerySuggested();
        if (message is not null) { status.Text = message; AppendLog(message); }
    }

    /// <summary>Marks edited settings and identifies the current run and strategy.</summary>
    private void UpdateTitle()
    {
        strategyPage.Header = "Strategy";
        strategyPage.ToolTip = strategyType;
        configPage.Header = "Configuration" + (configDirty ? " *" : "");
        Title = $"{(configPath is null ? "Citrus" : Path.GetFileName(configPath) + " — Citrus")} — Backtesting Workbench";
    }

    /// <summary>Appends a timestamped diagnostic line and scrolls it into view.</summary>
    private void AppendLog(string message) { log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); log.ScrollToEnd(); }

    /// <summary>Redacts configured credentials from arbitrary strategy errors before presenting them.</summary>
    private void ShowError(Exception exception)
    {
        var message = ErrorDialog.Redact(exception.Message);
        status.Text = "Operation failed — see execution log"; AppendLog(message);
        ErrorDialog.Show(this, "Citrus operation failed", exception);
    }

    /// <summary>Prevents closing mid-run and preserves unsaved configuration edits.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; status.Text = "Wait for the current operation to finish before closing."; return; }
        try { e.Cancel = !ConfirmEdits(); } catch (Exception exception) { e.Cancel = true; ShowError(exception); }
    }
}
