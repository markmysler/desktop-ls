using System.Windows;

namespace DesktopLS;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Set default directory to user profile if no args
        string startPath = e.Args.Length > 0 ? e.Args[0] : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        Properties["StartPath"] = startPath;
    }
}
