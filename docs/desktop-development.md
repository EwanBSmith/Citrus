# Desktop development

Citrus.Desktop uses WPF and ScottPlot.WPF on .NET 10 for Windows. It shares the engine, configuration, historical cache, and reports with the CLI.

## Opening the designer

Install Visual Studio with .NET 10 and the .NET desktop development workload. Restore and build Citrus.slnx, then open src/Citrus.Desktop/MainWindow.xaml in Design or Split view. Enable the XAML Designer in Visual Studio options if needed.

Edit layouts in XAML, not generated files under obj. Parameterless view constructors must remain safe for the designer: initialize controls without reading user files or executing strategies. Load settings and history after Loaded, with design-mode checks.

| View | Responsibility |
| --- | --- |
| MainWindow.xaml | Menus, navigation, configuration and result tabs |
| ConfigurationEditor.xaml | Labelled backtest settings |
| EquityChart.xaml | Equity plot |
| GlobalSettingsWindow.xaml | Credentials and cache settings |
| HistoricalDataWindow.xaml | Downloads and local datasets |
| ErrorWindow.xaml | Redacted, copyable errors |

## Execution and state

App.xaml owns shared styles; App.xaml.cs starts the dispatcher. MainWindow runs the synchronous BacktestRunner.Run on a background thread and marshals configuration notifications to the dispatcher before data loading.

RunConfiguration.Read loads saved settings. ConfigurationEditor changes only edited fields, preserving numeric precision and optional dates. BacktestWorkspace creates offline examples.

ResultTable flattens up to 5,000 rows for display; exports retain all rows. MainWindow binds and disposes tables. Bind literal DataRowView descriptors so periods in column names are not treated as nested paths.

## Built-in strategies

MainWindow.RefreshStrategies synchronously lists public, concrete types in Citrus.Strategies without constructing them. StrategyCatalog finds matching JSON backtests, using per-user settings outside a checkout. See the [user workflow](../README.md#windows-desktop-workbench) for selection and rebuilding.

Validation and execution create fresh strategy instances on a background thread. StrategyConfiguration freezes Configure assignments before data selection; the editor locks those fields and displays the selected type read-only.

## Verification

Run the offline checks:

```powershell
dotnet build Citrus.slnx -c Release --no-restore
dotnet run --project tests/Citrus.Tests -c Release --no-build --no-restore
dotnet run --project src/Citrus.Desktop -c Release --no-build --no-restore -- --smoke-test artifacts/desktop-smoke
```

The smoke suite covers strategy selection, settings persistence and precision, authoritative fields, background runs, result bindings, and WPF rendering. It creates isolated fixtures and PNGs without downloads or changes to saved user settings. Test the installed Visual Studio designer and movement between monitors with different DPI settings separately.
