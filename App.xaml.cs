using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LyricBar;

public partial class App : Application
{
    private const int WM_TRAY = (int)Native.WM_APP + 1;
    private const uint TrayId = 1;

    private Mutex? _mutex;

    private FloatingBar? _floating;
    private EmbeddedBar? _embedded;
    private ControlCenter? _center;

    private DispatcherTimer? _timer;
    private HwndSource? _traySource;
    private bool _trayAdded;
    private IntPtr _trayHwnd;
    private int _tickCount;
    private int _startPage;

    public static new App Current => (App)Application.Current;

    public Settings Config { get; private set; } = new();
    public LyricEngine Engine { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "LyricBar.SingleInstance", out var isNew);
        if (!isNew)
        {
            // 已经有一份在跑：把命令行意图转达过去，然后自己退出。
            // 否则「再次运行 --settings」会毫无反应，看着像没生效。
            var bar = Native.FindWindow(null, "LyricBar Embedded");
            if (bar == IntPtr.Zero) bar = Native.FindWindow(null, "LyricBar Floating");
            if (bar != IntPtr.Zero)
            {
                Native.PostMessage(bar, (int)Native.WM_APP + 3, 1, IntPtr.Zero);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        Log.Write($"OnStartup: appdata={Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}");

        // 上次进程被强杀时窗口可能留在任务栏上，先清掉，否则会出现两条
        KillStrays();

        Config = Settings.Load();
        Engine = new LyricEngine(Config);

        _ = Engine.InitAsync();

        CreateTray();
        ApplyMode();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(64),
        };
        _timer.Tick += OnTimer;
        _timer.Start();

        // 首次启动自动打开设置面板，方便立刻选模式
        var firstRun = !Settings.Exists;
        if (firstRun) Config.Save();

        foreach (var a in e.Args)
        {
            if (a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(a[7..], out var page)) _startPage = Math.Clamp(page, 0, 3);
        }

        var wantSettings = firstRun
            || e.Args.Any(a => a is "--settings" or "-s");

        if (wantSettings) OpenControlCenter();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        // 每一步单独 try：内嵌窗口在 explorer 重启后会变成野指针，
        // 任何一步抛异常都不能连累后面的「任务栏守卫」，否则重启后就再也不回来了。
        try
        {
            Engine.Tick();
        }
        catch (Exception ex)
        {
            Log.Write("Engine.Tick failed", ex);
        }

        if (++_tickCount % 16 == 0)
        {
            try
            {
                GuardTaskbar();
            }
            catch (Exception ex)
            {
                Log.Write("GuardTaskbar failed", ex);
            }
        }

        try
        {
            if (Config.Mode == DisplayMode.Embedded) _embedded?.OnTick();
            else _floating?.OnTick();
        }
        catch (Exception ex)
        {
            Log.Write("Bar.OnTick failed", ex);
        }
    }

    private void GuardTaskbar()
    {
        var lay = Taskbar.Probe();
        var hwnd = lay?.Hwnd ?? IntPtr.Zero;

        if (hwnd == IntPtr.Zero)
        {
            // 任务栏正在重建（explorer 重启中），下一轮再看
            return;
        }

        if (hwnd != _trayHwnd)
        {
            Log.Write($"GuardTaskbar: tray changed {_trayHwnd} -> {hwnd}, rebuilding");
            _trayHwnd = hwnd;
            if (Config.Mode == DisplayMode.Embedded) ApplyMode();
            return;
        }

        if (Config.Mode != DisplayMode.Embedded) return;

        if (_embedded is null || _embedded.NeedsRebuild)
        {
            Log.Write("GuardTaskbar: embedded bar gone, rebuilding");
            ApplyMode();
        }
    }

    // ---------------------------------------------------------------- 模式

    public void SetMode(DisplayMode mode)
    {
        if (Config.Mode == mode)
        {
            _center?.OnModeChanged();
            return;
        }

        Config.Mode = mode;
        Config.Save();
        ApplyMode();
        _center?.OnModeChanged();
    }

    private void ApplyMode()
    {
        TearDownBars();

        if (Config.Mode == DisplayMode.Embedded)
        {
            _embedded = new EmbeddedBar(Engine, Config);
            _embedded.Show();
        }
        else
        {
            _floating = new FloatingBar(Engine, Config);
            _floating.Show();
        }

        _trayHwnd = Taskbar.Probe()?.Hwnd ?? IntPtr.Zero;
    }

    private void TearDownBars()
    {
        if (_floating is not null)
        {
            try
            {
                _floating.Close();
            }
            catch (Exception ex)
            {
                Log.Write("Close floating failed", ex);
            }
            _floating = null;
        }

        if (_embedded is not null)
        {
            var bar = _embedded;
            _embedded = null;

            try
            {
                bar.Close();
            }
            catch (Exception ex)
            {
                Log.Write("Close embedded failed", ex);
            }

            // Close 之后原生窗口可能还活着（父窗口消失时会这样），硬销毁避免留残影
            try
            {
                bar.ForceDestroy();
            }
            catch (Exception ex)
            {
                Log.Write("ForceDestroy embedded failed", ex);
            }
        }
    }

    /// <summary>清掉上次运行残留的歌词条窗口（进程被强杀时窗口可能留在任务栏上）。</summary>
    private static void KillStrays()
    {
        foreach (var title in new[] { "LyricBar Embedded", "LyricBar Floating" })
        {
            var h = Native.FindWindow(null, title);
            if (h == IntPtr.Zero) continue;

            Log.Write($"KillStrays: destroying stale window '{title}' hwnd={h}");
            Native.DestroyWindow(h);
        }
    }

    /// <summary>设置页改动后刷新两种模式的呈现。</summary>
    public void RefreshBars()
    {
        // 背景不透明度决定窗口是不是分层窗口，只能在建窗口时定下来 → 变了就重建
        if (_embedded is not null && _embedded.BgOpacity != Config.EmbBgOpacity)
        {
            Log.Write($"RefreshBars: bg opacity {_embedded.BgOpacity} -> {Config.EmbBgOpacity}, rebuilding");
            ApplyMode();
            return;
        }

        try
        {
            _embedded?.ApplySettings();
        }
        catch
        {
        }

        try
        {
            _floating?.ApplySettings();
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- 主界面

    /// <summary>
    /// 设置窗口存在期间冻结歌词条的布局与配色。
    /// 托盘窗口的矩形在开关任何窗口时都会跳几十像素（实测 trayLeft 2032↔2080），
    /// 跟着它重排就会看到条「跳一下 / 颜色变一下」。设置窗口开着时这点偏差无关紧要，
    /// 关掉之后再恢复跟随。
    /// </summary>
    public bool BarLayoutFrozen { get; private set; }

    public void OpenControlCenter()
    {
        if (_center is null)
        {
            _center = new ControlCenter(Config, Engine);
            _center.Closed += (_, _) =>
            {
                _center = null;
                BarLayoutFrozen = false;
                ScheduleBarRepaint();
            };

            // 设置窗口一激活/失去激活，任务栏那片会重新合成一次，覆盖模式的条
            // 容易在那一帧露出底色（看着像闪一下）。这里跟着补一次重画。
            _center.Activated += (_, _) =>
            {
                BarLayoutFrozen = true;
                ScheduleBarRepaint();
            };
            _center.Deactivated += (_, _) => ScheduleBarRepaint();
        }

        BarLayoutFrozen = true;
        _center.Show();

        if (_center.WindowState == WindowState.Minimized) _center.WindowState = WindowState.Normal;
        _center.Activate();
        _center.OnModeChanged();

        if (_startPage > 0) _center.ShowPage(_startPage);

        ScheduleBarRepaint();
    }

    /// <summary>合成顺序被打乱后补两次重画：一次立刻，一次 240 ms 后。</summary>
    private void ScheduleBarRepaint()
    {
        _embedded?.KickRepaint();

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        t.Tick += (s, e) =>
        {
            t.Stop();
            _embedded?.KickRepaint();
        };
        t.Start();
    }

    // ---------------------------------------------------------------- 托盘

    private void CreateTray()
    {
        try
        {
            var p = new HwndSourceParameters("LyricBarTray")
            {
                ParentWindow = (IntPtr)(-3), // HWND_MESSAGE
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };

            _traySource = new HwndSource(p);
            _traySource.AddHook(WndProc);

            var data = new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA)),
                hWnd = _traySource.Handle,
                uID = TrayId,
                uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
                uCallbackMessage = (uint)WM_TRAY,
                hIcon = TrayIcon(),
                szTip = "LyricBar 任务栏歌词",
                szInfo = "",
                szInfoTitle = "",
            };

            _trayAdded = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 托盘图标：把 exe 自己的图标取出来用（跟资源管理器里看到的一致）。
    /// 取不到才退回系统的默认应用图标。
    /// </summary>
    private static IntPtr TrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var n = Native.ExtractIconEx(exe, 0, out _, out var small, 1);
                if (n > 0 && small != IntPtr.Zero) return small;
            }
        }
        catch
        {
        }

        return Native.LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 第二实例转达过来的请求（--settings 之类）
        if (msg == (int)Native.WM_APP + 3)
        {
            Dispatcher.BeginInvoke(new Action(OpenControlCenter));
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WM_TRAY) return IntPtr.Zero;

        var evt = (int)((long)lParam & 0xFFFF);
        if (evt != 0x0205 && evt != 0x0202) return IntPtr.Zero; // RBUTTONUP / LBUTTONUP

        if (evt == 0x0202)
        {
            OpenControlCenter();
            handled = true;
            return IntPtr.Zero;
        }

        ShowTrayMenu(hwnd);
        handled = true;
        return IntPtr.Zero;
    }

    private void ShowTrayMenu(IntPtr hwnd)
    {
        try
        {
            var menu = Native.CreatePopupMenu();
            Native.AppendMenu(menu, Native.MF_STRING, (IntPtr)1, "打开设置");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
            Native.AppendMenu(menu, Native.MF_STRING, (IntPtr)2, "任务栏内嵌模式");
            Native.AppendMenu(menu, Native.MF_STRING, (IntPtr)3, "贴边浮窗模式");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
            Native.AppendMenu(menu, Native.MF_STRING, (IntPtr)4, "重新匹配歌词");
            Native.AppendMenu(menu, Native.MF_STRING, (IntPtr)5, "退出");

            Native.GetCursorPos(out var pt);
            Native.SetForegroundWindow(hwnd);

            var cmd = Native.TrackPopupMenu(
                menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON,
                pt.X, pt.Y, 0, hwnd, IntPtr.Zero);

            Native.PostMessage(hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Native.DestroyMenu(menu);

            switch (cmd)
            {
                case 1:
                    OpenControlCenter();
                    break;
                case 2:
                    SetMode(DisplayMode.Embedded);
                    break;
                case 3:
                    SetMode(DisplayMode.Floating);
                    break;
                case 4:
                    _ = Engine.ReloadLyricsAsync();
                    break;
                case 5:
                    Shutdown();
                    break;
            }
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- 退出

    public new void Shutdown()
    {
        try
        {
            _timer?.Stop();
        }
        catch
        {
        }

        if (_trayAdded && _traySource is not null)
        {
            try
            {
                var data = new Native.NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA)),
                    hWnd = _traySource.Handle,
                    uID = TrayId,
                    szTip = "",
                    szInfo = "",
                    szInfoTitle = "",
                };
                Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            }
            catch
            {
            }
            _trayAdded = false;
        }

        TearDownBars();

        try
        {
            _center?.Close();
        }
        catch
        {
        }

        try
        {
            Engine?.Dispose();
        }
        catch
        {
        }

        base.Shutdown();
    }
}
