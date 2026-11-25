using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Storage.Streams;

namespace LyricBar;

public partial class MainWindow : Window
{
    private const int BarWidth = 236;

    private enum Side
    {
        Left = 0,
        Right = 1,
    }

    private static readonly SolidColorBrush ActiveBrush = MakeBrush(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush DimBrush = MakeBrush(0x66, 0xFF, 0xFF, 0xFF);

    private static SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly Smtc _smtc = new();
    private readonly DispatcherTimer _tick;
    private readonly List<TextBlock> _blocks = new();

    private List<LyricLine> _lyric = new();
    private int _curLine = -999;
    private double _targetOffset;
    private string _trackKey = "";
    private int _tickCount;
    private Side _side = Side.Left;

    public MainWindow()
    {
        InitializeComponent();

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
        _tick.Tick += OnTick;

        Loaded += OnLoadedAsync;
        CompositionTarget.Rendering += OnFrame;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 隐藏 Alt+Tab 条目，让它像任务栏组件而不是普通窗口
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
        }
        catch
        {
        }
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        _side = LoadSide();
        ApplyDock();

        _smtc.TrackChanged += () => Dispatcher.BeginInvoke(new Action(RefreshHeader));
        _smtc.PlaybackChanged += () => Dispatcher.BeginInvoke(new Action(UpdatePlayIcon));

        try
        {
            await _smtc.InitAsync();
        }
        catch
        {
        }

        RefreshHeader();
        _tick.Start();
    }

    // ---------------------------------------------------------------- 停靠

    /// <summary>贴着工作区边缘竖排，避开任务栏（任务栏在左/右/上/下都能正确让位）。</summary>
    private void ApplyDock()
    {
        try
        {
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)) };
            Native.RECT work;

            if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
            {
                var tb = abd.rc;
                var pt = new Native.POINT { X = tb.Left + tb.Width / 2, Y = tb.Top + tb.Height / 2 };
                var mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
                var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };

                if (mon == IntPtr.Zero || !Native.GetMonitorInfo(mon, ref mi)) return;
                work = mi.rcWork;
            }
            else
            {
                var wa = SystemParameters.WorkArea;
                var dpiFallback = VisualTreeHelper.GetDpi(this);
                work = new Native.RECT
                {
                    Left = (int)(wa.Left * dpiFallback.DpiScaleX),
                    Top = (int)(wa.Top * dpiFallback.DpiScaleY),
                    Right = (int)(wa.Right * dpiFallback.DpiScaleX),
                    Bottom = (int)(wa.Bottom * dpiFallback.DpiScaleY),
                };
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

            var left = _side == Side.Left ? work.Left / sx + 6 : work.Right / sx - BarWidth - 6;
            var top = work.Top / sy + 8;
            var height = Math.Max(280, work.Height / sy - 16);

            if (Math.Abs(Width - BarWidth) > 0.5) Width = BarWidth;
            if (Math.Abs(Height - height) > 1) Height = height;
            if (Math.Abs(Left - left) > 0.5) Left = left;
            if (Math.Abs(Top - top) > 0.5) Top = top;
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- 曲目

    private void RefreshHeader()
    {
        var has = _smtc.HasSession && !string.IsNullOrWhiteSpace(_smtc.Title);

        TitleText.Text = has ? _smtc.Title : "未在播放";
        ArtistText.Text = has
            ? (string.IsNullOrWhiteSpace(_smtc.Artist) ? _smtc.Album : _smtc.Artist)
            : "打开播放器后自动识别";

        var key = _smtc.Title + "\u0001" + _smtc.Artist;
        if (key != _trackKey)
        {
            _trackKey = key;
            _ = LoadLyricsAsync();
        }

        UpdatePlayIcon();
        _ = UpdateCoverAsync();
    }

    private async Task LoadLyricsAsync()
    {
        var key = _trackKey;
        _lyric = new List<LyricLine>();
        BuildBlocks();

        if (string.IsNullOrWhiteSpace(_smtc.Title)) return;

        List<LyricLine> got;
        try
        {
            got = await LyricsApi.FetchAsync(_smtc.Title, _smtc.Artist, _smtc.Duration);
        }
        catch
        {
            got = new List<LyricLine>();
        }

        if (key != _trackKey) return;   // 抓取期间已经切歌

        _lyric = got;
        _curLine = -999;
        BuildBlocks();
    }

    private async Task UpdateCoverAsync()
    {
        var thumb = _smtc.Thumbnail;
        if (thumb is null)
        {
            Cover.Source = null;
            return;
        }

        try
        {
            using var stream = await thumb.OpenReadAsync();
            var size = (int)stream.Size;
            if (size <= 0 || size > 8 * 1024 * 1024) return;

            var buffer = new byte[size];
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)size);
                reader.ReadBytes(buffer);
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(buffer);
            bmp.EndInit();
            bmp.Freeze();

            Cover.Source = bmp;
        }
        catch
        {
        }
    }

    private void UpdatePlayIcon()
    {
        PlayBtn.Content = _smtc.IsPlaying ? "\uE769" : "\uE768";
    }

    // ---------------------------------------------------------------- 歌词渲染

    private void BuildBlocks()
    {
        Lines.Children.Clear();
        _blocks.Clear();
        _curLine = -999;
        _targetOffset = 0;
        Scroller.ScrollToVerticalOffset(0);

        if (_lyric.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = _smtc.HasSession
                    ? "没有匹配到歌词\n\n可把 .lrc 放进\n%APPDATA%\\LyricBar\\lyrics\\"
                    : "等待播放…",
                Foreground = DimBrush,
                FontSize = 11.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 24, 0, 0),
            };
            Lines.Children.Add(hint);
            return;
        }

        foreach (var line in _lyric)
        {
            var tb = new TextBlock
            {
                Text = line.Text,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 12.5,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = DimBrush,
                Margin = new Thickness(0, 5, 0, 5),
            };
            Lines.Children.Add(tb);
            _blocks.Add(tb);
        }
    }

    private void Paint(int idx)
    {
        for (var i = 0; i < _blocks.Count; i++)
        {
            var tb = _blocks[i];
            var on = i == idx;
            tb.FontSize = on ? 15.0 : 12.5;
            tb.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            tb.Foreground = on ? ActiveBrush : DimBrush;
        }

        Lines.UpdateLayout();
        Scroller.UpdateLayout();
        ScrollToLine(idx);
    }

    private void ScrollToLine(int idx)
    {
        if (idx < 0 || idx >= _blocks.Count) return;
        if (Scroller.ViewportHeight <= 0) return;

        double y = 0;
        for (var i = 0; i < idx; i++)
            y += _blocks[i].ActualHeight + _blocks[i].Margin.Top + _blocks[i].Margin.Bottom;

        var h = _blocks[idx].ActualHeight;
        _targetOffset = Math.Max(0, y + h / 2 - Scroller.ViewportHeight / 2);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;

        var cur = Scroller.VerticalOffset;
        var delta = _targetOffset - cur;
        if (Math.Abs(delta) < 0.4) return;

        Scroller.ScrollToVerticalOffset(cur + delta * 0.18);
    }

    private static int IndexAt(List<LyricLine> lines, TimeSpan t)
    {
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (lines[mid].Time <= t)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ans;
    }

    // ---------------------------------------------------------------- 时钟

    private void OnTick(object? sender, EventArgs e)
    {
        _tickCount++;

        if (_tickCount % 12 == 0) _smtc.SyncTimeline();  // ~1s 与系统时间轴对齐
        if (_tickCount % 40 == 0) ApplyDock();            // ~3.2s 跟随任务栏变化

        if (_lyric.Count > 0)
        {
            var idx = IndexAt(_lyric, _smtc.Position);
            if (idx != _curLine)
            {
                _curLine = idx;
                Paint(idx);
            }
        }

        // 播放器不报时长时（B站等），用歌词最后一行估算总长，进度条才不会空着
        var total = _smtc.Duration.TotalMilliseconds;
        if (total <= 1000 && _lyric.Count > 0 && _lyric[^1].Time > TimeSpan.Zero)
            total = _lyric[^1].Time.TotalMilliseconds + 8000;

        if (total > 1000 && ProgressFill.Parent is FrameworkElement host && host.ActualWidth > 0)
        {
            var ratio = Math.Clamp(_smtc.Position.TotalMilliseconds / total, 0, 1);
            var next = host.ActualWidth * ratio;
            if (Math.Abs(ProgressFill.Width - next) > 0.5) ProgressFill.Width = next;
        }
    }

    // ---------------------------------------------------------------- 交互

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        await _smtc.TogglePlayPauseAsync();
        UpdatePlayIcon();
    }

    private async void OnNext(object sender, RoutedEventArgs e) => await _smtc.NextAsync();

    private async void OnPrev(object sender, RoutedEventArgs e) => await _smtc.PrevAsync();

    private void OnDockLeft(object sender, RoutedEventArgs e)
    {
        _side = Side.Left;
        SaveSide(_side);
        ApplyDock();
    }

    private void OnDockRight(object sender, RoutedEventArgs e)
    {
        _side = Side.Right;
        SaveSide(_side);
        ApplyDock();
    }

    private void OnReloadLyrics(object sender, RoutedEventArgs e)
    {
        _trackKey = "";
        _lyric = new List<LyricLine>();
        BuildBlocks();
        RefreshHeader();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ---------------------------------------------------------------- 配置

    private static Side LoadSide()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\LyricBar");
            if (key?.GetValue("Side") is int v && v is >= 0 and <= 1) return (Side)v;
        }
        catch
        {
        }
        return Side.Left;
    }

    private static void SaveSide(Side side)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\LyricBar");
            key?.SetValue("Side", (int)side);
        }
        catch
        {
        }
    }
}
