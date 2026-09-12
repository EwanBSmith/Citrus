namespace Citrus.Desktop;

/// <summary>Starts the Windows backtesting workbench on an STA UI thread.</summary>
internal static class Program
{
    /// <summary>Initializes native controls and optionally opens a run configuration.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--smoke-test") Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--smoke-test") return DesktopSmokeTest.RunWithMessageLoop(args[1]);
        Application.Run(new MainForm(args.FirstOrDefault()));
        return 0;
    }
}
