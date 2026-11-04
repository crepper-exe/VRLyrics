using System.Text.Json.Serialization;

namespace VRLyrics.Core.Models;

/// <summary>A single timed word/syllable inside a lyric line (karaoke timing).</summary>
public sealed record LyricWord(TimeSpan Start, TimeSpan Duration, string Text)
{
    [JsonIgnore]
    public TimeSpan End => Start + Duration;
}

/// <summary>One lyric line. <see cref="Words"/> is empty when only line-level timing is available.</summary>
public sealed record LyricLine
{
    public required TimeSpan Start { get; init; }

    /// <summary>End of the line. Filled in by <see cref="Lyrics"/> normalisation (explicit duration or next line start).</summary>
    public TimeSpan End { get; init; }

    public required string Text { get; init; }

    public IReadOnlyList<LyricWord> Words { get; init; } = [];

    public string? Translation { get; init; }

    public string? Romanization { get; init; }

    [JsonIgnore]
    public bool HasWordTiming => Words.Count > 0;
}

/// <summary>A full set of lyrics for one track, lines sorted by start time.</summary>
public sealed record Lyrics
{
    public required IReadOnlyList<LyricLine> Lines { get; init; }

    /// <summary>Name of the provider that produced these lyrics (e.g. "NetEase", "LRCLIB").</summary>
    public string Source { get; init; } = "";

    /// <summary>Provider-specific id of the matched song, useful for debugging / manual override.</summary>
    public string? SourceId { get; init; }

    /// <summary>Title/artist of the matched song as reported by the provider.</summary>
    public string? MatchedTitle { get; init; }

    public string? MatchedArtist { get; init; }

    /// <summary>How confident the provider is that these lyrics belong to the playing track (0..1).</summary>
    public double MatchScore { get; init; } = 1.0;

    /// <summary>True for un-synced (plain text) lyrics; all lines have <see cref="TimeSpan.Zero"/> start.</summary>
    public bool IsUnsynced { get; init; }

    [JsonIgnore]
    public bool HasWordTiming => Lines.Any(l => l.HasWordTiming);

    [JsonIgnore]
    public bool HasTranslation => Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation));

    [JsonIgnore]
    public bool IsEmpty => Lines.Count == 0;
}
