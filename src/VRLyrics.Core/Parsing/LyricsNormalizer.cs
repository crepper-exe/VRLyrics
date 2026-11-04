using VRLyrics.Core.Models;

namespace VRLyrics.Core.Parsing;

/// <summary>Sorting, end-time computation and translation merging for parsed lyric lines.</summary>
public static class LyricsNormalizer
{
    /// <summary>Default display time for the very last line when nothing else bounds it.</summary>
    public static readonly TimeSpan LastLineDefaultDuration = TimeSpan.FromSeconds(6);

    /// <summary>Max time a line-level line stays "active" if the next line is far away (instrumental breaks).</summary>
    public static readonly TimeSpan MaxLineDuration = TimeSpan.FromSeconds(12);

    public static Lyrics Normalize(IEnumerable<LyricLine> rawLines, string source = "")
    {
        // Stable sort by start time.
        var lines = rawLines.Select((l, i) => (l, i)).OrderBy(x => x.l.Start).ThenBy(x => x.i).Select(x => x.l).ToList();
        var output = new List<LyricLine>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var nextStart = i + 1 < lines.Count ? lines[i + 1].Start : (TimeSpan?)null;

            var words = FixWordDurations(line.Words, line.End > line.Start ? line.End : nextStart);

            var end = line.End;
            if (end <= line.Start)
            {
                if (words.Count > 0) end = words[^1].End;
                if (end <= line.Start)
                {
                    end = nextStart ?? line.Start + LastLineDefaultDuration;
                    if (end - line.Start > MaxLineDuration) end = line.Start + MaxLineDuration;
                }
            }

            output.Add(line with { End = end, Words = words });
        }

        return new Lyrics { Lines = output, Source = source };
    }

    /// <summary>
    /// Attaches translations (or romanisation) to <paramref name="lyrics"/> by matching each line to the
    /// translation line whose timestamp is nearest, within <paramref name="tolerance"/>.
    /// </summary>
    public static Lyrics AttachTranslation(Lyrics lyrics, IReadOnlyList<LyricLine> translation, TimeSpan? tolerance = null, bool romanization = false)
    {
        var tol = tolerance ?? TimeSpan.FromMilliseconds(1000);
        var candidates = translation
            .Where(t => !string.IsNullOrWhiteSpace(t.Text) && !IsPlaceholder(t.Text))
            .OrderBy(t => t.Start)
            .ToList();
        if (candidates.Count == 0) return lyrics;

        var starts = candidates.Select(c => c.Start).ToList();
        var merged = lyrics.Lines.Select(line =>
        {
            if (string.IsNullOrWhiteSpace(line.Text)) return line;
            var idx = NearestIndex(starts, line.Start);
            if (idx < 0 || (starts[idx] - line.Start).Duration() > tol) return line;
            var text = candidates[idx].Text.Trim();
            if (string.Equals(text, line.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return line;
            return romanization ? line with { Romanization = text } : line with { Translation = text };
        }).ToList();

        return lyrics with { Lines = merged };
    }

    private static bool IsPlaceholder(string text) => text.Trim() is "//" or "/";

    private static int NearestIndex(List<TimeSpan> sorted, TimeSpan t)
    {
        if (sorted.Count == 0) return -1;
        var lo = 0;
        var hi = sorted.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < t) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && (t - sorted[lo - 1]).Duration() <= (sorted[lo] - t).Duration()) return lo - 1;
        return lo;
    }

    private static IReadOnlyList<LyricWord> FixWordDurations(IReadOnlyList<LyricWord> words, TimeSpan? lineEnd)
    {
        if (words.Count == 0) return words;
        var list = words.OrderBy(w => w.Start).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Duration > TimeSpan.Zero) continue;
            var end = i + 1 < list.Count ? list[i + 1].Start : lineEnd ?? list[i].Start + TimeSpan.FromMilliseconds(500);
            if (end <= list[i].Start) end = list[i].Start + TimeSpan.FromMilliseconds(300);
            list[i] = list[i] with { Duration = end - list[i].Start };
        }
        return list;
    }
}
