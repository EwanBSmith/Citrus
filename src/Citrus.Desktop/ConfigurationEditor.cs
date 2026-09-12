using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Citrus.Engine;
using Citrus.Simulation;
using Citrus.Trading;

namespace Citrus.Desktop;

/// <summary>Edits run settings with labelled native controls while preserving untouched serialized values.</summary>
internal sealed class ConfigurationEditor : UserControl
{
    private readonly Dictionary<string, Control> inputs = [];
    private readonly HashSet<string> changed = [];
    private RunConfiguration original = new();
    private bool loading;
    internal event EventHandler? ConfigurationChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? ConfigurationPath { get; set; }

    /// <summary>Builds grouped, scrollable settings using the configuration property types.</summary>
    internal ConfigurationEditor()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(16), BackColor = SystemColors.Window };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        AddGroup(layout, "Files and output", typeof(RunConfiguration), "Output", "References");
        AddGroup(layout, "Historical data", typeof(RunConfiguration), "Start", "End", "Interval");
        AddGroup(layout, "Capital and reproducibility", typeof(RunConfiguration), "InitialCash", "Seed", "RiskFreeRate");
        AddGroup(layout, "Execution costs and borrowing", typeof(SimulationOptions), "CommissionFixed", "CommissionPerUnit", "SpreadBps", "SlippageBps", "RejectionProbability", "AnnualBorrowRate", "ShortsAvailable");
        AddGroup(layout, "Margin requirements", typeof(SimulationOptions), "EquityInitialMargin", "EquityMaintenanceMargin", "PerpetualInitialMargin", "PerpetualMaintenanceMargin");
        LoadConfiguration(new());
    }

    /// <summary>Creates a titled section and its accessible input rows.</summary>
    private void AddGroup(TableLayoutPanel parent, string title, Type type, params string[] names)
    {
        var group = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 14) };
        var rows = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.Controls.Add(rows);
        parent.Controls.Add(group);
        foreach (var name in names)
        {
            var property = type.GetProperty(name)!;
            var key = type == typeof(SimulationOptions) ? "Simulation." + name : name;
            var input = CreateInput(property, key);
            inputs.Add(key, input);
            input.AccessibleName = Label(name);
            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(3, 5, 3, 5);
            var row = rows.RowCount++;
            rows.Controls.Add(new Label { Text = Label(name), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 8) }, 0, row);
            if (name is "Strategy" or "Data" or "Output" or "Cache")
            {
                var pathRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
                pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
                pathRow.Controls.Add(input, 0, 0);
                var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill, AccessibleName = "Browse " + Label(name) };
                browse.Click += (_, _) => Browse((TextBox)input, name);
                pathRow.Controls.Add(browse, 1, 0);
                rows.Controls.Add(pathRow, 1, row);
            }
            else rows.Controls.Add(input, 1, row);
        }
    }

    /// <summary>Maps each supported setting to a native input and tracks user edits.</summary>
    private Control CreateInput(PropertyInfo property, string key)
    {
        void Edited(object? sender, EventArgs e)
        {
            if (loading) return;
            changed.Add(key);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        var type = property.PropertyType;
        if (type == typeof(BarInterval))
        {
            var interval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(BarInterval.Name) };
            interval.Items.AddRange([BarInterval.Hourly, BarInterval.Daily]);
            interval.SelectedIndexChanged += Edited;
            return interval;
        }
        if (type == typeof(bool))
        {
            var check = new CheckBox { Text = "Enabled", AutoSize = true };
            check.CheckedChanged += Edited;
            return check;
        }
        if (type.IsEnum)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DataSource = Enum.GetValues(type) };
            combo.SelectedValueChanged += Edited;
            return combo;
        }
        if (type == typeof(DateTimeOffset?))
        {
            var date = new DateTimePicker { ShowCheckBox = true, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss 'UTC'" };
            date.ValueChanged += Edited;
            return date;
        }
        if (type == typeof(int) || type == typeof(decimal) || type == typeof(double))
        {
            var number = new NumericUpDown { Minimum = key == "Seed" ? int.MinValue : key == "RiskFreeRate" ? decimal.MinValue : 0, Maximum = type == typeof(int) ? int.MaxValue : decimal.MaxValue, DecimalPlaces = type == typeof(int) ? 0 : key == "InitialCash" ? 2 : 4, ThousandsSeparator = true, Increment = type == typeof(int) ? 1 : 0.01m };
            number.ValueChanged += Edited;
            return number;
        }
        var text = new TextBox { Multiline = type == typeof(string[]), Height = type == typeof(string[]) ? 65 : 25, ScrollBars = type == typeof(string[]) ? ScrollBars.Vertical : ScrollBars.None };
        text.TextChanged += Edited;
        return text;
    }

    /// <summary>Populates controls without marking the document dirty or changing original precision.</summary>
    internal void LoadConfiguration(RunConfiguration configuration)
    {
        loading = true;
        try
        {
            original = configuration;
            foreach (var (key, input) in inputs)
            {
                var parts = key.Split('.');
                object owner = parts.Length == 2 ? configuration.Simulation : configuration;
                var value = owner.GetType().GetProperty(parts[^1])!.GetValue(owner);
                switch (input)
                {
                    case NumericUpDown number:
                        var amount = Convert.ToDecimal(value);
                        number.DecimalPlaces = Math.Max(number.DecimalPlaces, (decimal.GetBits(amount)[3] >> 16) & 0xff);
                        number.Minimum = Math.Min(number.Minimum, amount);
                        number.Value = amount;
                        break;
                    case CheckBox check: check.Checked = (bool)value!; break;
                    case ComboBox combo:
                        if (value is BarInterval && !combo.Items.Contains(value)) combo.Items.Add(value);
                        combo.SelectedItem = value;
                        break;
                    case DateTimePicker date:
                        if (value is DateTimeOffset timestamp) date.Value = timestamp.UtcDateTime;
                        date.Checked = value is not null;
                        break;
                    case TextBox text: text.Text = value is string[] paths ? string.Join(Environment.NewLine, paths) : (string?)value ?? ""; break;
                }
            }
            changed.Clear();
        }
        finally { loading = false; }
    }

    /// <summary>Applies edited fields to the original document and validates simulation relationships.</summary>
    internal RunConfiguration ReadConfiguration()
    {
        var root = JsonSerializer.SerializeToNode(original)!;
        foreach (var key in changed)
        {
            var parts = key.Split('.');
            var node = parts.Length == 2 ? root["Simulation"]! : root;
            object? value = inputs[key] switch
            {
                NumericUpDown number => number.Value,
                CheckBox check => check.Checked,
                ComboBox combo => combo.SelectedItem,
                DateTimePicker date => date.Checked ? new DateTimeOffset(DateTime.SpecifyKind(date.Value, DateTimeKind.Utc)) : null,
                TextBox when key == "References" => ReadReferences(),
                TextBox text => text.Text,
                _ => throw new InvalidOperationException("Unsupported setting: " + key)
            };
            node[parts[^1]] = JsonSerializer.SerializeToNode(value);
        }
        var result = root.Deserialize<RunConfiguration>()!;
        result.Simulation.Validate();
        if (result.InitialCash <= 0) throw new ArgumentException("Initial cash must be positive.");
        return result;
    }

    /// <summary>Reads assembly paths independently of unfinished simulation settings for live code assistance.</summary>
    internal string[] ReadReferences() => ((TextBox)inputs["References"]).Lines
        .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToArray();

    /// <summary>Selects a file or directory and stores a path relative to the run file.</summary>
    private void Browse(TextBox input, string name)
    {
        string selected;
        if (name is "Output" or "Cache")
        {
            using var dialog = new FolderBrowserDialog { Description = "Select " + Label(name), UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            selected = dialog.SelectedPath;
        }
        else
        {
            using var dialog = new OpenFileDialog { Filter = name == "Strategy" ? "C# source (*.cs)|*.cs" : "Market dataset (*.json)|*.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            selected = dialog.FileName;
        }
        input.Text = ConfigurationPath is null ? selected : Path.GetRelativePath(StrategyFolder.Root(ConfigurationPath) ?? Path.GetDirectoryName(ConfigurationPath)!, selected);
    }

    /// <summary>Supplies readable labels with explicit units for rates, prices, and optional fields.</summary>
    private static string Label(string name) => name switch
    {
        "References" => "Assembly references (one per line)",
        "Start" => "Start (check to enable)", "End" => "End (exclusive; optional)",
        "SpreadBps" => "Spread (basis points)", "SlippageBps" => "Slippage (basis points)",
        "RiskFreeRate" => "Risk-free rate (fraction)", "AnnualBorrowRate" => "Annual borrow rate (fraction)",
        "RejectionProbability" => "Rejection probability (0–1)",
        _ => System.Text.RegularExpressions.Regex.Replace(name, "(?<!^)([A-Z])", " $1")
    };
}
