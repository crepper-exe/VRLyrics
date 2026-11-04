using VRLyrics.Core.Configuration;
using VRLyrics.Core.Models;

namespace VRLyrics.Core.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void SettingsService_SavesAndLoadsJsonRoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"vrlyrics_settings_{Guid.NewGuid():N}.json");
        try
        {
            using (var svc = new SettingsService(tempFile, enableFileWatcher: false))
            {
                Assert.True(File.Exists(tempFile));
                Assert.Equal(OverlayAttachmentMode.HeadLocked, svc.Current.AttachmentMode);

                LyricsOverlaySettings? notified = null;
                svc.SettingsChanged += s => notified = s;

                svc.Update(s => s with
                {
                    AttachmentMode = OverlayAttachmentMode.RightHand,
                    MainFontSize = 64f,
                    GlobalOffsetMs = -350,
                    SungTopColorHex = "#FFFFAA00",
                    LocalLyricsFolders = [@"D:\Music\Lyrics"]
                });

                Assert.NotNull(notified);
                Assert.Equal(OverlayAttachmentMode.RightHand, notified.AttachmentMode);
                Assert.Equal(64f, svc.Current.MainFontSize);
                Assert.Equal(-350, svc.Current.GlobalOffsetMs);
            }

            // Re-open from disk and verify persistence
            using (var svc2 = new SettingsService(tempFile, enableFileWatcher: false))
            {
                Assert.Equal(OverlayAttachmentMode.RightHand, svc2.Current.AttachmentMode);
                Assert.Equal(64f, svc2.Current.MainFontSize);
                Assert.Equal(-350, svc2.Current.GlobalOffsetMs);
                Assert.Equal("#FFFFAA00", svc2.Current.SungTopColorHex);
                Assert.Single(svc2.Current.LocalLyricsFolders);
                Assert.Equal(@"D:\Music\Lyrics", svc2.Current.LocalLyricsFolders[0]);

                svc2.ResetToDefaults();
                Assert.Equal(OverlayAttachmentMode.HeadLocked, svc2.Current.AttachmentMode);
                Assert.Equal(52f, svc2.Current.MainFontSize);
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
