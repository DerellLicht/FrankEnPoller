namespace FrankEnPoller;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\FrankEnPoller.Tray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("FrankEnPoller is already running. Look for its icon in the system tray.", "FrankEnPoller");
            return;
        }
        if (Mutex.TryOpenExisting(@"Local\EnphasePoller.Tray", out var oldInstance))
        {
            oldInstance.Dispose();
            MessageBox.Show("The old Enphase Poller is still running. Exit it from its tray menu, then start FrankEnPoller again.", "FrankEnPoller");
            return;
        }

        AppPaths.MigrateFromOldName();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            MessageBox.Show(e.Exception.Message, "FrankEnPoller error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        Log.Info("Started");
        using var app = new TrayApp();
        Application.Run(app);
        Log.Info("Exited");
    }
}
