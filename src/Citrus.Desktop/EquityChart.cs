using System.Drawing.Drawing2D;
using Citrus.Engine;

namespace Citrus.Desktop;

/// <summary>Draws portfolio equity against UTC time using native GDI+ with no chart package dependency.</summary>
internal sealed class EquityChart : Control
{
    private IReadOnlyList<EquityPoint> points = [];

    /// <summary>Creates a flicker-free, resize-aware white plotting surface.</summary>
    internal EquityChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
        Dock = DockStyle.Fill;
        AccessibleName = "Portfolio equity chart";
    }

    /// <summary>Replaces the plotted observations with a completed run.</summary>
    internal void SetPoints(IReadOnlyList<EquityPoint> values) { points = values; Invalidate(); }

    /// <summary>Paints axis labels, grid lines, and the equity series with a stable scale for flat results.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        if (points.Count == 0)
        {
            TextRenderer.DrawText(g, "Run a backtest to view portfolio equity.", Font, ClientRectangle,
                SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var plot = new RectangleF(90, 30, Width - 115, Height - 75);
        if (plot.Width < 20 || plot.Height < 20) return;
        var minimum = points.Min(p => p.Equity);
        var maximum = points.Max(p => p.Equity);
        var padding = Math.Max((maximum - minimum) * .08m, 1m);
        minimum -= padding; maximum += padding;
        using var grid = new Pen(Color.FromArgb(225, 228, 232));
        using var ink = new SolidBrush(SystemColors.ControlText);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString((maximum - (maximum - minimum) * i / 4).ToString("N0"), Font, ink, 5, y - 7);
        }
        var first = points[0].Time;
        var span = Math.Max((points[^1].Time - first).TotalSeconds, 1);
        // Map elapsed time and equity to the chart's plotting rectangle.
        PointF Position(EquityPoint p) => new(plot.Left + (float)((p.Time - first).TotalSeconds / span) * plot.Width,
            plot.Bottom - (float)((p.Equity - minimum) / (maximum - minimum)) * plot.Height);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var line = new Pen(Color.FromArgb(28, 70, 112), 1.8f);
        // Bound drawing work by pixel width while retaining each bucket's low and high observations.
        var bucketSize = Math.Max(1, points.Count / Math.Max(1, (int)plot.Width));
        var sampled = points.Chunk(bucketSize).SelectMany(bucket =>
            new[] { bucket[0], bucket.MinBy(p => p.Equity)!, bucket.MaxBy(p => p.Equity)!, bucket[^1] }
                .Distinct().OrderBy(p => p.Time)).Select(Position).ToArray();
        if (sampled.Length > 1) g.DrawLines(line, sampled);
        g.DrawString("Portfolio equity", Font, ink, plot.Left, 7);
        g.DrawString(first.ToString("yyyy-MM-dd"), Font, ink, plot.Left, plot.Bottom + 10);
        g.DrawString(points[^1].Time.ToString("yyyy-MM-dd") + " UTC", Font, ink, Math.Max(plot.Left, plot.Right - 140), plot.Bottom + 10);
    }
}
