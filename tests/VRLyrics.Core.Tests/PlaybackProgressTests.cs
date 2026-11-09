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

    [Fact]
    public async Task PlaybackClock_RepeatedPauseResumeWithStaleSmtcTimeline_DoesNotAccumulateDrift()
    {
        var clock = new PlaybackClock();
        clock.Reset(TimeSpan.Zero, TimeSpan.FromMinutes(4));
        clock.SetPlaying(true);

        var initialReportTime = DateTimeOffset.UtcNow;
        clock.ReportTimeline(TimeSpan.FromSeconds(20), initialReportTime, TimeSpan.FromMinutes(4));

        // Simulate 3 cycles of pausing for 80ms and resuming while SMTC continues returning the stale initial timeline report.
        for (var i = 0; i < 3; i++)
        {
            clock.SetPlaying(false);
            var pausedPos = clock.Position;
            await Task.Delay(80);
            clock.ReportTimeline(TimeSpan.FromSeconds(20), initialReportTime, TimeSpan.FromMinutes(4));
            Assert.InRange((clock.Position - pausedPos).Duration().TotalMilliseconds, 0, 5);

            clock.SetPlaying(true);
            clock.ReportTimeline(TimeSpan.FromSeconds(20), initialReportTime, TimeSpan.FromMinutes(4));
            // Resuming must NOT jump forward by the 80ms spent paused.
            Assert.InRange((clock.Position - pausedPos).Duration().TotalMilliseconds, 0, 25);
        }
    }

    [Fact]
    public void NeteaseLogProbe_RepeatedPauseResumeWithFadeOutAndRapidToggle_DoesNotAccumulateLag()
    {
        // Real event sequence from NetEase Cloud Music 3.x (cloudmusic.elog):
        // 1) Pause triggers SMTC status=1 immediately, but audio_player.cpp fades out for ~610 ms before pausing.
        // 2) Rapid pause+resume (<610 ms) logs Pause+Play on the same tick (4018093) and a delayed "native播放state",2 31 ms later (4018124).
        const string log = """
            [9332:12524:1109/134419:3844390:INFO:cloudmusic_player.cpp(422)] OnLoad:2166447251_J1MVO2
            [9332:12524:1109/134420:3844640:INFO:audio_player.cpp(169)] Play 2166447251_J1MVO2|play|token1
            [9332:13764:1109/134420:3844640:INFO:smtc_wrapper.cpp(175)] [SMTCWrapper] SMTC status=0
            [9332:13764:1109/134555:3940250:INFO:smtc_wrapper.cpp(260)] [SMTCWrapper] Button pressed: 1
            [9332:36664:1109/134555:3940250:INFO:cef_log.cpp(15)] [render log]info:,"【playing】","pause"
            [9332:13764:1109/134555:3940265:INFO:smtc_wrapper.cpp(175)] [SMTCWrapper] SMTC status=1
            [9332:12524:1109/134556:3940859:INFO:audio_player.cpp(198)] Pause 2166447251_J1MVO2|pause|token2
            [9332:36664:1109/134556:3940859:INFO:cef_log.cpp(15)] [render log]info:,"【playing】","native播放state",2,"2166447251_J1MVO2"
            [9332:13764:1109/134557:3942250:INFO:smtc_wrapper.cpp(260)] [SMTCWrapper] Button pressed: 0
            [9332:13764:1109/134557:3942265:INFO:smtc_wrapper.cpp(175)] [SMTCWrapper] SMTC status=0
            [9332:12524:1109/134557:3942265:INFO:audio_player.cpp(169)] Play 2166447251_J1MVO2|resume|token3
            [9332:13764:1109/134712:4017609:INFO:smtc_wrapper.cpp(260)] [SMTCWrapper] Button pressed: 1
            [9332:36664:1109/134712:4017609:INFO:cef_log.cpp(15)] [render log]info:,"【playing】","pause"
            [9332:13764:1109/134712:4017609:INFO:smtc_wrapper.cpp(175)] [SMTCWrapper] SMTC status=1
            [9332:13764:1109/134713:4018093:INFO:smtc_wrapper.cpp(260)] [SMTCWrapper] Button pressed: 0
            [9332:12524:1109/134713:4018093:INFO:audio_player.cpp(198)] Pause 2166447251_J1MVO2|pause|token4
            [9332:13764:1109/134713:4018093:INFO:smtc_wrapper.cpp(175)] [SMTCWrapper] SMTC status=0
            [9332:12524:1109/134713:4018093:INFO:audio_player.cpp(169)] Play 2166447251_J1MVO2|resume|token5
            [9332:36664:1109/134713:4018124:INFO:cef_log.cpp(15)] [render log]info:,"【playing】","native播放state",2,"2166447251_J1MVO2"
            """;

        var probe = new NeteaseCloudMusicLogProbe("nonexistent.elog");
        probe.FeedDecodedLog(log + "\n", targetPid: 9332);

        // Segment 1: 3940859 - 3844640 = 96,219 ms (includes the 609 ms fade-out)
        // Segment 2: 4018093 - 3942265 = 75,828 ms (includes the 484 ms rapid pause-resume fade)
        // Segment 3: 4020093 - 4018093 =  2,000 ms (still playing after rapid resume despite delayed native播放state=2)
        // Total expected = 174,047 ms
        var snap = probe.GetSnapshot(currentTickMs: 4020093);
        Assert.NotNull(snap);
        Assert.True(snap.Value.IsPlaying);
        Assert.Equal(174047, snap.Value.Position.TotalMilliseconds, precision: 1);
    }
}
