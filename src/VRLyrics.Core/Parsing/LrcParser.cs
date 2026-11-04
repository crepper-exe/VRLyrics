using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VRLyrics.Core.Models;

namespace VRLyrics.Core.Parsing;

/// <summary>
/// Parses standard LRC, "enhanced" LRC (word-level <c>&lt;mm:ss.xx&gt;</c> tags) and the JSON credit lines
/// that NetEase mixes into its LRC/YRC payloads (<c>{"t":0,"c":[{"tx":"..."}]}</c>).
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LineTagRegex();

    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordTagRegex();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"^\[[a-zA-Z#]+:.*\]$")]
    private static partial Regex MetadataRegex();

    /// <summary>Parses LRC text into raw (un-normalised) lines. Returns an empty list if the text has no timestamps.</summary>
    public static List<LyricLine> ParseLines(string? text)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var offset = TimeSpan.Zero;
        foreach (var rawLine in SplitLines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '{')
            {
                if (TryParseJsonLine(line, out var jsonLine)) result.Add(jsonLine);
                continue;
            }

            var offsetMatch = OffsetRegex().Match(line);
            if (offsetMatch.Success)
            {
                // Per LRC convention a positive offset makes lyrics appear sooner.
                offset = -TimeSpan.FromMilliseconds(int.Parse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }

            // Collect all leading time tags: "[00:12.00][01:30.00]text" repeats the text at both times.
            var times = new List<TimeSpan>();
            var pos = 0;
            while (pos < line.Length)
            {
                var m = LineTagRegex().Match(line, pos);
                if (!m.Success || m.Index != pos) break;
                times.Add(ParseTime(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
                pos = m.Index + m.Length;
            }

            if (times.Count == 0)
            {
                // Metadata such as [ar:..] or a non-timestamped line; ignored here.
                continue;
            }

            var body = line[pos..];
            var (plainText, words) = ParseEnhancedWords(body);

            foreach (var t in times)
            {
                var shifted = words.Count == 0
                    ? words
                    : words.Select(w => w with { Start = w.Start + offset }).ToList();
                result.Add(new LyricLine
                {
                    Start = Max(TimeSpan.Zero, t + offset),
                    Text = plainText,
                    Words = shifted,
                });
            }
        }

        return result;
    }

    /// <summary>Parses LRC text into a normalised <see cref="Lyrics"/>; falls back to unsynced plain text.</summary>
    public static Lyrics Parse(string? text, string source = "")
    {
        var lines = ParseLines(text);
        if (lines.Count > 0)
            return LyricsNormalizer.Normalize(lines, source);

        if (string.IsNullOrWhiteSpace(text))
            return new Lyrics { Lines = [], Source = source };

        // No timestamps at all: treat as plain, unsynced lyrics.
        var plain = SplitLines(text)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !MetadataRegex().IsMatch(l))
            .Select(l => new LyricLine { Start = TimeSpan.Zero, End = TimeSpan.Zero, Text = l })
            .ToList();
        return new Lyrics { Lines = plain, Source = source, IsUnsynced = true };
    }

    private static (string Text, List<LyricWord> Words) ParseEnhancedWords(string body)
    {
        var matches = WordTagRegex().Matches(body);
        if (matches.Count == 0)
            return (body.Trim(), []);

        var words = new List<LyricWord>();
        var sb = new StringBuilder();
        // Text before the first tag (rare) is kept as plain prefix.
        sb.Append(body[..matches[0].Index]);

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var start = ParseTime(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            var textStart = m.Index + m.Length;
            var textEnd = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var wordText = body[textStart..textEnd];
            sb.Append(wordText);

            if (wordText.Length == 0) continue; // trailing end-marker tag

            TimeSpan duration;
            if (i + 1 < matches.Count)
            {
                var next = matches[i + 1];
                duration = ParseTime(next.Groups[1].Value, next.Groups[2].Value, next.Groups[3].Value) - start;
            }
            else
            {
                duration = TimeSpan.Zero; // unknown, fixed up during normalisation
            }

            words.Add(new LyricWord(start, Max(TimeSpan.Zero, duration), wordText));
        }

        return (sb.ToString().Trim(), words);
    }

    internal static bool TryParseJsonLine(string line, out LyricLine result)
    {
        result = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var tProp) || !root.TryGetProperty("c", out var cProp)) return false;
            var sb = new StringBuilder();
            foreach (var part in cProp.EnumerateArray())
                if (part.TryGetProperty("tx", out var tx)) sb.Append(tx.GetString());
            var text = sb.ToString().Trim();
            if (text.Length == 0) return false;
            // NetEase sometimes uses negative times for credit lines; clamp to the start of the song.
            result = new LyricLine { Start = TimeSpan.FromMilliseconds(Math.Max(0, tProp.GetInt64())), Text = text };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static TimeSpan ParseTime(string min, string sec, string frac)
    {
        var ms = 0;
        if (!string.IsNullOrEmpty(frac))
        {
            var f = int.Parse(frac, CultureInfo.InvariantCulture);
            ms = frac.Length switch { 1 => f * 100, 2 => f * 10, _ => f };
        }
        return new TimeSpan(0, 0, int.Parse(min, CultureInfo.InvariantCulture), int.Parse(sec, CultureInfo.InvariantCulture), ms);
    }

    internal static IEnumerable<string> SplitLines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r'));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
