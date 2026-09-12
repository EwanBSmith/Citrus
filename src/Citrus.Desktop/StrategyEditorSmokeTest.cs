using System.Windows.Threading;
using Citrus.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using static Citrus.Desktop.DesktopSmokeTest;

namespace Citrus.Desktop;

/// <summary>Exercises real Roslyn providers and AvalonEdit operations in the offline WPF smoke run.</summary>
internal static class StrategyEditorSmokeTest
{
    private const string ValidSource = "using System;\nusing Citrus.Trading;\npublic sealed class TestStrategy : InstrumentStrategy\n{\n    public TestStrategy() : base(new Instrument(\"test\", AssetClass.LinearPerpetual, \"BTC\"), \"test\", 1m) { throw new Exception(\"Must not execute during editing\"); }\n    protected override void OnBar(InstrumentContext market, Bar bar)\n    {\n        market.BuyNotional(100m);\n    }\n}\n";


    /// <summary>Checks semantic assistance, actual WPF editor operations, revision cancellation, and undo.</summary>
    internal static async Task RunAsync(string directory)
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


        using var editor = new StrategyEditor();
        var window = new Window { Width = 1000, Height = 700, Content = editor };
        ShowHidden(window); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        editor.LoadSource(ValidSource, path, []);
        var changed = 0;
        editor.SourceChanged += (_, _) => changed++;
        await editor.AnalyzeAsync();
        Require(changed == 0 && editor.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error), "Diagnostics dirtied valid source or failed.");
        Require(editor.TextView.SyntaxHighlighting.Name == "C#", "C# syntax highlighting was not configured.");
        editor.LoadSource(broken, path, []);
        await editor.AnalyzeAsync();
        Require(editor.Diagnostics.Any(d => d.Id == "CS1061"), "Live editor diagnostics are missing.");
        editor.Navigate(missing.Location.SourceSpan);
        Require(editor.TextView.SelectedText == "DoesNotExist", "AvalonEdit selection lost Unicode diagnostic offsets.");
        await CaptureAsync(window, Path.Combine(directory, "strategy-editor-diagnostics.png"), 1000, 700);
        var stale = editor.AnalyzeAsync();
        editor.LoadSource(ValidSource, path, []);
        await stale; await editor.AnalyzeAsync();
        Require(!editor.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Stale diagnostics replaced the current document.");

        editor.LoadSource("class T{void M(){int x=1;}}\r\n", path, []);
        var unformatted = editor.SourceText;
        await editor.FormatAsync();
        Require(editor.SourceText.Contains("int x = 1;", StringComparison.Ordinal) && editor.SourceText.Contains("\r\n", StringComparison.Ordinal), "Formatting or CRLF preservation failed.");
        editor.TextView.Undo(); Require(editor.SourceText == unformatted, "Formatting was not one undo step.");
        editor.LoadSource("    alpha();\r\n    beta();\r\n", path, []);
        var uncommented = editor.SourceText;
        editor.TextView.Select(0, editor.TextView.Document.TextLength); editor.ToggleComments();
        Require(editor.SourceText == "    //alpha();\r\n    //beta();\r\n", "Selected line commenting failed.");
        editor.ToggleComments(); Require(editor.SourceText == uncommented, "Comment toggle did not restore the selection.");
        editor.TextView.Undo(); Require(editor.SourceText.Contains("//alpha", StringComparison.Ordinal), "Comment toggle broke undo.");

        editor.LoadSource("class T ", path, []); editor.TextView.CaretOffset = editor.TextView.Document.TextLength;
        Type(editor, "{"); Require(editor.SourceText == "class T {}", "Opening brace did not close automatically.");
        editor.InsertNewLine(); Require(editor.SourceText == "class T {\n    \n}", "Block indentation failed: " + editor.SourceText.Replace("\n", "\\n"));
        editor.TextView.Undo(); Require(editor.SourceText == "class T {}", "Newline indentation was not one undo step.");
        editor.LoadSource("", path, []); Type(editor, "()");
        Require(editor.SourceText == "()", "Typing a closer duplicated the automatic delimiter.");
        editor.TextView.Undo(); Require(editor.SourceText == "", "Paired delimiters were not one undo step.");
        editor.LoadSource("class T {\n    ", path, []); editor.TextView.CaretOffset = editor.TextView.Document.TextLength;
        Type(editor, "}"); Require(editor.SourceText == "class T {\n}", "Closing brace did not align with its opening block.");
        editor.LoadSource("// ", path, []); editor.TextView.CaretOffset = 3; Type(editor, "(");
        Require(editor.SourceText == "// (", "Bracket completion altered a comment.");
        editor.LoadSource("var s = \"text \";", path, []); editor.TextView.CaretOffset = 14; Type(editor, "(");
        Require(editor.SourceText == "var s = \"text (\";", "Bracket completion altered a string.");

        editor.LoadSource(incomplete, path, []); editor.TextView.CaretOffset = position; editor.TextView.Focus();
        await editor.ShowCompletionsAsync();
        Require(editor.CurrentCompletion is not null, "AvalonEdit completion popup did not open.");
        editor.CurrentCompletion!.CompletionList.SelectItem("BuyNotional");
        editor.CurrentCompletion.CompletionList.RequestInsertion(EventArgs.Empty);
        await WaitUntilAsync(() => editor.SourceText.Contains("market.BuyNotional;", StringComparison.Ordinal), "Completion did not commit.");
        editor.TextView.Undo(); Require(editor.SourceText == incomplete, "Completion commit broke undo.");
        editor.LoadSource(ValidSource, path, []);
        Require(!editor.TextView.CanUndo, "Opening a document retained the previous undo history.");
        var pendingFormat = editor.FormatAsync(); editor.SetReadOnly(true); await pendingFormat;
        editor.ToggleComments(); editor.ApplyChanges([new TextChange(new TextSpan(0, 0), "bad")]);
        Type(editor, "bad");
        Require(editor.SourceText == ValidSource, "An edit modified read-only source.");
        editor.SetReadOnly(false);
        await VerifySearchAsync(editor, window, directory);
        editor.LoadSource(ValidSource, path, []); await editor.AnalyzeAsync();
        await CaptureAsync(window, Path.Combine(directory, "strategy-editor-compact.png"), 670, 430);
        await CaptureAsync(window, Path.Combine(directory, "strategy-editor-high-dpi.png"), 1000, 700, 144);
        window.Close();
        File.WriteAllText(Path.Combine(directory, "strategy-editor-tests.txt"), "PASS: real Roslyn completion/commits, extension methods, quick info, signatures, reference changes, compiler parity, Unicode diagnostics/navigation, stale responses, C# highlighting, formatting/CRLF, comments, indentation, delimiters, undo, read-only edits, regex search/replacement, WPF layout and high-DPI rendering.");
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


    /// <summary>Checks regex search and replacement through the actual WPF search window.</summary>
    private static async Task VerifySearchAsync(StrategyEditor editor, Window owner, string directory)
    {
        Require(StrategySearchWindow.CreateExpression("buy", false, true, false).Matches("buy BUY buyer prébuy").Count == 2, "Whole-word/case search failed.");
        editor.LoadSource("Buy(10); Buy(20);", "Strategy.cs", []);
        var dialog = new StrategySearchWindow(editor) { ShowInTaskbar = false, Opacity = 0 };
        dialog.ShowSearch(true, "Buy\\((\\d+)\\)", owner);
        dialog.regex.IsChecked = true;
        dialog.replacement.Text = "Sell($1)";
        dialog.ReplaceAll();
        Require(editor.SourceText == "Sell(10); Sell(20);", "Regex replacement failed.");
        editor.TextView.Undo(); Require(editor.SourceText == "Buy(10); Buy(20);", "Replace all was not one undo step.");
        dialog.query.Text = "("; dialog.FindNext(false);
        Require(dialog.message.Text.StartsWith("Invalid expression", StringComparison.Ordinal), "Invalid regex was not surfaced inline.");
        await CaptureAsync(dialog, Path.Combine(directory, "strategy-search.png"), 670, 270);
        dialog.Close();
    }

    /// <summary>Delivers text through AvalonEdit's input pipeline without sending keys to other applications.</summary>
    private static void Type(StrategyEditor editor, string text)
    {
        editor.TextView.Focus();
        foreach (var character in text) editor.TextView.TextArea.PerformTextInput(character.ToString());
    }
}
