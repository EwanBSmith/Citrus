using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Citrus.Engine;
using Citrus.Trading;

namespace Citrus.Desktop;

/// <summary>Edits explicit XAML fields while retaining untouched JSON values and numeric precision.</summary>
public partial class ConfigurationEditor : UserControl
{
    private readonly Dictionary<string, Control> inputs;
    private readonly HashSet<string> changed = [];
    private RunConfiguration original = new();
    private bool loading = true;
    private HashSet<string> authoritative = [];
    internal event EventHandler? ConfigurationChanged;
    internal string? ConfigurationPath { get; set; }

    /// <summary>Connects the designable fields to configuration values without reading user files.</summary>
    public ConfigurationEditor()
    {
        InitializeComponent();
        inputs = new()
        {
            ["StrategyType"] = StrategyType,
            ["Output"] = Output,
            ["Start"] = Start,
            ["End"] = End,
            ["InitialCash"] = InitialCash,
            ["Seed"] = Seed,
            ["RiskFreeRate"] = RiskFreeRate,
            ["Simulation.CommissionFixed"] = CommissionFixed,
            ["Simulation.CommissionPerUnit"] = CommissionPerUnit,
            ["Simulation.SpreadBps"] = SpreadBps,
            ["Simulation.SlippageBps"] = SlippageBps,
            ["Simulation.RejectionProbability"] = RejectionProbability,
            ["Simulation.AnnualBorrowRate"] = AnnualBorrowRate,
            ["Simulation.ShortsAvailable"] = ShortsAvailable,
            ["Simulation.EquityInitialMargin"] = EquityInitialMargin,
            ["Simulation.EquityMaintenanceMargin"] = EquityMaintenanceMargin,
            ["Simulation.PerpetualInitialMargin"] = PerpetualInitialMargin,
            ["Simulation.PerpetualMaintenanceMargin"] = PerpetualMaintenanceMargin,
        };
        LoadConfiguration(new());
    }

    /// <summary>Records a user edit without parsing incomplete input during typing.</summary>
    private void FieldChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var key = inputs.Single(p => ReferenceEquals(p.Value, sender)).Key;
        if (key == "StrategyType") ClearAuthority();
        if (authoritative.Contains(key)) return;
        changed.Add(key);
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gets the owner and property for a top-level or simulation setting.</summary>
    private static (object Owner, PropertyInfo Property) Property(RunConfiguration configuration, string key)
    {
        var parts = key.Split('.');
        object owner = parts.Length == 2 ? configuration.Simulation : configuration;
        return (owner, owner.GetType().GetProperty(parts[^1])!);
    }

    /// <summary>Loads all values without marking the document dirty or rounding decimals and timestamps.</summary>
    internal void LoadConfiguration(RunConfiguration configuration, IReadOnlyCollection<string>? lockedFields = null)
    {
        loading = true;
        try
        {
            original = configuration;
            ClearAuthority();
            authoritative = lockedFields?.ToHashSet() ?? [];
            foreach (var (key, input) in inputs)
            {
                input.IsEnabled = !authoritative.Contains(key);
                if (authoritative.Contains(key)) input.ToolTip = "Defined in strategy C#; edit Configure, rebuild Citrus and restart to refresh.";
                var (owner, property) = Property(configuration, key);
                var value = property.GetValue(owner);
                switch (input)
                {
                    case CheckBox check: check.IsChecked = (bool)value!; break;
                    case TextBox text:
                        text.Text = value switch
                        {
                            string[] paths => string.Join(Environment.NewLine, paths),
                            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
                            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                            _ => value?.ToString() ?? ""
                        };
                        break;
                }
            }
            changed.Clear();
        }
        finally { loading = false; }
    }

    /// <summary>Unlocks fields when the selected strategy changes; new declarations are read on validation or execution.</summary>
    private void ClearAuthority()
    {
        foreach (var key in authoritative) { inputs[key].IsEnabled = true; inputs[key].ToolTip = null; }
        authoritative.Clear();
    }

    /// <summary>Parses only edited fields and checks the same simulation constraints as the engine.</summary>
    internal RunConfiguration ReadConfiguration(bool validate = true)
    {
        var root = JsonSerializer.SerializeToNode(original)!;
        foreach (var key in changed)
        {
            if (authoritative.Contains(key)) continue;
            var parts = key.Split('.');
            var node = parts.Length == 2 ? root["Simulation"]! : root;
            var (_, property) = Property(original, key);
            object? value;
            try
            {
                value = inputs[key] switch
                {
                    CheckBox check => check.IsChecked == true,
                    ComboBox combo => combo.SelectedItem,

                    TextBox text => ParseField(text.Text, property.PropertyType),
                    _ => throw new InvalidOperationException("Unsupported setting: " + key)
                };
            }
            catch (Exception error) when (error is FormatException or OverflowException)
            {
                throw new ArgumentException("Invalid value for " + key + ". Use a decimal point for numbers or an ISO 8601 timestamp.", error);
            }
            node[parts[^1]] = JsonSerializer.SerializeToNode(value);
        }
        var result = root.Deserialize<RunConfiguration>()!;
        if (validate) StrategyConfiguration.Validate(result);
        return result;
    }

    /// <summary>Converts a field according to its declared type, preserving nullable date boundaries.</summary>
    private static object? ParseField(string text, Type type)
    {
        if (type == typeof(int)) return int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(decimal)) return decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (type == typeof(DateTimeOffset?)) return string.IsNullOrWhiteSpace(text) ? null
            : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return text;
    }

    /// <summary>Selects an output directory and stores its path relative to the strategy workspace.</summary>
    private void BrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select output folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        Output.Text = ConfigurationPath is null ? dialog.FolderName : Path.GetRelativePath(
            Path.GetDirectoryName(ConfigurationPath)!, dialog.FolderName);
    }

}
