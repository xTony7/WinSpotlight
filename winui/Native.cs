using System.Runtime.InteropServices;
using System.Text.Json;

namespace LSearch;

/// <summary>Win32 plumbing: global hotkey, tray icon, window corner style, screen capture.</summary>
internal static partial class Native
{
    public const int WM_NCCALCSIZE = 0x0083, WM_HOTKEY = 0x0312, WM_APP_TRAY = 0x8001, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    public const uint MOD_NOREPEAT = 0x4000;

    public delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData);

    [LibraryImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, IntPtr id, IntPtr refData);
    [LibraryImport("comctl32.dll")]
    public static partial IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(IntPtr hWnd, int index);
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hWnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Makes Windows re-ask WM_NCCALCSIZE after the frame style changed.</summary>
    public static void RefreshFrame(IntPtr hwnd) =>
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE | NOMOVE | NOZORDER | NOACTIVATE | FRAMECHANGED

    public static void MakeTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // NOSIZE | NOMOVE | SHOWWINDOW
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")]
    public static partial IntPtr CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr menu);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint n);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA data);
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    public static void RoundCorners(IntPtr hwnd, bool round = true)
    {
        int pref = round ? 2 : 1; // DWMWCP_ROUND : DWMWCP_DONOTROUND
        DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
    }

    // ---- per-pixel transparent window ------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND { public uint dwFlags; public int fEnable; public IntPtr hRgnBlur; public int fTransitionOnMaximized; }
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND bb);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

    /// <summary>Lets the window's alpha channel through to the desktop. Blur-behind with an
    /// off-screen region turns on DWM's alpha composition without actually blurring anything.</summary>
    public static void EnableAlpha(IntPtr hwnd)
    {
        var rgn = CreateRectRgn(-2, -2, -1, -1);
        var bb = new DWM_BLURBEHIND { dwFlags = 0x1 | 0x2, fEnable = 1, hRgnBlur = rgn }; // ENABLE | BLURREGION
        DwmEnableBlurBehindWindow(hwnd, ref bb);
        DeleteObject(rgn);
        int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE: no Windows 11 border around the transparent window
        DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int)); // DWMWA_BORDER_COLOR
    }

    [StructLayout(LayoutKind.Sequential)] private struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions o, out IntPtr controller);
    private static IntPtr _dqController;

    /// <summary>Windows.UI.Composition (used for the window backdrop) needs a system DispatcherQueue.</summary>
    public static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null || _dqController != IntPtr.Zero) return;
        CreateDispatcherQueueController(new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 }, out _dqController);
    }

    // ---- screen capture --------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    /// <summary>BGRA pixels of a screen rectangle (what is behind the window while it is hidden).</summary>
    public static byte[]? CaptureScreen(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(mem, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bmp == IntPtr.Zero) return null;
            var old = SelectObject(mem, bmp);
            bool ok = BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            SelectObject(mem, old);
            if (!ok) return null;
            var px = new byte[w * h * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            return px;
        }
        finally
        {
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ---- hotkey -----------------------------------------------------------
    public static string ReadHotkeySetting()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LSearch", "config.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("hotkey").GetString() ?? "alt+space";
        }
        catch { return "alt+space"; }
    }

    public static (uint mods, uint vk) ParseHotkey(string spec)
    {
        uint mods = MOD_NOREPEAT, vk = 0;
        foreach (var part in spec.ToLowerInvariant().Replace(" ", "").Split('+'))
        {
            switch (part)
            {
                case "alt": mods |= 1; break;
                case "ctrl" or "control": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                case "space": vk = 0x20; break;
                case var f when f.Length > 1 && f[0] == 'f' && int.TryParse(f[1..], out var n): vk = (uint)(0x6F + n); break;
                case var c when c.Length == 1: vk = char.ToUpperInvariant(c[0]); break;
            }
        }
        return (mods, vk);
    }

    // ---- tray ----------------------------------------------------------------
    public static NOTIFYICONDATA AddTrayIcon(IntPtr hwnd, string tip)
    {
        ExtractIconExW("shell32.dll", 22, out _, out var small, 1); // magnifier
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = hwnd, uID = 1,
            uFlags = 0x1 | 0x2 | 0x4, // MESSAGE | ICON | TIP
            uCallbackMessage = WM_APP_TRAY, hIcon = small, szTip = tip, szInfo = "", szInfoTitle = "",
        };
        Shell_NotifyIconW(0, ref data); // NIM_ADD
        return data;
    }

    public static void RemoveTrayIcon(ref NOTIFYICONDATA data) => Shell_NotifyIconW(2, ref data);

    public static int ShowTrayMenu(IntPtr hwnd, params string?[] items)
    {
        var menu = CreatePopupMenu();
        for (int i = 0; i < items.Length; i++)
            AppendMenuW(menu, items[i] == null ? 0x800u : 0u, (nuint)(i + 1), items[i]); // MF_SEPARATOR
        GetCursorPos(out var p);
        SetForegroundWindow(hwnd);
        int cmd = TrackPopupMenu(menu, 0x100 | 0x20, p.X, p.Y, 0, hwnd, IntPtr.Zero); // RETURNCMD | BOTTOMALIGN
        DestroyMenu(menu);
        return cmd;
    }
}
