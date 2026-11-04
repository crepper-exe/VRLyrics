namespace VRLyrics.Core.Models;

/// <summary>Information about the track currently playing, as reported by the media source.</summary>
public sealed record TrackInfo(
    string Title,
    string Artist,
    string? Album = null,
    TimeSpan? Duration = null,
    string? SourceApp = null,
    long? NeteaseSongId = null)
{
    /// <summary>Stable key used for caching and change detection (duration intentionally excluded, it often arrives late).</summary>
    public string Key => $"{Title}\u001f{Artist}".ToLowerInvariant();

    public override string ToString() =>
        string.IsNullOrEmpty(Artist) ? Title : $"{Artist} - {Title}";
}
