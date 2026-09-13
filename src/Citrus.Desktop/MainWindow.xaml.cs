using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Citrus.Engine;

namespace Citrus.Desktop;

/// <summary>Coordinates XAML workspace views, saved documents, and background backtests.</summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, DataGrid> grids;
    private readonly string? historicalDataDirectory;
    private string? configPath;
    private string? strategyPath;
    private string? outputPath;
    private bool strategyDirty;
    private bool configDirty;
    private bool loading;
    private bool busy;

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
        strategyEditor.SourceChanged += (_, _) => { if (!loading) strategyDirty = true; UpdateTitle(); };
        configEditor.ConfigurationChanged += (_, _) =>
        {
            if (!loading) { configDirty = true; RefreshEditorReferences(); }
            UpdateTitle();
        };
        Closing += OnClosing;
        Closed += (_, _) => { strategyEditor.Dispose(); chart.Dispose(); ClearTables(); };
        if (initialPath is not null) Loaded += (_, _) => Guard(() => LoadConfiguration(initialPath));
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
            case "Example": CreateExample(); break;
            case "Save": SaveAll(); break;
            case "Validate": _ = ValidateAsync(); break;
            case "Run": _ = RunAsync(); break;
            case "Results": OpenResults(); break;
            case "Copy": CopyBacktest(); break;
            case "History": new HistoricalDataWindow(historicalDataDirectory) { Owner = this }.ShowDialog(); break;
            case "Settings": new GlobalSettingsWindow { Owner = this }.ShowDialog(); break;
            case "Exit": Close(); break;
            case "About": MessageBox.Show(this, "Citrus Backtesting Workbench\nWPF • AvalonEdit • .NET 10\n\nEdit trusted C# strategies and run deterministic backtests.\nAll result timestamps are UTC.", "About Citrus"); break;
        }
    });

    /// <summary>Loads a selected named backtest and restores the selection when navigation is cancelled.</summary>
    private void BacktestChanged(object sender, RoutedEventArgs e)
    {
        if (loading || configPath is null || sender is not MenuItem { Tag: string name }) return;
        Guard(() =>
        {
            try { LoadConfiguration(StrategyFolder.ConfigurationPath(StrategyFolder.Root(configPath)!, name)); }
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
            var root = configPath is null ? null : StrategyFolder.Root(configPath);
            backtests.IsEnabled = root is not null;
            if (root is null) return;
            foreach (var path in StrategyFolder.Backtests(root))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var item = new MenuItem
                {
                    Header = new TextBlock { Text = name },
                    Tag = name,
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
        if (configPath is null || StrategyFolder.Root(configPath) is not string root)
            throw new InvalidOperationException("Open a strategy folder first.");
        var dialog = new SaveFileDialog { Title = "Copy backtest", Filter = "Backtest (*.json)|*.json", InitialDirectory = Path.Combine(root, "Backtests"), FileName = "NewBacktest.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (!string.Equals(Path.GetDirectoryName(dialog.FileName), Path.Combine(root, "Backtests"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Save the backtest inside this strategy's Backtests folder.");
        SaveAll();
        var configuration = configEditor.ReadConfiguration() with { Strategy = null, Output = "Results/" + Path.GetFileNameWithoutExtension(dialog.FileName) };
        Citrus.Data.Json.Write(dialog.FileName, configuration);
        LoadConfiguration(dialog.FileName);
    }

    /// <summary>Contains synchronous command errors at the UI boundary.</summary>
    private void Guard(Action action)
    {
        if (busy) return;
        try { action(); } catch (Exception exception) { ShowError(exception); }
    }

    /// <summary>Prompts for an existing run configuration.</summary>
    private void Open()
    {
        var dialog = new OpenFolderDialog { Title = "Open Citrus strategy folder" };
        if (dialog.ShowDialog(this) == true) LoadConfiguration(dialog.FolderName);
    }

    /// <summary>Loads both documents only after reads succeed and outstanding edits are resolved.</summary>
    internal void LoadConfiguration(string path)
    {
        var fullPath = StrategyFolder.ConfigurationPath(path);
        var configuration = StrategyFolder.Read(fullPath);
        var sourcePath = StrategyFolder.Source(fullPath, configuration);
        var source = File.ReadAllText(sourcePath);
        if (!ConfirmEdits()) return;
        // Re-read after saving in case the selected run is the currently edited document.
        configuration = StrategyFolder.Read(fullPath);
        sourcePath = StrategyFolder.Source(fullPath, configuration);
        source = File.ReadAllText(sourcePath);
        loading = true;
        try
        {
            configEditor.LoadConfiguration(configuration); configEditor.ConfigurationPath = fullPath;
            strategyEditor.LoadSource(source, sourcePath, configuration.References.Select(p => BacktestWorkspace.Resolve(fullPath, p)).ToArray());
        }
        finally { loading = false; }
        configPath = fullPath; strategyPath = sourcePath; RefreshBacktests();
        configDirty = strategyDirty = false;
        ClearResults(); UpdateTitle();
        status.Text = fullPath;
        AppendLog("Opened " + fullPath);
    }

    /// <summary>Generates an isolated sample workspace and opens it for immediate offline execution.</summary>
    private void CreateExample()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a parent folder for a new self-contained Citrus example" };
        if (dialog.ShowDialog(this) == true) LoadConfiguration(BacktestWorkspace.CreateExample(dialog.FolderName, historicalDataDirectory));
    }

    /// <summary>Saves valid configuration JSON and the source document currently shown in the editor.</summary>
    private void SaveAll()
    {
        if (configPath is null || strategyPath is null) throw new InvalidOperationException("Open a run configuration or create an example first.");
        var configuration = configEditor.ReadConfiguration();
        if (strategyDirty) { File.WriteAllText(strategyPath, strategyEditor.SourceText); strategyDirty = false; }
        if (configDirty) { Citrus.Data.Json.Write(configPath, configuration); configDirty = false; }
        UpdateTitle(); AppendLog("Saved workspace documents.");
    }

    /// <summary>Offers save, discard, or cancel before replacing documents or closing.</summary>
    private bool ConfirmEdits()
    {
        if (!strategyDirty && !configDirty) return true;
        var choice = MessageBox.Show(this, "Save changes to the strategy and run configuration?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) SaveAll();
        return true;
    }

    /// <summary>Captures saved settings and refreshes source if the configured strategy path has changed.</summary>
    private RunConfiguration Prepare()
    {
        SaveAll();
        var configuration = configEditor.ReadConfiguration();
        var path = StrategyFolder.Source(configPath!, configuration);
        if (!string.Equals(path, strategyPath, StringComparison.OrdinalIgnoreCase))
        {
            var text = File.ReadAllText(path);
            loading = true;
            try
            {
                strategyEditor.LoadSource(text, path, configuration.References.Select(p => BacktestWorkspace.Resolve(configPath!, p)).ToArray());
                strategyPath = path; strategyDirty = false;
            }
            finally { loading = false; }
            UpdateTitle();
        }
        return configuration;
    }

    /// <summary>Uses edited reference paths for live code assistance while keeping the currently displayed strategy.</summary>
    private void RefreshEditorReferences()
    {
        if (configPath is null) return;
        // Resolve and validate file names on the language service's background thread, including partially typed paths.
        strategyEditor.SetReferences(configEditor.ReadReferences().Select(p => StrategyFolder.Resolve(configPath, p)).ToArray());
    }

    /// <summary>Compiles the saved strategy away from the UI thread and reports compiler diagnostics.</summary>
    private async Task ValidateAsync()
    {
        if (busy) return;
        try
        {
            var config = Prepare();
            var path = configPath!;
            SetBusy(true, "Validating strategy...");
            var name = await Task.Run(() =>
            {
                using var compiled = CompiledStrategy.Load(StrategyFolder.Source(path, config), config.References.Select(p => BacktestWorkspace.Resolve(path, p)));
                return compiled.Strategy.GetType().Name;
            });
            AppendLog("Valid strategy: " + name); status.Text = "Strategy validation succeeded";
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
            SetBusy(true, "Running backtest — preparing data, compiling, simulating and exporting...");
            var watch = Stopwatch.StartNew();
            var completed = await Task.Run(() => BacktestWorkspace.RunAsync(path, config, historicalDataDirectory));
            Present(completed);
            status.Text = $"Completed in {watch.Elapsed.TotalSeconds:N1}s — {completed.Output}";
            AppendLog(status.Text);
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    /// <summary>Shows portfolio performance, equity, and flattened sortable execution/accounting records.</summary>
    internal void Present(WorkspaceResult completed)
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

    /// <summary>Flattens nested record properties into typed columns, limiting UI rows while preserving full exports.</summary>
    private void Bind<T>(string name, IEnumerable<T> records)
    {
        var rows = records.Take(5000).Select(record =>
        {
            var values = new Dictionary<string, object>();
            Flatten(JsonSerializer.SerializeToElement(record, Citrus.Data.Json.Options), "", values);
            return values;
        }).ToArray();
        var table = new DataTable();
        foreach (var key in rows.SelectMany(r => r.Keys).Distinct())
        {
            var value = rows.Select(r => r.GetValueOrDefault(key)).FirstOrDefault(v => v is not null && v != DBNull.Value);
            table.Columns.Add(key, value?.GetType() ?? typeof(string));
        }
        foreach (var row in rows) table.Rows.Add(table.Columns.Cast<DataColumn>().Select(c => row.GetValueOrDefault(c.ColumnName, DBNull.Value)).ToArray());
        var previous = (grids[name].ItemsSource as DataView)?.Table;
        grids[name].ItemsSource = table.DefaultView; previous?.Dispose();
    }

    /// <summary>Expands objects such as instruments and substrategy dictionaries into named scalar columns.</summary>
    private static void Flatten(JsonElement element, string prefix, Dictionary<string, object> row)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + "." + property.Name, row);
            return;
        }
        row[prefix] = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.Null => DBNull.Value,
            _ => element.ToString()
        };
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

    /// <summary>Prevents concurrent commands and source mutation while keeping results and logs responsive.</summary>
    private void SetBusy(bool value, string? message = null)
    {
        busy = value; menu.IsEnabled = !value;
        strategyEditor.SetReadOnly(value);
        configPage.IsEnabled = !value;
        progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        CommandManager.InvalidateRequerySuggested();
        if (message is not null) { status.Text = message; AppendLog(message); }
    }

    /// <summary>Marks edited documents and identifies the current run and source in their tabs.</summary>
    private void UpdateTitle()
    {
        strategyPage.Header = "Strategy" + (strategyDirty ? " *" : "");
        strategyPage.ToolTip = strategyPath;
        configPage.Header = "Configuration" + (configDirty ? " *" : "");
        Title = $"{(configPath is null ? "Citrus" : (StrategyFolder.Root(configPath) is string folder ? Path.GetFileName(folder) + " / " + Path.GetFileNameWithoutExtension(configPath) : Path.GetFileName(configPath)) + " — Citrus")} — Backtesting Workbench";
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

    /// <summary>Prevents closing mid-run and preserves unsaved source/configuration edits.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; status.Text = "Wait for the current operation to finish before closing."; return; }
        try { e.Cancel = !ConfirmEdits(); } catch (Exception exception) { e.Cancel = true; ShowError(exception); }
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {

    }
}
