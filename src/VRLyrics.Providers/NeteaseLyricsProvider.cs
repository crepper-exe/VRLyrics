using System.Text.Json;
using VRLyrics.Core.Abstractions;
using VRLyrics.Core.Matching;
using VRLyrics.Core.Models;
using VRLyrics.Core.Parsing;

namespace VRLyrics.Providers;

/// <summary>
/// NetEase Cloud Music (缃戞槗浜戦煶涔?. Provides word-timed YRC lyrics when available, plus translations.
/// Uses the public (unofficial) web API endpoints.
/// </summary>
public sealed class NeteaseLyricsProvider : ILyricsProvider, IDisposable
{
    private const string SearchUrl = "https://music.163.com/api/cloudsearch/pc";
    private const string LyricUrl = "https://music.163.com/api/song/lyric/v1";

    private readonly HttpClient _http;

    public NeteaseLyricsProvider(HttpClient? http = null)
    {
        _http = http ?? Http.CreateClient();
        _http.DefaultRequestHeaders.Referrer ??= new Uri("https://music.163.com/");
    }

    public string Name => "NetEase";

    public async Task<Lyrics?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default)
    {
        if (track.NeteaseSongId is > 0 and { } exactId)
        {
            var direct = await GetLyricsByIdAsync(exactId, cancellationToken).ConfigureAwait(false);
            if (direct is not null && !direct.IsEmpty)
            {
                return direct with
                {
                    SourceId = exactId.ToString(),
                    MatchedTitle = track.Title,
                    MatchedArtist = track.Artist,
                    MatchScore = 1.0,
                };
            }
        }

        var best = await FindBestMatchAsync(track, cancellationToken).ConfigureAwait(false);
        if (best is null) return null;

        var lyrics = await GetLyricsByIdAsync(best.Value.Id, cancellationToken).ConfigureAwait(false);
        return lyrics is null ? null : lyrics with
        {
            SourceId = best.Value.Id.ToString(),
            MatchedTitle = best.Value.Title,
            MatchedArtist = string.Join(" / ", best.Value.Artists),
            MatchScore = best.Value.Score,
        };
    }

    public async Task<(long Id, string Title, string[] Artists, double Score)?> FindBestMatchAsync(TrackInfo track, CancellationToken ct)
    {
        var queries = new List<string>();
        var core = TrackMatcher.CoreTitle(track.Title);
        if (core.Length == 0) core = track.Title;
        queries.Add($"{core} {track.Artist}".Trim());
        if (!string.IsNullOrWhiteSpace(track.Artist)) queries.Add(core);

        (long, string, string[], double)? best = null;
        foreach (var q in queries)
        {
            foreach (var song in await SearchAsync(q, 15, ct).ConfigureAwait(false))
            {
                var score = song.Names.Max(n => TrackMatcher.Score(track, n, song.Artists, song.Duration));
                if (best is null || score > best.Value.Item4)
                    best = (song.Id, song.Names[0], song.Artists, score);
            }
            if (best is { Item4: >= 0.85 }) break; // good enough, skip the broader query
        }

        return best is { Item4: >= TrackMatcher.MinimumScore } ? best : null;
    }

    public sealed record SearchResult(long Id, string[] Names, string[] Artists, TimeSpan? Duration);

    public async Task<List<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var url = $"{SearchUrl}?s={Uri.EscapeDataString(query)}&type=1&limit={limit}&offset=0";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var results = new List<SearchResult>();
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var s in songs.EnumerateArray())
        {
            var id = s.GetProperty("id").GetInt64();
            var names = new List<string> { s.GetProperty("name").GetString() ?? "" };
            foreach (var aliasProp in new[] { "alia", "tns" })
                if (s.TryGetProperty(aliasProp, out var alia) && alia.ValueKind == JsonValueKind.Array)
                    names.AddRange(alia.EnumerateArray().Select(a => a.GetString()).OfType<string>().Where(a => a.Length > 0));

            var artists = s.TryGetProperty("ar", out var ar) && ar.ValueKind == JsonValueKind.Array
                ? ar.EnumerateArray().Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().ToArray()
                : [];
            TimeSpan? duration = s.TryGetProperty("dt", out var dt) && dt.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromMilliseconds(dt.GetInt64())
                : null;
            results.Add(new SearchResult(id, names.ToArray(), artists, duration));
        }
        return results;
    }

    public async Task<Lyrics?> GetLyricsByIdAsync(long songId, CancellationToken ct)
    {
        var url = $"{LyricUrl}?id={songId}&lv=0&tv=0&rv=0&kv=0&yv=0&ytv=0&yrv=0";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        string? Get(string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("lyric", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()
                : null;

        var yrc = Get("yrc");
        var lrc = Get("lrc");

        Lyrics lyrics;
        bool wordTimed;
        if (!string.IsNullOrWhiteSpace(yrc) && YrcParser.ParseLines(yrc) is { Count: > 0 } yrcLines)
        {
            lyrics = LyricsNormalizer.Normalize(yrcLines, Name);
            wordTimed = true;
        }
        else if (!string.IsNullOrWhiteSpace(lrc))
        {
            lyrics = LrcParser.Parse(lrc, Name);
            wordTimed = false;
        }
        else
        {
            return null;
        }

        if (lyrics.IsEmpty) return null;

        // Translations: prefer the YRC-aligned one when we use YRC.
        var trans = (wordTimed ? Get("ytlrc") : null) ?? Get("tlyric");
        if (!string.IsNullOrWhiteSpace(trans))
            lyrics = LyricsNormalizer.AttachTranslation(lyrics, LrcParser.ParseLines(trans));

        var roma = (wordTimed ? Get("yromalrc") : null) ?? Get("romalrc");
        if (!string.IsNullOrWhiteSpace(roma))
            lyrics = LyricsNormalizer.AttachTranslation(lyrics, LrcParser.ParseLines(roma), romanization: true);

        return lyrics;
    }

    public void Dispose() => _http.Dispose();
}
