using System.ComponentModel;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Citrus.Desktop;

/// <summary>Hosts AvalonEdit with cancellable Roslyn assistance and undoable strategy editing.</summary>
public partial class StrategyEditor : UserControl, IDisposable
{
    private readonly StrategyLanguageService language = new();
    private readonly DispatcherTimer analysisTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly EditorDecorations decorations;
    private readonly FoldingManager? folding;
    private CancellationTokenSource revisionCancellation = new();
    private CancellationTokenSource? completionCancellation;
    private CompletionWindow? completionWindow;
    private readonly ToolTip information = new() { StaysOpen = true };
    private int revision;
    private string sourcePath = "Strategy.cs";
    private string[] references = [];
    private bool documentLoaded;
    private bool editorDisposed;
    private Diagnostic[] diagnostics = [];
    private StrategySearchWindow? search;
    private SyntaxTree? typingTree;

    /// <summary>Gets the AvalonEdit control for desktop integration checks.</summary>
    internal TextEditor TextView => source;
    /// <summary>Gets the currently open completion popup for integration checks.</summary>
    internal CompletionWindow? CurrentCompletion => completionWindow;
    /// <summary>Gets the current unsaved source.</summary>
    internal string SourceText => source.Text;
    /// <summary>Gets current compiler diagnostics.</summary>
    internal IReadOnlyList<Diagnostic> Diagnostics => diagnostics;
    internal event EventHandler? SourceChanged;

    /// <summary>Initializes XAML and local editor behavior without loading strategies or accessing files.</summary>
    public StrategyEditor()
    {
        InitializeComponent();
        source.Options.IndentationSize = 4;
        source.Options.ConvertTabsToSpaces = true;
        source.Options.HighlightCurrentLine = true;
        decorations = new EditorDecorations(source);
        source.TextArea.TextView.BackgroundRenderers.Add(decorations);
        if (DesignerProperties.GetIsInDesignMode(this)) return;
        folding = FoldingManager.Install(source.TextArea);
        source.TextChanged += (_, _) => { InvalidateRevision(); SourceChanged?.Invoke(this, EventArgs.Empty); };
        source.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();
        source.TextArea.TextEntering += TextEntering;
        source.TextArea.TextEntered += TextEntered;
        source.TextArea.TextView.MouseHover += async (_, e) =>
        {
            var point = e.GetPosition(source.TextArea.TextView) + source.TextArea.TextView.ScrollOffset;
            var position = source.TextArea.TextView.GetPosition(point);
            if (position is not null) await ShowInfoAsync(source.Document.GetOffset(position.Value.Location), false);
        };
        source.TextArea.TextView.MouseHoverStopped += (_, _) => information.IsOpen = false;
        source.LostKeyboardFocus += (_, _) => information.IsOpen = false;
        Unloaded += (_, _) => { analysisTimer.Stop(); completionWindow?.Close(); information.IsOpen = false; };
        Loaded += (_, _) => { if (documentLoaded && !editorDisposed) analysisTimer.Start(); };
        analysisTimer.Tick += (_, _) => { analysisTimer.Stop(); _ = AnalyzeAsync(); };
    }

    /// <summary>Loads a document and clears its predecessor's undo history.</summary>
    internal void LoadSource(string text, string path, string[] assemblyReferences)
    {
        sourcePath = path; references = assemblyReferences; documentLoaded = true;
        source.Text = text;
        source.Document.UndoStack.ClearAll();
        source.CaretOffset = 0;
        InvalidateRevision();
        UpdateFolding();
    }

    /// <summary>Refreshes reference context without discarding unsaved source.</summary>
    internal void SetReferences(string[] assemblyReferences)
    {
        if (references.SequenceEqual(assemblyReferences, StringComparer.OrdinalIgnoreCase)) return;
        references = assemblyReferences;
        InvalidateRevision();
    }

    /// <summary>Prevents in-flight authoring requests from mutating source while a run is active.</summary>
    internal void SetReadOnly(bool value) { source.IsReadOnly = value; InvalidateRevision(); }

    /// <summary>Cancels stale responses and clears positions that no longer match the document.</summary>
    private void InvalidateRevision()
    {
        if (editorDisposed) return;
        revision++;
        revisionCancellation.Cancel(); revisionCancellation.Dispose(); revisionCancellation = new();
        completionCancellation?.Cancel();
        completionWindow?.Close(); information.IsOpen = false;
        diagnostics = []; problems.ItemsSource = null; decorations.Diagnostics = [];
        analysisTimer.Stop();
        if (documentLoaded && IsLoaded) { analysisStatus.Text = "Checking C#…"; analysisTimer.Start(); }
        UpdateCaret();
    }

    /// <summary>Applies compiler diagnostics only to the revision that requested them.</summary>
    internal async Task AnalyzeAsync()
    {
        var version = revision;
        try
        {
            var result = await language.AnalyzeAsync(source.Text, sourcePath, references, revisionCancellation.Token);
            if (!IsCurrent(version)) return;
            diagnostics = result;
            problems.ItemsSource = result.OrderBy(d => d.Severity == DiagnosticSeverity.Error ? 0 : 1)
                .ThenBy(d => d.Location.SourceSpan.Start).Select(d => new EditorProblem(d)).ToArray();
            decorations.Diagnostics = result;
            source.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
            analysisStatus.Text = $"{result.Count(d => d.Severity == DiagnosticSeverity.Error)} errors, {result.Count(d => d.Severity == DiagnosticSeverity.Warning)} warnings";
            UpdateFolding();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportLanguageError(version, error); }
    }

    /// <summary>Checks whether an asynchronous response still belongs to this editor revision.</summary>
    private bool IsCurrent(int version) => !editorDisposed && version == revision;

    /// <summary>Reports assistance failures inline without interrupting typing.</summary>
    private void ReportLanguageError(int version, Exception error)
    {
        if (IsCurrent(version)) analysisStatus.Text = "Code assistance unavailable — " + error.Message;
    }

    /// <summary>Shows semantic completion candidates using Roslyn's replacement span and the WPF popup.</summary>
    internal async Task ShowCompletionsAsync(bool automatic = false)
    {
        if (source.IsReadOnly || !documentLoaded || editorDisposed) return;
        if (!automatic) source.Focus();
        completionCancellation?.Cancel(); completionCancellation?.Dispose();
        completionCancellation = CancellationTokenSource.CreateLinkedTokenSource(revisionCancellation.Token);
        var token = completionCancellation.Token;
        var version = revision; var position = source.CaretOffset;
        try
        {
            if (automatic) await Task.Delay(120, token);
            var result = await language.CompleteAsync(source.Text, position, sourcePath, references, token);
            if (!IsCurrent(version) || token.IsCancellationRequested || source.CaretOffset != position
                || !source.IsKeyboardFocusWithin || result is null || result.List.ItemsList.Count == 0) return;
            completionWindow?.Close();
            var popup = new CompletionWindow(source.TextArea) { StartOffset = result.List.Span.Start, EndOffset = position };
            completionWindow = popup;
            foreach (var item in result.List.ItemsList.GroupBy(i => i.DisplayTextPrefix + i.DisplayText + i.DisplayTextSuffix).Select(g => g.First()))
                popup.CompletionList.CompletionData.Add(new RoslynCompletion(item, () => _ = CommitCompletionAsync(result, item, version)));
            popup.Closed += (_, _) => { if (ReferenceEquals(completionWindow, popup)) completionWindow = null; };
            popup.Show();
            popup.CompletionList.SelectItem(source.Document.GetText(result.List.Span.Start, position - result.List.Span.Start));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportLanguageError(version, error); }
    }

    /// <summary>Commits the exact Roslyn change as a single undo step, rejecting stale selections.</summary>
    private async Task CommitCompletionAsync(StrategyCompletions result, CompletionItem item, int version)
    {
        if (!IsCurrent(version) || source.IsReadOnly) return;
        var position = source.CaretOffset;
        try
        {
            var change = await language.CompletionChangeAsync(result, item, revisionCancellation.Token);
            if (!IsCurrent(version) || source.IsReadOnly || source.CaretOffset != position) return;
            ApplyChanges([change.TextChange]);
            source.CaretOffset = change.NewPosition ?? change.TextChange.Span.Start + (change.TextChange.NewText?.Length ?? 0);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportLanguageError(version, error); }
    }

    /// <summary>Displays a diagnostic, symbol description, or callable signature.</summary>
    private async Task ShowInfoAsync(int position, bool signature)
    {
        if (!documentLoaded || position > source.Document.TextLength || editorDisposed) return;
        var version = revision; var token = revisionCancellation.Token;
        try
        {
            var error = diagnostics.FirstOrDefault(d => d.Location.IsInSource && d.Location.SourceSpan.Contains(position));
            var message = !signature && error is not null ? error.ToString() : signature
                ? await language.SignatureAsync(source.Text, position, sourcePath, references, token)
                : await language.QuickInfoAsync(source.Text, position, sourcePath, references, token);
            if (IsCurrent(version) && source.IsKeyboardFocusWithin && message is not null)
            {
                information.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 };
                information.PlacementTarget = source;
                information.IsOpen = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportLanguageError(version, error); }
    }

    /// <summary>Formats unsaved source as one undoable operation.</summary>
    internal async Task FormatAsync()
    {
        if (source.IsReadOnly || !documentLoaded) return;
        var version = revision;
        try
        {
            var changes = await language.FormatAsync(source.Text, sourcePath, references, revisionCancellation.Token);
            if (IsCurrent(version) && !source.IsReadOnly) ApplyChanges(changes);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportLanguageError(version, error); }
    }

    /// <summary>Applies edits from the end of the document, retaining UTF-16 spans and caret affinity.</summary>
    internal void ApplyChanges(IEnumerable<TextChange> changes)
    {
        if (source.IsReadOnly) return;
        var caret = source.CaretOffset;
        using (source.Document.RunUpdate())
        {
            foreach (var change in changes.OrderByDescending(c => c.Span.Start))
            {
                var replacement = change.NewText ?? "";
                source.Document.Replace(change.Span.Start, change.Span.Length, replacement);
                if (caret >= change.Span.End) caret += replacement.Length - change.Span.Length;
                else if (caret > change.Span.Start) caret = change.Span.Start + replacement.Length;
            }
        }
        source.CaretOffset = Math.Clamp(caret, 0, source.Document.TextLength);
    }

    /// <summary>Toggles the selected nonblank lines while preserving line endings and a single undo step.</summary>
    internal void ToggleComments()
    {
        if (source.IsReadOnly) return;
        var first = source.Document.GetLineByOffset(source.SelectionStart).LineNumber;
        var last = source.Document.GetLineByOffset(Math.Max(source.SelectionStart, source.SelectionStart + source.SelectionLength - 1)).LineNumber;
        var lines = Enumerable.Range(first, last - first + 1).Select(source.Document.GetLineByNumber)
            .Select(l => (Line: l, Text: source.Document.GetText(l))).Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToArray();
        var uncomment = lines.Length > 0 && lines.All(l => l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal));
        ApplyChanges(lines.Select(l => new TextChange(new TextSpan(l.Line.Offset + l.Text.Length - l.Text.TrimStart().Length, uncomment ? 2 : 0), uncomment ? "" : "//")));
        var start = source.Document.GetLineByNumber(first).Offset;
        var end = source.Document.GetLineByNumber(last).EndOffset;
        source.Select(start, end - start);
    }

    /// <summary>Skips an already paired closer without duplicating it.</summary>
    private void TextEntering(object? sender, TextCompositionEventArgs e)
    {
        if (!source.IsReadOnly && e.Text.Length == 1 && ")]}".Contains(e.Text[0]) && source.SelectionLength == 0
            && source.CaretOffset < source.Document.TextLength && source.Document.GetCharAt(source.CaretOffset) == e.Text[0] && IsCode(source.CaretOffset))
        {
            e.Handled = true;
            source.CaretOffset++;
        }
    }

    /// <summary>Pairs code delimiters and requests semantic assistance after typing.</summary>
    private void TextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (source.IsReadOnly || e.Text.Length != 1) return;
        var character = e.Text[0]; var position = source.CaretOffset;
        if ("([{".Contains(character) && IsCode(position - 1))
        {
            var next = position < source.Document.TextLength ? source.Document.GetCharAt(position) : '\0';
            if (next == '\0' || char.IsWhiteSpace(next) || ")]};,".Contains(next))
            {
                source.Document.UndoStack.StartContinuedUndoGroup(null);
                try { source.Document.Insert(position, character == '(' ? ")" : character == '[' ? "]" : "}"); }
                finally { source.Document.UndoStack.EndUndoGroup(); }
                source.CaretOffset = position;
            }
        }
        else if (character == '}' && IsCode(position - 1))
        {
            var line = source.Document.GetLineByOffset(position);
            var text = source.Document.GetText(line);
            var opening = SyntaxRoot().FindToken(position - 1).Parent?.ChildTokens()
                .FirstOrDefault(t => t.IsKind(SyntaxKind.OpenBraceToken));
            if (text.Trim() == "}" && opening is { RawKind: not 0, IsMissing: false } token)
            {
                var openingText = source.Document.GetText(source.Document.GetLineByOffset(token.SpanStart));
                var indent = new string(openingText.TakeWhile(char.IsWhiteSpace).ToArray());
                var prefixLength = text.Length - text.TrimStart().Length;
                source.Document.UndoStack.StartContinuedUndoGroup(null);
                try { source.Document.Replace(line.Offset, prefixLength, indent); }
                finally { source.Document.UndoStack.EndUndoGroup(); }
                source.CaretOffset = position + indent.Length - prefixLength;
            }
        }
        if (character is '(' or ',') _ = ShowInfoAsync(source.CaretOffset, true);
        else if ((character == '.' || char.IsLetter(character) || character == '_') && IsCode(source.CaretOffset - 1))
            _ = ShowCompletionsAsync(true);
    }

    /// <summary>Inserts the document's newline convention and indents a newly opened code block.</summary>
    internal void InsertNewLine()
    {
        if (source.IsReadOnly) return;
        var line = source.Document.GetLineByOffset(source.SelectionStart);
        var prefix = source.Document.GetText(line.Offset, source.SelectionStart - line.Offset);
        var indentation = new string(prefix.TakeWhile(char.IsWhiteSpace).ToArray());
        var opens = prefix.TrimEnd().EndsWith('{') && IsCode(source.SelectionStart - 1);
        var newline = source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = newline + indentation + (opens ? "    " : "");
        var caret = source.SelectionStart + insertion.Length;
        var after = source.SelectionStart + source.SelectionLength;
        if (opens && after < source.Document.TextLength && source.Document.GetCharAt(after) == '}')
            insertion += newline + indentation;
        ApplyChanges([new TextChange(new TextSpan(source.SelectionStart, source.SelectionLength), insertion)]);
        source.CaretOffset = caret;
    }

    /// <summary>Parses incrementally to distinguish code from strings, comments, and disabled text.</summary>
    private SyntaxNode SyntaxRoot()
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(source.Text);
        typingTree = typingTree is null ? CSharpSyntaxTree.ParseText(text) : typingTree.WithChangedText(text);
        return typingTree.GetRoot();
    }

    /// <summary>Checks whether a source offset is eligible for delimiter pairing.</summary>
    private bool IsCode(int position)
    {
        if (source.Document.TextLength == 0) return true;
        var root = SyntaxRoot();
        position = Math.Clamp(position, 0, source.Document.TextLength - 1);
        var trivia = root.FindTrivia(position);
        if (trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
            or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia or SyntaxKind.DisabledTextTrivia) return false;
        return !root.FindToken(position).Parent!.AncestorsAndSelf().Any(p => p is InterpolatedStringTextSyntax or InterpolatedStringExpressionSyntax
            || p is LiteralExpressionSyntax literal && literal.Kind() is SyntaxKind.StringLiteralExpression or SyntaxKind.CharacterLiteralExpression or SyntaxKind.Utf8StringLiteralExpression);
    }

    /// <summary>Derives fold ranges from paired syntax tokens, excluding braces in comments and strings.</summary>
    private void UpdateFolding()
    {
        if (folding is null || editorDisposed) return;
        var stack = new Stack<int>();
        var ranges = new List<NewFolding>();
        foreach (var token in SyntaxRoot().DescendantTokens())
        {
            if (token.IsMissing) continue;
            if (token.IsKind(SyntaxKind.OpenBraceToken)) stack.Push(token.SpanStart);
            else if (token.IsKind(SyntaxKind.CloseBraceToken) && stack.TryPop(out var start)
                && source.Document.GetLineByOffset(start) != source.Document.GetLineByOffset(token.Span.End))
                ranges.Add(new NewFolding(start, token.Span.End) { Name = "{ … }" });
        }
        folding.UpdateFoldings(ranges.OrderBy(f => f.StartOffset), -1);
    }

    /// <summary>Updates cursor coordinates and matching syntax-brace highlights.</summary>
    private void UpdateCaret()
    {
        if (editorDisposed) return;
        var location = source.Document.GetLocation(source.CaretOffset);
        caretStatus.Text = $"Ln {location.Line}, Col {location.Column} | C# | Spaces: 4";
        decorations.Braces = [];
        if (source.CaretOffset > 0 && "()[]{}".Contains(source.Document.GetCharAt(source.CaretOffset - 1)))
        {
            var token = SyntaxRoot().FindToken(source.CaretOffset - 1);
            var siblings = token.Parent?.ChildTokens().Where(t => !t.IsMissing).ToArray() ?? [];
            var match = token.Kind() switch
            {
                SyntaxKind.OpenBraceToken => SyntaxKind.CloseBraceToken, SyntaxKind.CloseBraceToken => SyntaxKind.OpenBraceToken,
                SyntaxKind.OpenParenToken => SyntaxKind.CloseParenToken, SyntaxKind.CloseParenToken => SyntaxKind.OpenParenToken,
                SyntaxKind.OpenBracketToken => SyntaxKind.CloseBracketToken, SyntaxKind.CloseBracketToken => SyntaxKind.OpenBracketToken,
                _ => SyntaxKind.None
            };
            var other = siblings.FirstOrDefault(t => t.IsKind(match));
            if (other.RawKind != 0) decorations.Braces = [token.SpanStart, other.SpanStart];
        }
        source.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
    }

    /// <summary>Unfolds and reveals a compiler span using the same UTF-16 offsets as Roslyn.</summary>
    internal void Navigate(TextSpan span)
    {
        var start = Math.Clamp(span.Start, 0, source.Document.TextLength);
        var end = Math.Clamp(span.End, start, source.Document.TextLength);
        if (folding is not null)
            foreach (var range in folding.AllFoldings.Where(f => f.StartOffset <= end && f.EndOffset >= start)) range.IsFolded = false;
        source.Select(start, end - start);
        var location = source.Document.GetLocation(start);
        source.ScrollTo(location.Line, location.Column);
        source.Focus();
    }

    /// <summary>Navigates from a double-clicked diagnostic row to its source span.</summary>
    private void ProblemActivated(object sender, MouseButtonEventArgs e) => NavigateProblem();

    /// <summary>Supports keyboard activation of a diagnostic.</summary>
    private void ProblemKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { NavigateProblem(); e.Handled = true; } }

    /// <summary>Reveals the currently selected compiler diagnostic.</summary>
    private void NavigateProblem()
    {
        if (problems.SelectedItem is EditorProblem { Diagnostic.Location.IsInSource: true } problem)
            Navigate(problem.Diagnostic.Location.SourceSpan);
    }

    /// <summary>Finds and selects the local declaration for the symbol at the caret.</summary>
    private async Task GoToDefinitionAsync()
    {
        var version = revision;
        try
        {
            var span = await language.DefinitionAsync(source.Text, source.CaretOffset, sourcePath, references, revisionCancellation.Token);
            if (!IsCurrent(version)) return;
            if (span is not null) Navigate(span.Value); else analysisStatus.Text = "No definition in this file; hover for API information.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportLanguageError(version, error); }
    }

    /// <summary>Opens a reusable search window for the current source document.</summary>
    private void ShowSearch(bool replace)
    {
        if (search is null)
        {
            search = new StrategySearchWindow(this);
            search.Closed += (_, _) => search = null;
        }
        search.ShowSearch(replace, source.SelectedText, Window.GetWindow(this));
    }

    /// <summary>Prompts for a one-based line and selects it in the editor.</summary>
    private void GoToLine()
    {
        var dialog = new GoToLineWindow(source.Document.LineCount, source.TextArea.Caret.Line) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) Navigate(new TextSpan(source.Document.GetLineByNumber(dialog.LineNumber).Offset, 0));
    }

    /// <summary>Shows or hides the problems pane while preserving source editing space.</summary>
    private void ToggleProblems(object sender, RoutedEventArgs e)
    {
        var show = problemToggle.IsChecked == true;
        problemsRow.Height = new GridLength(show ? 130 : 0);
        splitRow.Height = new GridLength(show ? 5 : 0);
        problems.Visibility = problemSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Runs a toolbar or edit-menu action.</summary>
    private void CommandClicked(object sender, RoutedEventArgs e)
    {
        switch (((FrameworkElement)sender).Tag as string)
        {
            case "Undo": if (!source.IsReadOnly) source.Undo(); break;
            case "Redo": if (!source.IsReadOnly) source.Redo(); break;
            case "Cut": if (!source.IsReadOnly) source.Cut(); break;
            case "Copy": source.Copy(); break;
            case "Paste": if (!source.IsReadOnly) source.Paste(); break;
            case "SelectAll": source.SelectAll(); break;
            case "Comments": ToggleComments(); break;
            case "Complete": _ = ShowCompletionsAsync(); break;
            case "Signature": _ = ShowInfoAsync(source.CaretOffset, true); break;
            case "Definition": _ = GoToDefinitionAsync(); break;
            case "Collapse": if (folding is not null) foreach (var f in folding.AllFoldings) f.IsFolded = true; break;
            case "Expand": if (folding is not null) foreach (var f in folding.AllFoldings) f.IsFolded = false; break;
            case "Find": ShowSearch(false); break;
            case "Replace": ShowSearch(true); break;
            case "Line": GoToLine(); break;
            case "Format": _ = FormatAsync(); break;
        }
    }

    /// <summary>Handles editor shortcuts and preserves workbench save, validate, and run bindings.</summary>
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Enter && modifiers == ModifierKeys.None && source.IsKeyboardFocusWithin && completionWindow is null)
        { InsertNewLine(); e.Handled = true; return; }
        Action? action = (e.Key, modifiers) switch
        {
            (Key.F, ModifierKeys.Control) => () => ShowSearch(false),
            (Key.H, ModifierKeys.Control) => () => ShowSearch(true),
            (Key.G, ModifierKeys.Control) => GoToLine,
            (Key.F, ModifierKeys.Control | ModifierKeys.Shift) => () => _ = FormatAsync(),
            (Key.OemQuestion, ModifierKeys.Control) => ToggleComments,
            (Key.Space, ModifierKeys.Control) => () => _ = ShowCompletionsAsync(),
            (Key.Space, ModifierKeys.Control | ModifierKeys.Shift) => () => _ = ShowInfoAsync(source.CaretOffset, true),
            (Key.F12, ModifierKeys.None) => () => _ = GoToDefinitionAsync(),
            (Key.F3, ModifierKeys.None) => () => { if (search is null) ShowSearch(false); else search.FindNext(false); },
            (Key.F3, ModifierKeys.Shift) => () => { if (search is null) ShowSearch(false); else search.FindNext(true); },
            (Key.Escape, ModifierKeys.None) => () => { completionCancellation?.Cancel(); completionWindow?.Close(); information.IsOpen = false; },
            _ => null
        };
        if (action is not null) { action(); e.Handled = true; }
    }

    /// <summary>Stops asynchronous analysis and releases editor resources when its owner closes.</summary>
    public void Dispose()
    {
        if (editorDisposed) return;
        editorDisposed = true;
        analysisTimer.Stop(); revisionCancellation.Cancel(); revisionCancellation.Dispose();
        completionCancellation?.Cancel(); completionCancellation?.Dispose();
        completionWindow?.Close(); information.IsOpen = false; search?.Close();
        if (folding is not null) FoldingManager.Uninstall(folding);
        language.Dispose();
    }
}

/// <summary>Presents a compiler diagnostic as bindable WPF table columns.</summary>
internal sealed record EditorProblem(Diagnostic Diagnostic)
{
    public string Severity => Diagnostic.Severity.ToString();
    public string Code => Diagnostic.Id;
    public string Position => Diagnostic.Location.IsInSource
        ? $"{Diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1} : {Diagnostic.Location.GetLineSpan().StartLinePosition.Character + 1}" : "";
    public string Message => Diagnostic.GetMessage();
}

/// <summary>Adapts a Roslyn completion candidate to AvalonEdit's insertion callback.</summary>
internal sealed class RoslynCompletion(CompletionItem item, Action commit) : ICompletionData
{
    public ImageSource? Image => null;
    public string Text => item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix;
    public object Content => Text;
    public object Description => item.InlineDescription;
    public double Priority => 0;

    /// <summary>Delegates insertion to the revision-checked Roslyn change handler.</summary>
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => commit();
}
