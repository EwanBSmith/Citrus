using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;

namespace Citrus.Desktop;

/// <summary>Provides modeless, wrapping search and undoable replacement for the WPF strategy editor.</summary>
public partial class StrategySearchWindow : Window
{
    private readonly StrategyEditor? editor;

    /// <summary>Constructs a standalone layout for the XAML designer.</summary>
    public StrategySearchWindow() => InitializeComponent();

    /// <summary>Attaches runtime search commands to a strategy editor.</summary>
    internal StrategySearchWindow(StrategyEditor editor) : this() => this.editor = editor;

    /// <summary>Reuses the query and presents the requested search or replacement mode.</summary>
    internal void ShowSearch(bool replace, string selection, Window? owner)
    {
        replacement.IsEnabled = replaceButton.IsEnabled = replaceAllButton.IsEnabled = replace;
        if (!string.IsNullOrEmpty(selection) && !selection.Contains('\n')) query.Text = selection;
        if (Owner is null) Owner = owner;
        if (!IsVisible) Show(); else Activate();
        query.Focus(); query.SelectAll();
    }

    /// <summary>Routes modeless search buttons without dismissing the window on Enter.</summary>
    private void CommandClicked(object sender, RoutedEventArgs e)
    {
        if (editor is null) return;
        switch (((FrameworkElement)sender).Tag as string)
        {
            case "Next": FindNext(false); break;
            case "Previous": FindNext(true); break;
            case "Replace": ReplaceCurrent(); break;
            case "All": ReplaceAll(); break;
            case "Close": Hide(); break;
        }
    }

    /// <summary>Hides the reusable search window when Escape is pressed.</summary>
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }

    /// <summary>Creates a culture-stable expression with a timeout so a bad pattern cannot hang the UI.</summary>
    internal static Regex CreateExpression(string query, bool regex, bool wholeWord, bool matchCase)
    {
        var pattern = regex ? query : Regex.Escape(query);
        if (wholeWord) pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Returns matches from an immutable source snapshot, reporting empty and invalid patterns inline.</summary>
    private Match[] Matches()
    {
        if (query.Text.Length == 0) { message.Text = "Enter text to find."; return []; }
        return CreateExpression(query.Text, regex.IsChecked == true, wholeWord.IsChecked == true, matchCase.IsChecked == true).Matches(editor!.SourceText).Cast<Match>().ToArray();
    }

    /// <summary>Finds the next or previous match and wraps at the document boundary.</summary>
    internal void FindNext(bool backwards)
    {
        if (editor is null) return;
        try
        {
            var matches = Matches();
            if (matches.Length == 0) { if (query.Text.Length > 0) message.Text = "No matches."; return; }
            var view = editor!.TextView;
            var index = backwards ? Array.FindLastIndex(matches, m => m.Index < view.SelectionStart)
                : Array.FindIndex(matches, m => m.Index >= (view.SelectionStart + view.SelectionLength) && !(m.Length == 0 && m.Index == view.CaretOffset));
            var wrapped = index < 0;
            if (wrapped) index = backwards ? matches.Length - 1 : 0;
            editor.Navigate(new TextSpan(matches[index].Index, matches[index].Length));
            message.Text = $"Match {index + 1} of {matches.Length}" + (wrapped ? " (wrapped)" : "");
        }
        catch (ArgumentException exception) { message.Text = "Invalid expression: " + exception.Message; }
        catch (RegexMatchTimeoutException) { message.Text = "Expression took too long; simplify the pattern."; }
    }

    /// <summary>Replaces only an exact selected match, using regex substitutions only in regular expression mode.</summary>
    private void ReplaceCurrent()
    {
        if (editor is null) return;
        if (editor.TextView.IsReadOnly) { message.Text = "Wait for the current operation to finish."; return; }
        try
        {
            var match = Matches().FirstOrDefault(m => m.Index == editor.TextView.SelectionStart && m.Length == editor.TextView.SelectionLength);
            if (match is not null) editor.ApplyChanges([new TextChange(new TextSpan(match.Index, match.Length), regex.IsChecked == true ? match.Result(replacement.Text) : replacement.Text)]);
            FindNext(false);
        }
        catch (ArgumentException exception) { message.Text = "Invalid expression or replacement: " + exception.Message; }
        catch (RegexMatchTimeoutException) { message.Text = "Expression took too long; simplify the pattern."; }
    }

    /// <summary>Replaces snapshot matches in one undo step without searching newly inserted text.</summary>
    internal void ReplaceAll()
    {
        if (editor is null) return;
        if (editor.TextView.IsReadOnly) { message.Text = "Wait for the current operation to finish."; return; }
        try
        {
            var matches = Matches();
            var changes = matches.Select(m => new TextChange(new TextSpan(m.Index, m.Length), regex.IsChecked == true ? m.Result(replacement.Text) : replacement.Text)).ToArray();
            editor.ApplyChanges(changes);
            message.Text = $"Replaced {matches.Length} matches.";
        }
        catch (ArgumentException exception) { message.Text = "Invalid expression or replacement: " + exception.Message; }
        catch (RegexMatchTimeoutException) { message.Text = "Expression took too long; simplify the pattern."; }
    }
}
