using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VRLyrics.Core.Models;

namespace VRLyrics.Core.Matching;

/// <summary>Scores how well a provider search result matches the currently playing track (0..1).</summary>
public static partial class TrackMatcher
{
    /// <summary>Candidates at or above this are considered a confident match.</summary>
    public const double AcceptThreshold = 0.6;

    /// <summary>Candidates below this are discarded entirely; between this and <see cref="AcceptThreshold"/> they are last-resort fallbacks.</summary>
    public const double MinimumScore = 0.45;

    /// <summary>Upper bound for candidates whose artist clearly differs (typically covers).</summary>
    private const double ArtistMismatchCap = 0.55;

    private static readonly string[] VariantKeywords =
        ["live", "伴奏", "instrumental", "karaoke", "remix", "cover", "翻唱", "翻自", "原唱", "dj", "现场", "纯音乐", "inst.", "off vocal", "sped up", "slowed", "加速", "降调", "升调"];

    [GeneratedRegex(@"\(.*?\)|\[.*?\]|（.*?）|【.*?】|「.*?」|<.*?>")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s+-\s+.*$")]
    private static partial Regex DashSuffixRegex();

    [GeneratedRegex(@"\b(feat\.?|ft\.?|featuring)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();

    [GeneratedRegex(@"\s*(?:/|,|、|&|;|\bx\b|\band\b|\bfeat\.?|\bft\.?|，)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplitRegex();

    public static double Score(TrackInfo query, string title, IEnumerable<string> artists, TimeSpan? duration)
    {
        var artistList = artists.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        var titleScore = TitleScore(query.Title, title);
        var artistScore = ArtistScore(query.Artist, artistList);
        var durationScore = DurationScore(query.Duration, duration);

        var score = 0.55 * titleScore + 0.30 * artistScore + 0.15 * durationScore;

        // Penalise variants (live / instrumental / remix ...) the user isn't listening to.
        var qLower = query.Title.ToLowerInvariant();
        var cLower = title.ToLowerInvariant();
        if (VariantKeywords.Any(k => cLower.Contains(k) && !qLower.Contains(k)))
            score -= 0.15;

        // A known artist that doesn't match at all is most likely a cover: never a confident match.
        if (!string.IsNullOrWhiteSpace(query.Artist) && artistScore < 0.5)
            score = Math.Min(score, ArtistMismatchCap);

        return Math.Clamp(score, 0, 1);
    }

    public static double TitleScore(string query, string candidate)
    {
        var q = Normalize(query);
        var c = Normalize(candidate);
        if (q.Length == 0 || c.Length == 0) return 0;
        if (q == c) return 1;

        var qc = Normalize(CoreTitle(query));
        var cc = Normalize(CoreTitle(candidate));
        if (qc.Length > 0 && qc == cc) return 0.92;
        if (qc.Length > 0 && cc.Length > 0 && (qc.Contains(cc) || cc.Contains(qc)))
            return 0.75 * (double)Math.Min(qc.Length, cc.Length) / Math.Max(qc.Length, cc.Length) + 0.15;

        return Similarity(qc.Length > 0 ? qc : q, cc.Length > 0 ? cc : c) * 0.85;
    }

    public static double ArtistScore(string queryArtist, IReadOnlyList<string> candidateArtists)
    {
        if (string.IsNullOrWhiteSpace(queryArtist)) return 0.5; // unknown, neutral
        if (candidateArtists.Count == 0) return 0.3;

        var qTokens = SplitArtists(queryArtist);
        var cTokens = candidateArtists.SelectMany(SplitArtists).ToList();
        if (qTokens.Count == 0 || cTokens.Count == 0) return 0.3;

        if (qTokens.Any(q => cTokens.Any(c => c == q))) return 1;
        if (qTokens.Any(q => cTokens.Any(c => c.Length >= 2 && q.Length >= 2 && (c.Contains(q) || q.Contains(c))))) return 0.85;

        var best = qTokens.Max(q => cTokens.Max(c => Similarity(q, c)));
        return best * 0.8;
    }

    public static double DurationScore(TimeSpan? query, TimeSpan? candidate)
    {
        if (query is not { } q || candidate is not { } c || q <= TimeSpan.Zero || c <= TimeSpan.Zero) return 0.5;
        var diff = Math.Abs((q - c).TotalSeconds);
        return diff switch
        {
            <= 2 => 1,
            <= 5 => 0.85,
            <= 10 => 0.6,
            <= 20 => 0.3,
            _ => 0,
        };
    }

    /// <summary>Title with bracketed suffixes, " - xxx" suffixes and "feat." removed.</summary>
    public static string CoreTitle(string title)
    {
        var t = BracketRegex().Replace(title, " ");
        t = DashSuffixRegex().Replace(t, "");
        t = FeatRegex().Replace(t, "");
        return t.Trim();
    }

    /// <summary>Lower-case, width-folded (NFKC), punctuation and whitespace stripped.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var folded = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (char.IsLetterOrDigit(ch) || cat == UnicodeCategory.OtherLetter) sb.Append(ch);
        }
        return sb.ToString();
    }

    public static List<string> SplitArtists(string artists) =>
        ArtistSplitRegex().Split(artists)
            .Select(Normalize)
            .Where(a => a.Length > 0)
            .Distinct()
            .ToList();

    /// <summary>Levenshtein similarity ratio in 0..1.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }
}
