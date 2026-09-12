using Citrus.Engine;
using ScottPlot.WinForms;

namespace Citrus.Desktop;

/// <summary>Displays portfolio equity against UTC time with ScottPlot pan, zoom, and image export.</summary>
internal sealed class EquityChart : FormsPlot
{
    /// <summary>Creates a resize-aware plot with UTC dates and an empty-run prompt.</summary>
    internal EquityChart()
    {
        Dock = DockStyle.Fill;
        AccessibleName = "Portfolio equity chart";
        Plot.Axes.DateTimeTicksBottom();
        Plot.XLabel("Time (UTC)");
        Plot.YLabel("Equity");
        SetPoints([]);
    }

    /// <summary>Replaces the series and resets the view, retaining every observation for zooming.</summary>
    internal void SetPoints(IReadOnlyList<EquityPoint> values)
    {
        Plot.Clear();
        Plot.Title(values.Count == 0 ? "Run a backtest to view portfolio equity." : "Portfolio equity");
        if (values.Count == 0)
        {
            Plot.Axes.SetLimits(0, 1, 0, 1);
            Refresh();
            return;
        }

        var dates = values.Select(p => p.Time.UtcDateTime.ToOADate()).ToArray();
        var equity = values.Select(p => (double)p.Equity).ToArray();
        if (values.Count == 1)
        {
            var point = Plot.Add.Scatter(dates, equity);
            point.Color = ScottPlot.Color.FromHex("1C4670");
            point.MarkerSize = 6;
        }
        else
        {
            // SignalXY renders ordered, irregular timestamps efficiently without discarding extrema.
            var series = Plot.Add.SignalXY(dates, equity);
            series.Color = ScottPlot.Color.FromHex("1C4670");
            series.LineWidth = 1.8f;
        }

        var minimum = equity.Min();
        var maximum = equity.Max();
        var padding = Math.Max((maximum - minimum) * .08, 1);
        Plot.Axes.AutoScale();
        Plot.Axes.SetLimitsY(minimum - padding, maximum + padding);
        if (dates[0] == dates[^1]) Plot.Axes.SetLimitsX(dates[0] - .5, dates[0] + .5);
        Refresh();
    }
}
