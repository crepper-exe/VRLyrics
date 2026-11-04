using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VRLyrics.Core.Models;

namespace VRLyrics.Core.Parsing;

/// <summary>
/// Parses NetEase Cloud Music "YRC" word-timed lyrics:
/// <code>[16210,3460](16210,670,0)还(16880,410,0)没...</code>
/// where the line tag is <c>[startMs,durationMs]</c> and each word is <c>(startMs,durationMs,0)text</c>.
/// JSON credit lines (<c>{"t":..,"c":[..]}</c>) are also supported.
/// </summary>
public static partial class YrcParser
{
    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"\((\d+),(\d+),(-?\d+)\)")]
    private static partial Regex WordRegex();

    public static List<LyricLine> ParseLines(string? text)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var raw in LrcParser.SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '{')
            {
                if (LrcParser.TryParseJsonLine(line, out var jsonLine)) result.Add(jsonLine);
                continue;
            }

            var m = LineRegex().Match(line);
            if (!m.Success) continue;

            var lineStart = TimeSpan.FromMilliseconds(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            var lineDuration = TimeSpan.FromMilliseconds(long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            var body = m.Groups[3].Value;

            var words = new List<LyricWord>();
            var sb = new StringBuilder();
            var wordMatches = WordRegex().Matches(body);
            for (var i = 0; i < wordMatches.Count; i++)
            {
                var wm = wordMatches[i];
                var textStart = wm.Index + wm.Length;
                var textEnd = i + 1 < wordMatches.Count ? wordMatches[i + 1].Index : body.Length;
                var wordText = body[textStart..textEnd];
                if (wordText.Length == 0) continue;

                sb.Append(wordText);
                words.Add(new LyricWord(
                    TimeSpan.FromMilliseconds(long.Parse(wm.Groups[1].Value, CultureInfo.InvariantCulture)),
                    TimeSpan.FromMilliseconds(long.Parse(wm.Groups[2].Value, CultureInfo.InvariantCulture)),
                    wordText));
            }

            // Lines without word tags: keep raw body as text.
            var lineText = words.Count > 0 ? sb.ToString() : body;
            result.Add(new LyricLine
            {
                Start = lineStart,
                End = lineStart + lineDuration,
                Text = lineText.Trim(),
                Words = words,
            });
        }

        return result;
    }

    public static Lyrics Parse(string? text, string source = "") =>
        LyricsNormalizer.Normalize(ParseLines(text), source);
}
