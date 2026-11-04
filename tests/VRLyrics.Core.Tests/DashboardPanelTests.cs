using SkiaSharp;
using VRLyrics.Core.Abstractions;
using VRLyrics.Core.Models;
using VRLyrics.Overlay.Dashboard;
using VRLyrics.Overlay.Rendering;
using VRLyrics.Providers;

namespace VRLyrics.Core.Tests;

public class DashboardPanelTests
{
    [Fact]
    public void ConvertOpenVrMouseToCanvas_InvertsYFromBottomLeftToTopLeft()
    {
        // Bottom-left in OpenVR (0, 0) -> Bottom-left in SkiaSharp (0, PanelHeight)
        var (x0, y0) = DashboardPanelRenderer.ConvertOpenVrMouseToCanvas(0f, 0f);
        Assert.Equal(0f, x0, 3);
        Assert.Equal(DashboardPanelRenderer.PanelHeight, y0, 3);

        // Top-right in OpenVR (PanelWidth, PanelHeight) -> Top-right in SkiaSharp (PanelWidth, 0)
        var (x1, y1) = DashboardPanelRenderer.ConvertOpenVrMouseToCanvas(
            DashboardPanelRenderer.PanelWidth,
            DashboardPanelRenderer.PanelHeight);
        Assert.Equal(DashboardPanelRenderer.PanelWidth, x1, 3);
        Assert.Equal(0f, y1, 3);

        // Center point remains center
        var (cx, cy) = DashboardPanelRenderer.ConvertOpenVrMouseToCanvas(600f, 380f);
        Assert.Equal(600f, cx, 3);
        Assert.Equal(380f, cy, 3);
    }

    [Fact]
    public void Render_PopulatesAllDashboardButtons_AndHitTestMatchesCenters()
    {
        using var renderer = new DashboardPanelRenderer();
        var settings = new LyricsOverlaySettings
        {
            AttachmentMode = OverlayAttachmentMode.WorldLocked,
            GlobalOffsetMs = 150,
            PreferredLyricsProvider = "NetEase",
        };

        var lyrics = new Lyrics
        {
            Source = "NetEase",
            Lines =
            [
                new LyricLine
                {
                    Start = TimeSpan.FromSeconds(15),
                    End = TimeSpan.FromSeconds(21),
                    Text = "故事的小黄花 从出生那年就飘着",
                    Translation = "Little yellow flowers of the story"
                }
            ]
        };

        var frame = new LyricsRenderFrame
        {
            Settings = settings,
            Track = new TrackInfo("晴天", "周杰伦", "叶惠美", TimeSpan.FromMinutes(4.5)),
            Lyrics = lyrics,
            PlaybackPosition = TimeSpan.FromSeconds(18),
            IsPlaying = true,
            StatusText = "NetEase · 逐字双语歌词",
        };

        var bmp = renderer.Render(new DashboardRenderState
        {
            Frame = frame,
            IsDemoLoopEnabled = false,
            CursorX = 400f,
            CursorY = 300f,
            IsCursorPressed = false,
            HoveredAction = DashboardAction.SetModeWorld,
        });

        Assert.Equal(DashboardPanelRenderer.PanelWidth, bmp.Width);
        Assert.Equal(DashboardPanelRenderer.PanelHeight, bmp.Height);
        Assert.Equal(Enum.GetValues<DashboardAction>().Length, renderer.Buttons.Count);

        // Every defined DashboardAction should be present and hit-testable at its center
        var expectedActions = Enum.GetValues<DashboardAction>().ToHashSet();

        foreach (var btn in renderer.Buttons)
        {
            var centerX = btn.Bounds.MidX;
            var centerY = btn.Bounds.MidY;
            var hit = renderer.HitTest(centerX, centerY, settings, isDemoLoop: false);
            Assert.Equal(btn.Action, hit);
            expectedActions.Remove(btn.Action);
        }

        Assert.Empty(expectedActions);
    }

    [Fact]
    public void RenderThumbnailPng_GeneratesValid128x128Png()
    {
        var bytes = DashboardPanelRenderer.RenderThumbnailPng();
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 200);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(128, decoded.Width);
        Assert.Equal(128, decoded.Height);
    }

    [Fact]
    public void ColorThemePreset_ApplyTo_And_Matches_WorkCorrectly()
    {
        var settings = new LyricsOverlaySettings();
        foreach (var preset in ColorThemePreset.All)
        {
            var updated = preset.ApplyTo(settings);
            Assert.True(preset.Matches(updated));
        }
    }

    [Fact]
    public async Task LyricsService_PrioritizesPreferredProvider()
    {
        var track = new TrackInfo("SongB", "ArtistA", "AlbumC", TimeSpan.FromSeconds(200));

        var pNetEase = new FakeProvider("NetEase", "NetEase Lyric Line");
        var pLrclib = new FakeProvider("LRCLIB", "LRCLIB Lyric Line");
        var service = new LyricsService([pNetEase, pLrclib], cacheDirectory: null);

        // Auto mode picks first provider in registration order (NetEase)
        var autoRes = await service.GetLyricsAsync(track, bypassCache: true, preferredProviderName: "Auto");
        Assert.NotNull(autoRes.Lyrics);
        Assert.Equal("NetEase", autoRes.Lyrics.Source);

        // Explicit LRCLIB preference picks LRCLIB first even though NetEase is first in the default list
        var lrclibRes = await service.GetLyricsAsync(track, bypassCache: true, preferredProviderName: "LRCLIB");
        Assert.NotNull(lrclibRes.Lyrics);
        Assert.Equal("LRCLIB", lrclibRes.Lyrics.Source);
    }

    private sealed class FakeProvider(string name, string lineText) : ILyricsProvider
    {
        public string Name => name;

        public Task<Lyrics?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default)
        {
            var doc = new Lyrics
            {
                Source = Name,
                MatchedTitle = track.Title,
                MatchedArtist = track.Artist,
                Lines =
                [
                    new LyricLine
                    {
                        Start = TimeSpan.Zero,
                        End = TimeSpan.FromSeconds(5),
                        Text = lineText
                    }
                ]
            };
            return Task.FromResult<Lyrics?>(doc);
        }
    }
}
