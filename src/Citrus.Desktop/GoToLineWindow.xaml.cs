namespace Citrus.Desktop;

/// <summary>Validates a one-based line number for strategy navigation.</summary>
public partial class GoToLineWindow : Window
{
    private readonly int maximum = 1;
    internal int LineNumber { get; private set; } = 1;

    /// <summary>Creates the designable line-number prompt.</summary>
    public GoToLineWindow() => InitializeComponent();

    /// <summary>Sets the valid line range and current cursor line.</summary>
    internal GoToLineWindow(int maximum, int current) : this()
    {
        this.maximum = maximum;
        number.Text = current.ToString();
        Loaded += (_, _) => { number.Focus(); number.SelectAll(); };
    }

    /// <summary>Accepts a valid line or leaves an inline explanation visible.</summary>
    private void GoClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(number.Text, out var value) || value < 1 || value > maximum)
        { error.Text = $"Enter a line from 1 to {maximum}."; return; }
        LineNumber = value;
        DialogResult = true;
    }
}
