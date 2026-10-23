using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopLS.Services;

namespace DesktopLS;

public partial class App : Application
{
    // One instance per user session. A second copy would see the first one's
    // crash-recovery file and reset the desktop underneath it.
    private const string SingleInstanceMutexName = @"Local\DesktopLS.SingleInstance";
    private Mutex? _singleInstance;

    /// <summary>
    /// The live desktop redirection, set by MainWindow. Restored from every
    /// exit path below so the user's desktop is never left pointing elsewhere.
    /// </summary>
    public DesktopFolderService? DesktopFolder { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("DesktopLS is already running.", "DesktopLS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // For uninstallers: put the Desktop back to normal and exit
        // (--no-restart leaves Explorer alone; the change then shows at the next sign-in)
        if (e.Args.Any(a => a.Equals("--undo-setup", StringComparison.OrdinalIgnoreCase)))
        {
            // No main window here: creating it would set everything up again
            bool restart = !e.Args.Any(a => a.Equals("--no-restart", StringComparison.OrdinalIgnoreCase));
            int exitCode = 0;
            try
            {
                DesktopFolderService.UndoSetup(restart);
                var settings = new SettingsService();
                settings.Load();
                settings.StartupEnabled = false;
            }
            catch (Exception ex)
            {
                Log.Error("Undoing the DesktopLS setup failed", ex);
                exitCode = 1;
            }
            Shutdown(exitCode);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
            RestoreDesktop();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        SessionEnding += (_, _) => RestoreDesktop();

        base.OnStartup(e);

        // Optional folder to open; without one MainWindow starts on the user's
        // real desktop (resolved after crash recovery, not here)
        if (e.Args.Length > 0)
            Properties["StartPath"] = NormalizeStartArgument(e.Args[0]);

        // Created here rather than through StartupUri, so the early exits
        // above never construct it (its constructor sets up the desktop)
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RestoreDesktop();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        RestoreDesktop();
        MessageBox.Show($"DesktopLS hit an unexpected error and will close. Your desktop has been restored.\n\n{e.Exception.Message}",
            "DesktopLS", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    /// <summary>
    /// Windows command-line parsing turns a quoted path ending in a backslash
    /// ("D:\") into D:" (the \" becomes a literal quote). Undo that.
    /// </summary>
    internal static string NormalizeStartArgument(string arg)
    {
        string path = arg.Trim().Trim('"').Trim();
        if (path.Length == 2 && path[1] == ':') path += "\\";
        return path;
    }

    private void RestoreDesktop()
    {
        try { DesktopFolder?.Restore(); }
        catch (Exception ex) { Log.Error("Restoring the desktop failed", ex); }
    }
}
