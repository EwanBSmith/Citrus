# Desktop development

Citrus.Desktop is a WPF application targeting .NET 10 on Windows. Its launch path, strategy folders, JSON configuration, historical cache, and report formats are shared with the existing engine and CLI. The desktop project references AvalonEdit, ScottPlot.WPF, and Roslyn language services; it does not host Windows Forms controls.

## Opening the designer

Use Visual Studio with .NET 10 and the .NET desktop development workload installed. Restore and build Citrus.slnx, then open a view's XAML file and select Design or Split view. Start with src/Citrus.Desktop/MainWindow.xaml. Enable the XAML Designer in Visual Studio's options if only the source editor appears.

The layouts are declared in XAML. Each view has a public parameterless constructor that initializes its visual tree. Runtime overloads supply workspace paths or editor dependencies. Global settings and historical library reads run after Loaded, with design-mode checks. Roslyn analysis starts only after a source document is opened; constructing a view never compiles or executes a strategy.

Keep layout edits in the XAML files. Do not edit the generated files under obj, and do not move file access into view constructors. WPF uses XAML resources and generated initialization; the old WinForms .resx files are not needed for the WPF layouts.

| View | Responsibility |
| --- | --- |
| MainWindow.xaml | Menus, workspace navigation, tabs, logs, and results |
| StrategyEditor.xaml | AvalonEdit, authoring commands, diagnostics, and status |
| ConfigurationEditor.xaml | Explicit, labelled fields for backtest settings |
| EquityChart.xaml | ScottPlot WPF chart surface |
| GlobalSettingsWindow.xaml | Credentials, masking/reveal, cache path, save/cancel |
| HistoricalDataWindow.xaml | Provider/date inputs and local dataset management |
| StrategySearchWindow.xaml | Find and replace with bounded regex matching |
| GoToLineWindow.xaml | Validated one-based source navigation |
| ErrorWindow.xaml | Redacted error summary and copyable details |

App.xaml owns shared styles and App.xaml.cs starts the WPF dispatcher. MainWindow retains workspace orchestration and calls the existing BacktestWorkspace service. ConfigurationEditor applies only changed fields to the original configuration so opening and saving does not round values or change optional dates. Result columns bind literal DataRowView descriptors, including names containing periods.

## Editor integration

AvalonEdit owns text storage, UTF-16 positions, syntax highlighting, input, selection, scrolling, and undo. StrategyLanguageService retains the existing Roslyn reference resolution and semantic operations. Assistance never instantiates a strategy; explicit validation and backtests do.

Completion callbacks apply Roslyn's exact replacement span and caret position. Each source or reference change cancels stale requests. Formatting, comment toggles, and replace-all use one document update transaction. Read-only mode also invalidates pending edits during backtests. The owning window disposes the editor's timers, cancellation sources, language service, folding manager, and popups when it closes.

The smoke suite exercises these behaviors through real AvalonEdit controls, along with WPF result bindings, settings persistence, and background backtests. Run:

```powershell
dotnet build Citrus.slnx -c Release --no-restore
dotnet run --project tests/Citrus.Tests -c Release --no-build --no-restore
dotnet run --project src/Citrus.Desktop -c Release --no-build --no-restore -- --smoke-test artifacts/desktop-smoke
```

The desktop check creates isolated offline fixtures and PNGs in its output directory. It does not download provider data or change the user's saved global settings. Rendering checks cover arranged WPF controls; they are not a substitute for testing the installed Visual Studio designer or moving the app between monitors with different DPI settings.
