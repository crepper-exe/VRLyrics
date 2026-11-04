using System.Net;
using System.Text;
using System.Text.Json;
using VRLyrics.Core.Abstractions;
using VRLyrics.Core.Matching;
using VRLyrics.Core.Models;
using VRLyrics.Core.Parsing;

namespace VRLyrics.Providers;

/// <summary>
/// QQ Music (QQ音乐) lyrics provider. Fetches synchronized LRC and bilingual translation (<c>trans</c>)
/// from QQ Music's public web endpoints.
/// </summary>
public sealed class QqMusicLyricsProvider : ILyricsProvider, IDisposable
{
    private const string SearchUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string LyricUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";

    private readonly HttpClient _http;

    public QqMusicLyricsProvider(HttpClient? http = null)
    {
        _http = http ?? Http.CreateClient();
        _http.DefaultRequestHeaders.Referrer ??= new Uri("https://y.qq.com/");
    }

    public string Name => "QQMusic";

    public async Task<Lyrics?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default)
    {
        var best = await FindBestMatchAsync(track, cancellationToken).ConfigureAwait(false);
        if (best is null) return null;

        var lyrics = await GetLyricsByMidAsync(best.Value.SongMid, cancellationToken).ConfigureAwait(false);
        return lyrics is null ? null : lyrics with
        {
            SourceId = best.Value.SongMid,
            MatchedTitle = best.Value.Title,
            MatchedArtist = string.Join(" / ", best.Value.Artists),
            MatchScore = best.Value.Score,
        };
    }

    public sealed record QqSearchResult(string SongMid, string Title, string[] Artists, TimeSpan? Duration);

    public async Task<(string SongMid, string Title, string[] Artists, double Score)?> FindBestMatchAsync(
        TrackInfo track,
        CancellationToken ct)
    {
        var core = TrackMatcher.CoreTitle(track.Title);
        if (core.Length == 0) core = track.Title;

        var query = $"{core} {track.Artist}".Trim();
        var candidates = await SearchAsync(query, 12, ct).ConfigureAwait(false);

        (string SongMid, string Title, string[] Artists, double Score)? best = null;
        foreach (var song in candidates)
        {
            var score = TrackMatcher.Score(track, song.Title, song.Artists, song.Duration);
            if (best is null || score > best.Value.Score)
                best = (song.SongMid, song.Title, song.Artists, score);
        }

        return best is { Score: >= TrackMatcher.MinimumScore } ? best : null;
    }

    public async Task<List<QqSearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var payload = $$"""
        {
          "req_1": {
            "method": "DoSearchForQQMusicDesktop",
            "module": "music.search.SearchCgiService",
            "param": {
              "num_per_page": {{limit}},
              "page_num": 1,
              "query": {{JsonSerializer.Serialize(query)}},
              "search_type": 0
            }
          }
        }
        """;

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(SearchUrl, content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var results = new List<QqSearchResult>();
        if (!doc.RootElement.TryGetProperty("req_1", out var req1) ||
            !req1.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("body", out var body) ||
            !body.TryGetProperty("song", out var songObj) ||
            !songObj.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in list.EnumerateArray())
        {
            var mid = item.TryGetProperty("mid", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(mid)) continue;

            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "";

            var artists = item.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array
                ? singers.EnumerateArray()
                    .Select(s => s.TryGetProperty("name", out var sn) ? sn.GetString() : null)
                    .OfType<string>()
                    .ToArray()
                : [];

            TimeSpan? duration = item.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromSeconds(iv.GetInt32())
                : null;

            results.Add(new QqSearchResult(mid, name, artists, duration));
        }

        return results;
    }

    public async Task<Lyrics?> GetLyricsByMidAsync(string songMid, CancellationToken ct)
    {
        var url = $"{LyricUrl}?songmid={Uri.EscapeDataString(songMid)}&format=json&nobase64=0";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://y.qq.com/portal/player.html");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var rawText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var jsonText = UnwrapJsonp(rawText);
        if (string.IsNullOrWhiteSpace(jsonText)) return null;

        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        var lyricB64 = root.TryGetProperty("lyric", out var lp) && lp.ValueKind == JsonValueKind.String
            ? lp.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(lyricB64)) return null;

        var lrcText = DecodeBase64OrPlain(lyricB64);
        if (string.IsNullOrWhiteSpace(lrcText)) return null;

        var lyrics = LrcParser.Parse(lrcText, Name);
        if (lyrics.IsEmpty) return null;

        var transB64 = root.TryGetProperty("trans", out var tp) && tp.ValueKind == JsonValueKind.String
            ? tp.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(transB64))
        {
            var transText = DecodeBase64OrPlain(transB64);
            if (!string.IsNullOrWhiteSpace(transText))
                lyrics = LyricsNormalizer.AttachTranslation(lyrics, LrcParser.ParseLines(transText));
        }

        return lyrics;
    }

    public static string DecodeBase64OrPlain(string input)
    {
        try
        {
            var bytes = Convert.FromBase64String(input.Trim());
            return WebUtility.HtmlDecode(Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return WebUtility.HtmlDecode(input);
        }
    }

    private static string UnwrapJsonp(string text)
    {
        var trimmed = text.Trim();
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return trimmed[firstBrace..(lastBrace + 1)];
        return trimmed;
    }

    public void Dispose() => _http.Dispose();
}
