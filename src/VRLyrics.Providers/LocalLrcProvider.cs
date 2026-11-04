using VRLyrics.Core.Abstractions;
using VRLyrics.Core.Matching;
using VRLyrics.Core.Models;
using VRLyrics.Core.Parsing;

namespace VRLyrics.Providers;

/// <summary>
/// Looks for <c>.lrc</c> files in user-configured folders. Matches "Artist - Title.lrc", "Title - Artist.lrc"
/// or "Title.lrc" (case/width/punctuation-insensitive).
/// </summary>
public sealed class LocalLrcProvider : ILyricsProvider
{
    private readonly Func<IReadOnlyList<string>> _folders;

    public LocalLrcProvider(Func<IReadOnlyList<string>> folders) => _folders = folders;

    public string Name => "Local";

    public async Task<Lyrics?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default)
    {
        var title = TrackMatcher.Normalize(track.Title);
        var core = TrackMatcher.Normalize(TrackMatcher.CoreTitle(track.Title));
        var artists = TrackMatcher.SplitArtists(track.Artist);

        string? bestPath = null;
        var bestRank = int.MaxValue;

        foreach (var folder in _folders())
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.lrc", SearchOption.AllDirectories); }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var parts = name.Split(" - ", 2, StringSplitOptions.TrimEntries);
                int rank;
                if (parts.Length == 2)
                {
                    var a = TrackMatcher.Normalize(parts[0]);
                    var b = TrackMatcher.Normalize(parts[1]);
                    bool IsArtist(string s) => artists.Contains(s);
                    bool IsTitle(string s) => s == title || s == core;
                    if ((IsArtist(a) && IsTitle(b)) || (IsTitle(a) && IsArtist(b))) rank = 0;
                    else if (IsTitle(a) || IsTitle(b)) rank = 2;
                    else continue;
                }
                else
                {
                    var n = TrackMatcher.Normalize(name);
                    if (n == title) rank = 1;
                    else if (n == core) rank = 3;
                    else continue;
                }

                if (rank < bestRank) { bestRank = rank; bestPath = file; }
                if (rank == 0) break;
            }
            if (bestRank == 0) break;
        }

        if (bestPath is null) return null;
        var text = await File.ReadAllTextAsync(bestPath, cancellationToken).ConfigureAwait(false);
        var lyrics = LrcParser.Parse(text, Name);
        return lyrics.IsEmpty ? null : lyrics with { SourceId = bestPath };
    }
}
