using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LyricBar;

/// <summary>主界面：模式切换 + 每个模式各自独立的设置页。</summary>
public partial class ControlCenter : Window
{
    private readonly Settings _s;
    private readonly LyricEngine _engine;
    private bool _ready;

    private bool _dragging;
    private Native.POINT _dragOrigin;
    private Point _dragFrom;

    public ControlCenter(Settings settings, LyricEngine engine)
    {
        _s = settings;
        _engine = engine;

        InitializeComponent();

        LrcPathText.Text = Path.Combine(AppPaths.LyricsDir, "歌手 - 歌名.lrc");

        Loaded += (_, _) =>
        {
            SyncFromSettings();
            _ready = true;
            UpdateNowPlaying();
        };

        _engine.TrackChanged += () => Dispatcher.BeginInvoke(new Action(UpdateNowPlaying));
        _engine.PlaybackChanged += () => Dispatcher.BeginInvoke(new Action(UpdateNowPlaying));
        _engine.LineChanged += () => Dispatcher.BeginInvoke(new Action(UpdateNowPlaying));

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.TryRoundCorners(hwnd);
        };
    }

    // ---------------------------------------------------------------- 同步

    private void SyncFromSettings()
    {
        // 概览
        Theme0.IsChecked = _s.ThemeMode == 0;
        Theme1.IsChecked = _s.ThemeMode == 1;
        Theme2.IsChecked = _s.ThemeMode == 2;

        SldOffset.Value = Math.Clamp(_s.OffsetMs, -3000, 3000);
        TxtOffset.Text = $"{_s.OffsetMs} ms";

        HighlightModeCards();

        // 内嵌
        ( _s.EmbAlign switch { 0 => EmbAlign0, 2 => EmbAlign2, _ => EmbAlign1 }).IsChecked = true;
        ChkEmbAuto.IsChecked = _s.EmbAuto;
        SldEmbWidth.Value = Math.Clamp(_s.EmbWidth <= 0 ? 320 : _s.EmbWidth, 140, 1200);
        SldEmbWidth.IsEnabled = !_s.EmbAuto;
        TxtEmbWidth.Text = _s.EmbAuto
            ? $"{Math.Round(AvailableWidth())} px 可用（随歌词收放）"
            : $"{_s.EmbWidth} px";

        SldEmbLyricWidth.Value = Math.Clamp(_s.EmbLyricWidth, 0, 800);
        TxtEmbLyricWidth.Text = _s.EmbLyricWidth <= 0 ? "自动" : $"{_s.EmbLyricWidth} px";

        (_s.EmbScrollMode == 1 ? EmbScroll1 : EmbScroll0).IsChecked = true;
        ChkEmbScrollAuto.IsChecked = _s.EmbScrollAuto;
        SldEmbScrollSpeed.Value = Math.Clamp(_s.EmbScrollSpeed, 10, 200);
        TxtEmbScrollSpeed.Text = _s.EmbScrollAuto ? $"{_s.EmbScrollSpeed} px/s（下限）" : $"{_s.EmbScrollSpeed} px/s";

        SldEmbFade.Value = Math.Clamp(_s.EmbFade, 0, 60);
        TxtEmbFade.Text = _s.EmbFade <= 0 ? "关闭" : $"{_s.EmbFade} px";

        SldTrackWidth.Value = Math.Clamp(_s.TrackWidthPercent, 10, 70);
        TxtTrackWidth.Text = $"整体宽度的 {_s.TrackWidthPercent}%";
        SldTrackHold.Value = Math.Clamp(_s.TrackHoldMs / 100.0, 2, 50);
        TxtTrackHold.Text = $"{_s.TrackHoldMs / 1000.0:0.0} s";

        (_s.EmbStyle == 1 ? EmbStyle1 : EmbStyle0).IsChecked = true;
        SldEmbBlend.Value = Math.Clamp(_s.EmbBlend, 0, 1);
        TxtEmbBlend.Text = $"{_s.EmbBlend * 100:0}%";

        SldEmbFont.Value = Math.Clamp(_s.EmbFontSize, 9, 20);
        TxtEmbFont.Text = $"{_s.EmbFontSize:0.#} px";
        SldEmbBgOpacity.Value = Math.Clamp(_s.EmbBgOpacity, 0, 100);
        TxtEmbBgOpacity.Text = $"{_s.EmbBgOpacity}%";

        ChkEmbCover.IsChecked = _s.EmbShowCover;
        ChkEmbTrack.IsChecked = _s.EmbShowTrack;
        ChkEmbControls.IsChecked = _s.EmbShowControls;

        // 浮窗
        (_s.FloatSide == 1 ? FloatSide1 : FloatSide0).IsChecked = true;
        SldFloatWidth.Value = Math.Clamp(_s.FloatWidth, 160, 460);
        TxtFloatWidth.Text = $"{_s.FloatWidth} px";

        SldFloatFont.Value = Math.Clamp(_s.FloatFontSize, 9, 22);
        TxtFloatFont.Text = $"{_s.FloatFontSize:0.#} px";

        SldFloatOpacity.Value = Math.Clamp(_s.FloatOpacity, 0.3, 1);
        TxtFloatOpacity.Text = $"{_s.FloatOpacity * 100:0}%";

        ChkFloatCover.IsChecked = _s.FloatShowCover;
        ChkFloatProgress.IsChecked = _s.FloatShowProgress;
        ChkFloatControls.IsChecked = _s.FloatShowControls;
    }

    private double AvailableWidth()
    {
        var lay = Taskbar.Probe();
        return lay is null ? 720 : Math.Max(120, lay.TrayLeft - lay.IconsRight - 20) / lay.Scale;
    }

    private void HighlightModeCards()
    {
        var embedded = _s.Mode == DisplayMode.Embedded;

        CardEmbedded.Background = new SolidColorBrush(embedded
            ? Color.FromArgb(0x2E, 0x4C, 0xC2, 0xFF)
            : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        CardEmbedded.BorderBrush = new SolidColorBrush(embedded
            ? Color.FromArgb(0x99, 0x4C, 0xC2, 0xFF)
            : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));

        CardFloating.Background = new SolidColorBrush(!embedded
            ? Color.FromArgb(0x2E, 0x4C, 0xC2, 0xFF)
            : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        CardFloating.BorderBrush = new SolidColorBrush(!embedded
            ? Color.FromArgb(0x99, 0x4C, 0xC2, 0xFF)
            : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));

        CheckEmbedded.Visibility = embedded ? Visibility.Visible : Visibility.Collapsed;
        CheckFloating.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;

        StatusChip.Text = embedded ? "当前 · 任务栏内嵌" : "当前 · 贴边浮窗";
    }

    private void UpdateNowPlaying()
    {
        if (_engine.HasTrack)
        {
            NowTitle.Text = _engine.Title;
            var who = string.IsNullOrWhiteSpace(_engine.Artist) ? _engine.Album : _engine.Artist;
            NowSub.Text = string.IsNullOrWhiteSpace(who)
                ? (_engine.IsPlaying ? "正在播放" : "已暂停")
                : $"{who} · {(_engine.IsPlaying ? "正在播放" : "已暂停")}";

            NavNowPlaying.Text = _engine.CurrentText;
        }
        else
        {
            NowTitle.Text = "未在播放";
            NowSub.Text = "打开任意播放器（Media Player、浏览器、网易云等）后自动识别";
            NavNowPlaying.Text = "未在播放";
        }
    }

    private void Commit()
    {
        if (!_ready) return;
        _s.Save();
        App.Current?.RefreshBars();
    }

    // ---------------------------------------------------------------- 导航

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out var idx)) return;

        Page0.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        Page1.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        Page3.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowPage(int idx)
    {
        var nav = idx switch
        {
            1 => Nav1,
            2 => Nav2,
            3 => Nav3,
            _ => Nav0,
        };
        nav.IsChecked = true;
    }

    // ---------------------------------------------------------------- 概览

    private void OnPickEmbedded(object sender, MouseButtonEventArgs e) => App.Current?.SetMode(DisplayMode.Embedded);

    private void OnPickFloating(object sender, MouseButtonEventArgs e) => App.Current?.SetMode(DisplayMode.Floating);

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out var mode)) return;

        if (!_ready) return;
        _s.ThemeMode = mode;
        Commit();
    }

    private void OnOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtOffset is null) return;
        if (!_ready) return;
        _s.OffsetMs = (int)Math.Round(SldOffset.Value);
        TxtOffset.Text = $"{_s.OffsetMs} ms";
        Commit();
    }

    // ---------------------------------------------------------------- 内嵌

    private void OnEmbAlign(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out var align)) return;

        if (!_ready) return;
        _s.EmbAlign = align;
        Commit();
    }

    private void OnEmbAuto(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var auto = ChkEmbAuto.IsChecked == true;
        _s.EmbAuto = auto;
        SldEmbWidth.IsEnabled = !auto;
        if (!auto) _s.EmbWidth = (int)Math.Round(SldEmbWidth.Value);
        TxtEmbWidth.Text = auto
            ? $"{Math.Round(AvailableWidth())} px 可用（随歌词收放）"
            : $"{_s.EmbWidth} px";
        Commit();
    }

    private void OnEmbWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtEmbWidth is null || ChkEmbAuto is null) return;
        if (!_ready) return;
        if (ChkEmbAuto.IsChecked == true) return;

        _s.EmbWidth = (int)Math.Round(SldEmbWidth.Value);
        TxtEmbWidth.Text = $"{_s.EmbWidth} px";
        Commit();
    }

    private void OnEmbLyricWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtEmbLyricWidth is null) return;
        if (!_ready) return;

        _s.EmbLyricWidth = (int)Math.Round(SldEmbLyricWidth.Value);
        TxtEmbLyricWidth.Text = _s.EmbLyricWidth <= 0 ? "自动" : $"{_s.EmbLyricWidth} px";
        Commit();
    }

    private void OnEmbScroll(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out var mode)) return;

        if (!_ready) return;
        _s.EmbScrollMode = mode;
        Commit();
    }

    private void OnEmbScrollSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtEmbScrollSpeed is null) return;
        if (!_ready) return;

        _s.EmbScrollSpeed = (int)Math.Round(SldEmbScrollSpeed.Value);
        TxtEmbScrollSpeed.Text = _s.EmbScrollAuto ? $"{_s.EmbScrollSpeed} px/s（下限）" : $"{_s.EmbScrollSpeed} px/s";
        Commit();
    }

    private void OnEmbScrollAuto(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        _s.EmbScrollAuto = ChkEmbScrollAuto.IsChecked == true;
        TxtEmbScrollSpeed.Text = _s.EmbScrollAuto ? $"{_s.EmbScrollSpeed} px/s（下限）" : $"{_s.EmbScrollSpeed} px/s";
        Commit();
    }

    private void OnEmbFadeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtEmbFade is null) return;
        if (!_ready) return;

        _s.EmbFade = (int)Math.Round(SldEmbFade.Value);
        TxtEmbFade.Text = _s.EmbFade <= 0 ? "关闭" : $"{_s.EmbFade} px";
        Commit();
    }

    private void OnTrackWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtTrackWidth is null) return;
        if (!_ready) return;

        _s.TrackWidthPercent = (int)Math.Round(SldTrackWidth.Value);
        TxtTrackWidth.Text = $"整体宽度的 {_s.TrackWidthPercent}%";
        Commit();
    }

    private void OnTrackHoldChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtTrackHold is null) return;
        if (!_ready) return;

        // 滑块是 0.1 秒为单位的整数（2..50 → 0.2..5.0 s）
        _s.TrackHoldMs = (int)Math.Round(SldTrackHold.Value) * 100;
        TxtTrackHold.Text = $"{_s.TrackHoldMs / 1000.0:0.0} s";
        Commit();
    }

    private void OnEmbStyle(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out var style)) return;

        if (!_ready) return;
        _s.EmbStyle = style;
        Commit();
    }

    private void OnEmbBlendChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtEmbBlend is null) return;
        if (!_ready) return;
        _s.EmbBlend = Math.Round(SldEmbBlend.Value, 2);
        TxtEmbBlend.Text = $"{_s.EmbBlend * 100:0}%";
        Commit();
    }

    private void OnEmbFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtEmbFont is null) return;
        if (!_ready) return;
        _s.EmbFontSize = Math.Round(SldEmbFont.Value, 1);
        TxtEmbFont.Text = $"{_s.EmbFontSize:0.#} px";
        Commit();
    }

    private void OnEmbBgOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtEmbBgOpacity is null) return;
        if (!_ready) return;
        _s.EmbBgOpacity = (int)Math.Round(SldEmbBgOpacity.Value);
        TxtEmbBgOpacity.Text = $"{_s.EmbBgOpacity}%";
        Commit();
    }

    private void OnEmbToggle(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.EmbShowCover = ChkEmbCover.IsChecked == true;
        _s.EmbShowTrack = ChkEmbTrack.IsChecked == true;
        _s.EmbShowControls = ChkEmbControls.IsChecked == true;
        Commit();
    }

    // ---------------------------------------------------------------- 浮窗

    private void OnFloatSide(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out var side)) return;

        if (!_ready) return;
        _s.FloatSide = side;
        Commit();
    }

    private void OnFloatWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFloatWidth is null) return;
        if (!_ready) return;
        _s.FloatWidth = (int)Math.Round(SldFloatWidth.Value);
        TxtFloatWidth.Text = $"{_s.FloatWidth} px";
        Commit();
    }

    private void OnFloatFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFloatFont is null) return;
        if (!_ready) return;
        _s.FloatFontSize = Math.Round(SldFloatFont.Value, 1);
        TxtFloatFont.Text = $"{_s.FloatFontSize:0.#} px";
        Commit();
    }

    private void OnFloatOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFloatOpacity is null) return;
        if (!_ready) return;
        _s.FloatOpacity = Math.Round(SldFloatOpacity.Value, 2);
        TxtFloatOpacity.Text = $"{_s.FloatOpacity * 100:0}%";
        Commit();
    }

    private void OnFloatToggle(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.FloatShowCover = ChkFloatCover.IsChecked == true;
        _s.FloatShowProgress = ChkFloatProgress.IsChecked == true;
        _s.FloatShowControls = ChkFloatControls.IsChecked == true;
        Commit();
    }

    // ---------------------------------------------------------------- 杂项

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // 自己的拖动实现：DragMove() 在无边框 + 自绘窗口里容易抛异常被吞掉，拖不动还没反馈。
        // 用鼠标捕获 + 屏幕坐标算位移，逻辑一目了然。
        _dragOrigin = Native.CursorPos();
        _dragFrom = new Point(Left, Top);
        _dragging = true;

        CaptureMouse();
        MouseMove += OnDragMove;
        MouseLeftButtonUp += OnDragEnd;

        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            OnDragEnd(sender, e);
            return;
        }

        var now = Native.CursorPos();
        var dpi = VisualTreeHelper.GetDpi(this);
        var pos = new Point(
            _dragFrom.X + (now.X - _dragOrigin.X) / dpi.DpiScaleX,
            _dragFrom.Y + (now.Y - _dragOrigin.Y) / dpi.DpiScaleY);

        // 别让窗口被拖到屏幕外面找不回来
        var screen = SystemParameters.WorkArea;
        Left = Math.Clamp(pos.X, screen.Left - ActualWidth + 120, screen.Right - 120);
        Top = Math.Clamp(pos.Y, screen.Top, screen.Bottom - 60);
    }

    private void OnDragEnd(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        MouseMove -= OnDragMove;
        MouseLeftButtonUp -= OnDragEnd;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    private void OnReloadLyrics(object sender, RoutedEventArgs e) => _ = _engine.ReloadLyricsAsync();

    private void OnOpenLyricsFolder(object sender, RoutedEventArgs e)
    {
        var dir = AppPaths.LyricsDir;

        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
    {
        var path = AppPaths.SettingsFile;

        if (!File.Exists(path)) _s.Save();
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void OnQuit(object sender, RoutedEventArgs e) => App.Current?.Shutdown();

    /// <summary>模式切换后由 App 回调，刷新高亮。</summary>
    public void OnModeChanged()
    {
        HighlightModeCards();
    }
}
