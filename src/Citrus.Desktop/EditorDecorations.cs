using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.CodeAnalysis;

namespace Citrus.Desktop;

/// <summary>Draws compiler underlines, matching braces, and indentation guides over visible source lines.</summary>
internal sealed class EditorDecorations(TextEditor editor) : IBackgroundRenderer
{
    internal Diagnostic[] Diagnostics { get; set; } = [];
    internal int[] Braces { get; set; } = [];
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Renders only decorations intersecting the visible document region.</summary>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
        var first = textView.VisualLines.First().FirstDocumentLine.Offset;
        var last = textView.VisualLines.Last().LastDocumentLine.EndOffset;
        var guide = new Pen(new SolidColorBrush(Color.FromRgb(225, 230, 236)), 1);
        foreach (var line in textView.VisualLines)
        {
            var text = editor.Document.GetText(line.FirstDocumentLine);
            var spaces = text.TakeWhile(c => c == ' ').Count();
            for (var column = 4; column <= spaces; column += 4)
            {
                var x = column * textView.WideSpaceWidth - textView.HorizontalOffset;
                if (x >= 0) drawingContext.DrawLine(guide, new Point(x, line.VisualTop - textView.VerticalOffset),
                    new Point(x, line.VisualTop + line.Height - textView.VerticalOffset));
            }
        }
        foreach (var position in Braces.Where(p => p >= first && p <= last && p < editor.Document.TextLength))
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = position, Length = 1 }))
                drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, 100, 165, 240)), null, rect);
        foreach (var diagnostic in Diagnostics.Where(d => d.Location.IsInSource))
        {
            var span = diagnostic.Location.SourceSpan;
            if (span.End < first || span.Start > last || editor.Document.TextLength == 0) continue;
            var start = Math.Min(span.Start, editor.Document.TextLength - 1);
            var length = Math.Min(Math.Max(1, span.Length), editor.Document.TextLength - start);
            var pen = new Pen(diagnostic.Severity == DiagnosticSeverity.Error ? Brushes.Firebrick : Brushes.DarkGoldenrod, 1);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start, Length = length }))
            {
                var left = Math.Max(0, rect.Left); var right = Math.Min(textView.ActualWidth, rect.Right);
                for (var x = left; x < right; x += 4)
                {
                    drawingContext.DrawLine(pen, new Point(x, rect.Bottom - 2), new Point(Math.Min(x + 2, right), rect.Bottom));
                    if (x + 2 < right) drawingContext.DrawLine(pen, new Point(x + 2, rect.Bottom), new Point(Math.Min(x + 4, right), rect.Bottom - 2));
                }
            }
        }
    }
}
