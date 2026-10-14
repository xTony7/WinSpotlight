using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LSearch;

/// <summary>First-run (and tray "Setup…") wizard. Writes %APPDATA%\LSearch\config.json.</summary>
public sealed partial class SetupWindow : Window
{
    public const long LlaMinRamKb = 16L * 1024 * 1024;
    public static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LSearch", "config.json");

    private readonly ObservableCollection<string> _folders = new();
    private readonly JsonObject _cfg;
    private readonly List<FrameworkElement> _pages;
    private readonly bool _showLla;
    private int _page;

    /// <summary>Raised after settings are saved.</summary>
    public event Action? Completed;

    public SetupWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        var hwnd = WindowNative.GetWindowHandle(this);
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(620 * scale), (int)(540 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsResizable = false; p.IsMaximizable = false; }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));

        _cfg = LoadConfig();
        foreach (var f in _cfg["content_roots"]?.AsArray() ?? new JsonArray())
            if (f?.GetValue<string>() is { } s) _folders.Add(s);
        if (_cfg["content_roots"] == null)
            foreach (var d in new[] { "Desktop", "Documents", "Downloads" })
                _folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), d));
        FolderList.ItemsSource = _folders;
        AllDrivesToggle.IsOn = _cfg["index_all_drives"]?.GetValue<bool>() ?? true;
        HistoryToggle.IsOn = _cfg["browser_history"]?.GetValue<bool>() ?? true;
        LlaToggle.IsOn = _cfg["llm"]?["enabled"]?.GetValue<bool>() ?? false;
        UpdateLlaDownload();
        HotkeyText.Text = $"Open LSearch any time with {_cfg["hotkey"]?.GetValue<string>()?.ToUpperInvariant() ?? "ALT+SPACE"}.";

        long ramKb = InstalledRamKb();
        _showLla = ramKb >= LlaMinRamKb;
        RamText.Text = $"This PC has {ramKb / 1024.0 / 1024.0:0} GB of RAM.";
        _pages = new() { PageWelcome, PageFolders, PageHistory };
        if (_showLla) _pages.Add(PageLla);
        _pages.Add(PageDone);
        ShowPage(0);
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long kb);

    private static long InstalledRamKb() => GetPhysicallyInstalledSystemMemory(out var kb) ? kb : 0;

    public static JsonObject LoadConfig()
    {
        try { return JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    public static bool SetupComplete() => LoadConfig()["setup_complete"]?.GetValue<bool>() ?? false;

    private void ShowPage(int i)
    {
        _page = i;
        for (int k = 0; k < _pages.Count; k++)
            _pages[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = $"LSearch setup · Step {i + 1} of {_pages.Count}";
        BackButton.Visibility = i == 0 || _pages[i] == PageDone ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _pages[i] == PageDone ? "Finish" : i == _pages.Count - 2 ? "Save" : "Next";
        if (_pages[i] == PageDone) SummaryText.Text = Summary();
    }

    private string Summary()
    {
        var lines = new List<string>
        {
            $"• Indexing contents of {_folders.Count} folder{(_folders.Count == 1 ? "" : "s")}",
            AllDrivesToggle.IsOn ? "• Finding files by name on all drives" : "• Finding files only inside those folders",
            HistoryToggle.IsOn ? "• Including browser history" : "• Browser history off",
        };
        if (_showLla) lines.Add(LlaToggle.IsOn ? "• LLA on" : "• LLA off");
        lines.Add("\nLSearch will now restart its index in the background.");
        return string.Join("\n", lines);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowPage(Math.Max(0, _page - 1));

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_pages[_page] == PageDone) { Close(); return; }
        if (_page == _pages.Count - 2) Save();
        ShowPage(_page + 1);
    }

    // LLA is published on Hugging Face; llama.cpp's CPU build runs it on any x64 PC.
    private const string LlaUrl = "https://huggingface.co/scutoid/2x4B/resolve/main/LLA-2x4B-Q4_K_M.gguf";
    private const string LlamaServerZip = "https://github.com/ggml-org/llama.cpp/releases/download/b11559/llama-b11559-bin-win-cpu-x64.zip";
    private static readonly string AppLocal = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LSearch");
    private static readonly string LlaModelPath = Path.Combine(AppLocal, "models", "LLA-2x4B-Q4_K_M.gguf");
    private static readonly string LlamaServerExe = Path.Combine(AppLocal, "llama", "llama-server.exe");
    private bool _downloading;

    private void LlaToggle_Toggled(object sender, RoutedEventArgs e) => UpdateLlaDownload();

    private void UpdateLlaDownload()
    {
        var configured = _cfg["llm"]?["model_path"]?.GetValue<string>() is { Length: > 0 } mp && File.Exists(mp);
        var have = configured || (File.Exists(LlaModelPath) && File.Exists(LlamaServerExe));
        LlaDownload.Visibility = LlaToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (_downloading) return;
        LlaDownloadButton.Visibility = have ? Visibility.Collapsed : Visibility.Visible;
        LlaDownloadText.Text = have ? "✓ LLA is installed." : "";
    }

    private async void LlaDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloading = true;
        LlaDownloadButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        LlaProgress.Visibility = Visibility.Visible;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            if (!File.Exists(LlamaServerExe))
            {
                LlaDownloadText.Text = "Downloading llama.cpp…";
                var zip = Path.Combine(Path.GetTempPath(), "lsearch-llama.zip");
                await DownloadAsync(http, LlamaServerZip, zip);
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, Path.GetDirectoryName(LlamaServerExe)!, true);
                File.Delete(zip);
            }
            await DownloadAsync(http, LlaUrl, LlaModelPath);
            _downloading = false;
            UpdateLlaDownload();
        }
        catch (Exception ex)
        {
            _downloading = false;
            LlaDownloadButton.IsEnabled = true;
            LlaDownloadButton.Content = "Resume download";
            LlaDownloadText.Text = "Download failed: " + ex.Message;
        }
        finally
        {
            NextButton.IsEnabled = true;
            LlaProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Resumable download via a .part file, so an interrupted 4 GB download continues.</summary>
    private async Task DownloadAsync(HttpClient http, string url, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server ignored Range
        long total = have + (resp.Content.Headers.ContentLength ?? 0);
        using (var src = await resp.Content.ReadAsStreamAsync())
        using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            var buf = new byte[1 << 20];
            var name = Path.GetFileName(dest);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long done = have, lastDone = have;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (sw.ElapsedMilliseconds >= 500)
                {
                    double mbps = (done - lastDone) / 1048576.0 / sw.Elapsed.TotalSeconds;
                    lastDone = done; sw.Restart();
                    LlaProgress.Value = total > 0 ? (double)done / total : 0;
                    LlaDownloadText.Text = $"{name}: {done / 1073741824.0:0.00} / {total / 1073741824.0:0.00} GB · {mbps:0.0} MB/s";
                }
            }
        }
        File.Move(part, dest, true);
    }

    private void Save()
    {
        _cfg["content_roots"] = new JsonArray(_folders.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        _cfg["index_all_drives"] = AllDrivesToggle.IsOn;
        _cfg["browser_history"] = HistoryToggle.IsOn;
        var llm = _cfg["llm"] as JsonObject ?? new JsonObject();
        if (_showLla)
        {
            llm["enabled"] = LlaToggle.IsOn;
            if (LlaToggle.IsOn) llm["display_name"] = "LLA";
            if (LlaToggle.IsOn && File.Exists(LlaModelPath) && !File.Exists(LlaModelPath + ".part"))
            {   // run the downloaded model locally with llama-server
                llm["model_path"] = LlaModelPath;
                llm["model"] = "LLA";
                llm["base_url"] = "http://127.0.0.1:1114/v1";
            }
        }
        else llm["enabled"] = false; // under 16 GB: LLA is never offered
        _cfg["llm"] = llm;
        _cfg["setup_complete"] = true;
        // Appearance may have changed on disk while the wizard was open.
        if (LoadConfig()["appearance"] is { } look) _cfg["appearance"] = look.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, _cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Completed?.Invoke();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null && !_folders.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
            _folders.Add(folder.Path);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is string s) _folders.Remove(s);
    }
}
