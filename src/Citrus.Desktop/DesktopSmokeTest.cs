using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Citrus.Data;
using Citrus.Engine;

namespace Citrus.Desktop;

/// <summary>Checks real WPF views, external strategy selection, and offline workspace integration.</summary>
internal static class DesktopSmokeTest
{
    internal static bool IsRunning { get; private set; }

    /// <summary>Runs the desktop regression suite on the application dispatcher and writes diagnostic artifacts.</summary>
    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        IsRunning = true;
        try
        {
            VerifyEquityChart(directory);

            var globalPath = Path.Combine(directory, "global-config.json");
            // Start from a fixture so rerunning the suite does not depend on previous user input.
            Json.Write(globalPath, new GlobalConfiguration());
            var settings = new GlobalSettingsWindow(globalPath);
            await CaptureAsync(settings, Path.Combine(directory, "global-settings.png"), 720, 480);
            Require(settings.key.Visibility == Visibility.Visible && settings.secret.Visibility == Visibility.Visible
                && settings.visibleSecret.Visibility == Visibility.Collapsed, "Credentials must start masked.");
            settings.key.Password = "fixture-key"; settings.secret.Password = "fixture-secret";
            settings.reveal.IsChecked = true;
            Require(settings.visibleSecret.Text == "fixture-secret", "Revealing credentials lost the saved value.");
            settings.visibleSecret.Text = "edited-secret";
            settings.reveal.IsChecked = false;
            Require(settings.secret.Password == "edited-secret" && settings.visibleSecret.Text == "", "Masking did not transfer and clear visible credentials.");
            Require(settings.SaveSettings(), "Global settings save failed."); settings.Close();
            Require(GlobalConfiguration.Load(globalPath).AlpacaApiSecretKey == "edited-secret", "Global settings were not persisted.");
            var cancelled = new GlobalSettingsWindow(globalPath);
            await CaptureAsync(cancelled, Path.Combine(directory, "global-settings-compact.png"), 620, 460);
            cancelled.secret.Password = "discard"; cancelled.Close();
            Require(GlobalConfiguration.Load(globalPath).AlpacaApiSecretKey == "edited-secret", "Cancel changed saved settings.");
            var damagedPath = Path.Combine(directory, "damaged-settings.json");
            File.WriteAllText(damagedPath, "{broken");
            var damaged = new GlobalSettingsWindow(damagedPath);
            ShowHidden(damaged); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!damaged.SaveSettings() && File.ReadAllText(damagedPath) == "{broken", "Damaged settings were overwritten.");
            damaged.Close();
            var library = Path.Combine(Path.GetFullPath(directory), "historical-library");
            Directory.CreateDirectory(library);
            var exampleLibrary = Path.Combine(Path.GetFullPath(directory), "example-library");
            var path = BacktestWorkspace.CreateExample(directory, exampleLibrary);
            var config = Json.Read<RunConfiguration>(path);
            var alternatePath = Path.Combine(Path.GetDirectoryName(path)!, "HigherCosts.json");
            Json.Write(alternatePath, config with { InitialCash = 75000, Output = "Results/HigherCosts" });
            var fixture = Citrus.Data.BrownianGenerator.Generate(new Citrus.Trading.Instrument("hyperliquid", Citrus.Trading.AssetClass.LinearPerpetual, "BTC"),
                Citrus.Trading.BarInterval.Daily, DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 24, 42);
            Json.Write(Path.Combine(library, "fixture.json"), fixture);
            File.WriteAllText(Path.Combine(library, "broken.json"), "{broken");
            Json.Write(Path.Combine(library, "empty.json"), new MarketDataset());
            var entries = HistoricalDataLibrary.Scan(library);
            Require(entries.Single(e => e.Status == "Valid").Instruments == "BTC", "History should be displayed by symbol, not venue.");
            if (entries.Count != 3 || entries.Count(e => e.Status == "Valid") != 1 || entries.Single(e => e.Status == "Valid").Bars != 24)
                throw new InvalidOperationException("Library scanning did not isolate invalid datasets.");
            var exported = Path.Combine(directory, "historical-export.json");
            HistoricalDataLibrary.Export(Path.Combine(library, "fixture.json"), exported);
            HistoricalDataLibrary.Export(Path.Combine(library, "fixture.json"), exported);
            if (Json.Read<MarketDataset>(exported).Bars.Count != 24) throw new InvalidOperationException("Historical export lost bars.");
            ExpectFailure(() => HistoricalDataLibrary.Export(Path.Combine(library, "broken.json"), exported));
            if (Json.Read<MarketDataset>(exported).Bars.Count != 24) throw new InvalidOperationException("Invalid export damaged an existing file.");

            var history = new HistoricalDataWindow(library);
            ShowHidden(history);
            await WaitUntilAsync(() => history.grid.Items.Count == 3, "Historical library did not load.");
            Require(history.grid.Columns.Any(c => c.Header as string == "Instruments"), "Instrument metadata column is missing.");
            await CaptureAsync(history, Path.Combine(directory, "historical-data.png"), 1100, 760);
            await CaptureAsync(history, Path.Combine(directory, "historical-data-compact.png"), 900, 700);
            history.Close();
            var exampleHistoryPath = Path.Combine(exampleLibrary, "desktop-example.json");
            var exampleHistory = Json.Read<MarketDataset>(exampleHistoryPath);
            Json.Write(exampleHistoryPath, exampleHistory with { Bars = exampleHistory.Bars.Select(b =>
                b with { Instrument = b.Instrument with { Venue = "different-data-source" } }).ToList() });
            var first = BacktestRunner.Run(path, config, exampleLibrary);
            Require(first.Result.Fills.Single().Instrument.Venue == "different-data-source", "Symbol binding must retain source metadata without requiring the strategy to name its venue.");
            if (first.Result.Fills.Count != 1 || first.Result.Equity.Count < 365)
                throw new InvalidOperationException("Example did not execute the expected holding strategy.");
            var equityPath = Path.Combine(first.Output, "equity.csv");
            var before = File.ReadAllText(equityPath);
            File.WriteAllText(Path.Combine(first.Output, "retain.txt"), "Keep unrelated output files");
            BacktestRunner.Run(path, config, exampleLibrary);
            if (before != File.ReadAllText(equityPath) || !File.Exists(Path.Combine(first.Output, "retain.txt")))
                throw new InvalidOperationException("Repeated run or output preservation failed.");
            ExpectFailure(() => BacktestWorkspace.Parse("{\"unknownSetting\":1}"));
            ExpectFailure(() => BacktestRunner.Run(path, config, Path.Combine(directory, "missing-cache")));
            ExpectFailure(() => BacktestRunner.Run(path, config with { Start = DateTimeOffset.Parse("2025-01-02T00:00:00Z"), End = DateTimeOffset.Parse("2025-01-01T00:00:00Z") }, library));

            var window = new MainWindow(null, exampleLibrary);
            ShowHidden(window); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await window.RefreshStrategiesAsync();
            Require(window.strategies.Items.Contains("Citrus.Strategies.DemoHold")
                && window.strategies.Items.Contains("ZorroPortfolio"), "The main strategy catalog did not discover both strategies.");
            window.strategies.SelectedItem = "ZorroPortfolio";
            await window.OptionsReady;
            Require(window.configEditor.ReadConfiguration().InitialCash == 17000
                && window.backtests.Items.Count == 2, "Catalog selection did not apply strategy options and filter named backtests.");
            window.strategies.SelectedItem = "Citrus.Strategies.DemoHold";
            await window.OptionsReady;
            Require(window.configEditor.ReadConfiguration().StrategyType == "Citrus.Strategies.DemoHold"
                && window.backtests.Items.Count == 1, "Selecting another strategy retained the previous backtests.");
            await CaptureAsync(window, Path.Combine(directory, "strategy-list.png"), 1280, 850);
            var catalogFixture = Path.Combine(directory, "catalog-fixture");
            Directory.CreateDirectory(catalogFixture);
            File.WriteAllText(Path.Combine(catalogFixture, "unrelated.json"), "{\"other\":true}");
            File.WriteAllText(Path.Combine(catalogFixture, "malformed.json"), "{broken");
            var newConfiguration = StrategyCatalog.Configuration(catalogFixture, "Citrus.Strategies.DemoHold");
            Require(StrategyCatalog.Configuration(catalogFixture, "Citrus.Strategies.DemoHold") == newConfiguration
                && RunConfiguration.Read(newConfiguration).StrategyType == "Citrus.Strategies.DemoHold", "New strategy settings were not created and reused.");
            window.LoadConfiguration(path);
            await window.OptionsReady;
            window.backtests.Items.Cast<MenuItem>().Single(item => Path.GetFileNameWithoutExtension(item.Tag as string) == "HigherCosts")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.OptionsReady;
            Require(window.configEditor.ReadConfiguration().InitialCash == 75000, "Named backtest selection did not load.");
            window.backtests.Items.Cast<MenuItem>().Single(item => Path.GetFileNameWithoutExtension(item.Tag as string) == "Default")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.OptionsReady;
            window.Present(first);
            Require(window.fills.Items.Count == 1, "Fill results were not bound.");
            window.tabs.SelectedIndex = 4;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var symbolColumn = window.fills.Columns.Single(c => c.Header as string == "instrument.symbol");
            Require(symbolColumn.GetCellContent(window.fills.Items[0]) is TextBlock { Text: "BTC" },
                "Flattened instrument column did not render its value.");
            window.tabs.SelectedIndex = 2;
            await CaptureAsync(window, Path.Combine(directory, "desktop-overview.png"), 1280, 850);
            await CaptureAsync(window, Path.Combine(directory, "desktop-compact.png"), 900, 620);

            window.tabs.SelectedIndex = 0;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.tabs.SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var editor = window.configEditor;
            Require(System.Text.Json.JsonSerializer.Serialize(editor.ReadConfiguration(), Json.Options) ==
                System.Text.Json.JsonSerializer.Serialize(config, Json.Options), "Untouched configuration values changed.");
            editor.InitialCash.Text = "125000";
            editor.SpreadBps.Text = "2.123456789";
            Require(editor.ReadConfiguration().Simulation.SpreadBps == 2.123456789m, "Simulation precision was lost.");
            editor.SpreadBps.Text = config.Simulation.SpreadBps.ToString(System.Globalization.CultureInfo.InvariantCulture);
            editor.Seed.Text = "-42";
            Require(editor.ReadConfiguration().Seed == -42, "Signed seed values were rejected.");
            editor.Seed.Text = config.Seed.ToString();
            editor.Start.Text = "2024-01-01T02:00:00+02:00";
            Require(editor.ReadConfiguration().Start == DateTimeOffset.Parse("2024-01-01T00:00:00Z"), "Date conversion did not retain the instant.");
            editor.Start.Clear();
            editor.InitialCash.Text = "not a number"; ExpectFailure(() => editor.ReadConfiguration()); editor.InitialCash.Text = "125000";
            var pending = window.RunAsync();
            Require(!window.strategyPage.IsEnabled, "Strategy actions remained enabled during a backtest.");
            await pending.WaitAsync(TimeSpan.FromSeconds(45));
            Require(Json.Read<RunConfiguration>(path).InitialCash == 125000, "Configuration edits were not saved.");
            Require(before != File.ReadAllText(equityPath), "Updated capital did not affect exported equity.");
            Require(window.strategyPage.IsEnabled && window.menu.IsEnabled && window.configPage.IsEnabled, "Controls were not restored after the run.");
            foreach (var (index, name) in new[] { (0, "strategy"), (1, "configuration"), (4, "fills") })
            {
                window.tabs.SelectedIndex = index;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                await CaptureAsync(window, Path.Combine(directory, "desktop-" + name + ".png"), 1280, 850);
                if (index == 1) await CaptureAsync(window, Path.Combine(directory, "desktop-configuration-compact.png"), 900, 620);
            }
            // Built-in declarations lock only assigned fields, and changing strategies clears those locks.
            var declaredPath = Path.Combine(directory, "declared.json");
            Json.Write(declaredPath, config with { StrategyType = "ZorroPortfolio" });
            window.LoadConfiguration(declaredPath);
            await window.OptionsReady;
            Require(editor.ReadConfiguration().InitialCash == 17000 && !editor.InitialCash.IsEnabled
                && editor.Seed.IsEnabled && editor.Output.IsEnabled,
                "Built-in declarations were not displayed and locked selectively.");
            editor.InitialCash.Text = "1";
            Require(editor.ReadConfiguration().InitialCash == 17000, "A disabled field bypassed strategy authority.");
            window.tabs.SelectedIndex = 1;
            await CaptureAsync(window, Path.Combine(directory, "strategy-options.png"), 1000, 760);
            window.LoadConfiguration(path);
            await window.OptionsReady;
            Require(editor.InitialCash.IsEnabled && editor.Seed.IsEnabled && editor.ShortsAvailable.IsEnabled,
                "Changing strategy left fields locked.");
            editor.LoadConfiguration(config with { StrategyType = "Missing" });
            var failed = false;
            try { await window.RunAsync(); }
            catch (InvalidOperationException) { failed = true; }
            Require(failed && window.fills.Items.Count == 0 && window.metrics.Text == "No completed backtest",
                "A failed background run left stale results visible.");
            Require(window.strategyPage.IsEnabled && window.menu.IsEnabled && window.configPage.IsEnabled,
                "A failed run left the workspace locked.");
            window.Close();
            var errorWindow = new ErrorWindow();
            errorWindow.SetError("Offline fixture error", new IOException("A fixture file could not be opened."));
            await CaptureAsync(errorWindow, Path.Combine(directory, "error-dialog.png"), 820, 480); errorWindow.Close();

            File.WriteAllText(Path.Combine(directory, "smoke-test.txt"), "PASS: WPF rendering, built-in strategy workflow, settings save/cancel/masking, damaged settings protection, historical library, offline backtests, deterministic repeated runs, output preservation, strict JSON, configuration precision, background execution, result binding, configuration saves and restored controls.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "smoke-test.txt"), error.ToString());
            return 1;
        }
        finally { IsRunning = false; }
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


    /// <summary>Shows a test window without displaying an interactive desktop surface.</summary>
    internal static void ShowHidden(Window window)
    {
        window.ShowInTaskbar = false;
        window.Opacity = 0;
        window.Show();
    }

    /// <summary>Captures the arranged WPF content at normal or high-DPI output resolution.</summary>
    internal static async Task CaptureAsync(Window window, string path, double width, double height, double dpi = 96)
    {
        window.Width = width; window.Height = height;
        if (!window.IsVisible) ShowHidden(window);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bounds = new Rect(0, 0, content.ActualWidth + content.Margin.Left + content.Margin.Right,
            content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background ?? Brushes.White, null, bounds);
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(content.Margin.Left, content.Margin.Top,
                content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * dpi / 96),
            (int)Math.Ceiling(bounds.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    /// <summary>Waits for dispatcher callbacks without blocking the UI thread.</summary>
    internal static async Task WaitUntilAsync(Func<bool> complete, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!complete())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }

    /// <summary>Fails a smoke check with the user-visible behavior that regressed.</summary>
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
