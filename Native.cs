using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LyricBar;

/// <summary>Win32 互操作：任务栏定位、窗口重定父、主题、托盘。</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // 任务栏
    internal const uint ABM_GETTASKBARPOS = 5;
    internal const uint ABM_GETSTATE = 4;
    internal const uint ABS_AUTOHIDE = 0x1;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    // 通知状态（全屏应用判定）
    internal const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    internal const int QUNS_PRESENTATION_MODE = 4;
    internal const int QUNS_BUSY = 2;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;

    // 窗口样式
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;

    internal const int WS_CHILD = 0x40000000;
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_VISIBLE = 0x10000000;
    internal const int WS_CLIPSIBLINGS = 0x04000000;

    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const int WS_EX_APPWINDOW = 0x00040000;

    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;

    internal static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const uint SWP_NOOWNERZORDER = 0x0200;

    internal const int GW_CHILD = 5;
    internal const int GW_HWNDNEXT = 2;
    internal const uint GA_ROOT = 2;

    // DWM
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // 托盘
    internal const uint NIM_ADD = 0;
    internal const uint NIM_MODIFY = 1;
    internal const uint NIM_DELETE = 2;
    internal const uint NIF_MESSAGE = 0x01;
    internal const uint NIF_ICON = 0x02;
    internal const uint NIF_TIP = 0x04;
    internal const uint MF_STRING = 0x0000;
    internal const uint MF_SEPARATOR = 0x0800;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint WM_APP = 0x8000;
    internal const int WM_NULL = 0x0000;
    internal const int WM_DESTROY = 0x0002;

    [DllImport("shell32.dll")]
    internal static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);

    /// <summary>托盘那个收消息的 HwndSource 挂在 HWND_MESSAGE 下，FindWindow 看不到它。</summary>
    internal static IntPtr FindMessageWindow(string title)
        => FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, title);

    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    internal const uint RDW_INVALIDATE = 0x0001;
    internal const uint RDW_ALLCHILDREN = 0x0080;
    internal const uint RDW_UPDATENOW = 0x0100;

    [DllImport("user32.dll")]
    internal static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(
        IntPtr hdcDest, int x, int y, int cx, int cy,
        IntPtr hdcSrc, int x1, int y1, uint rop);

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    /// <summary>
    /// 读屏幕上某点的真实像素。任务栏是 DWM 合成的，直接 GetPixel 桌面 DC 常拿到壁纸色，
    /// 所以用 BitBlt + CAPTUREBLT 抓合成后的画面。
    /// </summary>
    internal static bool TryScreenPixel(int x, int y, out System.Windows.Media.Color color)
    {
        color = default;

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;

        var mem = CreateCompatibleDC(screen);
        if (mem == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screen);
            return false;
        }

        var bmp = CreateCompatibleBitmap(screen, 1, 1);
        var old = IntPtr.Zero;
        var ok = false;

        try
        {
            if (bmp == IntPtr.Zero) return false;
            old = SelectObject(mem, bmp);

            if (!BitBlt(mem, 0, 0, 1, 1, screen, x, y, SRCCOPY | CAPTUREBLT)) return false;

            var c = GetPixel(mem, 0, 0);
            if (c == 0xFFFFFFFF) return false;

            color = System.Windows.Media.Color.FromRgb(
                (byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
            ok = true;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }

        return ok;
    }

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT pt);

    /// <summary>当前鼠标位置（屏幕物理像素）。</summary>
    internal static POINT CursorPos()
    {
        GetCursorPos(out var pt);
        return pt;
    }

    [DllImport("user32.dll")]
    internal static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    internal static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    /// <summary>从 exe/dll 里抠图标（托盘用小的那个，跟资源管理器一致）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    internal static extern bool SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("shcore.dll")]
    internal static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);

    [DllImport("kernel32.dll")]
    internal static extern uint GetLastError();

    [DllImport("shell32.dll")]
    internal static extern int SHQueryUserNotificationState(out int state);    // ------------------------------------------------------------ 辅助

    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    /// <summary>返回 0= unaware, 1= system, 2= per-monitor。</summary>
    internal static int ProcessDpiAwareness()
    {
        try
        {
            return GetProcessDpiAwareness(IntPtr.Zero, out var v) == 0 ? v : -1;
        }
        catch
        {
            return -1;
        }
    }

    internal static IntPtr FindTray() => FindWindow("Shell_TrayWnd", null);

    /// <summary>任务栏的自动隐藏 / 总在最前状态位。</summary>
    internal static uint AppBarState(IntPtr hwnd)
    {
        try
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
            return (uint)SHAppBarMessage(ABM_GETSTATE, ref data).ToInt64();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>前台是否全屏应用（全屏游戏 / 演示），此时应当让位。</summary>
    internal static bool IsFullscreenApp()
    {
        return UserNotificationState() is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    /// <summary>
    /// SHQueryUserNotificationState 的原始值。只认 3（D3D 全屏）和 4（演示模式）：
    /// 2 = QUNS_BUSY 在窗口切换、媒体全屏、通知弹出的瞬间都会翻一下，跟着它让位
    /// 就是「条突然消失一下又回来」。
    /// </summary>
    internal static int UserNotificationState()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return 0;
            return state;
        }
        catch
        {
            return 0;
        }
    }

    internal static void TryRoundCorners(IntPtr hwnd)
    {
        try
        {
            var pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
        }
    }

    internal static void TryMica(IntPtr hwnd, bool dark)
    {
        try
        {
            var v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
            var backdrop = 2; // DWMSBT_MAINWINDOW (Mica)
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
        catch
        {
        }
    }

    internal static void HideFromAltTab(IntPtr hwnd)
    {
        try
        {
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        }
        catch
        {
        }
    }

    internal static void NoActivate(IntPtr hwnd)
    {
        try
        {
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
        }
        catch
        {
        }
    }
}
