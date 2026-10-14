using System.Text.Json;
using System.Text;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
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

namespace WinSpotlight;

public sealed partial class MainWindow : Window
{
    // The window is transparent; the glass panel sits inside it with room for its shadow.
    private const int Gutter = 32, WidthDip = 720 + 2 * Gutter, BarDip = 58 + 2 * Gutter, ExpandedDip = 520 + 2 * Gutter;
    private const double PillRadius = 29, PanelRadius = 24;

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

    private Appearance _look;
    private readonly SolidColorBrush _accent;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private SpriteVisual? _shadowVisual;
    private byte[]? _capture;
    private int _capW, _capH;
    private bool _loadingLook = true; // until the settings controls hold the loaded values

    /// <summary>Debug aid (--pin): keep the window open when it loses focus.</summary>
    public bool Pinned { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow;

        _appWindow.IsShownInSwitchers = false;
        _appWindow.Closing += (_, e) => { if (!_quitting) { e.Cancel = true; Hide(); } };
        Native.RoundCorners(_hwnd, false);
        Native.EnableAlpha(_hwnd);

        _look = Appearance.Load();
        _accent = new SolidColorBrush(_look.AccentColor);
        foreach (var key in new[] {
            "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed",
            "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
            "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" })
            Root.Resources[key] = _accent;
        var white = new SolidColorBrush(Colors.White);
        foreach (var key in new[] { "ListViewItemForegroundSelected", "ListViewItemForegroundSelectedPointerOver", "ListViewItemForegroundSelectedPressed" })
            Root.Resources[key] = white;
        foreach (var key in new[] { "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed" })
            Root.Resources[key] = white;
        Root.Resources["ListViewItemSelectionIndicatorVisualEnabled"] = false;
        Root.Resources["ListViewItemBackgroundPointerOver"] = new SolidColorBrush(ColorHelper.FromArgb(0x1C, 0x80, 0x80, 0x80));
        Query.SelectionHighlightColor = _accent;
        BuildAccentDots();
        // Follow the system light/dark switch while in Auto.
        _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyLook);
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => _look.Save();
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
        _tray = Native.AddTrayIcon(_hwnd, hotkeyOk ? $"WinSpotlight — {Pretty(_hotkey)}" : $"WinSpotlight — {L.HotkeyTaken(_hotkey)}");
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
        if (s == null) { StatusText.Text = L.StartingBackend; return; }
        StatusText.Text = s.Status;
        _llmName = s.Llm;
        UpdateHints();
    }

    private void UpdateHints() => HintsText.Text = SettingsOpen
        ? L.HintsSettings
        : L.Hints + (_llmName != null ? L.HintFix(_llmName) : "");

    // ---- window plumbing ----------------------------------------------------
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData)
    {
        // No non-client frame: the transparent client area is the whole window.
        if (msg == Native.WM_NCCALCSIZE && wParam != IntPtr.Zero) return IntPtr.Zero;
        if (msg == Native.WM_NCPAINT) return IntPtr.Zero;
        if (msg == Native.WM_NCACTIVATE) return new IntPtr(1);
        if (msg == Native.WM_HOTKEY) Toggle();
        else if (msg == Native.WM_APP_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            if (ev == Native.WM_LBUTTONUP) Toggle();
            else if (ev == Native.WM_RBUTTONUP)
            {
                switch (Native.ShowTrayMenu(_hwnd, L.MenuSearch, L.MenuAppearance, L.MenuReindex, L.MenuSetup, L.MenuConfig, null, L.MenuQuit))
                {
                    case 1: Toggle(); break;
                    case 2: OpenAppearance(); break;
                    case 3: _ = _backend.ReindexAsync(); break;
                    case 4: OpenSetup(); break;
                    case 5: _ = Launcher.LaunchUriAsync(new Uri(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSpotlight", "config.json"))); break;
                    case 7: Quit(); break;
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
        StatusText.Text = L.ApplyingSettings;
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
        int x = area.X + (area.Width - _appWindow.Size.Width) / 2, y = area.Y + (int)(area.Height * 0.2) - (int)(Gutter * Scale);
        _appWindow.Move(new PointInt32(x, y));
        CaptureBackdrop(x, y);
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
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        Native.RoundCorners(_hwnd, false);
        Native.EnableAlpha(_hwnd);
        Native.StripFrame(_hwnd);
        Native.RefreshFrame(_hwnd);
    }

    /// <summary>WinUI only builds the visual tree once a window is activated, so activate
    /// once at startup, then hide (or show at the Spotlight position) once content has loaded.</summary>
    public void Start(bool show, string? query)
    {
        Root.Loaded += (_, _) =>
        {
            ConfigurePresenter();
            CreateShadow();
            LoadLookControls();
            ApplyLook();
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
        if (SettingsOpen) ShowSettings(false);
    }

    /// <summary>Clicks on the transparent margin around the glass dismiss it, like clicking away.</summary>
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Root)) Hide();
    }

    private void Quit()
    {
        _quitting = true;
        Native.UnregisterHotKey(_hwnd, 1);
        Native.RemoveTrayIcon(ref _tray);
        if (_saveTimer.IsRunning) _look.Save();
        _backend.Dispose();
        Close();
        Application.Current.Exit();
    }

    // ---- search ----------------------------------------------------------
    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SettingsOpen) ShowSettings(false);
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
            catch { StatusText.Text = L.BackendUnreachable; SetExpanded(true); return; }
            if (cts.IsCancellationRequested || r == null) return;
            Render(r, q);
        }
    }

    private void Render(SearchResponse r, string q)
    {
        _items.Clear();
        if (r.Calc != null)
        {
            _items.Add(ResultItem.Header(L.Calculator));
            _items.Add(new ResultItem { Type = ItemType.Calc, Title = $"= {r.Calc}", Subtitle = q, Value = r.Calc, Glyph = "" });
        }
        if (r.Top != null)
        {
            _items.Add(ResultItem.Header(L.TopHit));
            _items.Add(FileItem(r.Top));
        }
        if (_llmName != null && r.Task != null)
        {
            _items.Add(ResultItem.Header(_llmName));
            _items.Add(new ResultItem
            {
                Type = ItemType.Task, Title = L.Fix(System.IO.Path.GetFileName(r.Task.Target)),
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
            Subtitle = h.Kind == "app" ? L.Application : (Path.GetDirectoryName(h.Path) ?? "").Replace(home, "~"),
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
        // A capsule while it is just the search field, a rounded card once results show.
        double r = on ? PanelRadius : PillRadius;
        Glass.CornerRadius = Rim.CornerRadius = new CornerRadius(r);
        ShadowShape.RadiusX = ShadowShape.RadiusY = r;
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
        ShowPane(ask);
        AiView.Visibility = ask ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = ask ? Visibility.Collapsed : Visibility.Visible;
        if (ask)
        {
            AiTitle.Text = _llmName ?? "";
            if (_askCts == null) AiText.Text = L.AgentIntro(_llmName ?? "", it.Value ?? "");
            return;
        }
        PvIcon.Source = null;
        if (it.Type == ItemType.Calc)
        {
            PvGlyph.Glyph = "";
            PvName.Text = it.Value;
            PvKind.Text = L.CalcHint;
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
            PvKind.Text = $"{L.WebPage} · {h.Snippet}";
            PvSnippet.Text = h.Path;
            PvMeta.Text = $"{L.LastVisited}   {L.Date(mod)}";
            return;
        }
        PvGlyph.Glyph = "";
        PvName.Text = it.Title;
        var kind = L.Kind(h.Kind);
        PvKind.Text = h.Kind is "app" or "folder" ? kind : $"{kind} · {HumanSize(h.Size ?? 0)}";
        PvSnippet.Text = h.Snippet ?? "";
        PvMeta.Text = $"{L.Modified}   {L.Date(mod)}\n{L.Where}   {Path.GetDirectoryName(h.Path)}";
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
        const VirtualKey comma = (VirtualKey)188; // VK_OEM_COMMA: Ctrl+, opens Appearance, like Cmd+, on a Mac
        if (SettingsOpen && e.Key != VirtualKey.Escape && !(ctrl && e.Key == comma)) return;
        switch (e.Key)
        {
            case VirtualKey.Escape when SettingsOpen: ShowSettings(false); break;
            case VirtualKey.Escape:
                if (Query.Text.Length > 0) Query.Text = ""; else Hide();
                break;
            case comma when ctrl: ShowSettings(!SettingsOpen); break;
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
        ShowPane(true);
        AiView.Visibility = Visibility.Visible;
        Preview.Visibility = Visibility.Collapsed;
        AiTitle.Text = _llmName;
        AiText.Text = L.Thinking;
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
        catch (Exception ex) { if (!cts.IsCancellationRequested) AiText.Text = $"{L.CantReach(_llmName)}:\n{ex.Message}"; }
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
        ShowPane(true);
        AiView.Visibility = Visibility.Visible;
        Preview.Visibility = Visibility.Collapsed;
        AiTitle.Text = $"{_llmName} · {row.Title}";
        AiText.Text = L.Starting;
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
                    case "start": Add(L.WorkingOn(Str(ev, "target"))); break;
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
        catch (Exception ex) { if (!cts.IsCancellationRequested) Add($"❌ {L.CantReach(_llmName ?? "")}: {ex.Message}"); }
    }

    private void CancelAsk()
    {
        _askCts?.Cancel();
        _askCts = null;
    }

    // ---- appearance -------------------------------------------------------------
    private bool SettingsOpen => SettingsView.Visibility == Visibility.Visible;

    public void OpenAppearance()
    {
        if (!_appWindow.IsVisible) Toggle();
        ShowSettings(true);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings(!SettingsOpen);
        if (!SettingsOpen) Query.Focus(FocusState.Programmatic);
    }

    private void ShowSettings(bool on)
    {
        SettingsView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ResultsArea.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        UpdateHints();
        SetExpanded(on || (_items.Count > 0 && Query.Text.Trim().Length > 0));
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || !SettingsOpen) return;
        ShowSettings(false);
        Query.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    /// <summary>Preview pane on the right; LLM answers always get it, files only if enabled.</summary>
    private void ShowPane(bool ai)
    {
        bool show = _look.Preview || ai;
        ResultsCol.Width = show ? new GridLength(290) : new GridLength(1, GridUnitType.Star);
        ResultsArea.ColumnDefinitions[2].Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PreviewDivider.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Snapshots the desktop behind the (still hidden) window, at its expanded size,
    /// so the glass can show it frosted. Taken on every show, since what's behind changes.</summary>
    private void CaptureBackdrop(int x, int y)
    {
        _capW = (int)(WidthDip * Scale);
        _capH = (int)(ExpandedDip * Scale);
        _capture = Native.CaptureScreen(x, y, _capW, _capH);
        Backdrop.Width = WidthDip;
        Backdrop.Height = ExpandedDip;
        RenderBackdrop();
        ApplyLook();
    }

    private void RenderBackdrop()
    {
        if (_capture == null) { Backdrop.Source = null; return; }
        var (px, w, h) = Frost.Process(_capture, _capW, _capH, _look.Blur * 0.5 * Scale, 1 + _look.Vibrancy / 100.0);
        var bmp = new WriteableBitmap(w, h);
        using (var s = bmp.PixelBuffer.AsStream()) s.Write(px, 0, px.Length);
        bmp.Invalidate();
        Backdrop.Source = bmp;
    }

    private void CreateShadow()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(ShadowHost).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 44;
        shadow.Offset = new Vector3(0, 16, 0);
        shadow.Color = Colors.Black;
        shadow.Mask = ShadowShape.GetAlphaMask();
        var visual = _shadowVisual = compositor.CreateSpriteVisual();
        visual.Shadow = shadow;
        visual.Size = new Vector2((float)ShadowHost.ActualWidth, (float)ShadowHost.ActualHeight);
        ShadowHost.SizeChanged += (_, e) => visual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, visual);
    }

    private bool SystemIsDark() => _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R < 128;

    private static Windows.UI.Color Rgba(byte a, byte r, byte g, byte b) => ColorHelper.FromArgb(a, r, g, b);

    private void ApplyLook()
    {
        // Only the glass is themed: a Light theme on the window root makes WinUI paint the
        // transparent margin around it opaque white.
        bool dark = _look.Theme switch { "light" => false, "dark" => true, _ => SystemIsDark() };
        Root.RequestedTheme = ElementTheme.Dark;
        Glass.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        // Without a capture there is nothing to see through, so the tint goes opaque.
        byte a = (byte)(255 * (_capture == null ? 1 : 1 - _look.Transparency / 100.0 * (dark ? 0.85 : 0.7)));
        Tint.Background = new SolidColorBrush(dark ? Rgba(a, 0x1E, 0x1E, 0x20) : Rgba(a, 0xF6, 0xF6, 0xF8));

        if (_look.GlassEdge)
        {
            var rim = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
            rim.GradientStops.Add(new GradientStop { Offset = 0, Color = dark ? Rgba(0x66, 255, 255, 255) : Rgba(0xFF, 255, 255, 255) });
            rim.GradientStops.Add(new GradientStop { Offset = 0.4, Color = dark ? Rgba(0x16, 255, 255, 255) : Rgba(0x80, 255, 255, 255) });
            rim.GradientStops.Add(new GradientStop { Offset = 1, Color = dark ? Rgba(0x2A, 255, 255, 255) : Rgba(0xB0, 255, 255, 255) });
            Rim.BorderBrush = rim;
        }
        else Rim.BorderBrush = new SolidColorBrush(dark ? Rgba(0x22, 255, 255, 255) : Rgba(0x18, 0, 0, 0));

        if (_shadowVisual is { Shadow: DropShadow sh })
        {
            _shadowVisual.IsVisible = _look.Shadow;
            sh.Opacity = dark ? 0.55f : 0.28f;
        }

        _accent.Color = _look.AccentColor;
        foreach (var dot in AccentDots.Children.OfType<Border>())
        {
            bool sel = (string)dot.Tag == _look.Accent;
            dot.BorderBrush = new SolidColorBrush(sel ? (dark ? Rgba(0xD0, 255, 255, 255) : Rgba(0x60, 0, 0, 0)) : Colors.Transparent);
            dot.Child.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
        }
        ThemeTrack.Background = new SolidColorBrush(dark ? Rgba(0x1F, 255, 255, 255) : Rgba(0x12, 0, 0, 0));
        foreach (var b in new[] { ThemeSystem, ThemeLight, ThemeDark })
            b.Background = new SolidColorBrush((string)b.Tag == _look.Theme ? (dark ? Rgba(255, 0x63, 0x63, 0x66) : Colors.White) : Colors.Transparent);

        ShowPane(Results.SelectedItem is ResultItem { Type: ItemType.Ask or ItemType.Task });
    }

    private void BuildAccentDots()
    {
        foreach (var (key, _, color) in Appearance.Accents)
        {
            var dot = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(color), Tag = key,
                Child = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Colors.White) },
            };
            ToolTipService.SetToolTip(dot, L.AccentName(key));
            dot.Tapped += (_, _) => { _look.Accent = key; ApplyLook(); SaveLookSoon(); };
            AccentDots.Children.Add(dot);
        }
    }

    private void LoadLookControls()
    {
        _loadingLook = true;
        BlurSlider.Value = _look.Blur;
        TransparencySlider.Value = _look.Transparency;
        VibrancySlider.Value = _look.Vibrancy;
        EdgeSwitch.IsOn = _look.GlassEdge;
        ShadowSwitch.IsOn = _look.Shadow;
        PreviewSwitch.IsOn = _look.Preview;
        _loadingLook = false;
        UpdateLookValues();
    }

    private void UpdateLookValues()
    {
        BlurValue.Text = $"{_look.Blur}%";
        TransparencyValue.Text = $"{_look.Transparency}%";
        VibrancyValue.Text = $"{_look.Vibrancy}%";
    }

    private void LookSlider_Changed(object sender, RangeBaseValueChangedEventArgs e) => ReadLookControls();

    private void LookSwitch_Toggled(object sender, RoutedEventArgs e) => ReadLookControls();

    private void ReadLookControls()
    {
        if (_loadingLook) return;
        int blur = (int)BlurSlider.Value, vibrancy = (int)VibrancySlider.Value;
        bool refrost = blur != _look.Blur || vibrancy != _look.Vibrancy;
        _look.Blur = blur;
        _look.Vibrancy = vibrancy;
        _look.Transparency = (int)TransparencySlider.Value;
        _look.GlassEdge = EdgeSwitch.IsOn;
        _look.Shadow = ShadowSwitch.IsOn;
        _look.Preview = PreviewSwitch.IsOn;
        UpdateLookValues();
        if (refrost) RenderBackdrop();
        ApplyLook();
        SaveLookSoon();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _look.Theme = (string)((FrameworkElement)sender).Tag;
        ApplyLook();
        SaveLookSoon();
    }

    private void ResetLook_Click(object sender, RoutedEventArgs e)
    {
        _look = new Appearance();
        LoadLookControls();
        RenderBackdrop();
        ApplyLook();
        SaveLookSoon();
    }

    private void SaveLookSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
