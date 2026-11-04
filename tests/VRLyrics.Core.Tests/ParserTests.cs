using VRLyrics.Core.Parsing;

namespace VRLyrics.Core.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parses_basic_lines_and_computes_end_times()
    {
        var lyr = LrcParser.Parse("""
            [ar:Someone]
            [00:01.50]First line
            [00:04.000]Second line
            [01:02.3]Third
            """);

        Assert.Equal(3, lyr.Lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), lyr.Lines[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(4000), lyr.Lines[0].End);
        Assert.Equal(TimeSpan.FromMilliseconds(62300), lyr.Lines[2].Start);
        Assert.Equal("Second line", lyr.Lines[1].Text);
        Assert.False(lyr.IsUnsynced);
    }

    [Fact]
    public void Repeated_timestamps_duplicate_line_and_sort()
    {
        var lyr = LrcParser.Parse("[00:10.00][00:30.00]Chorus\n[00:20.00]Verse");
        Assert.Equal(["Chorus", "Verse", "Chorus"], lyr.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Offset_tag_shifts_lines_earlier()
    {
        var lyr = LrcParser.Parse("[offset:500]\n[00:02.00]A\n[00:03.00]B");
        Assert.Equal(TimeSpan.FromMilliseconds(1500), lyr.Lines[0].Start);
    }

    [Fact]
    public void Parses_enhanced_word_tags()
    {
        var lyr = LrcParser.Parse("[00:01.00]<00:01.00>Hello <00:01.50>world<00:02.20>\n[00:05.00]next");
        var line = lyr.Lines[0];
        Assert.Equal("Hello world", line.Text);
        Assert.Equal(2, line.Words.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(500), line.Words[0].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(700), line.Words[1].Duration);
    }

    [Fact]
    public void Parses_netease_json_credit_lines()
    {
        var lyr = LrcParser.Parse("{\"t\":0,\"c\":[{\"tx\":\"作词: \"},{\"tx\":\"唐恬\"}]}\n[00:05.00]歌词");
        Assert.Equal("作词: 唐恬", lyr.Lines[0].Text);
        Assert.Equal("歌词", lyr.Lines[1].Text);
    }

    [Fact]
    public void Plain_text_is_unsynced()
    {
        var lyr = LrcParser.Parse("line one\nline two");
        Assert.True(lyr.IsUnsynced);
        Assert.Equal(2, lyr.Lines.Count);
    }

    [Fact]
    public void Attaches_translation_by_nearest_timestamp()
    {
        var main = LrcParser.Parse("[00:01.00]Hello\n[00:03.00]World\n[00:09.00]Untranslated");
        var trans = LrcParser.ParseLines("[00:01.00]你好\n[00:03.10]世界\n[00:20.00]远处");
        var merged = LyricsNormalizer.AttachTranslation(main, trans);
        Assert.Equal("你好", merged.Lines[0].Translation);
        Assert.Equal("世界", merged.Lines[1].Translation);
        Assert.Null(merged.Lines[2].Translation);
    }
}

public class YrcParserTests
{
    private const string Sample = """
        {"t":0,"c":[{"tx":"作曲: "},{"tx":"钱雷"}]}
        [16210,3460](16210,670,0)还(16880,410,0)没(17290,500,0)好(17790,1880,0)好
        [19670,2000](19670,1000,0)Hello (20670,1000,0)world
        """;

    [Fact]
    public void Parses_lines_and_words()
    {
        var lyr = YrcParser.Parse(Sample);
        Assert.Equal(3, lyr.Lines.Count);
        var l = lyr.Lines[1];
        Assert.Equal("还没好好", l.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(16210), l.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(19670), l.End);
        Assert.Equal(4, l.Words.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(410), l.Words[1].Duration);
        Assert.Equal("Hello world", lyr.Lines[2].Text);
        Assert.True(lyr.HasWordTiming);
    }

    [Fact]
    public void Word_text_with_parentheses_is_preserved()
    {
        var lyr = YrcParser.Parse("[0,2000](0,1000,0)(Yeah) (1000,1000,0)go");
        Assert.Equal("(Yeah) go", lyr.Lines[0].Text);
        Assert.Equal(2, lyr.Lines[0].Words.Count);
    }
}
