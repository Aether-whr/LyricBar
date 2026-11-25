using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LyricBar;

/// <summary>
/// 通过 Windows SMTC (System Media Transport Controls) 读取当前播放器状态并下发控制指令。
/// 支持：网易云 / QQ音乐 / Spotify / 浏览器 / Groove / PotPlayer(SMTC) 等一切接入 SMTC 的播放器。
/// </summary>
public sealed class Smtc : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>歌曲切换（标题/歌手/封面变化）。</summary>
    public event Action? TrackChanged;

    /// <summary>播放/暂停状态变化。</summary>
    public event Action? PlaybackChanged;

    public IRandomAccessStreamReference? Thumbnail { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public TimeSpan Duration { get; private set; }

    public bool HasSession => _session is not null;

    private TimeSpan _base = TimeSpan.Zero;
    private DateTimeOffset _baseAt = DateTimeOffset.UtcNow;

    /// <summary>插值后的播放位置（两次系统时间轴之间由本地时钟推进）。</summary>
    public TimeSpan Position
    {
        get
        {
            var p = _base;
            if (IsPlaying) p += DateTimeOffset.UtcNow - _baseAt;
            if (p < TimeSpan.Zero) return TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && p > Duration) return Duration;
            return p;
        }
    }

    public async Task InitAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.CurrentSessionChanged += (_, _) => Attach();
        _mgr.SessionsChanged += (_, _) => Attach();
        Attach();
    }

    private void Attach()
    {
        Detach();

        var s = _mgr?.GetCurrentSession();
        _session = s;

        if (s is null)
        {
            Title = Artist = Album = "";
            Thumbnail = null;
            IsPlaying = false;
            Duration = TimeSpan.Zero;
            TrackChanged?.Invoke();
            PlaybackChanged?.Invoke();
            return;
        }

        s.MediaPropertiesChanged += (_, _) => _ = RefreshAsync();
        s.PlaybackInfoChanged += (_, _) => PullPlayback();
        s.TimelinePropertiesChanged += (_, _) => SyncTimeline();

        _ = RefreshAsync();
    }

    private void Detach()
    {
        if (_session is null) return;

        // 无法按 lambda 解绑，直接丢弃引用；会话对象随播放器销毁。
        _session = null;
    }

    public async Task RefreshAsync()
    {
        var s = _session;
        if (s is null) return;

        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            var newTitle = p.Title ?? "";
            var newArtist = p.Artist ?? "";

            // 切歌：把本地时钟归零，否则上一首的进度会带过来
            if (newTitle != Title || newArtist != Artist)
            {
                _base = TimeSpan.Zero;
                _baseAt = DateTimeOffset.UtcNow;
            }

            Title = newTitle;
            Artist = newArtist;
            Album = p.AlbumTitle ?? "";
            Thumbnail = p.Thumbnail;

            PullPlayback();
            SyncTimeline();
            TrackChanged?.Invoke();
        }
        catch
        {
            /* 播放器切歌瞬间会抛 COM 异常，忽略即可 */
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PullPlayback()
    {
        var s = _session;
        if (s is null) return;

        GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
        try { status = s.GetPlaybackInfo().PlaybackStatus; }
        catch { return; }

        var playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (playing == IsPlaying) return;

        var p = Position;      // 用旧状态推进到此刻
        IsPlaying = playing;
        _base = p;
        _baseAt = DateTimeOffset.UtcNow;
        PlaybackChanged?.Invoke();
    }

    /// <summary>用系统时间轴校正本地时钟（漂移 >800ms 视为跳转，直接对齐；否则按 40% 缓动收敛）。</summary>
    public void SyncTimeline()
    {
        var s = _session;
        if (s is null) return;

        try
        {
            var tl = s.GetTimelineProperties();

            // 部分播放器（B站客户端、部分浏览器）不上报时间轴，pos/end 恒为 0。
            // 此时必须完全跳过校正，否则每一秒都会把本地时钟重置回 0。
            if (tl.EndTime <= TimeSpan.Zero && tl.Position <= TimeSpan.Zero) return;

            var end = tl.EndTime;
            if (end > TimeSpan.Zero) Duration = end;

            var cur = Position;
            var diffMs = (tl.Position - cur).TotalMilliseconds;

            if (Math.Abs(diffMs) > 800)
            {
                _base = tl.Position;
            }
            else
            {
                _base = cur + TimeSpan.FromMilliseconds(diffMs * 0.4);
            }

            if (_base < TimeSpan.Zero) _base = TimeSpan.Zero;
            _baseAt = DateTimeOffset.UtcNow;
        }
        catch
        {
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var s = _session;
        if (s is null) return;
        try { await s.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync()
    {
        var s = _session;
        if (s is null) return;
        try { await s.TrySkipNextAsync(); } catch { }
    }

    public async Task PrevAsync()
    {
        var s = _session;
        if (s is null) return;
        try { await s.TrySkipPreviousAsync(); } catch { }
    }

    public void Dispose()
    {
        Detach();
        _gate.Dispose();
    }
}
