using VRLyrics.Core.Models;

namespace VRLyrics.Core.Timing;

/// <summary>Snapshot of where playback is inside a set of lyrics.</summary>
public readonly record struct LyricState
{
    /// <summary>Index of the current line (last line whose start is &lt;= position), or -1 before the first line.</summary>
    public int LineIndex { get; init; }

    /// <summary>True while position is inside [Start, End) of the current line (false during gaps).</summary>
    public bool IsLineActive { get; init; }

    /// <summary>0..1 progress through the current line.</summary>
    public double LineProgress { get; init; }

    /// <summary>Index of the word being sung, or -1 if the line has no word timing / hasn't started.</summary>
    public int WordIndex { get; init; }

    /// <summary>0..1 progress through <see cref="WordIndex"/>.</summary>
    public double WordProgress { get; init; }

    /// <summary>
    /// Number of characters of <see cref="LyricLine.Text"/> that are "sung" (fractional, for smooth karaoke wipes).
    /// For lines without word timing this follows <see cref="LineProgress"/>.
    /// </summary>
    public double SungCharacters { get; init; }

    public static LyricState None => new() { LineIndex = -1, WordIndex = -1 };
}

public static class LyricTimeline
{
    /// <summary>Binary search for the last line with Start &lt;= position. Returns -1 if position precedes all lines.</summary>
    public static int FindLineIndex(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, result = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (lines[mid].Start <= position) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result;
    }

    public static LyricState GetState(Lyrics? lyrics, TimeSpan position)
    {
        if (lyrics is null || lyrics.IsEmpty || lyrics.IsUnsynced) return LyricState.None;

        var idx = FindLineIndex(lyrics.Lines, position);
        if (idx < 0) return LyricState.None;

        var line = lyrics.Lines[idx];
        var length = line.End - line.Start;
        var lineProgress = length > TimeSpan.Zero ? Math.Clamp((position - line.Start) / length, 0, 1) : 1;
        var active = position < line.End;

        if (!line.HasWordTiming)
        {
            return new LyricState
            {
                LineIndex = idx,
                IsLineActive = active,
                LineProgress = lineProgress,
                WordIndex = -1,
                SungCharacters = active ? line.Text.Length * lineProgress : line.Text.Length,
            };
        }

        var (wordIndex, wordProgress, sungChars) = GetWordProgress(line, position);
        return new LyricState
        {
            LineIndex = idx,
            IsLineActive = active,
            LineProgress = lineProgress,
            WordIndex = wordIndex,
            WordProgress = wordProgress,
            SungCharacters = sungChars,
        };
    }

    /// <summary>Computes karaoke progress for a word-timed line.</summary>
    public static (int WordIndex, double WordProgress, double SungCharacters) GetWordProgress(LyricLine line, TimeSpan position)
    {
        var words = line.Words;
        double chars = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (position < w.Start)
                return (i - 1, i == 0 ? 0 : 1, chars);

            if (position < w.End)
            {
                var p = w.Duration > TimeSpan.Zero ? (position - w.Start) / w.Duration : 1;
                p = Math.Clamp(p, 0, 1);
                return (i, p, chars + w.Text.Length * p);
            }

            chars += w.Text.Length;
        }
        return (words.Count - 1, 1, chars);
    }
}
