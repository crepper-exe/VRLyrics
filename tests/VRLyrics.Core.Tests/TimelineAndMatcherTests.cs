using VRLyrics.Core.Matching;
using VRLyrics.Core.Models;
using VRLyrics.Core.Parsing;
using VRLyrics.Core.Timing;

namespace VRLyrics.Core.Tests;

public class LyricTimelineTests
{
    private static readonly Lyrics Lyr = YrcParser.Parse("""
        [1000,2000](1000,500,0)ab(1500,1500,0)cd
        [5000,1000](5000,1000,0)ef
        """);

    [Theory]
    [InlineData(0, -1)]
    [InlineData(999, -1)]
    [InlineData(1000, 0)]
    [InlineData(4000, 0)]
    [InlineData(5000, 1)]
    [InlineData(99999, 1)]
    public void Finds_current_line(int ms, int expected) =>
        Assert.Equal(expected, LyricTimeline.FindLineIndex(Lyr.Lines, TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public void Karaoke_progress_inside_words()
    {
        var s = LyricTimeline.GetState(Lyr, TimeSpan.FromMilliseconds(1250));
        Assert.Equal(0, s.WordIndex);
        Assert.Equal(0.5, s.WordProgress, 3);
        Assert.Equal(1.0, s.SungCharacters, 3); // half of "ab"

        s = LyricTimeline.GetState(Lyr, TimeSpan.FromMilliseconds(2250));
        Assert.Equal(1, s.WordIndex);
        Assert.Equal(3.0, s.SungCharacters, 3); // "ab" + half of "cd"
        Assert.True(s.IsLineActive);
    }

    [Fact]
    public void Gap_between_lines_is_inactive_and_fully_sung()
    {
        var s = LyricTimeline.GetState(Lyr, TimeSpan.FromMilliseconds(4000));
        Assert.Equal(0, s.LineIndex);
        Assert.False(s.IsLineActive);
        Assert.Equal(4.0, s.SungCharacters, 3);
    }
}

public class TrackMatcherTests
{
    [Fact]
    public void Exact_match_scores_high()
    {
        var q = new TrackInfo("孤勇者", "陈奕迅", null, TimeSpan.FromSeconds(256));
        Assert.True(TrackMatcher.Score(q, "孤勇者", ["陈奕迅"], TimeSpan.FromSeconds(256)) > 0.95);
    }

    [Fact]
    public void Live_version_scores_lower_than_original()
    {
        var q = new TrackInfo("孤勇者", "陈奕迅", null, TimeSpan.FromSeconds(256));
        var original = TrackMatcher.Score(q, "孤勇者", ["陈奕迅"], TimeSpan.FromSeconds(256));
        var live = TrackMatcher.Score(q, "孤勇者 (Live版)", ["陈奕迅"], TimeSpan.FromSeconds(282));
        Assert.True(original > live);
    }

    [Fact]
    public void Wrong_song_is_rejected()
    {
        var q = new TrackInfo("Shape of You", "Ed Sheeran", null, TimeSpan.FromSeconds(234));
        Assert.True(TrackMatcher.Score(q, "Perfect", ["Ed Sheeran"], TimeSpan.FromSeconds(263)) < TrackMatcher.AcceptThreshold);
    }

    [Fact]
    public void Handles_feat_and_fullwidth()
    {
        var q = new TrackInfo("Stay (feat. Justin Bieber)", "The Kid LAROI, Justin Bieber");
        Assert.True(TrackMatcher.Score(q, "STAY", ["The Kid LAROI", "Justin Bieber"], null) > 0.8);
        Assert.Equal(TrackMatcher.Normalize("ＡＢＣ　１２３"), TrackMatcher.Normalize("abc123"));
    }
}
