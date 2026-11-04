using System.Text;
using VRLyrics.Media;

namespace VRLyrics.Core.Tests;

public sealed class PlaybackProgressTests
{
    [Fact]
    public void PlaybackClock_MidSongSmtcReportOlderThan30Seconds_ExtrapolatesCorrectPosition()
    {
        var clock = new PlaybackClock();
        clock.SetPlaying(true);

        // Simulate launching VRLyrics 95 seconds into a 4-minute song where SMTC only updated Position=0 at track start.
        var lastUpdated = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(95);
        clock.ReportTimeline(TimeSpan.Zero, lastUpdated, TimeSpan.FromMinutes(4));

        var pos = clock.Position;
        Assert.InRange(pos.TotalSeconds, 94.5, 96.5);
    }

    [Fact]
    public void PlaybackClock_SyncFromProbe_SnapsWhenDriftExceedsThreshold()
    {
        var clock = new PlaybackClock();
        clock.Reset(TimeSpan.Zero, TimeSpan.FromSeconds(227));
        clock.SetPlaying(false);

        clock.SyncFromProbe(TimeSpan.FromSeconds(189.6), TimeSpan.FromSeconds(227));

        Assert.InRange(clock.Position.TotalSeconds, 189.5, 189.7);
        Assert.Equal(TimeSpan.FromSeconds(227), clock.Duration);
    }

    [Fact]
    public void NeteaseLogProbe_CipherRoundTrip_RecoversOriginalUtf8()
    {
        const string sample = "[31804:32624:1104/111127:2367656:INFO:audio_player.cpp(716)] DoSeek pos:95.42\r\n";
        var bytes = Encoding.UTF8.GetBytes(sample);
        NeteaseCloudMusicLogProbe.EncodeInPlace(bytes);
        Assert.NotEqual(sample, Encoding.UTF8.GetString(bytes));

        NeteaseCloudMusicLogProbe.DecodeInPlace(bytes);
        Assert.Equal(sample, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void NeteaseLogProbe_ReconstructsMidSongPositionAndSeekAndPreloadIsolation()
    {
        const string log = """
            [31804:32624:1104/110627:2068140:INFO:cloudmusic_player.cpp(422)] OnLoad:26305527_26305527_36028797018964068_1762225587320
            [31804:32624:1104/110627:2068171:INFO:cloudmusic_player.cpp(1269)] DoQuerySongCacheInfo songId26305527_26305527_36028797018964068_1762225587320, info:{"bitrate":320000,"time":227000}
            [31804:30972:1104/110627:2068250:INFO:smtc_wrapper.cpp(175)] [SMTCWrapper] SMTC status=0
            [31804:32624:1104/110635:2076250:INFO:cloudmusic_player.cpp(1269)] DoQuerySongCacheInfo songId99999999_99999999_36028797018964068_1762225589000, info:{"bitrate":320000,"time":310000}
            [31804:30972:1104/110727:2128250:INFO:smtc_wrapper.cpp(175)] [SMTCWrapper] SMTC status=1
            [31804:30972:1104/110737:2138250:INFO:smtc_wrapper.cpp(175)] [SMTCWrapper] SMTC status=0
            [31804:32624:1104/110750:2151250:INFO:audio_player.cpp(716)] DoSeek pos:120.5
            """;

        var probe = new NeteaseCloudMusicLogProbe("nonexistent.elog");
        probe.FeedDecodedLog(log + "\n", targetPid: 31804);

        // Query 15 seconds after the seek at tick 2151250 -> expected position = 120.5s + 15.0s = 135.5s
        var snapshot = probe.GetSnapshot(currentTickMs: 2166250);
        Assert.NotNull(snapshot);
        Assert.Equal(26305527L, snapshot.Value.SongId);
        Assert.True(snapshot.Value.IsPlaying);
        // Ensure preloaded song 99999999's duration (310s) did not overwrite current song 26305527's duration (227s)
        Assert.Equal(TimeSpan.FromSeconds(227), snapshot.Value.Duration);
        Assert.InRange(snapshot.Value.Position.TotalSeconds, 135.4, 135.6);
    }
}
