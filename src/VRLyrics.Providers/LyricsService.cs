using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VRLyrics.Core.Abstractions;
using VRLyrics.Core.Matching;
using VRLyrics.Core.Models;

namespace VRLyrics.Providers;

/// <summary>Result of a lyrics lookup, including diagnostics for the UI.</summary>
public sealed record LyricsLookupResult(TrackInfo Track, Lyrics? Lyrics, bool FromCache, IReadOnlyList<string> Log);

/// <summary>Queries providers in order, with memory + disk caching.</summary>
public sealed class LyricsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly string? _cacheDir;
    private readonly ConcurrentDictionary<string, Lyrics?> _memory = new();

    public LyricsService(IEnumerable<ILyricsProvider> providers, string? cacheDirectory = null)
    {
        _providers = providers.ToList();
        _cacheDir = cacheDirectory;
        if (_cacheDir is not null) Directory.CreateDirectory(_cacheDir);
    }

    public IReadOnlyList<ILyricsProvider> Providers => _providers;

    public static string DefaultCacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRLyrics", "cache");

    public async Task<LyricsLookupResult> GetLyricsAsync(
        TrackInfo track,
        bool bypassCache = false,
        CancellationToken ct = default,
        string? preferredProviderName = null)
    {
        var log = new List<string>();
        var normalizedPref = string.IsNullOrWhiteSpace(preferredProviderName) ? "Auto" : preferredProviderName.Trim();
        var isExplicitProvider = !string.Equals(normalizedPref, "Auto", StringComparison.OrdinalIgnoreCase);
        var key = CacheKey(track, normalizedPref);

        if (!bypassCache)
        {
            if (_memory.TryGetValue(key, out var mem))
                return new LyricsLookupResult(track, mem, true, ["memory cache"]);
            if (TryReadDisk(key) is { } disk)
            {
                _memory[key] = disk;
                return new LyricsLookupResult(track, disk, true, ["disk cache"]);
            }
        }

        var orderedProviders = isExplicitProvider
            ? _providers
                .OrderByDescending(p => string.Equals(p.Name, normalizedPref, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : _providers;

        Lyrics? doubtful = null; // synced but low-confidence match (e.g. a cover), used if nobody has a confident one
        Lyrics? fallback = null; // unsynced lyrics, used only if nobody has synced ones
        foreach (var provider in orderedProviders)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var isPreferred = isExplicitProvider &&
                                  string.Equals(provider.Name, normalizedPref, StringComparison.OrdinalIgnoreCase);

                var lyrics = await provider.GetLyricsAsync(track, ct).ConfigureAwait(false);
                if (lyrics is null || lyrics.IsEmpty)
                {
                    log.Add($"{provider.Name}: not found");
                    continue;
                }
                if (lyrics.IsUnsynced)
                {
                    log.Add($"{provider.Name}: unsynced only");
                    fallback ??= lyrics;
                    continue;
                }

                var minScore = isPreferred ? 0.25 : TrackMatcher.AcceptThreshold;
                if (lyrics.MatchScore < minScore)
                {
                    log.Add($"{provider.Name}: weak match {lyrics.MatchScore:0.00} ({lyrics.MatchedArtist} - {lyrics.MatchedTitle})");
                    if (doubtful is null || lyrics.MatchScore > doubtful.MatchScore) doubtful = lyrics;
                    continue;
                }

                log.Add($"{provider.Name}: OK {lyrics.MatchScore:0.00} ({lyrics.Lines.Count} lines{(lyrics.HasWordTiming ? ", word-timed" : "")}{(lyrics.HasTranslation ? ", translated" : "")})");
                Store(key, lyrics);
                return new LyricsLookupResult(track, lyrics, false, log);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"{provider.Name}: error {ex.GetType().Name}: {ex.Message}");
            }
        }

        var best = doubtful ?? fallback;
        // Only remember "not found" in memory, so a later retry (e.g. network back) can succeed.
        if (best is not null) Store(key, best);
        else _memory[key] = null;
        return new LyricsLookupResult(track, best, false, log);
    }

    public void Invalidate(TrackInfo track)
    {
        foreach (var pref in new[] { "Auto", "NetEase", "LRCLIB", "LocalLRC" })
        {
            var key = CacheKey(track, pref);
            _memory.TryRemove(key, out _);
            if (_cacheDir is not null)
            {
                var path = Path.Combine(_cacheDir, key + ".json");
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private void Store(string key, Lyrics lyrics)
    {
        _memory[key] = lyrics;
        if (_cacheDir is null) return;
        try
        {
            File.WriteAllText(Path.Combine(_cacheDir, key + ".json"), JsonSerializer.Serialize(lyrics, JsonOptions));
        }
        catch (IOException) { /* cache is best-effort */ }
    }

    private Lyrics? TryReadDisk(string key)
    {
        if (_cacheDir is null) return null;
        var path = Path.Combine(_cacheDir, key + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Lyrics>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CacheKey(TrackInfo track, string preferredProvider = "Auto")
    {
        // Duration bucketed to 5 s so that slightly different reports still hit the cache.
        var dur = track.Duration is { TotalSeconds: > 0 } d ? ((int)Math.Round(d.TotalSeconds / 5)).ToString() : "?";
        var prefSuffix = string.Equals(preferredProvider, "Auto", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"\u001f{preferredProvider.ToLowerInvariant()}";
        var raw = $"{track.Key}\u001f{dur}{prefSuffix}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }
}
