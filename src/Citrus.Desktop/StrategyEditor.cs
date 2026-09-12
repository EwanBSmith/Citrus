using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ScintillaNET;

namespace Citrus.Desktop;

/// <summary>Hosts a native C# editor, authoring commands, and navigable live compiler diagnostics.</summary>
internal sealed class StrategyEditor : UserControl
{
    private const int ErrorIndicator = 8, WarningIndicator = 9;
    private readonly Scintilla source = new() { Dock = DockStyle.Fill, AccessibleName = "C# strategy source" };
    private readonly StrategyLanguageService language = new();
    private readonly System.Windows.Forms.Timer analysisTimer = new() { Interval = 450 };
    private readonly ToolStrip tools = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    private readonly ToolStripStatusLabel caretStatus = new();
    private readonly ToolStripStatusLabel analysisStatus = new("Open a strategy to begin") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ListView problems = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        AccessibleName = "Strategy diagnostics", MultiSelect = false, ShowItemToolTips = true
    };
    private readonly SplitContainer split = new()
    {
        Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2,
        Size = new Size(800, 500), SplitterDistance = 370, Panel1MinSize = 90, Panel2MinSize = 65
    };
    private CancellationTokenSource revisionCancellation = new();
    private CancellationTokenSource? completionCancellation;
    private StrategyCompletions? completions;
    private Dictionary<string, CompletionItem> completionItems = [];
    private int revision;
    private int completionRevision;
    private string sourcePath = "Strategy.cs";
    private string[] references = [];
    private bool documentLoaded;
    private bool editorDisposed;
    private Diagnostic[] diagnostics = [];
    private StrategySearchForm? search;
    private SyntaxTree? typingTree;

    /// <summary>Gets the native editor for desktop integration checks.</summary>
    internal Scintilla TextView => source;
    /// <summary>Gets the current unsaved strategy source.</summary>
    internal string SourceText => source.Text;
    /// <summary>Gets the latest diagnostics for the current source revision.</summary>
    internal IReadOnlyList<Diagnostic> Diagnostics => diagnostics;
    /// <summary>Signals source mutations only; styling and diagnostics do not dirty the document.</summary>
    internal event EventHandler? SourceChanged;

    /// <summary>Builds the editor, command menu, status strip, and problem list.</summary>
    internal StrategyEditor()
    {
        Dock = DockStyle.Fill;
        ConfigureTextView();
        var edit = new ToolStripDropDownButton("Edit");
        AddCommand(edit, "Undo", "Ctrl+Z", source.Undo);
        AddCommand(edit, "Redo", "Ctrl+Y", source.Redo);
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddCommand(edit, "Cut", "Ctrl+X", source.Cut);
        AddCommand(edit, "Copy", "Ctrl+C", source.Copy);
        AddCommand(edit, "Paste", "Ctrl+V", source.Paste);
        AddCommand(edit, "Select all", "Ctrl+A", source.SelectAll);
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddCommand(edit, "Toggle line comments", "Ctrl+/", ToggleComments);
        AddCommand(edit, "Complete code", "Ctrl+Space", () => _ = ShowCompletionsAsync());
        AddCommand(edit, "Parameter info", "Ctrl+Shift+Space", () => _ = ShowInfoAsync(source.CurrentPosition, true));
        AddCommand(edit, "Go to definition in file", "F12", () => _ = GoToDefinitionAsync());
        AddCommand(edit, "Collapse all", "", () => source.FoldAll(FoldAction.Contract));
        AddCommand(edit, "Expand all", "", () => source.FoldAll(FoldAction.Expand));
        tools.Items.Add(edit);
        AddButton("Find", "Find (Ctrl+F)", () => ShowSearch(false));
        AddButton("Replace", "Replace (Ctrl+H)", () => ShowSearch(true));
        AddButton("Go to line", "Go to line (Ctrl+G)", GoToLine);
        AddButton("Format", "Format document (Ctrl+Shift+F)", () => _ = FormatAsync());
        var problemToggle = new ToolStripButton("Problems") { CheckOnClick = true, Checked = true };
        problemToggle.CheckedChanged += (_, _) => split.Panel2Collapsed = !problemToggle.Checked;
        tools.Items.Add(problemToggle);
        var status = new StatusStrip { RenderMode = ToolStripRenderMode.System, SizingGrip = false };
        status.Items.AddRange([analysisStatus, caretStatus]);
        problems.Columns.Add("Severity", 65);
        problems.Columns.Add("Code", 75);
        problems.Columns.Add("Line : Col", 75);
        problems.Columns.Add("Description", 500);
        problems.Resize += (_, _) => problems.Columns[3].Width = Math.Max(150, problems.ClientSize.Width - 220);
        problems.ItemActivate += (_, _) => NavigateProblem();
        split.Panel1.Controls.Add(source);
        split.Panel2.Controls.Add(problems);
        Controls.Add(split); Controls.Add(tools); Controls.Add(status);
        source.TextChanged += (_, _) =>
        {
            InvalidateRevision();
            SourceChanged?.Invoke(this, EventArgs.Empty);
        };
        source.UpdateUI += (_, _) => UpdateCaret();
        source.CharAdded += (_, e) => CharacterAdded(e.Char);
        source.KeyPress += (_, e) =>
        {
            if (!source.ReadOnly && e.KeyChar is ')' or ']' or '}' && source.SelectionStart == source.SelectionEnd
                && source.GetCharAt(source.CurrentPosition) == e.KeyChar && IsCode(source.CurrentPosition))
            {
                e.Handled = true; source.GotoPosition(source.CurrentPosition + 1);
            }
        };
        source.AutoCSelection += (_, e) =>
        {
            var result = completions;
            var version = completionRevision;
            completionItems.TryGetValue(e.Text, out var item);
            source.AutoCCancel();
            if (result is not null && item is not null) _ = CommitCompletionAsync(result, item, version);
        };
        source.DwellStart += (_, e) => { if (e.Position >= 0) _ = ShowInfoAsync(e.Position, false); };
        source.DwellEnd += (_, _) => source.CallTipCancel();
        source.Leave += (_, _) =>
        {
            if (editorDisposed) return;
            completionCancellation?.Cancel(); source.AutoCCancel(); source.CallTipCancel();
        };
        analysisTimer.Tick += (_, _) => { analysisTimer.Stop(); _ = AnalyzeAsync(); };
    }

    /// <summary>Configures C# syntax, folding, indentation, completion popups, and diagnostic indicators.</summary>
    private void ConfigureTextView()
    {
        source.Styles[Style.Default].Font = "Consolas";
        source.Styles[Style.Default].Size = 11;
        source.Styles[Style.Default].ForeColor = Color.FromArgb(35, 40, 48);
        source.Styles[Style.Default].BackColor = Color.White;
        source.StyleClearAll();
        source.LexerName = "cpp";
        source.SetKeywords(0, string.Join(' ', SyntaxFacts.GetKeywordKinds().Concat(SyntaxFacts.GetContextualKeywordKinds()).Select(SyntaxFacts.GetText)));
        source.SetKeywords(1, string.Join(' ', typeof(Citrus.Trading.IStrategy).Assembly.GetExportedTypes().Select(t => t.Name)));
        foreach (var style in new[] { Style.Cpp.Word, Style.Cpp.Preprocessor }) source.Styles[style].ForeColor = Color.FromArgb(0, 65, 185);
        source.Styles[Style.Cpp.Word2].ForeColor = Color.FromArgb(0, 120, 120);
        foreach (var style in new[] { Style.Cpp.Comment, Style.Cpp.CommentLine, Style.Cpp.CommentDoc, Style.Cpp.CommentLineDoc })
            source.Styles[style].ForeColor = Color.FromArgb(0, 115, 55);
        foreach (var style in new[] { Style.Cpp.String, Style.Cpp.Character, Style.Cpp.Verbatim, Style.Cpp.StringEol })
            source.Styles[style].ForeColor = Color.FromArgb(165, 50, 30);
        source.Styles[Style.Cpp.Number].ForeColor = Color.FromArgb(115, 45, 155);
        source.Styles[Style.LineNumber].ForeColor = Color.DimGray;
        source.Styles[Style.LineNumber].BackColor = Color.FromArgb(245, 246, 248);
        source.Styles[Style.BraceLight].BackColor = Color.FromArgb(210, 230, 255);
        source.Styles[Style.BraceBad].ForeColor = Color.Red;
        source.Styles[Style.IndentGuide].ForeColor = Color.LightGray;
        source.TabWidth = source.IndentWidth = 4;
        source.UseTabs = false; source.TabIndents = true; source.BackspaceUnindents = true;
        source.IndentationGuides = IndentView.LookBoth;
        source.WrapMode = WrapMode.None;
        source.CaretLineBackColor = Color.FromArgb(244, 248, 253);
        source.Margins[0].Type = MarginType.Number;
        source.Margins[0].Width = 48;
        source.SetProperty("fold", "1");
        source.SetProperty("fold.comment", "1");
        source.SetProperty("lexer.cpp.track.preprocessor", "0");
        source.Margins[1].Type = MarginType.Symbol;
        source.Margins[1].Mask = Marker.MaskFolders;
        source.Margins[1].Sensitive = true;
        source.Margins[1].Width = 18;
        foreach (var (marker, symbol) in new[]
        {
            (Marker.Folder, MarkerSymbol.BoxPlus), (Marker.FolderOpen, MarkerSymbol.BoxMinus),
            (Marker.FolderEnd, MarkerSymbol.BoxPlusConnected), (Marker.FolderOpenMid, MarkerSymbol.BoxMinusConnected),
            (Marker.FolderMidTail, MarkerSymbol.TCorner), (Marker.FolderTail, MarkerSymbol.LCorner), (Marker.FolderSub, MarkerSymbol.VLine)
        })
        {
            source.Markers[marker].Symbol = symbol;
            source.Markers[marker].SetForeColor(Color.White);
            source.Markers[marker].SetBackColor(Color.Gray);
        }
        source.AutomaticFold = AutomaticFold.Show | AutomaticFold.Click | AutomaticFold.Change;
        source.AutoCIgnoreCase = true; source.AutoCSeparator = '\n'; source.AutoCMaxHeight = 12; source.AutoCMaxWidth = 70;
        source.AutoCOrder = Order.PerformSort;
        source.MouseDwellTime = 650;
        foreach (var (index, color) in new[] { (ErrorIndicator, Color.Firebrick), (WarningIndicator, Color.DarkGoldenrod) })
        {
            source.Indicators[index].Style = IndicatorStyle.Squiggle;
            source.Indicators[index].ForeColor = color;
        }
    }

    /// <summary>Adds a discoverable editor command with its keyboard shortcut.</summary>
    private static void AddCommand(ToolStripDropDownButton menu, string label, string shortcut, Action action)
    {
        var item = new ToolStripMenuItem(label) { ShortcutKeyDisplayString = shortcut };
        item.Click += (_, _) => action(); menu.DropDownItems.Add(item);
    }

    /// <summary>Adds a compact authoring toolbar button.</summary>
    private void AddButton(string label, string hint, Action action)
    {
        var item = new ToolStripButton(label) { ToolTipText = hint };
        item.Click += (_, _) => action(); tools.Items.Add(item);
    }

    /// <summary>Loads a different document and resets its undo history while preserving its newline convention.</summary>
    internal void LoadSource(string text, string path, string[] assemblyReferences)
    {
        sourcePath = path; references = assemblyReferences; documentLoaded = true;
        source.Text = text;
        source.EolMode = text.Contains("\r\n", StringComparison.Ordinal) ? Eol.CrLf : Eol.Lf;
        source.EmptyUndoBuffer(); source.SetSavePoint(); source.GotoPosition(0);
        InvalidateRevision();
    }

    /// <summary>Refreshes the authoring references without replacing unsaved source or its undo history.</summary>
    internal void SetReferences(string[] assemblyReferences)
    {
        if (references.SequenceEqual(assemblyReferences, StringComparer.OrdinalIgnoreCase)) return;
        references = assemblyReferences; InvalidateRevision();
    }

    /// <summary>Prevents pending asynchronous edits from changing the source during validation or a backtest.</summary>
    internal void SetReadOnly(bool value)
    {
        source.ReadOnly = value;
        InvalidateRevision();
    }

    /// <summary>Cancels stale requests and removes obsolete diagnostic positions immediately after an edit.</summary>
    private void InvalidateRevision()
    {
        if (editorDisposed) return;
        revision++;
        revisionCancellation.Cancel(); revisionCancellation.Dispose(); revisionCancellation = new();
        completionCancellation?.Cancel();
        source.AutoCCancel(); source.CallTipCancel();
        diagnostics = []; problems.Items.Clear();
        foreach (var indicator in new[] { ErrorIndicator, WarningIndicator })
        {
            source.IndicatorCurrent = indicator; source.IndicatorClearRange(0, source.TextLength);
        }
        analysisTimer.Stop();
        if (documentLoaded) { analysisStatus.Text = "Checking C#..."; analysisTimer.Start(); }
        UpdateCaret();
    }

    /// <summary>Applies diagnostics only if both the document and its reference context are still current.</summary>
    internal async Task AnalyzeAsync()
    {
        var version = revision;
        var token = revisionCancellation.Token;
        try
        {
            var result = await language.AnalyzeAsync(source.Text, sourcePath, references, token);
            if (!IsCurrent(version)) return;
            diagnostics = result;
            problems.BeginUpdate();
            try
            {
                problems.Items.Clear();
                foreach (var diagnostic in result.OrderBy(d => d.Severity == DiagnosticSeverity.Error ? 0 : 1).ThenBy(d => d.Location.SourceSpan.Start))
                {
                    var position = diagnostic.Location.GetLineSpan().StartLinePosition;
                    var item = new ListViewItem([diagnostic.Severity.ToString(), diagnostic.Id,
                        diagnostic.Location.IsInSource ? $"{position.Line + 1} : {position.Character + 1}" : "", diagnostic.GetMessage()])
                        { Tag = diagnostic, ToolTipText = diagnostic.GetMessage() };
                    problems.Items.Add(item);
                    if (!diagnostic.Location.IsInSource) continue;
                    var span = diagnostic.Location.SourceSpan;
                    source.IndicatorCurrent = diagnostic.Severity == DiagnosticSeverity.Error ? ErrorIndicator : WarningIndicator;
                    var start = Math.Min(span.Start, Math.Max(0, source.TextLength - 1));
                    source.IndicatorFillRange(start, Math.Min(Math.Max(1, span.Length), source.TextLength - start));
                }
            }
            finally { problems.EndUpdate(); }
            analysisStatus.Text = $"{result.Count(d => d.Severity == DiagnosticSeverity.Error)} errors, {result.Count(d => d.Severity == DiagnosticSeverity.Warning)} warnings";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportLanguageError(version, exception); }
    }

    /// <summary>Checks whether an asynchronous response belongs to the displayed document.</summary>
    private bool IsCurrent(int version) => !editorDisposed && !IsDisposed && !Disposing && version == revision;

    /// <summary>Surfaces authoring failures without interrupting typing or allowing an async event to crash the form.</summary>
    private void ReportLanguageError(int version, Exception exception)
    {
        if (!IsCurrent(version)) return;
        analysisStatus.Text = "Code assistance unavailable — " + exception.Message;
    }

    /// <summary>Requests context-aware completions and keeps the original Roslyn replacement spans.</summary>
    internal async Task ShowCompletionsAsync(bool automatic = false)
    {
        if (source.ReadOnly || !documentLoaded) return;
        if (!automatic) source.Focus();
        completionCancellation?.Cancel(); completionCancellation?.Dispose();
        completionCancellation = CancellationTokenSource.CreateLinkedTokenSource(revisionCancellation.Token);
        var token = completionCancellation.Token;
        var version = revision; var position = source.CurrentPosition;
        try
        {
            if (automatic) await Task.Delay(120, token);
            var result = await language.CompleteAsync(source.Text, position, sourcePath, references, token);
            if (!IsCurrent(version) || token.IsCancellationRequested || source.CurrentPosition != position || !source.Focused || result is null) return;
            completions = result; completionRevision = version;
            completionItems = result.List.ItemsList.GroupBy(i => i.DisplayTextPrefix + i.DisplayText + i.DisplayTextSuffix)
                .ToDictionary(g => g.Key, g => g.First());
            if (completionItems.Count > 0)
                source.AutoCShow(position - result.List.Span.Start, string.Join('\n', completionItems.Keys));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportLanguageError(version, exception); }
    }

    /// <summary>Commits the exact completion change as one undoable operation, rejecting stale results.</summary>
    private async Task CommitCompletionAsync(StrategyCompletions result, CompletionItem item, int version)
    {
        if (!IsCurrent(version) || source.ReadOnly) return;
        var token = revisionCancellation.Token;
        var position = source.CurrentPosition;
        try
        {
            var change = await language.CompletionChangeAsync(result, item, token);
            if (!IsCurrent(version) || source.ReadOnly || source.CurrentPosition != position) return;
            ApplyChanges([change.TextChange]);
            source.GotoPosition(change.NewPosition ?? change.TextChange.Span.Start + (change.TextChange.NewText?.Length ?? 0));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportLanguageError(version, exception); }
    }

    /// <summary>Shows diagnostics at a squiggle or semantic symbol information and callable signatures.</summary>
    private async Task ShowInfoAsync(int position, bool signature)
    {
        if (!documentLoaded || position > source.TextLength) return;
        var version = revision; var token = revisionCancellation.Token;
        try
        {
            var error = diagnostics.FirstOrDefault(d => d.Location.IsInSource && d.Location.SourceSpan.Contains(position));
            var message = !signature && error is not null ? error.ToString() : signature
                ? await language.SignatureAsync(source.Text, position, sourcePath, references, token)
                : await language.QuickInfoAsync(source.Text, position, sourcePath, references, token);
            if (IsCurrent(version) && source.Focused && message is not null) source.CallTipShow(position, message);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportLanguageError(version, exception); }
    }

    /// <summary>Formats the current unsaved source while retaining a single undo step.</summary>
    internal async Task FormatAsync()
    {
        if (source.ReadOnly || !documentLoaded) return;
        var version = revision; var token = revisionCancellation.Token;
        try
        {
            var changes = await language.FormatAsync(source.Text, sourcePath, references, token);
            if (IsCurrent(version) && !source.ReadOnly) ApplyChanges(changes);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportLanguageError(version, exception); }
    }

    /// <summary>Applies edits from the end of the document to preserve earlier spans and caret affinity.</summary>
    internal void ApplyChanges(IEnumerable<TextChange> changes)
    {
        if (source.ReadOnly) return;
        var caret = source.CurrentPosition;
        source.BeginUndoAction();
        try
        {
            foreach (var change in changes.OrderByDescending(c => c.Span.Start))
            {
                var replacement = change.NewText ?? "";
                source.TargetStart = change.Span.Start; source.TargetEnd = change.Span.End;
                source.ReplaceTarget(replacement);
                if (caret >= change.Span.End) caret += replacement.Length - change.Span.Length;
                else if (caret > change.Span.Start) caret = change.Span.Start + replacement.Length;
            }
            source.GotoPosition(caret);
        }
        finally { source.EndUndoAction(); }
    }

    /// <summary>Toggles comments for the current line or all selected nonblank lines without disturbing line endings.</summary>
    internal void ToggleComments()
    {
        if (source.ReadOnly) return;
        var first = source.LineFromPosition(source.SelectionStart);
        var last = source.LineFromPosition(Math.Max(source.SelectionStart, source.SelectionEnd - 1));
        var lines = Enumerable.Range(first, last - first + 1).Select(i => source.Lines[i]).Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToArray();
        var uncomment = lines.Length > 0 && lines.All(l => l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal));
        ApplyChanges(lines.Select(l => new TextChange(new TextSpan(l.IndentPosition, uncomment ? 2 : 0), uncomment ? "" : "//")));
        source.SetSelection(source.Lines[last].EndPosition, source.Lines[first].Position);
    }

    /// <summary>Maintains indentation, closes delimiters, and triggers completion and parameter information while typing.</summary>
    private void CharacterAdded(int character)
    {
        if (source.ReadOnly) return;
        var position = source.CurrentPosition;
        var line = source.LineFromPosition(position);
        if (character == '\n' && line > 0)
        {
            var previous = source.Lines[line - 1];
            var indent = previous.Indentation;
            var opensBlock = previous.Text.TrimEnd().EndsWith('{') && IsCode(previous.EndPosition - 1);
            source.BeginUndoAction();
            try
            {
                source.Lines[line].Indentation = indent + (opensBlock ? 4 : 0);
                source.GotoPosition(source.Lines[line].IndentPosition);
                if (opensBlock && source.GetCharAt(source.CurrentPosition) == '}')
                {
                    var newline = source.EolMode == Eol.CrLf ? "\r\n" : "\n";
                    source.InsertText(source.CurrentPosition, newline + new string(' ', indent));
                }
            }
            finally { source.EndUndoAction(); }
        }
        else if (character is '(' or '[' or '{' && IsCode(position - 1))
        {
            var next = source.GetCharAt(position);
            if (next == 0 || char.IsWhiteSpace((char)next) || next is ')' or ']' or '}' or ';' or ',')
                source.InsertText(position, character == '(' ? ")" : character == '[' ? "]" : "}");
        }
        else if (character == '}' && line > 0 && source.Lines[line].Text.Trim() == "}" && IsCode(position - 1))
        {
            var match = source.BraceMatch(position - 1);
            if (match >= 0) source.Lines[line].Indentation = source.Lines[source.LineFromPosition(match)].Indentation;
        }
        if (character is '(' or ',') _ = ShowInfoAsync(source.CurrentPosition, true);
        else if ((character == '.' || char.IsLetter((char)character) || character == '_') && IsCode(source.CurrentPosition - 1))
            _ = ShowCompletionsAsync(true);
    }

    /// <summary>Uses incremental C# parsing because native lexer styling can lag behind a character notification.</summary>
    private bool IsCode(int position)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(source.Text);
        typingTree = typingTree is null ? CSharpSyntaxTree.ParseText(text) : typingTree.WithChangedText(text);
        var root = typingTree.GetRoot();
        position = Math.Clamp(position, 0, Math.Max(0, text.Length - 1));
        var trivia = root.FindTrivia(position);
        if (trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
            or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia or SyntaxKind.DisabledTextTrivia) return false;
        var parent = root.FindToken(position).Parent;
        return parent is not InterpolatedStringTextSyntax and not InterpolatedStringExpressionSyntax
            && (parent is not LiteralExpressionSyntax literal || literal.Kind() is not
                (SyntaxKind.StringLiteralExpression or SyntaxKind.CharacterLiteralExpression or SyntaxKind.Utf8StringLiteralExpression));
    }

    /// <summary>Updates cursor coordinates, the number margin, and matching brace highlights.</summary>
    private void UpdateCaret()
    {
        if (editorDisposed) return;
        var position = source.CurrentPosition;
        var line = source.LineFromPosition(position);
        caretStatus.Text = $"Ln {line + 1}, Col {source.GetColumn(position) + 1}  |  C#  |  Spaces: 4";
        source.Margins[0].Width = source.TextWidth(Style.LineNumber, new string('9', Math.Max(3, source.Lines.Count.ToString().Length))) + 14;
        var brace = position > 0 && "()[]{}".Contains((char)source.GetCharAt(position - 1)) ? position - 1 : -1;
        if (brace < 0) { source.BraceHighlight(-1, -1); return; }
        var match = source.BraceMatch(brace);
        if (match < 0) source.BraceBadLight(brace); else source.BraceHighlight(brace, match);
    }

    /// <summary>Reveals a source span even if its surrounding block was folded.</summary>
    internal void Navigate(TextSpan span)
    {
        var start = Math.Clamp(span.Start, 0, source.TextLength);
        var end = Math.Clamp(span.End, start, source.TextLength);
        for (var line = source.LineFromPosition(start); line <= source.LineFromPosition(end); line++) source.Lines[line].EnsureVisible();
        source.SetSelection(end, start); source.ScrollCaret(); source.Focus(); UpdateCaret();
    }

    /// <summary>Navigates from an activated problem row to its current compiler location.</summary>
    private void NavigateProblem()
    {
        if (problems.SelectedItems.Count == 1 && problems.SelectedItems[0].Tag is Diagnostic { Location.IsInSource: true } diagnostic)
            Navigate(diagnostic.Location.SourceSpan);
    }

    /// <summary>Finds a local declaration and selects it in the strategy source.</summary>
    private async Task GoToDefinitionAsync()
    {
        var version = revision; var token = revisionCancellation.Token;
        try
        {
            var span = await language.DefinitionAsync(source.Text, source.CurrentPosition, sourcePath, references, token);
            if (!IsCurrent(version)) return;
            if (span is not null) Navigate(span.Value); else analysisStatus.Text = "No definition in this file; hover for API information.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportLanguageError(version, exception); }
    }

    /// <summary>Opens or reuses the modeless source search window.</summary>
    private void ShowSearch(bool replace)
    {
        if (search is null || search.IsDisposed) search = new StrategySearchForm(this);
        search.ShowSearch(replace, source.SelectedText, FindForm());
    }

    /// <summary>Prompts for a one-based source line and reveals it.</summary>
    private void GoToLine()
    {
        using var dialog = new Form { Text = "Go to line", ClientSize = new Size(290, 95), FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var number = new NumericUpDown { Minimum = 1, Maximum = source.Lines.Count, Value = source.CurrentLine + 1, Location = new Point(90, 14), Width = 180, AccessibleName = "Line number" };
        dialog.Controls.Add(new Label { Text = "Line number", AutoSize = true, Location = new Point(12, 17) }); dialog.Controls.Add(number);
        var go = new Button { Text = "Go", DialogResult = DialogResult.OK, Location = new Point(114, 55) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(195, 55) };
        dialog.Controls.Add(go); dialog.Controls.Add(cancel); dialog.AcceptButton = go; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK) Navigate(new TextSpan(source.Lines[(int)number.Value - 1].Position, 0));
    }

    /// <summary>Handles editor shortcuts while allowing the workbench's save, validate, and run shortcuts through.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.F: ShowSearch(false); return true;
            case Keys.Control | Keys.H: ShowSearch(true); return true;
            case Keys.Control | Keys.G: GoToLine(); return true;
            case Keys.Control | Keys.Shift | Keys.F: _ = FormatAsync(); return true;
            case Keys.Control | Keys.OemQuestion: ToggleComments(); return true;
            case Keys.Control | Keys.Space: _ = ShowCompletionsAsync(); return true;
            case Keys.Control | Keys.Shift | Keys.Space: _ = ShowInfoAsync(source.CurrentPosition, true); return true;
            case Keys.F12: _ = GoToDefinitionAsync(); return true;
            case Keys.F3: if (search is null || search.IsDisposed) ShowSearch(false); else search.FindNext(false); return true;
            case Keys.Shift | Keys.F3: if (search is null || search.IsDisposed) ShowSearch(false); else search.FindNext(true); return true;
            case Keys.Escape: completionCancellation?.Cancel(); source.AutoCCancel(); source.CallTipCancel(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Stops timers and pending authoring work before native editor handles are released.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !editorDisposed)
        {
            editorDisposed = true;
            analysisTimer.Dispose(); revisionCancellation.Cancel(); revisionCancellation.Dispose();
            completionCancellation?.Cancel(); completionCancellation?.Dispose(); search?.Dispose(); language.Dispose();
        }
        base.Dispose(disposing);
    }
}
