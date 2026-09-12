using System.Runtime.InteropServices;
using Citrus.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ScintillaNET;

namespace Citrus.Desktop;

/// <summary>Exercises real Roslyn providers and native editor operations in the offline Windows smoke run.</summary>
internal static class StrategyEditorSmokeTest
{
    private const string ValidSource = "using System;\nusing Citrus.Trading;\npublic sealed class TestStrategy : InstrumentStrategy\n{\n    public TestStrategy() : base(new Instrument(\"test\", AssetClass.LinearPerpetual, \"BTC\"), \"test\", 1m) { throw new Exception(\"Must not execute during editing\"); }\n    protected override void OnBar(InstrumentContext market, Bar bar)\n    {\n        market.BuyNotional(100m);\n    }\n}\n";

    /// <summary>Delivers test characters only to a control owned by this smoke-test process.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);

    /// <summary>Checks semantic assistance, reference changes, editing, stale responses, and rendered editor layouts.</summary>
    internal static void Run(string directory)
    {
        var path = Path.Combine(Path.GetFullPath(directory), "EditorFixture.cs");
        using var language = new StrategyLanguageService();
        var diagnostics = language.AnalyzeAsync(ValidSource, path, [], CancellationToken.None).GetAwaiter().GetResult();
        Require(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Valid strategy analysis failed: " + string.Join("; ", diagnostics.Select(d => d.ToString())));
        var incomplete = ValidSource.Replace("BuyNotional(100m)", "Bu");
        var position = incomplete.IndexOf("market.Bu", StringComparison.Ordinal) + "market.Bu".Length;
        var completions = language.CompleteAsync(incomplete, position, path, [], CancellationToken.None).GetAwaiter().GetResult()!;
        var item = completions.List.ItemsList.Single(i => i.DisplayText == "BuyNotional");
        Require(completions.List.ItemsList.Any(i => i.DisplayText == "ExitLong"), "Citrus members missing from completion.");
        var change = language.CompletionChangeAsync(completions, item, CancellationToken.None).GetAwaiter().GetResult();
        Require(Microsoft.CodeAnalysis.Text.SourceText.From(incomplete).WithChanges(change.TextChange).ToString().Contains("market.BuyNotional;", StringComparison.Ordinal), "Completion did not replace the typed prefix.");
        var extended = "using Citrus.Trading; class T { void M(IStrategyContext c) { c.Bu; } }";
        var extensions = language.CompleteAsync(extended, extended.IndexOf("c.Bu", StringComparison.Ordinal) + 4, path, [], CancellationToken.None).GetAwaiter().GetResult()!;
        Require(extensions.List.ItemsList.Any(i => i.DisplayText == "BuyLimit"), "Imported strategy extension methods missing from completion.");
        var quickInfo = language.QuickInfoAsync(ValidSource, ValidSource.IndexOf("BuyNotional", StringComparison.Ordinal), path, [], CancellationToken.None).GetAwaiter().GetResult();
        Require(quickInfo?.Contains("BuyNotional", StringComparison.Ordinal) == true, "Semantic quick information is unavailable.");
        var signature = language.SignatureAsync(ValidSource, ValidSource.IndexOf("100m", StringComparison.Ordinal), path, [], CancellationToken.None).GetAwaiter().GetResult();
        Require(signature?.Contains("BuyNotional", StringComparison.Ordinal) == true && signature.Contains("Argument 1", StringComparison.Ordinal), "Parameter information is unavailable.");
        var broken = ValidSource.Replace("market.BuyNotional(100m);", "// café 🍋\n        market.DoesNotExist();");
        var errors = language.AnalyzeAsync(broken, path, [], CancellationToken.None).GetAwaiter().GetResult();
        var missing = errors.Single(d => d.Id == "CS1061");
        Require(broken.Substring(missing.Location.SourceSpan.Start, missing.Location.SourceSpan.Length) == "DoesNotExist", "Unicode diagnostic spans are incorrect.");
        var compilerErrors = StrategyCompilation.Create(broken, path).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id).Order().ToArray();
        Require(errors.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id).Order().SequenceEqual(compilerErrors), "Editor/compiler diagnostics differ.");
        VerifyReferences(language, directory, path);

        using var form = new Form { Size = new Size(1000, 700), ShowInTaskbar = false, Opacity = 0 };
        using var editor = new StrategyEditor();
        form.Controls.Add(editor); form.Show(); Application.DoEvents();
        editor.LoadSource(ValidSource, path, []);
        var changed = 0;
        editor.SourceChanged += (_, _) => changed++;
        Wait(editor.AnalyzeAsync());
        Require(changed == 0 && editor.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error), "Diagnostics dirtied valid source or failed.");
        editor.TextView.Colorize(0, -1);
        Require(editor.TextView.GetStyleAt(0) == ScintillaNET.Style.Cpp.Word, "C# syntax highlighting was not applied.");
        editor.LoadSource(broken, path, []);
        Wait(editor.AnalyzeAsync());
        Require(editor.Diagnostics.Any(d => d.Id == "CS1061"), "Live diagnostics missing from the editor.");
        editor.Navigate(missing.Location.SourceSpan);
        Require(editor.TextView.SelectedText == "DoesNotExist", "Native selection lost Unicode diagnostic offsets.");
        Capture(form, Path.Combine(directory, "strategy-editor-diagnostics.png"));
        var stale = editor.AnalyzeAsync();
        editor.LoadSource(ValidSource, path, []);
        Wait(stale); Wait(editor.AnalyzeAsync());
        Require(!editor.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Stale diagnostics replaced the current document.");

        editor.LoadSource("class T{void M(){int x=1;}}\r\n", path, []);
        var unformatted = editor.SourceText;
        Wait(editor.FormatAsync());
        Require(editor.SourceText.Contains("int x = 1;", StringComparison.Ordinal) && editor.SourceText.Contains("\r\n", StringComparison.Ordinal), "Formatting or CRLF preservation failed.");
        editor.TextView.Undo(); Require(editor.SourceText == unformatted, "Formatting was not one undo step.");
        editor.LoadSource("    alpha();\r\n    beta();\r\n", path, []);
        var uncommented = editor.SourceText;
        editor.TextView.SetSelection(editor.TextView.TextLength, 0); editor.ToggleComments();
        Require(editor.SourceText == "    //alpha();\r\n    //beta();\r\n", "Selected line commenting failed.");
        editor.ToggleComments(); Require(editor.SourceText == uncommented, "Comment toggle did not restore the selection.");
        editor.TextView.Undo(); Require(editor.SourceText.Contains("//alpha", StringComparison.Ordinal), "Comment toggling broke undo.");

        editor.LoadSource("class T ", path, []); editor.TextView.GotoPosition(editor.TextView.TextLength);
        Type(editor.TextView, "{"); Require(editor.SourceText == "class T {}", "Opening brace did not close automatically.");
        Type(editor.TextView, "\r"); Require(editor.SourceText == "class T {\n    \n}", "Block indentation failed: " + editor.SourceText.Replace("\n", "\\n"));
        editor.LoadSource("", path, []); Type(editor.TextView, "()");
        Require(editor.SourceText == "()", "Typing a closing delimiter duplicated the automatic delimiter.");
        editor.LoadSource("// ", path, []); editor.TextView.GotoPosition(3); Type(editor.TextView, "(");
        Require(editor.SourceText == "// (", "Bracket completion altered a comment: " + editor.SourceText + " style=" + editor.TextView.GetStyleAt(3));

        editor.LoadSource(incomplete, path, []); editor.TextView.GotoPosition(position); editor.TextView.Focus();
        Wait(editor.ShowCompletionsAsync());
        Require(editor.TextView.AutoCActive, "Native completion popup did not open. Focus=" + editor.TextView.Focused + "; " + string.Join("; ", Descendants(editor).OfType<StatusStrip>().SelectMany(s => s.Items.Cast<ToolStripItem>()).Select(i => i.Text)));
        editor.TextView.AutoCSelect("BuyNotional"); editor.TextView.ExecuteCmd(Command.Tab);
        WaitUntil(() => editor.SourceText.Contains("market.BuyNotional;", StringComparison.Ordinal), "Native completion did not commit.");
        editor.TextView.Undo(); Require(editor.SourceText == incomplete, "Completion commit broke undo.");
        editor.LoadSource(ValidSource, path, []);
        Require(!editor.TextView.CanUndo, "Opening a document retained the previous document's undo history.");
        var pendingFormat = editor.FormatAsync(); editor.SetReadOnly(true); Wait(pendingFormat);
        editor.ToggleComments(); editor.ApplyChanges([new TextChange(new TextSpan(0, 0), "bad")]);
        Require(editor.SourceText == ValidSource, "An editing command modified read-only source.");
        editor.SetReadOnly(false);
        VerifySearch(editor);
        editor.LoadSource(ValidSource, path, []); Wait(editor.AnalyzeAsync());
        form.Size = new Size(670, 430); Capture(form, Path.Combine(directory, "strategy-editor-compact.png"));
        File.WriteAllText(Path.Combine(directory, "strategy-editor-tests.txt"), "PASS: semantic completion and commits, extension methods, quick info, signatures, reference changes, compiler parity, Unicode diagnostics and navigation, stale diagnostics, syntax styling, formatting and CRLF, comments, indentation, paired delimiters, undo, read-only edits, regex search/replacement, native layout.");
    }

    /// <summary>Verifies that explicit reference DLLs participate in analysis and disappear when removed.</summary>
    private static void VerifyReferences(StrategyLanguageService language, string directory, string path)
    {
        var assemblyPath = Path.Combine(Path.GetFullPath(directory), "EditorReference.dll");
        using (var output = File.Create(assemblyPath))
            Require(StrategyCompilation.Create("public static class CustomIndicator { public static decimal Value => 42m; }", path, assemblyName: "EditorReference").Emit(output).Success, "Reference fixture compilation failed.");
        const string usesReference = "class T { decimal M() => CustomIndicator.Value; }";
        Require(language.AnalyzeAsync(usesReference, path, [assemblyPath], CancellationToken.None).GetAwaiter().GetResult().All(d => d.Severity != DiagnosticSeverity.Error), "Explicit reference was not resolved.");
        Require(language.AnalyzeAsync(usesReference, path, [], CancellationToken.None).GetAwaiter().GetResult().Any(d => d.Id == "CS0103"), "Removed reference remained in the authoring context.");
        try
        {
            language.AnalyzeAsync(usesReference, path, [assemblyPath + ".missing"], CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Missing reference unexpectedly succeeded.");
        }
        catch (FileNotFoundException) { }
        Require(language.AnalyzeAsync(ValidSource, path, [], CancellationToken.None).GetAwaiter().GetResult().All(d => d.Severity != DiagnosticSeverity.Error), "Language service failed to recover after a missing reference.");
    }

    /// <summary>Tests case, word, Unicode, regex replacement, and one-step undo through the actual search dialog.</summary>
    private static void VerifySearch(StrategyEditor editor)
    {
        Require(StrategySearchForm.CreateExpression("buy", false, true, false).Matches("buy BUY buyer prébuy").Count == 2, "Whole-word/case search failed.");
        editor.LoadSource("Buy(10); Buy(20);", "Strategy.cs", []);
        using var dialog = new StrategySearchForm(editor);
        dialog.ShowSearch(true, "Buy\\((\\d+)\\)", editor.FindForm());
        var controls = Descendants(dialog).ToArray();
        controls.OfType<CheckBox>().Single(c => c.Text == "Regular expression").Checked = true;
        controls.OfType<TextBox>().Single(c => c.AccessibleName == "Replace in strategy").Text = "Sell($1)";
        controls.OfType<Button>().Single(b => b.Text == "Replace all").PerformClick();
        Require(editor.SourceText == "Sell(10); Sell(20);", "Regex replacement failed.");
        editor.TextView.Undo(); Require(editor.SourceText == "Buy(10); Buy(20);", "Replace all was not one undo step.");
        controls.OfType<TextBox>().Single(c => c.AccessibleName == "Find in strategy").Text = "(";
        dialog.FindNext(false);
        Require(controls.OfType<Label>().Single(c => c.AccessibleName == "Search result").Text.StartsWith("Invalid expression", StringComparison.Ordinal), "Invalid regex was not surfaced inline.");
    }

    /// <summary>Enumerates descendants for testing accessible search fields and buttons.</summary>
    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>Types into the native control without directing input to any other application.</summary>
    private static void Type(Scintilla view, string text)
    {
        view.Focus();
        foreach (var character in text)
        {
            if (character == '\r') view.ExecuteCmd(Command.NewLine);
            else SendMessage(view.Handle, 0x0102, character, 0);
        }
    }

    /// <summary>Runs the UI message pump until an awaited authoring operation completes.</summary>
    private static void Wait(Task task)
    {
        WaitUntil(() => task.IsCompleted, "Editor operation timed out."); task.GetAwaiter().GetResult();
    }

    /// <summary>Waits for a native callback or continuation with a bounded smoke-test timeout.</summary>
    private static void WaitUntil(Func<bool> complete, string message)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!complete())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(message);
            Application.DoEvents(); Thread.Sleep(10);
        }
    }

    /// <summary>Captures the editor without showing an interactive test window.</summary>
    private static void Capture(Form form, string path)
    {
        Application.DoEvents(); form.PerformLayout();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path);
    }

    /// <summary>Fails the smoke run when an authoring behavior differs from the user-visible contract.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
