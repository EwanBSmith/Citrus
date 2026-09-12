namespace Citrus.Desktop;

/// <summary>Starts the WPF dispatcher and the workbench or its offline integration checks.</summary>
public partial class App : Application
{
    /// <summary>Runs desktop checks with a live dispatcher, or opens the requested strategy workspace.</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DesktopSmokeTest.RunAsync(e.Args[1]));
            return;
        }
        var window = new MainWindow(e.Args.FirstOrDefault(), null);
        MainWindow = window;
        window.Show();
    }
}
