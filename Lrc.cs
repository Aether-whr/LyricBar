using System.Text.RegularExpressions;

namespace LyricBar;

public readonly record struct LyricLine(TimeSpan Time, string Text);

/// <summary>LRC 解析：支持 [mm:ss]、[mm:ss.xx]、[mm:ss:xx]、一行多时间戳、[offset:n]。</summary>
public static class Lrc
{
    private static readonly Regex TagRe =
        new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    private static readonly Regex OffsetRe =
        new(@"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MetaRe =
        new(@"^\[(ti|ar|al|by|re|ve|length|kana):.*\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<LyricLine> Parse(string? lrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return result;

        var offsetMs = 0;
        var om = OffsetRe.Match(lrc);
        if (om.Success && int.TryParse(om.Groups[1].Value, out var off)) offsetMs = off;

        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || MetaRe.IsMatch(line)) continue;

            var matches = TagRe.Matches(line);
            if (matches.Count == 0) continue;

            var text = TagRe.Replace(line, "").Trim();
            if (text.Length == 0) continue;

            foreach (Match m in matches)
            {
                var mm = int.Parse(m.Groups[1].Value);
                var ss = int.Parse(m.Groups[2].Value);
                var ms = 0;
                if (m.Groups[3].Success)
                {
                    var f = m.Groups[3].Value;
                    ms = f.Length switch
                    {
                        1 => int.Parse(f) * 100,
                        2 => int.Parse(f) * 10,
                        _ => int.Parse(f[..3]),
                    };
                }

                var t = new TimeSpan(0, 0, mm, ss, ms) + TimeSpan.FromMilliseconds(offsetMs);
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                result.Add(new LyricLine(t, text));
            }
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));

        var dedup = new List<LyricLine>(result.Count);
        foreach (var l in result)
            if (dedup.Count == 0 || dedup[^1].Time != l.Time) dedup.Add(l);

        return dedup;
    }

    /// <summary>把翻译行（时间轴相同的 LRC）合并到主歌词后面。</summary>
    public static List<LyricLine> MergeTranslation(List<LyricLine> main, List<LyricLine> trans)
    {
        if (trans.Count == 0) return main;
        var map = new Dictionary<long, string>();
        foreach (var t in trans) map[t.Time.Ticks] = t.Text;

        var outp = new List<LyricLine>(main.Count);
        foreach (var l in main)
        {
            if (map.TryGetValue(l.Time.Ticks, out var tr) && tr != l.Text)
                outp.Add(new LyricLine(l.Time, $"{l.Text}\n{tr}"));
            else
                outp.Add(l);
        }
        return outp;
    }
}
