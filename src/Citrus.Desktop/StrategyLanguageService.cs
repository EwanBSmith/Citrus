using System.Reflection;
using Citrus.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace Citrus.Desktop;

/// <summary>Retains the Roslyn document that supplied a completion list so edits use the correct source span.</summary>
internal sealed record StrategyCompletions(Document Document, CompletionList List);

/// <summary>Provides cancellable, serialized Roslyn authoring operations without loading strategy assemblies.</summary>
internal sealed class StrategyLanguageService : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private AdhocWorkspace? workspace;
    private Document? document;
    private string? contextKey;
    private volatile bool disposed;

    /// <summary>Creates an immutable document snapshot with the same references and options as validation.</summary>
    private Document Snapshot(string source, string path, string[] references)
    {
        workspace ??= new AdhocWorkspace(MefHostServices.Create(MefHostServices.DefaultAssemblies.Concat(new[]
        {
            Assembly.Load("Microsoft.CodeAnalysis.CSharp.Workspaces"), Assembly.Load("Microsoft.CodeAnalysis.Features"),
            Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")
        }).Distinct()));
        var key = path + "\0" + string.Join('\0', references);
        if (document is null || contextKey != key)
        {
            var compilation = StrategyCompilation.Create("", path, references);
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(),
                "Strategy", "CitrusStrategy", LanguageNames.CSharp, compilationOptions: compilation.Options,
                parseOptions: compilation.SyntaxTrees.Single().Options, metadataReferences: compilation.References));
            var documentId = DocumentId.CreateNewId(projectId);
            document = solution.AddDocument(documentId, Path.GetFileName(path), SourceText.From(""), filePath: path).GetDocument(documentId)!;
            contextKey = key;
        }
        var text = SourceText.From(source);
        if (!document.TryGetText(out var previous) || !previous.ContentEquals(text)) document = document.WithText(text);
        return document;
    }

    /// <summary>Runs work on the thread pool and prevents workspace disposal during an active request.</summary>
    private Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token) => Task.Run(async () =>
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            return await action().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }, token);

    /// <summary>Checks unsaved source for compiler errors and warnings without constructing the strategy.</summary>
    internal Task<Diagnostic[]> AnalyzeAsync(string source, string path, string[] references, CancellationToken token) => RunAsync(async () =>
    {
        var compilation = (await Snapshot(source, path, references).Project.GetCompilationAsync(token).ConfigureAwait(false))!;
        return compilation.GetDiagnostics(token).Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).ToArray();
    }, token);

    /// <summary>Offers context-aware C# completions, including inherited members and imported extension methods.</summary>
    internal Task<StrategyCompletions?> CompleteAsync(string source, int position, string path, string[] references, CancellationToken token) => RunAsync(async () =>
    {
        var snapshot = Snapshot(source, path, references);
        var service = CompletionService.GetService(snapshot)!;
        var list = await service.GetCompletionsAsync(snapshot, position, cancellationToken: token).ConfigureAwait(false);
        return list is null ? null : new StrategyCompletions(snapshot, list);
    }, token);

    /// <summary>Resolves a selected completion to Roslyn's replacement text and optional caret location.</summary>
    internal Task<CompletionChange> CompletionChangeAsync(StrategyCompletions completions, CompletionItem item, CancellationToken token) =>
        RunAsync(() => CompletionService.GetService(completions.Document)!.GetChangeAsync(completions.Document, item, cancellationToken: token), token);

    /// <summary>Gets the declaration and documentation for a hovered source symbol.</summary>
    internal Task<string?> QuickInfoAsync(string source, int position, string path, string[] references, CancellationToken token) => RunAsync(async () =>
    {
        var snapshot = Snapshot(source, path, references);
        var info = await QuickInfoService.GetService(snapshot)!.GetQuickInfoAsync(snapshot, position, token).ConfigureAwait(false);
        return info is null ? null : string.Join(Environment.NewLine, info.Sections.Select(s => string.Concat(s.TaggedParts.Select(p => p.Text))));
    }, token);

    /// <summary>Shows callable overloads and the current argument number inside an invocation.</summary>
    internal Task<string?> SignatureAsync(string source, int position, string path, string[] references, CancellationToken token) => RunAsync(async () =>
    {
        var snapshot = Snapshot(source, path, references);
        var root = (await snapshot.GetSyntaxRootAsync(token).ConfigureAwait(false))!;
        var invocation = root.FindToken(Math.Max(0, position - 1)).Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.ArgumentList.Span.Start < position && (i.ArgumentList.CloseParenToken.IsMissing || position <= i.ArgumentList.CloseParenToken.SpanStart));
        if (invocation is null) return null;
        var model = (await snapshot.GetSemanticModelAsync(token).ConfigureAwait(false))!;
        var methods = model.GetMemberGroup(invocation.Expression, token).OfType<IMethodSymbol>().ToArray();
        var argument = invocation.ArgumentList.Arguments.GetSeparators().Count(s => s.SpanStart < position) + 1;
        return methods.Length == 0 ? null : $"Argument {argument}\n" + string.Join('\n', methods.Take(8).Select(m => m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }, token);

    /// <summary>Formats C# as incremental text changes so the editor can preserve undo history.</summary>
    internal Task<TextChange[]> FormatAsync(string source, string path, string[] references, CancellationToken token) => RunAsync(async () =>
    {
        var snapshot = Snapshot(source, path, references);
        var options = snapshot.Project.Solution.Options
            .WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, false)
            .WithChangedOption(FormattingOptions.TabSize, LanguageNames.CSharp, 4)
            .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, 4)
            .WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        var formatted = await Formatter.FormatAsync(snapshot, options, token).ConfigureAwait(false);
        return (await formatted.GetTextChangesAsync(snapshot, token).ConfigureAwait(false)).ToArray();
    }, token);

    /// <summary>Finds a definition in this strategy file, leaving external metadata in quick information.</summary>
    internal Task<TextSpan?> DefinitionAsync(string source, int position, string path, string[] references, CancellationToken token) => RunAsync(async () =>
    {
        var snapshot = Snapshot(source, path, references);
        var symbol = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindSymbolAtPositionAsync(snapshot, position, token).ConfigureAwait(false);
        return symbol?.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan;
    }, token);

    /// <summary>Disposes the workspace once any cancelled background operation has left its critical section.</summary>
    public void Dispose()
    {
        disposed = true;
        _ = Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { workspace?.Dispose(); }
            finally { gate.Release(); }
        });
    }
}
