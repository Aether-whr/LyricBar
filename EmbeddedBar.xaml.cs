using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Storage.Streams;

namespace LyricBar;

/// <summary>
/// 任务栏内嵌模式：优先把自己挂。Shell_TrayWnd 的子窗口。/// 系统拒绝跨进程挂载时退回「贴顶覆盖」——位置尺寸完全一致，用户看不出差别。/// </summary>
public partial class EmbeddedBar : Window
{
    private enum EmbedState
    {
        None = 0,

        /// <summary>真的是任务栏的子窗口。</summary>
        Child = 1,

        /// <summary>顶层置顶窗口，精确压在任务栏空白段上。</summary>
        Overlay = 2,
    }

    private readonly LyricEngine _engine;
    private readonly Settings _settings;

    private IntPtr _hwnd;
    private EmbedState _state = EmbedState.None;
    private int _tickCount;
    private bool _controlsOpen;
    private bool _overflow;
    private bool _shown = true;
    private bool _firstPaint = true;
    private int _visVotes;
    private int _lastWidth;
    private int _lastX = -1;
    private int _lastH;
    private int _iconRight;          // 图标实际右边界（客户区 x），用作条的左限
    private int _iconPending;        // 待确认的新边界
    private int _iconStable;         // 该值连续出现的次数
    private bool _scanBusy;
    private int _lastScanTick;
    private IntPtr _lastScanTray;    // 量过的任务栏句柄：一个 explorer 只量一次
    private int _repaintTicks;

    private Color _bg = Color.FromRgb(0x1B, 0x1B, 0x1B);
    private (int Bg, int Style, int Opacity, string? Accent, double Blend, bool Dark)? _lastStyleKey;
    private (string? Text, double View, double Overflow) _scrollSig;
    private ThicknessAnimationUsingKeyFrames? _scrollAnim;
    private DispatcherTimer? _scrollTimer;

    public EmbeddedBar(LyricEngine engine, Settings settings)
    {
        _engine = engine;
        _settings = settings;

        InitializeComponent();

        // 半透明背景需要分层窗口（per-pixel alpha），只能在创建窗口句柄之前定下来。
        // 改不透明度时 App 会重建这个窗口，所以这里读一次就够。
        // 卡片样式必须是分层窗口：只有分层窗口才能画圆角，否则窗口底色是个方块。
        AllowsTransparency = _settings.EmbBgOpacity < 100 || _settings.EmbStyle == 1;

        Loaded += OnLoaded;
    }

    /// <summary>当前实例是按哪个不透明度建的，用来判断改设置后要不要重建窗口。</summary>
    public int BgOpacity => _settings.EmbBgOpacity;

    public bool IsEmbedded => _state != EmbedState.None;

    public IntPtr Handle => _hwnd;

    /// <summary>explorer 重启会连子窗口一起销毁，此时必须重建整个 WPF 窗口。</summary>
    public bool NeedsRebuild => _hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd);

    /// <summary>窗口关掉之后如果原生窗口还活着（父窗口被销毁时会这样），硬销毁它，避免任务栏上留残影。</summary>
    public void ForceDestroy()
    {
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd))
        {
            Log.Write($"ForceDestroy: hwnd={_hwnd} still alive, destroying");
            Native.DestroyWindow(_hwnd);
        }

        _hwnd = IntPtr.Zero;
        _state = EmbedState.None;
    }

    // ---------------------------------------------------------------- 挂载

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.HideFromAltTab(_hwnd);
        Native.NoActivate(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(BarWndProc);
        TryAttach();
    }

    /// <summary>第二实例转达过来的请求（例如再点一次 exe 想打开设置）。</summary>
    private IntPtr BarWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)Native.WM_APP + 3)
        {
            Dispatcher.BeginInvoke(new Action(() => (Application.Current as App)?.OpenControlCenter()));
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        BindEngine();
        UpdateText();
        _ = UpdateCoverAsync();
        TryAttach();
    }

    /// <summary>先试真内嵌，不行就贴顶覆盖。</summary>
    private bool TryAttach()
    {
        if (_state != EmbedState.None) return true;
        if (_hwnd == IntPtr.Zero) return false;

        var lay = Taskbar.Probe();
        if (lay is null)
        {
            Log.Write("TryAttach: no taskbar");
            return false;
        }

        // explorer 刚启动时 ReBarWindow32 还没建好，等下一轮再定位
        if (!Taskbar.IsReady(lay))
        {
            Log.Write($"TryAttach: layout not ready icons={lay.IconsRight} trayLeft={lay.TrayLeft}");
            return false;
        }

        var (px, py, pw, ph) = Taskbar.Place(lay, _settings, WidthDip);
        Log.Write($"TryAttach: tray={lay.Hwnd} client={lay.ClientWidth}x{lay.ClientHeight} icons={lay.IconsRight} trayLeft={lay.TrayLeft} scale={lay.Scale:0.##} place={px},{py} {pw}x{ph}");

        if (TryAsChild(lay))
        {
            _state = EmbedState.Child;
            ApplyColors(lay);
            Place(lay);
            Log.Write("TryAttach: child ok");
            return true;
        }

        _state = EmbedState.Overlay;
        ApplyColors(lay);

        // 第一次挂载时先把图标边界量出来再摆位置。量之前窗口必须是藏着的，
        // 否则量到的是自己的歌词宽度（表现为条被自己吓窄，只剩 300 多像素）。
        if (_iconRight > 0) Place(lay);

        Log.Write("TryAttach: overlay fallback");
        return true;
    }

    /// <summary>尝试 SetParent 成任务栏子窗口；失败会还原窗口样式。</summary>
    private bool TryAsChild(Taskbar.Layout lay)
    {
        var style = Native.GetWindowLong(_hwnd, Native.GWL_STYLE);
        var child = style & ~Native.WS_POPUP | Native.WS_CHILD;
        Native.SetWindowLong(_hwnd, Native.GWL_STYLE, child);

        var prev = Native.SetParent(_hwnd, lay.Hwnd);
        var now = Native.GetParent(_hwnd);
        if (now == lay.Hwnd) return true;

        Native.SetWindowLong(_hwnd, Native.GWL_STYLE, style);
        if (prev != IntPtr.Zero) Native.SetParent(_hwnd, prev);
        return false;
    }

    /// <summary>条本身的宽度（DIP）。固定模式下就是用户设的值，自适应模式按当前这句歌词算。</summary>
    private double WidthDip => _settings.EmbAuto
        ? NaturalWidthDip()
        : Math.Clamp(_settings.EmbWidth > 0 ? _settings.EmbWidth : 320, 140, 1200);

    private void Place(Taskbar.Layout lay)
    {
        var (cx, cy, w, h) = Taskbar.Place(lay, _settings, WidthDip, _iconRight);

        // 子窗口用父窗口客户区坐标；覆盖模式用屏幕坐标
        var x = cx;
        var y = cy;
        if (_state == EmbedState.Overlay && Native.GetWindowRect(lay.Hwnd, out var sr))
        {
            x += sr.Left;
            y += sr.Top;
        }

        var after = _state == EmbedState.Child ? Native.HWND_TOP : Native.HWND_TOPMOST;

        // 任务栏自己的几何量本来就有几十像素的抖动（TrayNotifyWnd 的矩形会在两套值之间跳），
        // 一抖就重排窗口，肉眼看到的就是「条一会长一会短」。位。尺寸变化小于阈值时直接忽略。
        // 真正需要改变（。DPI、任务栏换边、窗口宽度改了）时差值都远大于这个阈值。
        if (_lastWidth > 0 &&
            Math.Abs(w - _lastWidth) < 64 &&
            Math.Abs(x - _lastX) < 64 &&
            Math.Abs(h - _lastH) < 16)
        {
            return;
        }

        _lastX = x;
        _lastH = h;

        Native.SetWindowPos(
            _hwnd,
            after,
            x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);

        if (w != _lastWidth)
        {
            _lastWidth = w;
            UpdateTrackWidth();
            Log.Write($"Place: {_state} screen={x},{y} {w}x{h} dip={WidthDip:0.#}");
        }

        // 分层窗口（背景不透明。< 100）在尺寸/位置刚变过的那一帧，DWM 有时会留一张空表面。
        // 表现为「条在那儿但什么都看不见」，鼠标划过才刷出来。这里主动要求重画一次，
        // 再过 8 帧补一次就够——补太多次等于让条自己反复藏了又露，看着就是闪。
        _repaintTicks = 9;
        ForceRepaint();
    }

    /// <summary>外部（打开/关闭设置窗口等）把合成顺序打乱后，主动补一次重画。</summary>
    public void KickRepaint()
    {
        if (_hwnd == IntPtr.Zero) return;
        Root.InvalidateVisual();
        UpdateLayout();
        Native.RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero,
            Native.RDW_INVALIDATE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
    }

    private void ForceRepaint()
    {
        if (_hwnd == IntPtr.Zero) return;

        Root.InvalidateVisual();
        UpdateLayout();
        Native.RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero,
            Native.RDW_INVALIDATE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);

        if (!Native.GetWindowRect(_hwnd, out var r)) return;

        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 2) return;

        // 藏一下再露出来能逼 WPF 完整走一遍 present，专治分层窗口首帧空白；
        // 但它是真·闪一下，所以只在建窗后的第一次重画时用，之后只做重绘
        // （之后每次位置变化都藏一次的话，用户看到的就是「条动不动闪一下」）。
        if (_firstPaint)
        {
            _firstPaint = false;
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            Root.InvalidateVisual();
            UpdateLayout();
        }

        if (_state == EmbedState.Overlay) Raise();
    }

    /// <summary>
    /// 歌名那一列的宽度上限：整体宽度的 N%（默认 40% = 2/5）。
    /// 条本身很窄时再加一个 40 DIP 的下限，否则歌名会挤成一个字。
    /// </summary>
    private void UpdateTrackWidth()
    {
        var percent = Math.Clamp(_settings.TrackWidthPercent, 10, 70);
        var max = Math.Max(40, WidthDip * percent / 100.0);
        if (Math.Abs(TrackHost.MaxWidth - max) < 0.5) return;

        TrackHost.MaxWidth = max;
        DebounceScroll();
        UpdateTrackScroll();
    }

    /// <summary>内容自身需要多宽（DIP）。只在「自适应宽度」模式下用。</summary>
    private double NaturalWidthDip()
    {
        // FormattedText 量真实文字宽度：ExtentWidth 会被列宽撑满，不可用
        var text = MeasureText(LyricText);
        if (_settings.EmbShowTrack && TrackHost.Visibility == Visibility.Visible)
            text += Math.Min(MeasureText(TrackText), TrackHost.MaxWidth) + 8;

        text = Math.Clamp(text, 40, 420);

        var cover = _settings.EmbShowCover ? 28 : 0;
        var accent = _settings.EmbStyle == 1 ? 12 : 0;
        const double padding = 19; // 左右内边。
        return Math.Max(140, text + cover + accent + padding);
    }

    private double MeasureText(System.Windows.Controls.TextBlock tb)
    {
        var content = tb.Text ?? string.Empty;
        if (content.Length == 0) return 0;

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ft = new FormattedText(
                content,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize,
                Brushes.White,
                dpi);

            return ft.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return tb.ActualWidth;
        }
    }

    /// <summary>覆盖模式下把窗口重新顶到最前面，防止被任务栏自己压下去。</summary>
    private void Raise()
    {
        if (_state != EmbedState.Overlay || _hwnd == IntPtr.Zero) return;

        // 每帧都往任务栏上顶一次，会逼着资源管理器不停重排它的合成层——肉眼看到的就是
        // 托盘那一片图标突然闪一下再回来。先问一句「我现在还在最上面吗」，
        // 位置没被压下去就什么都不做。
        if (!Native.GetWindowRect(_hwnd, out var r)) return;

        var cx = (r.Left + r.Right) / 2;
        var cy = (r.Top + r.Bottom) / 2;
        var top = Native.WindowFromPoint(new Native.POINT { X = cx, Y = cy });
        if (top != IntPtr.Zero)
        {
            var root = Native.GetAncestor(top, Native.GA_ROOT);
            if (root == _hwnd) return;
        }

        Native.SetWindowPos(
            _hwnd,
            Native.HWND_TOPMOST,
            0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }


    private void ApplyColors(Taskbar.Layout lay)
    {
        ApplyColorsCore(lay);
        MaybeScanIconEdge(lay);
    }

    /// <summary>
    /// 摸一次图标右边界。只在「还没量过」和「任务栏重建（explorer 重启）」的时候做。
    /// 反复量是宽度乱跳的根源，量一次就钉住，宽度在整个会话里恒定。
    /// </summary>
    private void MaybeScanIconEdge(Taskbar.Layout lay)
    {
        if (_scanBusy || _hwnd == IntPtr.Zero || _controlsOpen) return;

        // 一个任务栏（explorer 会话）只量一次。
        // 托盘矩形在开/关任何窗口时都会差几十像素（实测 2032↔2080），
        // 之前按矩形变化触发重测 → 每次开设置窗口都「闪一下」。改成按句柄去重。
        if (_iconRight > 0 && _lastScanTray == lay.Hwnd) return;

        _scanBusy = true;
        _lastScanTray = lay.Hwnd;
        _lastScanTick = _tickCount;

        try
        {
            // 不再为了量像素把自己藏起来：像素扫描在透明任务栏上完全不可靠
            // （壁纸透色、渐变、悬停高亮全会被当成图标），而且 hide/show 本身就是一次闪烁。
            // IconRightEdge 现在只走 UIA，摸不到就按 ReBar 右缘 + 固定余量兜底。
            var raw = Taskbar.IconRightEdge(lay, _bg);
            Log.Write($"IconEdge: raw={raw} icons={lay.IconsRight} trayLeft={lay.TrayLeft} scale={lay.Scale:0.##}");

            if (raw <= 0)
            {
                _iconPending = 0;

                // 量不到就当没有限制，先把条摆出来（否则窗口一直藏着，条不出现）
                if (_iconRight == 0) Place(lay);
                return;
            }

            // 第一次量到（窗口刚建好、任务栏刚重建）直接采用：这时候窗口是藏着的，量得最干净。
            if (_iconRight == 0)
            {
                _iconRight = raw;
                _iconPending = 0;
                _iconStable = 0;
                Place(lay);
                return;
            }

            // 之后只有布局真的变了才会再量，仍然要求连续两次一致。
        if (raw == _iconRight)
            {
                _iconPending = 0;
                _iconStable = 0;
                return;
            }

            if (raw == _iconPending)
            {
                _iconStable++;
            }
            else
            {
                _iconPending = raw;
                _iconStable = 0;
                return;
            }

            if (_iconStable >= 1)
            {
                _iconRight = raw;
                _iconPending = 0;
                _iconStable = 0;
                Place(lay);
            }
        }
        catch (Exception ex)
        {
            Log.Write("MaybeScanIconEdge failed", ex);
        }
        finally
        {
            _scanBusy = false;
        }
    }

    private void ApplyColorsCore(Taskbar.Layout lay)
    {
        var dark = Taskbar.IsDark(_settings);

        // 抓任务栏真实底色（DWM 合成后的画面，含透明工具的模糊壁纸）；不可信就退回主题中性色。
        // 采样区间跟着对齐方式走，条贴在哪儿就在哪儿采，透明任务栏下才不会露出色差。
        var (fromFrac, toFrac) = _settings.EmbAlign switch
        {
            0 => (0.02, 0.30),
            2 => (0.70, 0.99),
            _ => (0.32, 0.68),
        };

        _bg = Taskbar.SafeTaskbarColor(lay, dark, fromFrac, toFrac);

        // 每 2.5 秒都会进来一次。底色和样式没变就别重建画刷——重建会让整条重新合成一遍，
        // 正在滚动的歌词就会顿一下。
        var styleKey = (_bg.A << 24 | _bg.R << 16 | _bg.G << 8 | _bg.B, _settings.EmbStyle, _settings.EmbBgOpacity, _settings.Accent, _settings.EmbBlend, dark);
        if (styleKey == _lastStyleKey) return;
        _lastStyleKey = styleKey;

        // 背景不透明度：0 = 全透明，直接透出任务。壁纸（TranslucentTB 那种效果）。
        // 。1/255 当底，避免整窗变成逐像素点击穿透——否则右键菜单和悬停按钮都会失灵。
        var opacity = Math.Clamp(_settings.EmbBgOpacity, 0, 100);
        var alpha = (byte)Math.Max(1, Math.Round(opacity * 255.0 / 100.0));

        // 底色一律画在带圆角的 Shell 上，窗口自己保持全透明。
        // 早先是给 Window.Background 上色，结果圆角外面会露出一个方块底——
        // 透明度越低越明显（半透明方块叠在任务栏上）。
        var fill = Color.FromArgb(alpha, _bg.R, _bg.G, _bg.B);
        if (AllowsTransparency)
        {
            Background = Brushes.Transparent;
            // 1/255 的底：肉眼看不见，但能让整块区域参与命中测试，
            // 否则全透明的分层窗口会把鼠标事件直接漏给下面的任务栏，右键菜单和悬停按钮全废。
            Root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }
        else
        {
            Root.Background = Brushes.Transparent;
            Background = new SolidColorBrush(_bg);
        }

        // 前景按底色亮度选，兼容透明任务栏下采到的浅色模糊壁。
        var bgLum = (_bg.R * 299 + _bg.G * 587 + _bg.B * 114) / 1000;
        var light = bgLum < 150;

        var fg = light ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1A, 0x1A, 0x1A);
        var dim = light ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00);

        LyricText.Foreground = new SolidColorBrush(fg);
        TrackText.Foreground = new SolidColorBrush(dim);

        // 无卡片模式下文字直接压在任务栏/壁纸上，必须有一层柔化光晕才读得清；
        // 卡片模式本身有底色，再加阴影反而发脏。
        var shadow = _settings.EmbStyle == 0 ? MakeShadow(opacity < 60 ? 9 : 6) : null;
        LyricText.Effect = shadow;
        TrackText.Effect = shadow;

        if (_settings.EmbStyle == 1)
        {
            var accent = ParseColor(_settings.Accent, Color.FromRgb(0x4C, 0xC2, 0xFF));
            var t = Math.Clamp(_settings.EmbBlend, 0, 1) * 0.22;
            var card = Blend(_bg, accent, t);
            var cardAlpha = (byte)Math.Max(1, Math.Round(0xE6 * opacity / 100.0));
            Shell.Background = new SolidColorBrush(Color.FromArgb(cardAlpha, card.R, card.G, card.B));
        }
        else
        {
            Shell.Background = AllowsTransparency ? new SolidColorBrush(fill) : Brushes.Transparent;
        }

        AccentBar.Background = new SolidColorBrush(ParseColor(_settings.Accent, Color.FromRgb(0x4C, 0xC2, 0xFF)));
    }

    private static System.Windows.Media.Effects.DropShadowEffect MakeShadow(double blur) => new()
    {
        BlurRadius = blur,
        ShadowDepth = 0,
        Direction = 0,
        Opacity = 0.9,
        Color = Colors.Black,
    };

    private static Color ParseColor(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return c;
            }
        }
        catch
        {
        }
        return fallback;
    }

    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    // ---------------------------------------------------------------- 设置

    public void ApplySettings()
    {
        var size = Math.Clamp(_settings.EmbFontSize, 9, 22);
        LyricText.FontSize = size;
        TrackText.FontSize = Math.Max(9, size - 2);

        AccentBar.Visibility = _settings.EmbStyle == 1 ? Visibility.Visible : Visibility.Collapsed;
        CoverBox.Visibility = _settings.EmbShowCover ? Visibility.Visible : Visibility.Collapsed;
        CoverBox.Margin = _settings.EmbShowCover ? new Thickness(0, 0, 8, 0) : new Thickness(0);
        AccentBar.Margin = _settings.EmbStyle == 1 ? new Thickness(0, 0, 9, 0) : new Thickness(0);

        // 歌词区：默认吃掉封面和按钮之间的全部宽度（左端贴着封面不动，右端随按钮展开收窄）。
        // 只有手动锁了「歌词宽度」才固定那一列，此时整行仍然左对齐。
        Row.HorizontalAlignment = HorizontalAlignment.Stretch;
        LyricCol.Width = _settings.EmbLyricWidth > 0
            ? new GridLength(Math.Clamp(_settings.EmbLyricWidth, 40, 1200))
            : new GridLength(1, GridUnitType.Star);

        TextStack.HorizontalAlignment = HorizontalAlignment.Left;

        // 歌名列上限跟着「整体宽度 × 比例」走（默认 2/5），窗口宽度一变就重算
        UpdateTrackWidth();

        ShowControls(_settings.EmbShowControls && IsMouseOver, animate: false);
        UpdateText();

        if (_state != EmbedState.None)
        {
            var lay = Taskbar.Probe();
            if (lay is not null)
            {
                ApplyColors(lay);
                Place(lay);
            }
        }
    }

    /// <summary>悬停显形：按钮从 0 宽度拉出来，歌词区同步让位（不改窗口大小）。</summary>
    private void ShowControls(bool on, bool animate = true)
    {
        _controlsOpen = on && _settings.EmbShowControls;

        var dur = animate ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var width = _controlsOpen ? 96.0 : 0.0;
        var opacity = _controlsOpen ? 1.0 : 0.0;

        if (animate)
        {
            ControlsHost.BeginAnimation(MaxWidthProperty, new DoubleAnimation(width, dur) { EasingFunction = ease });
            ControlsHost.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, dur) { EasingFunction = ease });
        }
        else
        {
            ControlsHost.BeginAnimation(MaxWidthProperty, null);
            ControlsHost.BeginAnimation(OpacityProperty, null);
            ControlsHost.MaxWidth = width;
            ControlsHost.Opacity = opacity;
        }
    }

    private void OnRootEnter(object sender, MouseEventArgs e) => ShowControls(true);

    private void OnRootLeave(object sender, MouseEventArgs e) => ShowControls(false);

    private void OnLyricHostSizeChanged(object sender, SizeChangedEventArgs e) => DebounceScroll();

    /// <summary>
    /// 悬停展开/收起按钮时，歌词区宽度在 150 ms 动画里逐帧变化，每帧都重建滚动动画
    /// 就会看到「一直弹回开头」。这里等宽度稳定下来再重建。
    /// </summary>
    private void DebounceScroll()
    {
        _scrollTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220),
        };

        _scrollTimer.Stop();
        _scrollTimer.Tick -= OnScrollTimerTick;
        _scrollTimer.Tick += OnScrollTimerTick;
        _scrollTimer.Start();
    }

    private void OnScrollTimerTick(object? sender, EventArgs e)
    {
        _scrollTimer?.Stop();
        UpdateScroll();
    }

    /// <summary>歌词左右两端线性渐隐；短句不渐隐，避免把字割掉。</summary>
    private void ApplyFade()
    {
        LyricHost.OpacityMask = FadeMask(LyricHost, _overflow);
        ApplyTrackFade();
    }

    /// <summary>歌名滚动时两端同样渐隐（和歌词用同一套渐变宽度）。</summary>
    private void ApplyTrackFade()
    {
        var view = TrackHost.ActualWidth;
        var overflow = TrackHost.Visibility == Visibility.Visible
                       && view > 0
                       && TrackText.ActualWidth > view + 2;

        TrackHost.OpacityMask = FadeMask(TrackHost, overflow);
    }

    private LinearGradientBrush? FadeMask(FrameworkElement host, bool overflow)
    {
        var fade = Math.Clamp(_settings.EmbFade, 0, 60);
        var width = host.ActualWidth;

        if (fade <= 0 || width < 8 || !overflow) return null;

        var f = Math.Min(0.45, fade / width);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, f));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        brush.Freeze();

        return brush;
    }

    private void BindEngine()
    {
        _engine.TrackChanged += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateText();
            _ = UpdateCoverAsync();
        }));

        _engine.LineChanged += () => Dispatcher.BeginInvoke(new Action(UpdateText));
        _engine.LyricsChanged += () => Dispatcher.BeginInvoke(new Action(UpdateText));
        _engine.PlaybackChanged += () => Dispatcher.BeginInvoke(new Action(UpdatePlayIcon));
    }

    private void UpdateText()
    {
        TrackText.Text = _engine.HasTrack ? _engine.Title : "";
        TrackHost.Visibility = _settings.EmbShowTrack && _engine.HasTrack
            ? Visibility.Visible
            : Visibility.Collapsed;

        LyricText.Text = _engine.CurrentText;

        // 文字变了要等一次布局才能量出真实宽度，所以排到 Loaded 优先级再起动画。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTrackScroll();
            UpdateScroll();
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 歌名是独立模块：默认停在开头，满 5 秒才滚一遍，滚到底就停住（不循环）。
    /// 之前它和歌词在同一个 StackPanel 里，跟着歌词一起被推着走，看着很乱。
    /// </summary>
    private void UpdateTrackScroll()
    {
        if (!IsLoaded) return;

        TrackText.BeginAnimation(FrameworkElement.MarginProperty, null);
        TrackText.Margin = new Thickness(0);
        ApplyTrackFade();

        if (TrackHost.Visibility != Visibility.Visible) return;

        var view = TrackHost.ActualWidth;
        var extent = TrackText.ActualWidth;
        if (view <= 0 || extent <= view + 2) return;

        var overflow = extent - view;
        var speed = 26.0;                                        // 歌名滚得慢一点
        var dur = TimeSpan.FromSeconds(Math.Max(1.2, overflow / speed));

        // 每次滚动之间的停顿（默认 1 秒，设置里可调）。
        // 停 → 滚到末尾 → 再停 → 回卷到开头，循环，所以「滚动之间」始终是这个间隔。
        var holdMs = Math.Clamp(_settings.TrackHoldMs, 200, 5000);
        var hold = TimeSpan.FromMilliseconds(holdMs);

        var anim = new ThicknessAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(0), KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(0), KeyTime.FromTimeSpan(hold)));
        anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(-overflow, 0, 0, 0), KeyTime.FromTimeSpan(hold + dur)));
        anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(-overflow, 0, 0, 0), KeyTime.FromTimeSpan(hold + dur + hold)));
        anim.Duration = new Duration(hold + dur + hold);

        TrackText.BeginAnimation(FrameworkElement.MarginProperty, anim);
    }

    private void OnTrackHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyTrackFade();
        UpdateTrackScroll();
    }

    private void UpdatePlayIcon()
    {
        PlayBtn.Content = _engine.IsPlaying ? "\uE769" : "\uE768";
    }

    private async Task UpdateCoverAsync()
    {
        if (!_settings.EmbShowCover)
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

    // ---------------------------------------------------------------- 计时

    /// <summary>。App 的共享计时器调用：跟随任务栏变化 + 文字溢出时走马灯。</summary>
    public void OnTick()
    {
        _tickCount++;

        if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd)) return; // 。App 重建

        // 真子窗口一旦被 explorer 带走就重新挂
        if (_state == EmbedState.Child && Native.GetParent(_hwnd) != Native.FindTray())
        {
            _state = EmbedState.None;
            TryAttach();
            return;
        }

        if (_state == EmbedState.None)
        {
            TryAttach();
            return;
        }

        // 设置窗口开着的时候冻结布局与配色：那期间托盘矩形本来就在跳，
        // 跟着重排就会看到条「跳一下 / 颜色变一下」。
        if (Application.Current is App { BarLayoutFrozen: true })
        {
            if (_state == EmbedState.Overlay) Raise();
            return;
        }

        if (_tickCount % 40 == 0)
        {
            var lay = Taskbar.Probe();
            if (lay is not null)
            {
                ApplyColors(lay);
                Place(lay);
            }
        }
        else if (_state == EmbedState.Overlay)
        {
            Raise();
        }

        ApplyVisibility();

        // 起来之后补几次重画，专治分层窗口首帧空白
        if (_repaintTicks > 0)
        {
            _repaintTicks--;
            if (_repaintTicks % 8 == 0) ForceRepaint();
        }
    }

    /// <summary>跟随任务栏：可见性（自动隐藏/全屏让位。 内容变宽变窄时重新贴合。</summary>
    private void ApplyVisibility()
    {
        if (_tickCount % 8 != 0) return;

        var lay = Taskbar.Probe();
        var visible = Taskbar.TaskbarVisible(lay);

        // 可见性变化必须连续三次一致才生效。SHQueryUserNotificationState 在窗口切换、
        // 弹出全屏视频、通知弹出的瞬间会翻一下，直接跟着翻就会「整条闪一下又回来」。
        if (visible == _shown)
        {
            _visVotes = 0;
        }
        else if (++_visVotes >= 3)
        {
            _shown = visible;
            _visVotes = 0;
            Native.ShowWindow(_hwnd, visible ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
            Log.Write($"Visibility: {visible} quns={Native.UserNotificationState()} offscreen={lay?.Offscreen}");
        }

        if (lay is null || _state == EmbedState.None || !visible) return;

        // 固定宽度模式下窗口尺寸不变，不用。8 tick 重排；自适应模式才跟着内容收放
        if (_settings.EmbAuto)
        {
            var (_, _, w, _) = Taskbar.Place(lay, _settings, WidthDip);
            if (w != _lastWidth) Place(lay);
        }
    }

    /// <summary>
    /// 长歌词滚动。用 WPF 关键帧动画驱动 RenderTransform，而不是每帧去改 ScrollViewer 的
    /// 偏移量——后者受 15.6 tick/s 的计时器限制，还会被滚动的像素对齐反复取整，看起来
    /// 就是「卡 + 左右抽搐」。动画交给合成线程插值，60 fps 平滑，且只在这句开始时建一次。
    /// </summary>
    private void UpdateScroll()
    {
        if (!IsLoaded) return;

        var view = LyricHost.ActualWidth;
        var extent = TextStack.ActualWidth;
        if (view <= 0)
        {
            return;
        }

        var overflow = extent - view;
        _overflow = overflow > 2;

        // 悬停时按钮区在 150 ms 动画里一直变宽，歌词区每帧都在 SizeChanged。
        // 每次都重建动画，看到的就是「滚到一半突然回滚」——所以同样的一行、同样的宽度就别重建。
        var sig = (LyricText.Text, Math.Round(view), Math.Round(overflow));
        if (sig == _scrollSig && _scrollAnim is not null) return;
        _scrollSig = sig;

        ApplyFade();

        // 用 Margin 做位移（布局层面），不用 RenderTransform：
        // 分层窗口里给子树挂变换动画后，那一层会被提升成独立层，实测整段文字直接不画了。
        // 只有一句歌词、两个 TextBlock，每帧重排的开销可以忽略。
        TextStack.BeginAnimation(FrameworkElement.MarginProperty, null);
        TextStack.Margin = new Thickness(0);

        if (!_overflow)
        {
            _scrollAnim = null;
            TextStack.CacheMode = null;
            return;
        }

        // 文字和它的阴影只光栅化一次，之后只是整体平移 —— 60 fps 才不会卡。
        TextStack.CacheMode = new BitmapCache
        {
            RenderAtScale = VisualTreeHelper.GetDpi(this).PixelsPerDip,
        };

        double speed = Math.Clamp(_settings.EmbScrollSpeed, 10, 200); // px/s，自适应模式下当下限用
        var seamless = _settings.EmbScrollMode == 1;

        // 自适应：按「这句还剩多久」倒推速度；拿不到时间轴（网易云/B站）就回落固定速度
        if (_settings.EmbScrollAuto)
        {
            var remain = _engine.TimeToNextLine;
            if (remain is { TotalSeconds: > 0.4 })
            {
                var travel = overflow + (seamless ? 24 : 0);
                var window = remain.Value.TotalSeconds - (seamless ? 0.15 : 1.9); // 留出首尾停顿
                if (window > 0.35) speed = Math.Clamp(travel / window, 8, 420);
            }
        }

        var dur = TimeSpan.FromSeconds(Math.Max(0.25, overflow / Math.Max(1, speed)));

        var anim = new ThicknessAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        if (seamless)
        {
            // 无缝：匀速从 0 拉到 -overflow，到头立刻回卷
            anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(0), KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(-overflow, 0, 0, 0), KeyTime.FromTimeSpan(dur)));
        }
        else
        {
            // 停-滚-停：开头停 0.9 s，滚到底，再停 0.9 s 回卷
            var hold = TimeSpan.FromSeconds(0.9);
            anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(0), KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(0), KeyTime.FromTimeSpan(hold)));
            anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(-overflow, 0, 0, 0), KeyTime.FromTimeSpan(hold + dur)));
            anim.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(-overflow, 0, 0, 0), KeyTime.FromTimeSpan(hold + dur + hold)));
        }

        anim.Duration = new Duration(anim.KeyFrames[^1].KeyTime.TimeSpan);
        TextStack.BeginAnimation(FrameworkElement.MarginProperty, anim);
        _scrollAnim = anim;
    }

    /// <summary>歌词区始终左对齐（左端不动，右端随按钮让位）；溢出时挂两端渐隐。</summary>
    private void SetTextLayout(bool overflow)
    {
        TextStack.HorizontalAlignment = HorizontalAlignment.Left;
        ApplyFade();
    }

    // ---------------------------------------------------------------- 交互

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        await _engine.TogglePlayPauseAsync();
        UpdatePlayIcon();
    }

    private async void OnNext(object sender, RoutedEventArgs e) => await _engine.NextAsync();

    private async void OnPrev(object sender, RoutedEventArgs e) => await _engine.PrevAsync();

    private void OnAlignLeft(object sender, RoutedEventArgs e) => SetAlign(0);

    private void OnAlignCenter(object sender, RoutedEventArgs e) => SetAlign(1);

    private void OnAlignRight(object sender, RoutedEventArgs e) => SetAlign(2);

    private void SetAlign(int align)
    {
        _settings.EmbAlign = align;
        _settings.Save();

        var lay = Taskbar.Probe();
        if (_state != EmbedState.None && lay is not null) Place(lay);
    }

    private void OnReloadLyrics(object sender, RoutedEventArgs e) => _ = _engine.ReloadLyricsAsync();

    private void OnOpenSettings(object sender, RoutedEventArgs e) => App.Current?.OpenControlCenter();

    private void OnSwitchMode(object sender, RoutedEventArgs e) => App.Current?.SetMode(DisplayMode.Floating);

    private void OnExit(object sender, RoutedEventArgs e) => App.Current?.Shutdown();
}
