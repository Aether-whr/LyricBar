using System.IO;

namespace LyricBar;

/// <summary>排错用极简日志：与 exe 同目录的 lyricbar.log，超过 256KB 自动重置。</summary>
internal static class Log
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    private static readonly string LogPath = System.IO.Path.Combine(
        AppContext.BaseDirectory, "lyricbar.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes) info.Delete();

                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static void Write(string message, Exception ex) => Write($"{message}: {ex.GetType().Name}: {ex.Message}");
}
