using System.Text;
using System.Windows.Automation;
using System.Windows.Media;

namespace LyricBar;

/// <summary>任务栏几何与配色探测。</summary>
public static class Taskbar
{
    public sealed record Layout(
        IntPtr Hwnd,
        int ClientWidth,
        int ClientHeight,
        int IconsRight,
        int TrayLeft,
        double Scale,
        bool AutoHide,
        bool Offscreen);

    /// <summary>读取任务栏结构。拿不到（explorer 正在重启等）返回 null。</summary>
    public static Layout? Probe()
    {
        var tray = Native.FindTray();
        if (tray == IntPtr.Zero || !Native.IsWindow(tray)) return null;

        if (!Native.GetClientRect(tray, out var cr) || cr.Right <= 0) return null;

        var iconsRight = 0;
        var trayLeft = cr.Right;

        var sb = new StringBuilder(128);
        var child = Native.GetWindow(tray, Native.GW_CHILD);
        var guard = 0;
        while (child != IntPtr.Zero && guard++ < 64)
        {
            sb.Clear();
            Native.GetClassName(child, sb, 128);
            var cls = sb.ToString();

            if (Native.GetWindowRect(child, out var r))
            {
                if (cls == "ReBarWindow32") iconsRight = Math.Max(iconsRight, r.Right);
                else if (cls == "TrayNotifyWnd") trayLeft = Math.Min(trayLeft, r.Left);
            }

            child = Native.GetWindow(child, Native.GW_HWNDNEXT);
        }

        // 老任务栏没有 ReBarWindow32，用 Start 按钮兜底
        if (iconsRight == 0)
        {
            child = Native.GetWindow(tray, Native.GW_CHILD);
            guard = 0;
            while (child != IntPtr.Zero && guard++ < 64)
            {
                sb.Clear();
                Native.GetClassName(child, sb, 128);
                if (sb.ToString() == "Start" && Native.GetWindowRect(child, out var r2))
                    iconsRight = Math.Max(iconsRight, r2.Right);
                child = Native.GetWindow(child, Native.GW_HWNDNEXT);
            }
        }

        if (trayLeft <= iconsRight) trayLeft = cr.Right;

        var dpi = Native.GetDpiForWindow(tray);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;

        // 自动隐藏时任务栏会被推到屏幕外/只剩一条边，此时歌词条也要一起藏起来
        var autoHide = (Native.AppBarState(tray) & Native.ABS_AUTOHIDE) != 0;
        var offscreen = false;
        if (Native.GetWindowRect(tray, out var tr))
        {
            var center = new Native.POINT { X = (tr.Left + tr.Right) / 2, Y = (tr.Top + tr.Bottom) / 2 };
            var mon = Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi))
            {
                var bottom = Math.Min(tr.Bottom, mi.rcMonitor.Bottom);
                var top = Math.Max(tr.Top, mi.rcMonitor.Top);
                var visibleHeight = bottom - top;
                offscreen = visibleHeight < 8;
            }
            else
            {
                offscreen = tr.Height < 8;
            }
        }

        return new Layout(tray, cr.Right, cr.Bottom, iconsRight, trayLeft, scale, autoHide, offscreen);
    }

    /// <summary>任务栏此刻是否可见（自动隐藏收起、或正被全屏应用盖住时为 false）。</summary>
    public static bool TaskbarVisible(Layout? lay)
    {
        if (lay is null) return false;
        if (lay.Offscreen) return false;
        if (!Native.IsWindowVisible(lay.Hwnd)) return false;
        if (Native.IsFullscreenApp()) return false;
        return true;
    }

    /// <summary>
    /// 任务栏布局是否已经探测到位。explorer 刚启动时 ReBarWindow32 还没建好，
    /// 会返回 icons=0 / trayLeft=整屏宽，此时不能定位，否则歌词条会先跳到最右边再跳回来。
    /// </summary>
    public static bool IsReady(Layout lay)
        => lay.TrayLeft > lay.IconsRight + 200 && lay.IconsRight > 0;

    /// <summary>计算条在任务栏客户区里的位置（物理像素）。widthDip 是条本身的宽度（DIP）。</summary>
    public static (int X, int Y, int W, int H) Place(Layout lay, Settings s, double widthDip = 720, int leftLimit = 0)
    {
        const int gap = 10;
        var bandLeft = lay.IconsRight + gap;
        // 量出来的图标右边界总是略微偏左（图标边缘有阴影/抗锯齿，像素比对容易提前收工），
        // 再留 16 DIP 余量，条才不会压到最后那个图标的边上。
        if (leftLimit > 0) bandLeft = Math.Max(bandLeft, leftLimit + gap + (int)Math.Round(16 * lay.Scale));

        var bandRight = lay.TrayLeft - gap;
        var avail = Math.Max(120, bandRight - bandLeft);

        var wanted = (int)Math.Round(widthDip * lay.Scale);

        var w = Math.Min(avail, Math.Max(120, wanted));
        var x = s.EmbAlign switch
        {
            0 => bandLeft,
            2 => bandRight - w,
            _ => bandLeft + (avail - w) / 2,
        };

        var h = Math.Max(16, lay.ClientHeight - 8);
        var y = (lay.ClientHeight - h) / 2;

        return (x, y, w, h);
    }

    /// <summary>
    /// 采样任务栏底色。抓的是 DWM 合成后的画面，所以开了 TranslucentTB 之类的透明工具时采到的是模糊壁纸色。
    /// fromFrac/toFrac 指定在图标区与托盘之间的空白段里采样的相对区间——歌词条贴在哪儿就在哪儿采，
    /// 否则透明任务栏下（壁纸是渐变的）采到的颜色和条所在位置对不上，会露出一块色差。
    /// </summary>
    public static bool TrySampleColor(Layout lay, double fromFrac, double toFrac, out Color color, out int spread)
    {
        color = default;
        spread = 0;

        if (!Native.GetWindowRect(lay.Hwnd, out var sr)) return false;

        var y = sr.Top + Math.Max(2, lay.ClientHeight / 2);
        var left = sr.Left + lay.IconsRight + 16;
        var right = sr.Left + lay.TrayLeft - 16;
        if (right <= left) return false;

        fromFrac = Math.Clamp(fromFrac, 0, 1);
        toFrac = Math.Clamp(toFrac, fromFrac, 1);

        // 多点采样后取中位数：任务栏上偶尔有悬停高亮、tooltip 之类的亮色，平均值会被带偏
        var samples = new List<Color>();
        const int n = 6;
        var span = right - left;

        for (var i = 0; i < n; i++)
        {
            var t = fromFrac + (toFrac - fromFrac) * i / (n - 1);
            var x = (int)(left + span * t);
            if (!Native.TryScreenPixel(x, y, out var c)) continue;
            samples.Add(c);
        }

        if (samples.Count == 0) return false;

        samples.Sort((a, b) => Lum(a).CompareTo(Lum(b)));
        color = samples[samples.Count / 2];

        // 以中位数为准算最大偏差：横向落差大说明采到了图标 / 文字，
        // 平滑的模糊壁纸（透明任务栏）落差小，可以直接采用。
        foreach (var c in samples)
        {
            spread = Math.Max(spread, Math.Abs(Lum(c) - Lum(color)));
        }

        return true;
    }

    /// <summary>
    /// 任务栏底色。采不到、或采样点之间落差太大（说明撞上图标/文字）就用主题中性色兜底；
    /// 开了透明工具时允许采用带色调的模糊壁纸色，这样歌词条和透明任务栏连成一片。
    /// </summary>
    public static Color SafeTaskbarColor(Layout lay, bool dark, double fromFrac = 0.08, double toFrac = 0.78)
    {
        var fallback = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        if (!TrySampleColor(lay, fromFrac, toFrac, out var c, out var spread)) return fallback;

        if (spread > 26) return fallback;

        return c;
    }

    private static int Lum(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;

    /// <summary>
    /// 图标实际画到哪（客户区坐标）。
    ///
    /// 优先走 UI Automation：任务栏上的每个应用按钮都是一个可枚举的 Button，带精确的屏幕矩形，
    /// 取所有按钮里最右的 Right 就是图标块的真实右边界。这比抓像素稳得多——抓像素会被自家的窗口、
    /// 悬停高亮、tooltip、壁纸透色干扰，实测会导致宽度来回跳。
    /// UIA 拿不到时退回像素扫描。
    /// </summary>
    public static int IconRightEdge(Layout lay, Color bg)
    {
        if (!Native.GetWindowRect(lay.Hwnd, out var sr)) return 0;

        var fromUia = IconRightEdgeUia(lay, sr);
        if (fromUia > 0) return fromUia;

        // UIA 摸不到（explorer 与本进程完整性级别不同、或系统版本换了布局）时的兜底。
        //
        // 这里**不再**抓像素：透明任务栏（TranslucentTB 之类）上任务栏是模糊壁纸，
        // 底色本身带渐变、悬停还会高亮，像素比对分不清「图标」和「壁纸」，
        // 量出来的边界能从 1478 跳到 1880，宽度跟着乱跳。
        // 改成按 Win11 的固定溢出量估算：任务栏按钮画在 XAML 层，实测溢出
        // ReBarWindow32 右缘约 190 DIP（100% 缩放 177 px、150% 缩放 219 px）。
        // 值恒定、不做迟滞也不会抖，宽度只会在图标真正变多时变化（那时候 UIA 一般也可用了）。
        return lay.IconsRight + (int)Math.Round(190 * lay.Scale);
    }

    private static int IconRightEdgeUia(Layout lay, Native.RECT sr)
    {
        try
        {
            var root = AutomationElement.FromHandle(lay.Hwnd);
            if (root is null) return 0;

            var all = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

            double maxRight = 0;
            foreach (AutomationElement b in all)
            {
                System.Windows.Rect r;
                string cls;
                try
                {
                    // 只认任务栏上的应用按钮：Win11 的类名固定是 Taskbar.TaskListButtonAutomationPeer。
                    // 托盘那几个（SystemTray.NormalButton / AccentButton / OmniButton…）必须排除，
                    // 否则「显示隐藏的图标」「输入法指示」会被当成图标右边界，条被挤成一条缝。
                    cls = b.Current.ClassName ?? string.Empty;
                    if (!cls.Contains("TaskListButton", StringComparison.Ordinal)) continue;

                    r = b.Current.BoundingRectangle;
                }
                catch
                {
                    continue;
                }

                if (r.IsEmpty || r.Width < 8 || r.Height < 8) continue;
                if (r.Right > maxRight) maxRight = r.Right;
            }

            return maxRight > 0 ? (int)Math.Round(maxRight - sr.Left) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int IconRightEdgePixels(Layout lay, Native.RECT sr, Color bg)
    {
        var start = sr.Left + Math.Max(0, lay.IconsRight);
        // 只往右看 420 px：Win11 的任务栏按钮画在 XAML 层，会溢出 ReBarWindow32 一百多像素，
        // 但再往右就全是托盘的东西（「显示隐藏的图标」箭头、中/拼、时钟）。不设上限时
        // 会把输入法指示当成图标边界，条被挤成一条缝、宽度和位置都跟着乱。
        var hardStop = start + (int)Math.Round(420 * lay.Scale);
        var stop = Math.Min(sr.Left + Math.Min(lay.TrayLeft, lay.ClientWidth) - 6, hardStop);

        if (stop - start < 8) return 0;

        var h = Math.Max(8, lay.ClientHeight);
        var y1 = sr.Top + (int)(h * 0.32);
        var y2 = sr.Top + (int)(h * 0.50);

        var last = start;
        var gap = 0;

        for (var x = start; x < stop; x += 3)
        {
            // 两行取「或」：150% 缩放下图标只画在任务栏上半段。第三行（0.60h）不要用——
            // 那一行早就出了图标的范围，只剩壁纸透色，会把模糊的渐变当成人像，边界被量虚。
            var lit = Lit(x, y1) || Lit(x, y2);

            if (lit)
            {
                last = x;
                gap = 0;
                continue;
            }

            // 图标之间本身就有 20–30 px 空隙，阈值必须比它大、又要比「图标块 → 输入法指示」
            // 的距离小。实测这块间距在 200 px 以上，取 60 px 是安全的。
            gap += 3;
            if (gap >= 60) break;
        }

        // 一个图标都没摸到，或者压根没溢出 ReBarWindow32，就当没有额外限制
        return last > lay.IconsRight + 6 ? last + 3 - sr.Left : 0;

        bool Lit(int x, int y) => Native.TryScreenPixel(x, y, out var c) && Differs(c, bg, 34);
    }

    private static bool Differs(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) > tolerance || Math.Abs(a.G - b.G) > tolerance || Math.Abs(a.B - b.B) > tolerance;

    /// <summary>系统是否浅色主题。</summary>
    public static bool IsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int v) return v == 1;
        }
        catch
        {
        }
        return false;
    }

    public static bool IsDark(Settings s) => s.ThemeMode switch
    {
        1 => true,
        2 => false,
        _ => !IsLightTheme(),
    };
}
