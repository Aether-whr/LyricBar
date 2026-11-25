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

/// <summary>贴边浮窗模式：置顶无边框窗口贴在屏幕工作区左右边缘。</summary>
public partial class FloatingBar : Window
{
    private readonly LyricEngine _engine;
    private readonly Settings _settings;
    private readonly List<TextBlock> _blocks = new();

    private double _targetOffset;
    private int _tickCount;

    private static readonly SolidColorBrush ActiveBrush = MakeBrush(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush DimBrush = MakeBrush(0x66, 0xFF, 0xFF, 0xFF);

    private static SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public FloatingBar(LyricEngine engine, Settings settings)
    {
        _engine = engine;
        _settings = settings;

        InitializeComponent();

        Loaded += OnLoaded;
        CompositionTarget.Rendering += OnFrame;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            Native.HideFromAltTab(new WindowInteropHelper(this).Handle);
        }
        catch
        {
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        ApplyDock();
        HookEngine();
        RefreshHeader();
        BuildBlocks();
        UpdatePlayIcon();
    }

    private void HookEngine()
    {
        _engine.TrackChanged += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshHeader();
            _ = UpdateCoverAsync();
        }));

        _engine.PlaybackChanged += () => Dispatcher.BeginInvoke(new Action(UpdatePlayIcon));

        _engine.LyricsChanged += () => Dispatcher.BeginInvoke(new Action(BuildBlocks));

        _engine.LineChanged += () => Dispatcher.BeginInvoke(new Action(() =>
            Paint(_engine.CurrentIndex)));
    }

    // ---------------------------------------------------------------- 设置

    public void ApplySettings()
    {
        var op = Math.Clamp(_settings.FloatOpacity, 0.15, 1.0);
        Shell.Background = new SolidColorBrush(Color.FromArgb((byte)(op * 255), 0x11, 0x16, 0x1D));

        CoverBox.Visibility = _settings.FloatShowCover ? Visibility.Visible : Visibility.Collapsed;
        TitleBox.Margin = _settings.FloatShowCover ? new Thickness(10, 0, 0, 0) : new Thickness(0);

        ProgressRow.Visibility = _settings.FloatShowProgress ? Visibility.Visible : Visibility.Collapsed;
        ControlRow.Visibility = _settings.FloatShowControls ? Visibility.Visible : Visibility.Collapsed;

        BuildBlocks();
        ApplyDock();
    }

    private double LineFont => Math.Clamp(_settings.FloatFontSize, 9, 22);
    private double ActiveFont => LineFont * 1.2;

    // ---------------------------------------------------------------- 停靠

    /// <summary>贴着工作区边缘竖排，避开任务栏（任务栏在左/右/上/下都能正确让位）。</summary>
    public void ApplyDock()
    {
        try
        {
            var barWidth = Math.Clamp(_settings.FloatWidth, 160, 460);
            Native.RECT work;

            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)) };
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

            var left = _settings.FloatSide == 1
                ? work.Right / sx - barWidth - 6
                : work.Left / sx + 6;
            var top = work.Top / sy + 8;
            var height = Math.Max(280, work.Height / sy - 16);

            if (Math.Abs(Width - barWidth) > 0.5) Width = barWidth;
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
        var has = _engine.HasTrack;

        TitleText.Text = has ? _engine.Title : "未在播放";
        ArtistText.Text = has
            ? (string.IsNullOrWhiteSpace(_engine.Artist) ? _engine.Album : _engine.Artist)
            : "打开播放器后自动识别";
    }

    private async Task UpdateCoverAsync()
    {
        if (!_settings.FloatShowCover)
        {
            Cover.Source = null;
            return;
        }

        var thumb = _engine.Media.Thumbnail;
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
        PlayBtn.Content = _engine.IsPlaying ? "\uE769" : "\uE768";
    }

    // ---------------------------------------------------------------- 歌词渲染

    private void BuildBlocks()
    {
        Lines.Children.Clear();
        _blocks.Clear();
        _targetOffset = 0;
        Scroller.ScrollToVerticalOffset(0);

        var lines = _engine.Lines;
        if (lines.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = _engine.HasTrack
                    ? "没有匹配到歌词\n\n可把 .lrc 放进\n%APPDATA%\\LyricBar\\lyrics\\"
                    : "等待播放…",
                Foreground = DimBrush,
                FontSize = Math.Max(10, LineFont - 1),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 24, 0, 0),
            };
            Lines.Children.Add(hint);
            return;
        }

        foreach (var line in lines)
        {
            var tb = new TextBlock
            {
                Text = line.Text,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = LineFont,
                LineHeight = LineFont * 1.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = DimBrush,
                Margin = new Thickness(0, 5, 0, 5),
            };
            Lines.Children.Add(tb);
            _blocks.Add(tb);
        }

        Paint(_engine.CurrentIndex);
    }

    private void Paint(int idx)
    {
        for (var i = 0; i < _blocks.Count; i++)
        {
            var tb = _blocks[i];
            var on = i == idx;
            tb.FontSize = on ? ActiveFont : LineFont;
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

    // ---------------------------------------------------------------- 计时

    /// <summary>由 App 的共享计时器调用。</summary>
    public void OnTick()
    {
        _tickCount++;
        if (_tickCount % 12 == 0) ApplyDock();

        if (!_settings.FloatShowProgress) return;

        var ratio = _engine.ProgressRatio();
        if (ratio <= 0) return;

        if (ProgressFill.Parent is FrameworkElement host && host.ActualWidth > 0)
        {
            var next = host.ActualWidth * ratio;
            if (Math.Abs(ProgressFill.Width - next) > 0.5) ProgressFill.Width = next;
        }
    }

    // ---------------------------------------------------------------- 交互

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        await _engine.TogglePlayPauseAsync();
        UpdatePlayIcon();
    }

    private async void OnNext(object sender, RoutedEventArgs e) => await _engine.NextAsync();

    private async void OnPrev(object sender, RoutedEventArgs e) => await _engine.PrevAsync();

    private void OnDockLeft(object sender, RoutedEventArgs e)
    {
        _settings.FloatSide = 0;
        _settings.Save();
        ApplyDock();
    }

    private void OnDockRight(object sender, RoutedEventArgs e)
    {
        _settings.FloatSide = 1;
        _settings.Save();
        ApplyDock();
    }

    private void OnReloadLyrics(object sender, RoutedEventArgs e) => _ = _engine.ReloadLyricsAsync();

    private void OnOpenSettings(object sender, RoutedEventArgs e) => App.Current?.OpenControlCenter();

    private void OnSwitchMode(object sender, RoutedEventArgs e) => App.Current?.SetMode(DisplayMode.Embedded);

    private void OnExit(object sender, RoutedEventArgs e) => App.Current?.Shutdown();
}
