using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LyricBar;

public enum DisplayMode
{
    /// <summary>嵌进任务栏本体，作为 Shell_TrayWnd 的子窗口。</summary>
    Embedded = 0,

    /// <summary>独立的置顶浮窗，贴在屏幕工作区边缘。</summary>
    Floating = 1,
}

/// <summary>全部可配置项，落盘到 %APPDATA%\LyricBar\settings.json。</summary>
public sealed class Settings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static string FilePath => AppPaths.SettingsFile;

    // ------------------------------------------------------------ 通用

    private DisplayMode _mode = DisplayMode.Embedded;
    public DisplayMode Mode { get => _mode; set => Set(ref _mode, value); }

    /// <summary>0 跟随系统，1 强制深色，2 强制浅色。</summary>
    private int _themeMode = 0;
    public int ThemeMode { get => _themeMode; set => Set(ref _themeMode, value); }

    /// <summary>歌词整体偏移（毫秒），正数让歌词提前。</summary>
    private int _offsetMs = 0;
    public int OffsetMs { get => _offsetMs; set => Set(ref _offsetMs, value); }

    private string _accent = "#4CC2FF";
    public string Accent { get => _accent; set => Set(ref _accent, value); }

    // ------------------------------------------------------------ 嵌入任务栏

    /// <summary>0 靠图标区，1 居中，2 靠托盘。</summary>
    private int _embAlign = 2;
    public int EmbAlign { get => _embAlign; set => Set(ref _embAlign, value); }

    /// <summary>像素宽，0 表示自适应剩余空白。</summary>
    private int _embWidth = 420;
    public int EmbWidth { get => _embWidth; set => Set(ref _embWidth, value); }

    /// <summary>整体宽度随歌词长短收放（关掉则固定为 EmbWidth）。</summary>
    private bool _embAuto = false;
    public bool EmbAuto { get => _embAuto; set => Set(ref _embAuto, value); }

    /// <summary>歌词区宽度（DIP），0 = 占满剩余空间。</summary>
    private int _embLyricWidth = 0;
    public int EmbLyricWidth { get => _embLyricWidth; set => Set(ref _embLyricWidth, value); }

    /// <summary>歌词左右两端的线性渐隐宽度（DIP），0 = 硬边。</summary>
    private int _embFade = 14;
    public int EmbFade { get => _embFade; set => Set(ref _embFade, value); }

    /// <summary>长歌词滚动方式：0 停-滚-停，1 无缝连续。</summary>
    private int _embScrollMode = 0;
    public int EmbScrollMode { get => _embScrollMode; set => Set(ref _embScrollMode, value); }

    /// <summary>滚动速度（DIP/秒）。</summary>
    private int _embScrollSpeed = 40;
    public int EmbScrollSpeed { get => _embScrollSpeed; set => Set(ref _embScrollSpeed, value); }

    private bool _embScrollAuto = true;
    public bool EmbScrollAuto { get => _embScrollAuto; set => Set(ref _embScrollAuto, value); }

    private double _embFontSize = 13;
    public double EmbFontSize { get => _embFontSize; set => Set(ref _embFontSize, value); }

    private bool _embShowCover = true;
    public bool EmbShowCover { get => _embShowCover; set => Set(ref _embShowCover, value); }

    private bool _embShowControls = true;
    public bool EmbShowControls { get => _embShowControls; set => Set(ref _embShowControls, value); }

    /// <summary>在歌词右侧显示「歌手 · 歌名」。</summary>
    private bool _embShowTrack = false;
    public bool EmbShowTrack { get => _embShowTrack; set => Set(ref _embShowTrack, value); }

    /// <summary>歌名那一列最多占整体宽度的百分之几（默认 40 = 2/5）。</summary>
    private int _trackWidthPercent = 40;
    public int TrackWidthPercent { get => _trackWidthPercent; set => Set(ref _trackWidthPercent, value); }

    /// <summary>歌名滚动一圈之间停顿多久（毫秒）。</summary>
    private int _trackHoldMs = 1000;
    public int TrackHoldMs { get => _trackHoldMs; set => Set(ref _trackHoldMs, value); }

    /// <summary>0 纯文字（融进任务栏底色），1 卡片（带描边底）。</summary>
    private int _embStyle = 0;
    public int EmbStyle { get => _embStyle; set => Set(ref _embStyle, value); }

    /// <summary>背景与任务栏底色的融合强度，1 = 完全跟随采样色。</summary>
    private double _embBlend = 1.0;
    public double EmbBlend { get => _embBlend; set => Set(ref _embBlend, value); }

    /// <summary>条自身背景的不透明度，0 = 全透明（直接透出任务栏/壁纸），100 = 实色。</summary>
    private int _embBgOpacity = 100;
    public int EmbBgOpacity { get => _embBgOpacity; set => Set(ref _embBgOpacity, value); }

    // ------------------------------------------------------------ 贴边浮窗

    /// <summary>0 贴左，1 贴右。</summary>
    private int _floatSide = 0;
    public int FloatSide { get => _floatSide; set => Set(ref _floatSide, value); }

    private int _floatWidth = 236;
    public int FloatWidth { get => _floatWidth; set => Set(ref _floatWidth, value); }

    private double _floatFontSize = 12.5;
    public double FloatFontSize { get => _floatFontSize; set => Set(ref _floatFontSize, value); }

    private double _floatOpacity = 0.92;
    public double FloatOpacity { get => _floatOpacity; set => Set(ref _floatOpacity, value); }

    private bool _floatShowCover = true;
    public bool FloatShowCover { get => _floatShowCover; set => Set(ref _floatShowCover, value); }

    private bool _floatShowProgress = true;
    public bool FloatShowProgress { get => _floatShowProgress; set => Set(ref _floatShowProgress, value); }

    private bool _floatShowControls = true;
    public bool FloatShowControls { get => _floatShowControls; set => Set(ref _floatShowControls, value); }

    // ------------------------------------------------------------ 持久化

    /// <summary>配置文件是否已存在（用来判断首次启动）。</summary>
    public static bool Exists => File.Exists(FilePath);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s is not null) return s;
            }
        }
        catch
        {
        }

        var fresh = new Settings();
        fresh.MigrateLegacy();
        return fresh;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Write("Settings.Save failed", ex);
        }
    }

    /// <summary>把旧版 MainWindow 写在注册表里的 Side 迁过来。</summary>
    private void MigrateLegacy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\LyricBar");
            if (key?.GetValue("Side") is int v && v is >= 0 and <= 1) FloatSide = v;
        }
        catch
        {
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
