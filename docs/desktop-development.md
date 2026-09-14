# Desktop development

Citrus.Desktop is a WPF application targeting .NET 10 on Windows. Its launch path, built-in strategies, JSON configuration, historical cache, and report formats are shared with the existing engine and CLI. The desktop project references ScottPlot.WPF; it does not host Windows Forms controls.

## Opening the designer

Use Visual Studio with .NET 10 and the .NET desktop development workload installed. Restore and build Citrus.slnx, then open a view's XAML file and select Design or Split view. Start with src/Citrus.Desktop/MainWindow.xaml. Enable the XAML Designer in Visual Studio's options if only the source editor appears.

The layouts are declared in XAML. Each view has a public parameterless constructor that initializes its visual tree. Runtime overloads supply workspace paths. Global settings and historical library reads run after Loaded, with design-mode checks. Constructing a view never builds or executes a strategy.

Keep layout edits in the XAML files. Do not edit the generated files under obj, and do not move file access into view constructors. WPF uses XAML resources and generated initialization; the old WinForms .resx files are not needed for the WPF layouts.

| View | Responsibility |
| --- | --- |
| MainWindow.xaml | Menus, workspace navigation, tabs, logs, and results |
| ConfigurationEditor.xaml | Explicit, labelled fields for backtest settings |
| EquityChart.xaml | ScottPlot WPF chart surface |
| GlobalSettingsWindow.xaml | Credentials, masking/reveal, cache path, save/cancel |
| HistoricalDataWindow.xaml | Provider/date inputs and local dataset management |
| ErrorWindow.xaml | Redacted error summary and copyable details |

App.xaml owns shared styles and App.xaml.cs starts the WPF dispatcher. MainWindow coordinates workspace views and runs Citrus.Engine's synchronous BacktestRunner.Run method on a background thread. The CLI uses the same service for execution and replay. Its configuration callback runs before historical data loading; MainWindow marshals that notification to the dispatcher to update effective values and locked fields. BacktestWorkspace creates offline examples and parses configuration text. ConfigurationEditor applies only changed fields to the original configuration so opening and saving does not round values or change optional dates. ResultTable builds typed, flattened tables with up to 5,000 rows for display; exports retain every row. MainWindow owns table binding and disposal. Result columns bind literal DataRowView descriptors, including names containing periods.

## Built-in strategy integration

MainWindow lists the public, concrete strategy types shipped in Citrus.Strategies without constructing them. StrategyCatalog associates type names with standalone JSON configurations. Installed applications use per-user settings when no checkout is available. Open in IDE opens Citrus.slnx; code changes require rebuilding and restarting Citrus. ConfigurationEditor shows the selected type read-only and edits backtest settings. Validation and execution create fresh built-in instances on a background thread. StrategyConfiguration freezes Configure assignments before historical-data selection, and the editor locks assigned fields. No project builds, source compilation or external assembly loading take place at runtime.

The smoke suite checks built-in selection and standalone settings, authoritative field locking and refresh, effective report settings, configuration persistence, result bindings and background backtests. Run:

```powershell
dotnet build Citrus.slnx -c Release --no-restore
dotnet run --project tests/Citrus.Tests -c Release --no-build --no-restore
dotnet run --project src/Citrus.Desktop -c Release --no-build --no-restore -- --smoke-test artifacts/desktop-smoke
```

The desktop check creates isolated offline fixtures and PNGs in its output directory. It does not download provider data or change the user's saved global settings. Rendering checks cover arranged WPF controls; they are not a substitute for testing the installed Visual Studio designer or moving the app between monitors with different DPI settings.
