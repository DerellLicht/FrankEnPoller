namespace FrankEnPoller;

internal static class Program
{
    // Named event behind "FrankEnPoller.exe --exit": a second launch sets it, and the
    // running copy then quits through its normal Exit path (removing its tray icon),
    // which "taskkill /F" cannot do.  Local\ matches the single-instance mutex below.
    private const string ExitEventName = @"Local\FrankEnPoller.Exit";

    /// <summary>
    /// "--exit" on the command line signals the already-running copy to quit, then returns
    /// without showing anything.  Does nothing if no copy is running.
    /// </summary>
    private static void SignalRunningInstanceToExit()
    {
        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent)) {
            exitEvent.Set();
            exitEvent.Dispose();
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase))) {
            SignalRunningInstanceToExit();
            return;
        }

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

        // Wait for "--exit" requests on a thread-pool thread; RequestExit hops back to the UI thread.
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var exitWait = ThreadPool.RegisterWaitForSingleObject(
            exitEvent, (_, _) => app.RequestExit(), null, Timeout.Infinite, executeOnlyOnce: true);

        Application.Run(app);
        exitWait.Unregister(null);
        Log.Info("Exited");
    }
}
