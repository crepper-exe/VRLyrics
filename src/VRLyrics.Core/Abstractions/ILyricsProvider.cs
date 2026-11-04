using VRLyrics.Core.Models;

namespace VRLyrics.Core.Abstractions;

/// <summary>A source of lyrics (online service, local files, ...).</summary>
public interface ILyricsProvider
{
    /// <summary>Display name, e.g. "NetEase".</summary>
    string Name { get; }

    /// <summary>Find and download lyrics for <paramref name="track"/>. Returns null if nothing suitable was found.</summary>
    Task<Lyrics?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default);
}
