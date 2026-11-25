namespace LyricBar;

/// <summary>
/// 歌词状态中枢：把 SMTC 的播放信息 + 歌词文本 + 时间轴合成一个"当前该显示什么"的答案。
/// 两种显示模式共用同一份状态，切模式不需要重新取词。
/// </summary>
public sealed class LyricEngine : IDisposable
{
    private readonly Settings _settings;
    private readonly Smtc _smtc = new();
    private readonly List<LyricLine> _lines = new();

    private int _idx = -1;
    private string _key = "";
    private int _tick;
    private int _loading;

    public LyricEngine(Settings settings)
    {
        _settings = settings;
    }

    /// <summary>曲目变化（标题/歌手/封面）。</summary>
    public event Action? TrackChanged;

    /// <summary>当前高亮行变化。</summary>
    public event Action? LineChanged;

    /// <summary>播放/暂停切换。</summary>
    public event Action? PlaybackChanged;

    /// <summary>一整份歌词换掉了。</summary>
    public event Action? LyricsChanged;

    public Smtc Media => _smtc;
    public IReadOnlyList<LyricLine> Lines => _lines;
    public int CurrentIndex => _idx;

    public bool HasTrack => _smtc.HasSession && !string.IsNullOrWhiteSpace(_smtc.Title);
    public string Title => _smtc.Title;
    public string Artist => _smtc.Artist;
    public string Album => _smtc.Album;
    public bool IsPlaying => _smtc.IsPlaying;
    public TimeSpan Duration => _smtc.Duration;

    public TimeSpan Position
    {
        get
        {
            var p = _smtc.Position + TimeSpan.FromMilliseconds(_settings.OffsetMs);
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    /// <summary>
    /// 距离下一句还有多久（当前句的剩余时长）。用来给走马灯算自适应速度：
    /// 这句话能显示 6 秒，滚动就该在 6 秒内刚好走完。
    /// 拿不到下一句（最后一行 / 播放器不上报时间轴）时返回 null，调用方回落到固定速度。
    /// </summary>
    public TimeSpan? TimeToNextLine
    {
        get
        {
            if (_lines.Count == 0) return null;

            var pos = Position;
            var next = _idx >= 0 && _idx + 1 < _lines.Count
                ? _lines[_idx + 1].Time
                : (TimeSpan?)null;

            if (next is null)
            {
                // 没有下一句时，用总时长兜底；总时长也未知就别猜了
                var dur = _smtc.Duration;
                if (dur <= TimeSpan.Zero) return null;
                var left = dur - pos;
                return left > TimeSpan.Zero ? left : null;
            }

            var remain = next.Value - pos;
            return remain > TimeSpan.Zero ? remain : null;
        }
    }

    /// <summary>当前这一句；没有时给占位文案。</summary>
    public string CurrentText
    {
        get
        {
            if (_idx >= 0 && _idx < _lines.Count) return _lines[_idx].Text;
            if (!HasTrack) return "未在播放";
            if (_lines.Count == 0) return "暂无歌词";

            // 前奏还没到第一句时，先把第一句显示出来——比一个「…」友好得多
            return _lines[0].Text;
        }
    }

    public string NextText
    {
        get
        {
            var n = _idx + 1;
            return n >= 0 && n < _lines.Count ? _lines[n].Text : "";
        }
    }

    public async Task InitAsync()
    {
        _smtc.TrackChanged += OnMediaTrackChanged;
        _smtc.PlaybackChanged += () => PlaybackChanged?.Invoke();

        try
        {
            await _smtc.InitAsync();
        }
        catch
        {
        }

        await ReloadLyricsAsync();
    }

    private void OnMediaTrackChanged()
    {
        TrackChanged?.Invoke();
        _ = ReloadLyricsAsync();
    }

    /// <summary>由 UI 共用的 80ms 计时器驱动。</summary>
    public void Tick()
    {
        _tick++;

        if (_tick % 12 == 0) _smtc.SyncTimeline();   // ≈1s 与系统时间轴对齐

        if (_lines.Count == 0)
        {
            if (_idx != -1)
            {
                _idx = -1;
                LineChanged?.Invoke();
            }
            return;
        }

        var i = IndexAt(_lines, Position);
        if (i == _idx) return;

        _idx = i;
        LineChanged?.Invoke();
    }

    public async Task ReloadLyricsAsync()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            var key = _smtc.Title + "\u0001" + _smtc.Artist;
            _key = key;

            _lines.Clear();
            _idx = -1;
            LyricsChanged?.Invoke();

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

            if (key != _smtc.Title + "\u0001" + _smtc.Artist) return;   // 抓取期间已经切歌

            _lines.Clear();
            _lines.AddRange(got);
            _idx = -1;
            LyricsChanged?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    public Task TogglePlayPauseAsync() => _smtc.TogglePlayPauseAsync();

    public Task NextAsync() => _smtc.NextAsync();

    public Task PrevAsync() => _smtc.PrevAsync();

    /// <summary>进度比例，播放器不报时长时用歌词末行估算。</summary>
    public double ProgressRatio()
    {
        var total = Duration.TotalMilliseconds;
        if (total <= 1000 && _lines.Count > 0 && _lines[^1].Time > TimeSpan.Zero)
            total = _lines[^1].Time.TotalMilliseconds + 8000;

        if (total <= 1000) return 0;
        return Math.Clamp(Position.TotalMilliseconds / total, 0, 1);
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

    public void Dispose()
    {
        _smtc.Dispose();
    }
}
