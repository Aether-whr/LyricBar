using System.IO;
using System.Runtime.CompilerServices;

namespace LyricBar;

/// <summary>
/// 进程级启动钩子。必须在任何窗口创建之前把 DPI 感知切成 PerMonitorV2，
/// 否则跨进程 SetParent 到 explorer 的任务栏会被系统拒绝。
/// </summary>
internal static class Startup
{
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var before = Native.ProcessDpiAwareness();
            var ok = Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            var err = ok ? 0 : Native.GetLastError();
            var after = Native.ProcessDpiAwareness();
            Log.Write($"ModuleInit: dpi before={before} setPmv2={ok} err={err} after={after}");
        }
        catch (Exception ex)
        {
            Log.Write("ModuleInit failed", ex);
        }
    }
}

/// <summary>
/// 数据目录解析：优先 %APPDATA%\LyricBar，不可写时退到 exe 同目录的 data\。
/// </summary>
internal static class AppPaths
{
    private static readonly Lazy<string> RootLazy = new(Resolve);

    public static string Root => RootLazy.Value;

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LyricsDir
    {
        get
        {
            var dir = Path.Combine(Root, "lyrics");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
            }

            return dir;
        }
    }

    private static string Resolve()
    {
        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LyricBar");
        try
        {
            Directory.CreateDirectory(roaming);
            var probe = Path.Combine(roaming, ".writable");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return roaming;
        }
        catch (Exception ex)
        {
            Log.Write("AppPaths: roaming 不可写，退到 exe 目录", ex);
            var local = Path.Combine(AppContext.BaseDirectory, "data");
            try
            {
                Directory.CreateDirectory(local);
            }
            catch
            {
            }

            return local;
        }
    }
}
