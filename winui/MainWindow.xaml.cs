using System.Text.Json;
using System.Text;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace LSearch;

public sealed partial class MainWindow : Window
{
    private const int WidthDip = 760, BarDip = 60, ExpandedDip = 500;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Backend _backend = new();
    private readonly ObservableCollection<ResultItem> _items = new();
    private readonly Dictionary<string, ImageSource> _iconCache = new();
    private readonly DispatcherQueueTimer _debounce, _statusTimer;
    private readonly Native.SubclassProc _wndProc; // kept alive for the native callback
    private Native.NOTIFYICONDATA _tray;
    private CancellationTokenSource? _searchCts, _askCts;
    private string? _llmName;
    private string _hotkey;
    private bool _quitting;

    /// <summary>Debug aid (--pin): keep the window open when it loses focus.</summary>
    public bool Pinned { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow;

        _appWindow.IsShownInSwitchers = false;
        _appWindow.Closing += (_, e) => { if (!_quitting) { e.Cancel = true; Hide(); } };
        Native.RoundCorners(_hwnd);
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && !Pinned) Hide(); };

        Results.ItemsSource = _items;
        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(110);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => _ = RunSearchAsync();
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += (_, _) => _ = RefreshStatusAsync();

        _hotkey = Native.ReadHotkeySetting();
        _wndProc = WndProc;
        Native.SetWindowSubclass(_hwnd, _wndProc, 1, IntPtr.Zero);
        var (mods, vk) = Native.ParseHotkey(_hotkey);
        bool hotkeyOk = Native.RegisterHotKey(_hwnd, 1, mods, vk);
        _tray = Native.AddTrayIcon(_hwnd, hotkeyOk ? $"LSearch — {Pretty(_hotkey)}" : $"LSearch — {_hotkey} is taken; edit config.json");
        Resize(false);
        _ = StartBackendAsync();
    }

    private static string Pretty(string hk) =>
        string.Join("+", hk.Split('+').Select(p => p.Length <= 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]));

    private async Task StartBackendAsync()
    {
        try
        {
            await _backend.EnsureRunningAsync();
            for (int i = 0; i < 60 && await _backend.StatusAsync() == null; i++) await Task.Delay(500);
            await RefreshStatusAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task RefreshStatusAsync()
    {
        var s = await _backend.StatusAsync();
        if (s == null) { StatusText.Text = "Starting search backend…"; return; }
        StatusText.Text = s.Status;
        _llmName = s.Llm;
        HintsText.Text = "↵ Open    Ctrl+↵ Show in Explorer" + (_llmName != null ? $"    Tab Fix with {_llmName}" : "");
    }

    // ---- window plumbing ----------------------------------------------------
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData)
    {
        if (msg == Native.WM_HOTKEY) Toggle();
        else if (msg == Native.WM_APP_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            if (ev == Native.WM_LBUTTONUP) Toggle();
            else if (ev == Native.WM_RBUTTONUP)
            {
                switch (Native.ShowTrayMenu(_hwnd, "Search", "Rebuild index now", "Setup…", "Open config", null, "Quit LSearch"))
                {
                    case 1: Toggle(); break;
                    case 2: _ = _backend.ReindexAsync(); break;
                    case 3: OpenSetup(); break;
                    case 4: _ = Launcher.LaunchUriAsync(new Uri(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LSearch", "config.json"))); break;
                    case 6: Quit(); break;
                }
            }
        }
        return Native.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private SetupWindow? _setup;

    public void OpenSetup()
    {
        if (_setup != null) { _setup.Activate(); return; }
        _setup = new SetupWindow();
        _setup.Completed += () => _ = RestartBackendAsync();
        _setup.Closed += (_, _) => _setup = null;
        _setup.Activate();
    }

    private async Task RestartBackendAsync()
    {
        StatusText.Text = "Applying settings…";
        await _backend.RestartAsync();
        for (int i = 0; i < 60 && await _backend.StatusAsync() == null; i++) await Task.Delay(500);
        await RefreshStatusAsync();
    }

    private double Scale => Native.GetDpiForWindow(_hwnd) / 96.0;

    private void Resize(bool expanded) =>
        _appWindow.Resize(new SizeInt32((int)(WidthDip * Scale), (int)((expanded ? ExpandedDip : BarDip) * Scale)));

    public void Toggle()
    {
        if (_appWindow.IsVisible) { Hide(); return; }
        Native.GetCursorPos(out var p);
        var area = DisplayArea.GetFromPoint(new PointInt32(p.X, p.Y), DisplayAreaFallback.Nearest).WorkArea;
        Resize(Body.Visibility == Visibility.Visible);
        _appWindow.Move(new PointInt32(area.X + (area.Width - _appWindow.Size.Width) / 2, area.Y + (int)(area.Height * 0.2)));
        _appWindow.Show();
        Native.MakeTopmost(_hwnd);
        Activate();
        Native.SetForegroundWindow(_hwnd);
        Query.Focus(FocusState.Programmatic);
        Query.SelectAll();
        _statusTimer.Start();
        _ = RefreshStatusAsync();
    }

    private void ConfigurePresenter()
    {
        var presenter = (OverlappedPresenter)_appWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        Native.RoundCorners(_hwnd);
    }

    /// <summary>WinUI only builds the visual tree once a window is activated, so activate
    /// once at startup, then hide (or show at the Spotlight position) once content has loaded.</summary>
    public void Start(bool show, string? query)
    {
        Root.Loaded += (_, _) =>
        {
            ConfigurePresenter();
            _appWindow.Hide();
            if (query != null) Query.Text = query;
            if (show) Toggle();
            if (!SetupWindow.SetupComplete() || Environment.GetCommandLineArgs().Contains("--setup")) OpenSetup();
        };
        _appWindow.Move(new PointInt32(-32000, -32000));
        Activate();
    }

    private void Hide()
    {
        _statusTimer.Stop();
        _appWindow.Hide();
    }

    private void Quit()
    {
        _quitting = true;
        Native.UnregisterHotKey(_hwnd, 1);
        Native.RemoveTrayIcon(ref _tray);
        _backend.Dispose();
        Close();
        Application.Current.Exit();
    }

    // ---- search ----------------------------------------------------------
    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        CancelAsk();
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var q = Query.Text.Trim();
        if (q.Length == 0) { _items.Clear(); SetExpanded(false); return; }
        // Two passes: instant keyword + embedding results first, then (for sentence-like queries)
        // the slower cross-encoder reranked order replaces them.
        bool sentence = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
        foreach (var rerank in sentence ? new[] { false, true } : new[] { false })
        {
            SearchResponse? r;
            try { r = await _backend.SearchAsync(q, rerank, cts.Token); }
            catch (OperationCanceledException) { return; }
            catch { StatusText.Text = "Search backend not reachable"; SetExpanded(true); return; }
            if (cts.IsCancellationRequested || r == null) return;
            Render(r, q);
        }
    }

    private void Render(SearchResponse r, string q)
    {
        _items.Clear();
        if (r.Calc != null)
        {
            _items.Add(ResultItem.Header("Calculator"));
            _items.Add(new ResultItem { Type = ItemType.Calc, Title = $"= {r.Calc}", Subtitle = q, Value = r.Calc, Glyph = "" });
        }
        if (r.Top != null)
        {
            _items.Add(ResultItem.Header("Top Hit"));
            _items.Add(FileItem(r.Top));
        }
        if (_llmName != null && r.Task != null)
        {
            _items.Add(ResultItem.Header(_llmName));
            _items.Add(new ResultItem
            {
                Type = ItemType.Task, Title = $"Fix {System.IO.Path.GetFileName(r.Task.Target)}",
                Subtitle = r.Task.Target, Value = r.Task.Target, Glyph = "", GlyphBrush = ResultItem.Purple,
            });
        }
        foreach (var s in r.Sections)
        {
            _items.Add(ResultItem.Header(s.Title));
            foreach (var h in s.Items) _items.Add(FileItem(h));
        }
        SelectFirst();
        SetExpanded(_items.Count > 0);
    }

    private ResultItem FileItem(FileHit h)
    {
        if (h.Kind == "web")
        {
            var uri = Uri.TryCreate(h.Path, UriKind.Absolute, out var u) ? u : null;
            return new ResultItem
            {
                Type = ItemType.File, Hit = h, Title = h.Name, Glyph = "",
                Subtitle = uri != null ? uri.Host + uri.AbsolutePath.TrimEnd('/') : h.Path,
            };
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var item = new ResultItem
        {
            Type = ItemType.File, Hit = h,
            Title = h.Kind == "app" ? Path.GetFileNameWithoutExtension(h.Name) : h.Name,
            Subtitle = h.Kind == "app" ? "Application" : (Path.GetDirectoryName(h.Path) ?? "").Replace(home, "~"),
        };
        _ = SetIconAsync(item, h);
        return item;
    }

    private async Task SetIconAsync(ResultItem item, FileHit h) => item.Icon = await LoadIconAsync(h, 32);

    private async Task<ImageSource?> LoadIconAsync(FileHit h, uint size)
    {
        var ext = Path.GetExtension(h.Path).ToLowerInvariant();
        var key = (h.Kind is "app" or "folder" || ext is ".exe" or ".lnk" ? h.Path : ext) + "|" + size;
        if (_iconCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            StorageItemThumbnail thumb = h.Kind == "folder"
                ? await (await StorageFolder.GetFolderFromPathAsync(h.Path)).GetThumbnailAsync(ThumbnailMode.SingleItem, size)
                : await (await StorageFile.GetFileFromPathAsync(h.Path)).GetThumbnailAsync(
                    h.Kind == "image" && size > 64 ? ThumbnailMode.PicturesView : ThumbnailMode.SingleItem, size);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(thumb);
            return _iconCache[key] = bmp;
        }
        catch { return null; }
    }

    private void SetExpanded(bool on)
    {
        Body.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Resize(on);
    }

    private void SelectFirst()
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Type != ItemType.Header) { Results.SelectedIndex = i; return; }
    }

    private void Move(int step)
    {
        for (int i = Results.SelectedIndex + step; i >= 0 && i < _items.Count; i += step)
            if (_items[i].Type != ItemType.Header)
            {
                Results.SelectedIndex = i;
                Results.ScrollIntoView(_items[i]);
                return;
            }
    }

    // ---- list / preview -------------------------------------------------------
    private void Results_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        bool header = args.Item is ResultItem { Type: ItemType.Header };
        args.ItemContainer.IsHitTestVisible = !header;
        args.ItemContainer.MinHeight = header ? 24 : 40;
    }

    private void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => _ = ActivateAsync(false);

    private async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is not ResultItem it) return;
        if (it.Type == ItemType.Header) { SelectFirst(); return; }
        bool ask = it.Type is ItemType.Ask or ItemType.Task;
        AiView.Visibility = ask ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = ask ? Visibility.Collapsed : Visibility.Visible;
        if (ask)
        {
            AiTitle.Text = _llmName ?? "";
            if (_askCts == null) AiText.Text = $"Press Tab or ↵ and {_llmName}'s agents will edit\n{it.Value}\nA .lsearch.bak backup is kept.";
            return;
        }
        PvIcon.Source = null;
        if (it.Type == ItemType.Calc)
        {
            PvGlyph.Glyph = "";
            PvName.Text = it.Value;
            PvKind.Text = "Calculator · ↵ copies the result";
            PvSnippet.Text = "";
            PvMeta.Text = it.Subtitle;
            return;
        }
        var h = it.Hit!;
        var mod = DateTimeOffset.FromUnixTimeMilliseconds((long)((h.Mtime ?? 0) * 1000)).LocalDateTime;
        if (h.Kind == "web")
        {
            PvGlyph.Glyph = "";
            PvName.Text = it.Title;
            PvKind.Text = "Web Page · " + h.Snippet;
            PvSnippet.Text = h.Path;
            PvMeta.Text = $"Last visited   {mod:MMM d, yyyy 'at' h:mm tt}";
            return;
        }
        PvGlyph.Glyph = "";
        PvName.Text = it.Title;
        var kind = h.Kind switch { "app" => "Application", "folder" => "Folder", var k => char.ToUpperInvariant(k[0]) + k[1..] };
        PvKind.Text = h.Kind is "app" or "folder" ? kind : $"{kind} · {HumanSize(h.Size ?? 0)}";
        PvSnippet.Text = h.Snippet ?? "";
        PvMeta.Text = $"Modified   {mod:MMM d, yyyy 'at' h:mm tt}\nWhere   {Path.GetDirectoryName(h.Path)}";
        var big = await LoadIconAsync(h, 96);
        if (Results.SelectedItem == it) PvIcon.Source = big;
    }

    private static string HumanSize(double n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
        return u == 0 ? $"{n:0} B" : $"{n:0.0} {units[u]}";
    }

    // ---- keyboard / actions ---------------------------------------------------
    private void Query_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (Query.Text.Length > 0) Query.Text = ""; else Hide();
                break;
            case VirtualKey.Down: Move(1); break;
            case VirtualKey.Up: Move(-1); break;
            case VirtualKey.Enter: _ = ActivateAsync(ctrl); break;
            case VirtualKey.Tab when _items.Any(i => i.Type == ItemType.Task): _ = RunTaskAsync(); break;
            default: return;
        }
        e.Handled = true;
    }

    private async Task FlushSearchAsync()
    {
        if (!_debounce.IsRunning) return;
        _debounce.Stop();
        await RunSearchAsync();
    }

    private async Task ActivateAsync(bool reveal)
    {
        await FlushSearchAsync();
        if (Results.SelectedItem is not ResultItem it) return;
        switch (it.Type)
        {
            case ItemType.Ask:
                await AskAsync();
                return;
            case ItemType.Task:
                await RunTaskAsync();
                return;
            case ItemType.Calc:
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(it.Value!.Replace(",", ""));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                break;
            case ItemType.File:
                await _backend.OpenAsync(it.Hit!.Path, reveal);
                break;
        }
        Hide();
    }

    private async Task AskAsync()
    {
        await FlushSearchAsync();
        var q = Query.Text.Trim();
        if (q.Length == 0 || _llmName == null) return;
        CancelAsk();
        var askRow = _items.FirstOrDefault(i => i.Type == ItemType.Ask);
        var cts = _askCts = new CancellationTokenSource();
        if (askRow != null) Results.SelectedItem = askRow;
        AiView.Visibility = Visibility.Visible;
        Preview.Visibility = Visibility.Collapsed;
        AiTitle.Text = _llmName;
        AiText.Text = "Thinking…";
        var first = true;
        try
        {
            await foreach (var t in _backend.AskAsync(q, cts.Token))
            {
                if (first) { AiText.Text = ""; first = false; }
                AiText.Text += t;
                AiView.ChangeView(null, AiView.ScrollableHeight, null, true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) AiText.Text = $"Couldn't reach {_llmName}:\n{ex.Message}"; }
    }

    private async Task RunTaskAsync()
    {
        await FlushSearchAsync();
        var q = Query.Text.Trim();
        var row = _items.FirstOrDefault(i => i.Type == ItemType.Task);
        if (row == null) return;
        CancelAsk();
        var cts = _askCts = new CancellationTokenSource();
        Results.SelectedItem = row;
        AiView.Visibility = Visibility.Visible;
        Preview.Visibility = Visibility.Collapsed;
        AiTitle.Text = $"{_llmName} · {row.Title}";
        AiText.Text = "Starting…";
        var log = new StringBuilder();
        void Add(string s) { log.AppendLine(s).AppendLine(); AiText.Text = log.ToString(); AiView.ChangeView(null, AiView.ScrollableHeight, null, true); }
        string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        try
        {
            await foreach (var ev in _backend.TaskAsync(q, cts.Token))
            {
                var agent = Str(ev, "agent");
                switch (Str(ev, "type"))
                {
                    case "start": Add($"Working on {Str(ev, "target")}"); break;
                    case "thought": Add($"{agent} 💭 {Str(ev, "text")}"); break;
                    case "tool":
                        var args = ev.TryGetProperty("arg", out var a) ? a.ToString() : "";
                        Add($"{agent} → {Str(ev, "tool")} {(args.Length > 160 ? args[..160] + "…" : args)}");
                        break;
                    case "summary": Add($"{agent}: {Str(ev, "text")}"); break;
                    case "done":
                        var ok = ev.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                        Add((ok ? "✅ " : "⚠️ ") + Str(ev, "text"));
                        var diff = Str(ev, "diff");
                        if (diff.Length > 0) Add(diff);
                        break;
                    case "error": Add("❌ " + Str(ev, "text")); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) Add($"❌ Couldn't reach {_llmName}: {ex.Message}"); }
    }

    private void CancelAsk()
    {
        _askCts?.Cancel();
        _askCts = null;
    }
}
