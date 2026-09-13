# Desktop development

Citrus.Desktop is a WPF application targeting .NET 10 on Windows. Its launch path, strategy folders, JSON configuration, historical cache, and report formats are shared with the existing engine and CLI. The desktop project references ScottPlot.WPF; it does not host Windows Forms controls.

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

App.xaml owns shared styles and App.xaml.cs starts the WPF dispatcher. MainWindow retains workspace orchestration and calls the existing BacktestWorkspace service. ConfigurationEditor applies only changed fields to the original configuration so opening and saving does not round values or change optional dates. Result columns bind literal DataRowView descriptors, including names containing periods.

## External strategy integration

MainWindow opens the selected solution/project through Windows file associations. ConfigurationEditor preserves project, assembly, type and solution settings. Validation and runs call CompiledStrategy.LoadConfiguration on a background thread. Project builds report diagnostics in the execution log; WPF never owns or saves strategy source.

The smoke suite checks source preservation, configuration persistence, result bindings and background backtests. Run:

```powershell
dotnet build Citrus.slnx -c Release --no-restore
dotnet run --project tests/Citrus.Tests -c Release --no-build --no-restore
dotnet run --project src/Citrus.Desktop -c Release --no-build --no-restore -- --smoke-test artifacts/desktop-smoke
```

The desktop check creates isolated offline fixtures and PNGs in its output directory. It does not download provider data or change the user's saved global settings. Rendering checks cover arranged WPF controls; they are not a substitute for testing the installed Visual Studio designer or moving the app between monitors with different DPI settings.
