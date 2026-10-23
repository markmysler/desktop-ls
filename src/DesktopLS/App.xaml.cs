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

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreDesktop();
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        SessionEnding += (_, _) => RestoreDesktop();

        base.OnStartup(e);

        // Optional folder to open; without one MainWindow starts on the user's
        // real desktop (resolved after crash recovery, not here)
        if (e.Args.Length > 0)
            Properties["StartPath"] = e.Args[0];
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RestoreDesktop();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        RestoreDesktop();
        MessageBox.Show($"DesktopLS hit an unexpected error and will close. Your desktop has been restored.\n\n{e.Exception.Message}",
            "DesktopLS", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private void RestoreDesktop()
    {
        try { DesktopFolder?.Restore(); } catch { }
    }
}
