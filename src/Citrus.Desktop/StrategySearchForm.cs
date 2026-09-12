using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;

namespace Citrus.Desktop;

/// <summary>Provides modeless, wrapping source search and undoable replacement with bounded regular expressions.</summary>
internal sealed class StrategySearchForm : Form
{
    private readonly StrategyEditor editor;
    private readonly TextBox query = new() { Dock = DockStyle.Fill, AccessibleName = "Find in strategy" };
    private readonly TextBox replacement = new() { Dock = DockStyle.Fill, AccessibleName = "Replace in strategy" };
    private readonly CheckBox matchCase = new() { Text = "Match case", AutoSize = true };
    private readonly CheckBox wholeWord = new() { Text = "Whole word", AutoSize = true };
    private readonly CheckBox regex = new() { Text = "Regular expression", AutoSize = true };
    private readonly Label message = new() { Dock = DockStyle.Fill, AutoSize = true, AccessibleName = "Search result" };
    private readonly Button replaceButton;
    private readonly Button replaceAllButton;

    /// <summary>Creates keyboard-accessible search fields and replacement commands.</summary>
    internal StrategySearchForm(StrategyEditor editor)
    {
        this.editor = editor;
        Text = "Find and replace"; Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(640, 210); MinimumSize = new Size(570, 240);
        FormBorderStyle = FormBorderStyle.SizableToolWindow; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Find", AutoSize = true, Margin = new Padding(3, 6, 12, 8) }, 0, 0); layout.Controls.Add(query, 1, 0);
        layout.Controls.Add(new Label { Text = "Replace with", AutoSize = true, Margin = new Padding(3, 6, 12, 8) }, 0, 1); layout.Controls.Add(replacement, 1, 1);
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        options.Controls.AddRange([matchCase, wholeWord, regex]); layout.Controls.Add(options, 1, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var next = Button("Find next", () => FindNext(false));
        buttons.Controls.Add(next); buttons.Controls.Add(Button("Previous", () => FindNext(true)));
        replaceButton = Button("Replace", ReplaceCurrent); replaceAllButton = Button("Replace all", ReplaceAll);
        buttons.Controls.Add(replaceButton); buttons.Controls.Add(replaceAllButton);
        var close = Button("Close", Hide); buttons.Controls.Add(close); CancelButton = close; AcceptButton = next;
        layout.Controls.Add(buttons, 0, 3); layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(message, 0, 4); layout.SetColumnSpan(message, 2);
        Controls.Add(layout);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    /// <summary>Creates a search action button that sizes to its text.</summary>
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => action(); return button;
    }

    /// <summary>Reuses the query when no single-line selection is available and selects the requested editing mode.</summary>
    internal void ShowSearch(bool replace, string selection, Form? owner)
    {
        replacement.Enabled = replace;
        replaceButton.Enabled = replaceAllButton.Enabled = replace;
        if (!string.IsNullOrEmpty(selection) && !selection.Contains('\n')) query.Text = selection;
        if (!Visible) Show(owner); else Activate();
        query.Focus(); query.SelectAll();
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
        return CreateExpression(query.Text, regex.Checked, wholeWord.Checked, matchCase.Checked).Matches(editor.SourceText).Cast<Match>().ToArray();
    }

    /// <summary>Finds the next or previous match and wraps at the document boundary.</summary>
    internal void FindNext(bool backwards)
    {
        try
        {
            var matches = Matches();
            if (matches.Length == 0) { if (query.Text.Length > 0) message.Text = "No matches."; return; }
            var view = editor.TextView;
            var index = backwards ? Array.FindLastIndex(matches, m => m.Index < view.SelectionStart)
                : Array.FindIndex(matches, m => m.Index >= view.SelectionEnd && !(m.Length == 0 && m.Index == view.CurrentPosition));
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
        if (editor.TextView.ReadOnly) { message.Text = "Wait for the current operation to finish."; return; }
        try
        {
            var match = Matches().FirstOrDefault(m => m.Index == editor.TextView.SelectionStart && m.Length == editor.TextView.SelectionEnd - editor.TextView.SelectionStart);
            if (match is not null) editor.ApplyChanges([new TextChange(new TextSpan(match.Index, match.Length), regex.Checked ? match.Result(replacement.Text) : replacement.Text)]);
            FindNext(false);
        }
        catch (ArgumentException exception) { message.Text = "Invalid expression or replacement: " + exception.Message; }
        catch (RegexMatchTimeoutException) { message.Text = "Expression took too long; simplify the pattern."; }
    }

    /// <summary>Replaces snapshot matches in one undo step without searching newly inserted text.</summary>
    private void ReplaceAll()
    {
        if (editor.TextView.ReadOnly) { message.Text = "Wait for the current operation to finish."; return; }
        try
        {
            var matches = Matches();
            var changes = matches.Select(m => new TextChange(new TextSpan(m.Index, m.Length), regex.Checked ? m.Result(replacement.Text) : replacement.Text)).ToArray();
            editor.ApplyChanges(changes);
            message.Text = $"Replaced {matches.Length} matches.";
        }
        catch (ArgumentException exception) { message.Text = "Invalid expression or replacement: " + exception.Message; }
        catch (RegexMatchTimeoutException) { message.Text = "Expression took too long; simplify the pattern."; }
    }
}
