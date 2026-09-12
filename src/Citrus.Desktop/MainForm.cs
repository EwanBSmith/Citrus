using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Citrus.Engine;

namespace Citrus.Desktop;

/// <summary>Provides a classic Windows workbench for editing, running, and inspecting backtests.</summary>
internal sealed class MainForm : Form
{
    private readonly MenuStrip menu = new() { RenderMode = ToolStripRenderMode.System };
    private readonly ToolStrip toolbar = new() { RenderMode = ToolStripRenderMode.System, GripStyle = ToolStripGripStyle.Hidden };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TreeView navigation = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly RichTextBox strategyEditor = Editor("C# strategy source");
    private readonly ConfigurationEditor configEditor = new();
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly ToolStripStatusLabel status = new("Ready — open a run configuration or create an example") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar progress = new() { Visible = false, Style = ProgressBarStyle.Marquee };
    private readonly EquityChart chart = new();
    private readonly Label metrics = new() { Dock = DockStyle.Top, Height = 65, Padding = new Padding(12), Text = "No completed backtest", BackColor = SystemColors.ControlLightLight };
    private readonly TabPage strategyPage;
    private readonly TabPage configPage;
    private readonly Dictionary<string, DataGridView> grids = [];
    private string? configPath;
    private string? strategyPath;
    private string? outputPath;
    private bool strategyDirty;
    private bool configDirty;
    private bool loading;
    private bool busy;

    /// <summary>Builds the native menu, toolbar, split workspace, results tabs, and status area.</summary>
    internal MainForm(string? initialPath = null)
    {
        Text = "Citrus — Backtesting Workbench";
        Font = new Font("Segoe UI", 9F);
        Size = new Size(1280, 850);
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = SystemIcons.Application;
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Command("&Open run...", Keys.Control | Keys.O, Open));
        file.DropDownItems.Add(Command("Create &example...", Keys.None, CreateExample));
        file.DropDownItems.Add(Command("&Save all", Keys.Control | Keys.S, SaveAll));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Command("E&xit", Keys.Alt | Keys.F4, Close));
        var run = new ToolStripMenuItem("&Backtest");
        run.DropDownItems.Add(Command("&Validate strategy", Keys.F6, () => _ = ValidateAsync()));
        run.DropDownItems.Add(Command("&Run backtest", Keys.F5, () => _ = RunAsync()));
        run.DropDownItems.Add(Command("Open results &folder", Keys.None, OpenResults));
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Command("&About Citrus", Keys.None, () => MessageBox.Show(this,
            "Citrus Backtesting Workbench\nWindows Forms • .NET 10\n\nEdit trusted C# strategies and run deterministic backtests.\nStrategies execute with your normal process permissions.\nAll result timestamps are UTC.", "About Citrus", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        menu.Items.AddRange([file, run, help]);
        MainMenuStrip = menu;
        AddButton("Open...", Open);
        AddButton("New example...", CreateExample);
        AddButton("Save all", SaveAll);
        toolbar.Items.Add(new ToolStripSeparator());
        AddButton("Validate (F6)", () => _ = ValidateAsync());
        AddButton("Run backtest (F5)", () => _ = RunAsync());
        toolbar.Items.Add(new ToolStripSeparator());
        AddButton("Results folder", OpenResults);

        var workspace = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Size = new Size(1200, 700), SplitterDistance = 210, Panel1MinSize = 160 };
        workspace.Panel1.Controls.Add(navigation);
        workspace.Panel1.Controls.Add(new Label { Text = "  WORKSPACE", Dock = DockStyle.Top, Height = 29, TextAlign = ContentAlignment.MiddleLeft, BackColor = SystemColors.ControlLight });
        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, Size = new Size(950, 700), SplitterDistance = 555, Panel2MinSize = 85 };
        right.Panel1.Controls.Add(tabs);
        right.Panel2.Controls.Add(log);
        right.Panel2.Controls.Add(new Label { Text = "  Execution log", Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft });
        workspace.Panel2.Controls.Add(right);

        strategyPage = AddPage("Strategy", strategyEditor);
        strategyPage.Controls.Add(Hint("C# source • Trusted local code • Save all before validation or execution."));
        configPage = AddPage("Configuration", configEditor);
        configPage.Controls.Add(Hint("Paths are relative to the run file. Rates and margins use fractions: 0.05 = 5%."));
        var overview = AddPage("Overview", chart);
        overview.Controls.Add(metrics);
        foreach (var name in new[] { "Orders", "Fills", "Positions", "Costs", "Equity", "Attribution" })
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.Fixed3D, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AllowUserToOrderColumns = true, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
                AccessibleName = name + " results", AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(242, 245, 248) }
            };
            grids.Add(name, grid);
            AddPage(name, grid).Controls.Add(Hint("Completed run • UTC timestamps • Click column headers to sort; Ctrl+C to copy. First 5,000 rows shown; full data is exported."));
        }
        var root = navigation.Nodes.Add("Backtest workspace");
        foreach (TabPage page in tabs.TabPages) root.Nodes.Add(new TreeNode(page.Text) { Tag = page });
        root.Expand();
        navigation.AfterSelect += (_, e) => { if (e.Node?.Tag is TabPage page) tabs.SelectedTab = page; };
        var statusBar = new StatusStrip { RenderMode = ToolStripRenderMode.System };
        statusBar.Items.AddRange([status, progress]);
        Controls.Add(workspace); Controls.Add(toolbar); Controls.Add(menu); Controls.Add(statusBar);
        strategyEditor.TextChanged += (_, _) => { if (!loading) strategyDirty = true; UpdateTitle(); };
        configEditor.ConfigurationChanged += (_, _) => { if (!loading) configDirty = true; UpdateTitle(); };
        FormClosing += OnClosing;
        if (initialPath is not null) Shown += (_, _) => Guard(() => LoadConfiguration(initialPath));
    }

    /// <summary>Creates a monospaced, plain-text editor with tabs and unwrapped source lines.</summary>
    private static RichTextBox Editor(string name) => new()
    {
        Dock = DockStyle.Fill, Font = new Font("Consolas", 10F), AcceptsTab = true, WordWrap = false,
        DetectUrls = false, BorderStyle = BorderStyle.Fixed3D, AccessibleName = name
    };

    /// <summary>Creates the compact explanatory strip above an editor or result table.</summary>
    private static Label Hint(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 42, Padding = new Padding(8, 5, 8, 3) };

    /// <summary>Adds a native tab page containing a fill-docked control.</summary>
    private TabPage AddPage(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(3) };
        page.Controls.Add(content); tabs.TabPages.Add(page); return page;
    }

    /// <summary>Creates a menu command whose errors are shown in the log and a dialog.</summary>
    private ToolStripMenuItem Command(string text, Keys shortcut, Action action)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeys = shortcut };
        item.Click += (_, _) => Guard(action); return item;
    }

    /// <summary>Adds a textual toolbar button matching the classic enterprise menu commands.</summary>
    private void AddButton(string text, Action action)
    {
        var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += (_, _) => Guard(action); toolbar.Items.Add(button);
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
        using var dialog = new OpenFileDialog { Filter = "Run configuration (*.json)|*.json", Title = "Open Citrus run configuration" };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadConfiguration(dialog.FileName);
    }

    /// <summary>Loads both documents only after reads succeed and outstanding edits are resolved.</summary>
    internal void LoadConfiguration(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var text = File.ReadAllText(fullPath);
        var configuration = BacktestWorkspace.Parse(text);
        var sourcePath = BacktestWorkspace.Resolve(fullPath, configuration.Strategy);
        var source = File.ReadAllText(sourcePath);
        if (!ConfirmEdits()) return;
        // Re-read after saving in case the selected run is the currently edited document.
        text = File.ReadAllText(fullPath);
        configuration = BacktestWorkspace.Parse(text);
        sourcePath = BacktestWorkspace.Resolve(fullPath, configuration.Strategy);
        source = File.ReadAllText(sourcePath);
        loading = true;
        try { configEditor.LoadConfiguration(configuration); configEditor.ConfigurationPath = fullPath; strategyEditor.Text = source; }
        finally { loading = false; }
        configPath = fullPath; strategyPath = sourcePath;
        configDirty = strategyDirty = false;
        ClearResults(); UpdateTitle();
        status.Text = fullPath;
        AppendLog("Opened " + fullPath);
    }

    /// <summary>Generates an isolated sample workspace and opens it for immediate offline execution.</summary>
    private void CreateExample()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a parent folder for a new self-contained Citrus example", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadConfiguration(BacktestWorkspace.CreateExample(dialog.SelectedPath));
    }

    /// <summary>Saves valid configuration JSON and the source document currently shown in the editor.</summary>
    private void SaveAll()
    {
        if (configPath is null || strategyPath is null) throw new InvalidOperationException("Open a run configuration or create an example first.");
        var configuration = configEditor.ReadConfiguration();
        if (strategyDirty) { File.WriteAllText(strategyPath, strategyEditor.Text); strategyDirty = false; }
        if (configDirty) { Citrus.Data.Json.Write(configPath, configuration); configDirty = false; }
        UpdateTitle(); AppendLog("Saved workspace documents.");
    }

    /// <summary>Offers save, discard, or cancel before replacing documents or closing.</summary>
    private bool ConfirmEdits()
    {
        if (!strategyDirty && !configDirty) return true;
        var choice = MessageBox.Show(this, "Save changes to the strategy and run configuration?", "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return false;
        if (choice == DialogResult.Yes) SaveAll();
        return true;
    }

    /// <summary>Captures saved settings and refreshes source if the configured strategy path has changed.</summary>
    private RunConfiguration Prepare()
    {
        SaveAll();
        var configuration = configEditor.ReadConfiguration();
        var path = BacktestWorkspace.Resolve(configPath!, configuration.Strategy);
        if (!string.Equals(path, strategyPath, StringComparison.OrdinalIgnoreCase))
        {
            var text = File.ReadAllText(path);
            loading = true;
            try { strategyEditor.Text = text; strategyPath = path; strategyDirty = false; }
            finally { loading = false; }
            UpdateTitle();
        }
        return configuration;
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
                using var compiled = CompiledStrategy.Load(BacktestWorkspace.Resolve(path, config.Strategy), config.References.Select(p => BacktestWorkspace.Resolve(path, p)));
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
            var completed = await Task.Run(() => BacktestWorkspace.RunAsync(path, config));
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
        var previous = grids[name].DataSource as DataTable;
        grids[name].DataSource = table; previous?.Dispose();
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
        foreach (var grid in grids.Values) { var old = grid.DataSource as DataTable; grid.DataSource = null; old?.Dispose(); }
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
        busy = value; menu.Enabled = toolbar.Enabled = !value;
        strategyEditor.ReadOnly = value;
        configPage.Enabled = !value;
        progress.Visible = value;
        if (message is not null) { status.Text = message; AppendLog(message); }
    }

    /// <summary>Marks edited documents and identifies the current run and source in their tabs.</summary>
    private void UpdateTitle()
    {
        strategyPage.Text = "Strategy" + (strategyDirty ? " *" : "");
        strategyPage.ToolTipText = strategyPath;
        configPage.Text = "Configuration" + (configDirty ? " *" : "");
        Text = $"{(configPath is null ? "Citrus" : Path.GetFileName(configPath) + " — Citrus")} — Backtesting Workbench";
    }

    /// <summary>Appends a timestamped diagnostic line and scrolls it into view.</summary>
    private void AppendLog(string message) { log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); }

    /// <summary>Redacts configured credentials from arbitrary strategy errors before presenting them.</summary>
    private void ShowError(Exception exception)
    {
        var message = exception.Message;
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key.ToString() is { } key && (key.Contains("KEY", StringComparison.OrdinalIgnoreCase) || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)) && entry.Value?.ToString() is { Length: > 3 } value)
                message = message.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        status.Text = "Operation failed — see execution log"; AppendLog(message);
        MessageBox.Show(this, message, "Citrus", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>Prevents closing mid-run and preserves unsaved source/configuration edits.</summary>
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (busy) { e.Cancel = true; status.Text = "Wait for the current operation to finish before closing."; return; }
        try { e.Cancel = !ConfirmEdits(); } catch (Exception exception) { e.Cancel = true; ShowError(exception); }
    }
}
