using Microsoft.UI.Xaml;

namespace WinSpotlight;

public partial class App : Application
{
    private MainWindow? _window;

    public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "winspotlight-ui.log");

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

    /// <summary>The app used to be called LSearch, and Lumen before that; carry its config, index
    /// and models over once, including absolute paths in config.json that point into them.</summary>
    private static void MigrateDataFolders()
    {
        var moved = new List<(string Old, string Now)>();
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var dir = Environment.GetFolderPath(root);
            var now = Path.Combine(dir, "WinSpotlight");
            foreach (var name in new[] { "LSearch", "Lumen" })
            {
                var old = Path.Combine(dir, name);
                try
                {
                    if (!Directory.Exists(old) || Directory.Exists(now)) continue;
                    Directory.Move(old, now);
                    moved.Add((old, now));
                }
                catch (Exception e) { Log("migrate " + e.Message); }
            }
        }
        if (moved.Count == 0 || !File.Exists(SetupWindow.ConfigPath)) return;
        try
        {
            static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s)[1..^1];
            var text = File.ReadAllText(SetupWindow.ConfigPath);
            foreach (var (old, now) in moved) text = text.Replace(Json(old + "\\"), Json(now + "\\"));
            File.WriteAllText(SetupWindow.ConfigPath, text);
        }
        catch (Exception e) { Log("migrate config " + e.Message); }
    }
}
