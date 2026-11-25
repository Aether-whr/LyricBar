using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LyricBar;

/// <summary>
/// 歌词来源优先级：本地 LRC → LRCLIB（免密钥，多语种）→ 网易云音乐。
/// 本地放一份 {艺人} - {歌名}.lrc 到 %APPDATA%\LyricBar\lyrics\ 即可优先命中。
/// </summary>
public static class LyricsApi
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return c;
    }

    private static string LyricsDir => AppPaths.LyricsDir;

    public static async Task<List<LyricLine>> FetchAsync(
        string title, string artist, TimeSpan duration, CancellationToken ct = default)
    {
        var t = Clean(title);
        var a = Clean(artist);
        if (t.Length == 0) return new List<LyricLine>();

        // 1) 本地：先按清洗后的名字找，再按播放器原始标题找（带括号后缀的歌两种都能命中）
        var local = TryLocal(t, a);
        if (local.Count == 0)
        {
            var rawTitle = title.Trim();
            var rawArtist = artist.Trim();
            if (rawTitle != t || rawArtist != a) local = TryLocal(rawTitle, rawArtist);
        }

        if (local.Count > 0) return local;

        // 2) LRCLIB
        var lrclib = await LrclibAsync(t, a, duration, ct).ConfigureAwait(false);
        if (lrclib.Count > 0) return lrclib;

        // 3) 网易云
        try
        {
            var ne = await NeteaseAsync(t, a, ct).ConfigureAwait(false);
            if (ne.Count > 0) return ne;
        }
        catch
        {
        }

        return new List<LyricLine>();
    }

    // ---------- 本地 ----------

    private static List<LyricLine> TryLocal(string title, string artist)
    {
        try
        {
            foreach (var candidate in new[]
                     {
                         $"{artist} - {title}.lrc",
                         $"{title}.lrc",
                         $"{title} - {artist}.lrc",
                     })
            {
                var path = Path.Combine(LyricsDir, Safe(candidate));
                if (!File.Exists(path)) continue;
                var lines = Lrc.Parse(File.ReadAllText(path));
                if (lines.Count > 0) return lines;
            }
        }
        catch
        {
        }
        return new List<LyricLine>();
    }

    private static string Safe(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Trim();
    }

    // ---------- LRCLIB ----------

    private static async Task<List<LyricLine>> LrclibAsync(
        string title, string artist, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}";
            if (artist.Length > 0) url += $"&artist_name={Uri.EscapeDataString(artist)}";
            if (duration.TotalSeconds > 5) url += $"&duration={(int)Math.Round(duration.TotalSeconds)}";

            using (var r = await Http.GetAsync(url, ct).ConfigureAwait(false))
            {
                if (r.IsSuccessStatusCode)
                {
                    var lines = ParseSynced(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (lines.Count > 0) return lines;
                }
            }
        }
        catch
        {
        }

        try
        {
            var q = Uri.EscapeDataString($"{title} {artist}".Trim());
            using var r = await Http.GetAsync($"https://lrclib.net/api/search?q={q}", ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) return new List<LyricLine>();

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<LyricLine>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("syncedLyrics", out var sl)) continue;
                if (sl.ValueKind != JsonValueKind.String) continue;
                var lines = Lrc.Parse(sl.GetString());
                if (lines.Count > 0) return lines;
            }
        }
        catch
        {
        }

        return new List<LyricLine>();
    }

    private static List<LyricLine> ParseSynced(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String)
                return Lrc.Parse(sl.GetString());
        }
        catch
        {
        }
        return new List<LyricLine>();
    }

    // ---------- 网易云 ----------

    private static async Task<List<LyricLine>> NeteaseAsync(string title, string artist, CancellationToken ct)
    {
        var keyword = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";

        long songId;
        using (var search = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/search/get/web"))
        {
            search.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            search.Headers.TryAddWithoutValidation("Origin", "https://music.163.com");
            search.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = keyword,
                ["type"] = "1",
                ["limit"] = "5",
                ["offset"] = "0",
            });

            using var sr = await Http.SendAsync(search, ct).ConfigureAwait(false);
            if (!sr.IsSuccessStatusCode) return new List<LyricLine>();

            using var sd = JsonDocument.Parse(await sr.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!sd.RootElement.TryGetProperty("result", out var res)) return new List<LyricLine>();
            if (!res.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return new List<LyricLine>();
            if (songs.GetArrayLength() == 0) return new List<LyricLine>();

            songId = songs[0].GetProperty("id").GetInt64();
        }

        using var lyricReq = new HttpRequestMessage(HttpMethod.Get,
            $"https://music.163.com/api/song/lyric?id={songId}&lv=-1&kv=-1&tv=-1");
        lyricReq.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");

        using var lr = await Http.SendAsync(lyricReq, ct).ConfigureAwait(false);
        if (!lr.IsSuccessStatusCode) return new List<LyricLine>();

        using var ld = JsonDocument.Parse(await lr.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        string? main = null, trans = null;
        if (ld.RootElement.TryGetProperty("lrc", out var l) && l.TryGetProperty("lyric", out var lv))
            main = lv.GetString();
        if (ld.RootElement.TryGetProperty("tlyric", out var tr) && tr.TryGetProperty("lyric", out var tv))
            trans = tv.GetString();

        var lines = Lrc.Parse(main);
        if (lines.Count == 0) return lines;

        var translated = Lrc.Parse(trans);
        return Lrc.MergeTranslation(lines, translated);
    }

    // ---------- 清洗 ----------

    private static readonly Regex BracketRe =
        new(@"\s*[\(\[（【].*?[\)\]）】]", RegexOptions.Compiled);

    private static readonly Regex SuffixRe =
        new(@"\s*-\s*(remaster(ed)?|live|explicit|radio edit|feat\.?.*|ft\.?.*|instrumental|acoustic).*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var r = BracketRe.Replace(s, "");
        r = SuffixRe.Replace(r, "");
        r = r.Replace("　", " ");
        return Regex.Replace(r, @"\s{2,}", " ").Trim();
    }
}
