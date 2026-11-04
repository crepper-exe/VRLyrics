using System.Globalization;
using System.Net;
using System.Text.Json;
using VRLyrics.Core.Abstractions;
using VRLyrics.Core.Matching;
using VRLyrics.Core.Models;
using VRLyrics.Core.Parsing;

namespace VRLyrics.Providers;

/// <summary>LRCLIB (https://lrclib.net) 鈥?free, open lyrics database with line-synced LRC.</summary>
public sealed class LrclibLyricsProvider : ILyricsProvider, IDisposable
{
    private const string BaseUrl = "https://lrclib.net/api";
    private readonly HttpClient _http;

    public LrclibLyricsProvider(HttpClient? http = null)
    {
        // LRCLIB asks clients to identify themselves.
        _http = http ?? Http.CreateClient("VRLyrics/0.1 (https://github.com/; SteamVR lyrics overlay)");
    }

    public string Name => "LRCLIB";

    public async Task<Lyrics?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default)
    {
        // 1) Exact lookup (fast, uses LRCLIB's own matching incl. duration).
        if (!string.IsNullOrWhiteSpace(track.Artist))
        {
            var url = $"{BaseUrl}/get?track_name={Uri.EscapeDataString(track.Title)}&artist_name={Uri.EscapeDataString(track.Artist)}";
            if (!string.IsNullOrWhiteSpace(track.Album)) url += $"&album_name={Uri.EscapeDataString(track.Album)}";
            if (track.Duration is { TotalSeconds: > 0 } d) url += $"&duration={Math.Round(d.TotalSeconds).ToString(CultureInfo.InvariantCulture)}";

            var exact = await GetRecordAsync(url, cancellationToken).ConfigureAwait(false);
            if (exact is not null && ToLyrics(exact.Value) is { } lyr && !lyr.IsUnsynced) return lyr;
        }

        // 2) Search and score ourselves.
        var core = TrackMatcher.CoreTitle(track.Title);
        var searchUrl = $"{BaseUrl}/search?track_name={Uri.EscapeDataString(core.Length > 0 ? core : track.Title)}";
        if (!string.IsNullOrWhiteSpace(track.Artist)) searchUrl += $"&artist_name={Uri.EscapeDataString(track.Artist)}";

        using var resp = await _http.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        Lyrics? best = null;
        double bestScore = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var title = item.GetProperty("trackName").GetString() ?? "";
            var artist = item.GetProperty("artistName").GetString() ?? "";
            TimeSpan? dur = item.TryGetProperty("duration", out var dp) && dp.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromSeconds(dp.GetDouble()) : null;
            var score = TrackMatcher.Score(track, title, [artist], dur);
            var lyrics = ToLyrics(item.Clone());
            if (lyrics is null) continue;
            if (!lyrics.IsUnsynced) score += 0.05; // prefer synced
            if (score > bestScore) { bestScore = score; best = lyrics with { MatchScore = Math.Min(1, score) }; }
        }

        return bestScore >= TrackMatcher.MinimumScore ? best : null;
    }

    private async Task<JsonElement?> GetRecordAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound || !resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private Lyrics? ToLyrics(JsonElement item)
    {
        string? Str(string name) => item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        if (item.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True)
            return null;

        var synced = Str("syncedLyrics");
        var plain = Str("plainLyrics");
        var text = !string.IsNullOrWhiteSpace(synced) ? synced : plain;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lyrics = LrcParser.Parse(text, Name);
        if (lyrics.IsEmpty) return null;
        return lyrics with
        {
            SourceId = item.TryGetProperty("id", out var id) ? id.ToString() : null,
            MatchedTitle = Str("trackName"),
            MatchedArtist = Str("artistName"),
        };
    }

    public void Dispose() => _http.Dispose();
}
