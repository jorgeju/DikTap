using DikTap.Services.Startup;

namespace DikTap;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string exe = Environment.ProcessPath ?? string.Empty;

        // Cleanup: removes the startup entry without opening the application.
        if (args.Contains("--remove-startup", StringComparer.OrdinalIgnoreCase))
        {
            new RunKeyStartupRegistration(exe).Disable();
            return 0;
        }

        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        bool updated = args.Contains("--updated", StringComparer.OrdinalIgnoreCase);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Error("Fatal exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        // One instance per user/session. A second launch asks the first to show the settings.
        using var mutex = new Mutex(false, AppInfo.MutexName);
        if (!TryOwn(mutex, TimeSpan.Zero))
        {
            if (!background)
            {
                try { EventWaitHandle.OpenExisting(AppInfo.ShowSettingsEventName).Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
            }
            // The previous instance may be closing (Exit and reopen): wait before giving up.
            if (!TryOwn(mutex, TimeSpan.FromSeconds(4))) return 0;
        }

        try
        {
            return Run(exe, updated);
        }
        catch (Exception ex)
        {
            Logger.Error("The application failed to start", ex);
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static bool TryOwn(Mutex mutex, TimeSpan wait)
    {
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }

    private static int Run(string exe, bool updated)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += (_, e) =>
        {
            Logger.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };

        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.ShowSettingsEventName);
        var coordinator = new AppCoordinator(exe);
        var registration = ThreadPool.RegisterWaitForSingleObject(showSettings,
            (_, _) => dispatcher.InvokeAsync(coordinator.ShowSettingsFromAnotherInstance), null, Timeout.Infinite, false);

        coordinator.Start(updated);
        // Only the Dispatcher message loop (WindowsBase): full WPF is loaded only when Settings opens.
        System.Windows.Threading.Dispatcher.Run();

        registration.Unregister(null);
        return 0;
    }
}
