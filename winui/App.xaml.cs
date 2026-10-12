using Microsoft.UI.Xaml;

namespace LSearch;

public partial class App : Application
{
    private MainWindow? _window;

    public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "lsearch-ui.log");

    public static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}"); } catch { }
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { Log("UNHANDLED " + e.Exception); e.Handled = true; };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MigrateDataFolders();
        var argv = Environment.GetCommandLineArgs();
        int qi = Array.IndexOf(argv, "--query");
        _window = new MainWindow { Pinned = argv.Contains("--pin") };
        _window.Start(argv.Contains("--show"), qi >= 0 && qi + 1 < argv.Length ? argv[qi + 1] : null);
    }

    /// <summary>The app used to be called Lumen; carry its config, index and models over once.</summary>
    private static void MigrateDataFolders()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var dir = Environment.GetFolderPath(root);
            string old = Path.Combine(dir, "Lumen"), now = Path.Combine(dir, "LSearch");
            try { if (Directory.Exists(old) && !Directory.Exists(now)) Directory.Move(old, now); }
            catch (Exception e) { Log("migrate " + e.Message); }
        }
    }
}
