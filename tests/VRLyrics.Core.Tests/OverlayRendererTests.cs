using SkiaSharp;
using VRLyrics.Core.Models;
using VRLyrics.Core.Parsing;
using VRLyrics.Overlay.Direct3D;
using VRLyrics.Overlay.OpenVR;
using VRLyrics.Overlay.Rendering;

namespace VRLyrics.Core.Tests;

public class OverlayRendererTests
{
    [Fact]
    public void ColorParseHelper_parses_rgb_and_argb()
    {
        var rgb = ColorParseHelper.Parse("#123456", SKColors.Black);
        Assert.Equal(new SKColor(0x12, 0x34, 0x56, 0xFF), rgb);

        var argb = ColorParseHelper.Parse("#80123456", SKColors.Black);
        Assert.Equal(new SKColor(0x12, 0x34, 0x56, 0x80), argb);

        var fallback = ColorParseHelper.Parse("not-a-color", SKColors.Red);
        Assert.Equal(SKColors.Red, fallback);
    }

    [Fact]
    public void LyricsAnimator_animates_forward_step_and_snaps_on_seek()
    {
        var anim = new LyricsAnimator();
        var dur = TimeSpan.FromMilliseconds(200);

        var s0 = anim.Update("song1", 0, TimeSpan.FromMilliseconds(1000), dur, true);
        Assert.False(s0.IsTransitioning);

        // Advance to line 1 at t=2000ms -> start of transition
        var s1 = anim.Update("song1", 1, TimeSpan.FromMilliseconds(2000), dur, true);
        Assert.True(s1.IsTransitioning);
        Assert.Equal(0, s1.PreviousLineIndex);
        Assert.Equal(1, s1.CurrentLineIndex);
        Assert.Equal(0f, s1.TransitionProgress, 3);

        // Midway at t=2100ms
        var sMid = anim.Update("song1", 1, TimeSpan.FromMilliseconds(2100), dur, true);
        Assert.True(sMid.IsTransitioning);
        Assert.Equal(0.5f, sMid.TransitionProgress, 3);
        Assert.True(sMid.EasedProgress > 0.8f); // cubic ease-out is > 0.5 at t=0.5

        // Finished at t=2250ms
        var sDone = anim.Update("song1", 1, TimeSpan.FromMilliseconds(2250), dur, true);
        Assert.False(sDone.IsTransitioning);

        // Seek far ahead to line 10 -> snaps immediately
        var sSeek = anim.Update("song1", 10, TimeSpan.FromMilliseconds(2300), dur, true);
        Assert.False(sSeek.IsTransitioning);
        Assert.Equal(10, sSeek.CurrentLineIndex);
    }

    [Fact]
    public void OverlayTransformHelper_builds_expected_translation_and_rotation()
    {
        var settings = new LyricsOverlaySettings
        {
            AttachmentMode = OverlayAttachmentMode.HeadLocked,
            HeadOffsetX = 0.05f,
            HeadOffsetY = -0.25f,
            HeadOffsetZ = -1.20f,
            HeadPitchDegrees = 0f,
        };

        var m = OverlayTransformHelper.BuildTransform(settings);
        Assert.Equal(0.05f, m.m3, 4);
        Assert.Equal(-0.25f, m.m7, 4);
        Assert.Equal(-1.20f, m.m11, 4);
        Assert.Equal(1f, m.m0, 4);
        Assert.Equal(1f, m.m5, 4);
        Assert.Equal(1f, m.m10, 4);
    }

    [Fact]
    public void LyricsRenderer_renders_karaoke_and_uploads_to_d3d11()
    {
        var yrc = """
            [1000,2000](1000,1000,0)Hello (2000,1000,0)World
            [3000,2000](3000,1000,0)Next (4000,1000,0)Line
            """;
        var trans = LrcParser.ParseLines("[00:01.00]你好世界\n[00:03.00]下一行");
        var lyrics = LyricsNormalizer.AttachTranslation(YrcParser.Parse(yrc, "NetEase"), trans);

        using var renderer = new LyricsRenderer();
        var frame = new LyricsRenderFrame
        {
            Track = new TrackInfo("Test Song", "Test Artist"),
            Lyrics = lyrics,
            PlaybackPosition = TimeSpan.FromMilliseconds(1500),
            IsPlaying = true,
            FrameTimestamp = TimeSpan.FromSeconds(1),
            Settings = new LyricsOverlaySettings { TextureWidth = 800, TextureHeight = 240 },
        };

        var bmp = renderer.RenderFrame(frame);
        Assert.Equal(800, bmp.Width);
        Assert.Equal(240, bmp.Height);

        var png = renderer.EncodeToPng();
        Assert.True(png.Length > 1000);

        using var d3d = new D3D11TextureBridge();
        var handle = d3d.UploadBitmap(bmp);
        Assert.NotEqual(0, handle);
    }
}
