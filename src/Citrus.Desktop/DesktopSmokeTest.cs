using Citrus.Data;
using Citrus.Engine;

namespace Citrus.Desktop;

/// <summary>Runs offline desktop integration checks and captures native-control layouts on Windows.</summary>
internal static class DesktopSmokeTest
{
    /// <summary>Runs async editor checks under the same persistent Windows Forms synchronization context as the application.</summary>
    internal static int RunWithMessageLoop(string directory)
    {
        using var host = new Form { ShowInTaskbar = false, Opacity = 0 };
        var result = 1;
        host.Shown += (_, _) => host.BeginInvoke(() =>
        {
            try { result = Run(directory); }
            finally { host.Close(); }
        });
        Application.Run(host);
        return result;
    }

    /// <summary>Verifies example execution, replay, strict configuration parsing, errors, result binding, and form rendering.</summary>
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            VerifyEquityChart(directory);
            StrategyEditorSmokeTest.Run(directory);
            var globalPath = Path.Combine(Path.GetFullPath(directory), "global-config.json");
            using (var settingsForm = new GlobalSettingsForm(globalPath))
            {
                Capture(settingsForm, Path.Combine(directory, "global-settings.png"), new Size(680, 390));
                var fields = Descendants(settingsForm).OfType<TextBox>().ToArray();
                var keyField = fields.Single(c => c.AccessibleName == "Alpaca API key ID");
                var secretField = fields.Single(c => c.AccessibleName == "Alpaca API secret key");
                if (!keyField.UseSystemPasswordChar || !secretField.UseSystemPasswordChar)
                    throw new InvalidOperationException("Credentials must start masked.");
                keyField.Text = "fixture-key";
                secretField.Text = "fixture-secret";
                ((Button)settingsForm.AcceptButton!).PerformClick();
                if (GlobalConfiguration.Load(globalPath).AlpacaApiSecretKey != "fixture-secret")
                    throw new InvalidOperationException("Global settings were not saved.");
            }
            using (var cancelled = new GlobalSettingsForm(globalPath))
            {
                Descendants(cancelled).OfType<TextBox>().Single(c => c.AccessibleName == "Alpaca API secret key").Text = "discard";
                cancelled.Close();
                if (GlobalConfiguration.Load(globalPath).AlpacaApiSecretKey != "fixture-secret")
                    throw new InvalidOperationException("Cancel changed global settings.");
            }
            var library = Path.Combine(Path.GetFullPath(directory), "historical-library");
            Directory.CreateDirectory(library);
            var exampleLibrary = Path.Combine(Path.GetFullPath(directory), "example-library");
            var path = BacktestWorkspace.CreateExample(directory, exampleLibrary);
            var config = Json.Read<RunConfiguration>(path);
            var alternatePath = Path.Combine(Path.GetDirectoryName(path)!, "HigherCosts.json");
            Json.Write(alternatePath, config with { InitialCash = 75000, Output = "Results/HigherCosts" });
            var fixture = Citrus.Data.BrownianGenerator.Generate(new Citrus.Trading.Instrument("hyperliquid", Citrus.Trading.AssetClass.LinearPerpetual, "BTC"),
                Citrus.Trading.BarInterval.Hourly, DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 24, 42);
            Json.Write(Path.Combine(library, "fixture.json"), fixture);
            File.WriteAllText(Path.Combine(library, "broken.json"), "{broken");
            Json.Write(Path.Combine(library, "empty.json"), new MarketDataset());
            var entries = HistoricalDataLibrary.Scan(library);
            if (entries.Count != 3 || entries.Count(e => e.Status == "Valid") != 1 || entries.Single(e => e.Status == "Valid").Bars != 24)
                throw new InvalidOperationException("Library scanning did not isolate invalid datasets.");
            var exported = Path.Combine(directory, "historical-export.json");
            HistoricalDataLibrary.Export(Path.Combine(library, "fixture.json"), exported);
            HistoricalDataLibrary.Export(Path.Combine(library, "fixture.json"), exported);
            if (Json.Read<MarketDataset>(exported).Bars.Count != 24) throw new InvalidOperationException("Historical export lost bars.");
            ExpectFailure(() => HistoricalDataLibrary.Export(Path.Combine(library, "broken.json"), exported));
            if (Json.Read<MarketDataset>(exported).Bars.Count != 24) throw new InvalidOperationException("Invalid export damaged an existing file.");
            using (var history = new HistoricalDataForm(library))
            {
                Capture(history, Path.Combine(directory, "historical-data.png"), new Size(1100, 760));
                var historyGrid = Descendants(history).OfType<DataGridView>().Single();
                var historyWait = System.Diagnostics.Stopwatch.StartNew();
                while (historyGrid.Rows.Count != 3)
                {
                    if (historyWait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Historical library did not load.");
                    Application.DoEvents(); Thread.Sleep(10);
                }
                Capture(history, Path.Combine(directory, "historical-data.png"), new Size(1100, 760));
                Capture(history, Path.Combine(directory, "historical-data-compact.png"), new Size(900, 700));
            }
            var first = BacktestWorkspace.RunAsync(path, config, exampleLibrary).GetAwaiter().GetResult();
            if (first.Result.Fills.Count != 1 || first.Result.Equity.Count < 365)
                throw new InvalidOperationException("Example did not execute the expected holding strategy.");
            var equityPath = Path.Combine(first.Output, "equity.csv");
            var before = File.ReadAllText(equityPath);
            File.WriteAllText(Path.Combine(first.Output, "retain.txt"), "Keep unrelated output files");
            BacktestWorkspace.RunAsync(path, config, exampleLibrary).GetAwaiter().GetResult();
            if (before != File.ReadAllText(equityPath) || !File.Exists(Path.Combine(first.Output, "retain.txt")))
                throw new InvalidOperationException("Replay or output preservation failed.");
            ExpectFailure(() => BacktestWorkspace.Parse("{\"unknownSetting\":1}"));
            ExpectFailure(() => BacktestWorkspace.RunAsync(path, config, Path.Combine(directory, "missing-cache")).GetAwaiter().GetResult());
            ExpectFailure(() => BacktestWorkspace.RunAsync(path, config with { Start = DateTimeOffset.Parse("2025-01-02T00:00:00Z"), End = DateTimeOffset.Parse("2025-01-01T00:00:00Z") }, library).GetAwaiter().GetResult());
            using var form = new MainForm(historicalDataDirectory: exampleLibrary);
            form.LoadConfiguration(StrategyFolder.Root(path)!);
            var selector = Descendants(form).OfType<ToolStrip>().SelectMany(t => t.Items.OfType<ToolStripComboBox>()).Single();
            selector.SelectedItem = "HigherCosts";
            if (Descendants(form).OfType<ConfigurationEditor>().Single().ReadConfiguration().InitialCash != 75000)
                throw new InvalidOperationException("Named backtest selection did not load its settings.");
            selector.SelectedItem = "Default";
            form.Present(first);
            Capture(form, Path.Combine(directory, "desktop-overview.png"), new Size(1280, 850));
            Capture(form, Path.Combine(directory, "desktop-compact.png"), new Size(900, 620));
            var controls = Descendants(form).ToArray();
            var editors = controls.OfType<StrategyEditor>().ToArray();
            var tabs = controls.OfType<TabControl>().Single();
            tabs.SelectedIndex = 0;
            Application.DoEvents();
            editors.Single().TextView.AppendText("\n// Desktop save verification\n");
            tabs.SelectedIndex = 1;
            Application.DoEvents();
            var configEditor = controls.OfType<ConfigurationEditor>().Single();
            if (System.Text.Json.JsonSerializer.Serialize(configEditor.ReadConfiguration(), Json.Options) != System.Text.Json.JsonSerializer.Serialize(config, Json.Options))
                throw new InvalidOperationException("Configuration controls changed untouched settings.");
            controls.OfType<NumericUpDown>().Single(e => e.AccessibleName == "Initial Cash").Value = 125000;
            var spread = controls.OfType<NumericUpDown>().Single(e => e.AccessibleName == "Spread (basis points)");
            spread.Value = 2;
            if (configEditor.ReadConfiguration().Simulation.SpreadBps != 2)
                throw new InvalidOperationException("Simulation control changes were not captured.");
            spread.Value = config.Simulation.SpreadBps;
            var pending = form.RunAsync();
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!pending.IsCompleted)
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Desktop background run did not complete.");
                Application.DoEvents();
                Thread.Sleep(10);
            }
            pending.GetAwaiter().GetResult();
            if (Json.Read<RunConfiguration>(path).InitialCash != 125000) throw new InvalidOperationException("Configuration changes were not saved.");
            if (!File.ReadAllText(StrategyFolder.Source(path, config)).Contains("Desktop save verification"))
                throw new InvalidOperationException("Source changes were not saved.");
            if (before == File.ReadAllText(equityPath)) throw new InvalidOperationException("Updated capital did not change exports.");
            if (editors.Any(e => e.TextView.ReadOnly)) throw new InvalidOperationException("Editors remained read-only after completion.");
            foreach (var (index, name) in new[] { (0, "strategy"), (1, "configuration"), (4, "fills") })
            {
                tabs.SelectedIndex = index;
                if (index == 0)
                {
                    var analyzed = editors.Single().AnalyzeAsync();
                    var analysisWait = System.Diagnostics.Stopwatch.StartNew();
                    while (!analyzed.IsCompleted)
                    {
                        if (analysisWait.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Editor analysis did not complete.");
                        Application.DoEvents(); Thread.Sleep(10);
                    }
                    analyzed.GetAwaiter().GetResult();
                }
                Capture(form, Path.Combine(directory, "desktop-" + name + ".png"), new Size(1280, 850));
                if (index == 1) Capture(form, Path.Combine(directory, "desktop-configuration-compact.png"), new Size(900, 620));
            }
            File.WriteAllText(Path.Combine(directory, "smoke-test.txt"), "PASS: example, exports, deterministic replay, preserved files, strict JSON, missing data, conflicting data mode, result binding, native form rendering, document saves, background UI run, controls restored after completion.");
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(directory, "smoke-test.txt"), exception.ToString());
            return 1;
        }
    }

    /// <summary>Renders edge cases and checks UTC conversion, extrema, and replacement of previous runs.</summary>
    private static void VerifyEquityChart(string directory)
    {
        using var chart = new EquityChart();
        var time = DateTimeOffset.Parse("2024-01-01T02:00:00+02:00");
        var first = new EquityPoint(time, 100, 100, 0, new Dictionary<string, decimal>());
        chart.Plot.SavePng(Path.Combine(directory, "chart-empty.png"), 800, 400);
        chart.SetPoints([first]);
        chart.Plot.SavePng(Path.Combine(directory, "chart-single.png"), 800, 400);
        var limits = chart.Plot.Axes.GetLimits();
        if (Math.Abs(limits.HorizontalCenter - time.UtcDateTime.ToOADate()) > 1e-8 || limits.Top <= limits.Bottom)
            throw new InvalidOperationException("Single-point chart lost UTC time or a usable scale.");
        chart.SetPoints([first, first with { Time = time.AddDays(1) }]);
        chart.Plot.SavePng(Path.Combine(directory, "chart-flat.png"), 800, 400);
        chart.SetPoints([first, first with { Equity = -50 }, first with { Time = time.AddDays(3), Equity = 200 }]);
        chart.Plot.SavePng(Path.Combine(directory, "chart-irregular.png"), 800, 400);
        limits = chart.Plot.Axes.GetLimits();
        if (limits.Bottom >= -50 || limits.Top <= 200 || chart.Plot.GetPlottables().Count() != 1)
            throw new InvalidOperationException("Chart lost extrema or retained the previous series.");
        chart.SetPoints([]);
        if (chart.Plot.GetPlottables().Any()) throw new InvalidOperationException("Clearing a run left stale equity plotted.");
    }

    /// <summary>Requires an invalid desktop input to fail instead of continuing silently.</summary>
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch { return; }
        throw new InvalidOperationException("Invalid input unexpectedly succeeded.");
    }

    /// <summary>Enumerates native descendants so integration checks can edit documents and select tabs.</summary>
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>Renders the form without opening an interactive window.</summary>
    private static void Capture(Form form, string path, Size size)
    {
        form.Size = size;
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
    }
}
