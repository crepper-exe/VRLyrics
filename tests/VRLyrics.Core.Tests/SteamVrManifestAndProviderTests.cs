using System.Net;
using System.Text;
using System.Text.Json;
using VRLyrics.Core.Models;
using VRLyrics.Overlay.OpenVR;
using VRLyrics.Overlay.Rendering;
using VRLyrics.Providers;

namespace VRLyrics.Core.Tests;

public class SteamVrManifestAndProviderTests
{
    [Fact]
    public void BuildManifestJson_ProducesValidSteamVrManifestSchema()
    {
        var exePath = @"E:\VR Dev\VRLyrics\dist\VRLyrics.App.exe";
        var iconPath = @"E:\VR Dev\VRLyrics\dist\dashboard_thumb.png";
        var json = SteamVrManifestService.BuildManifestJson(exePath, iconPath, "--minimized");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("builtin", root.GetProperty("source").GetString());

        var apps = root.GetProperty("applications");
        Assert.Equal(JsonValueKind.Array, apps.ValueKind);
        Assert.Equal(1, apps.GetArrayLength());

        var app = apps[0];
        Assert.Equal(SteamVrManifestService.AppKey, app.GetProperty("app_key").GetString());
        Assert.Equal("binary", app.GetProperty("launch_type").GetString());
        Assert.Equal(exePath, app.GetProperty("binary_path_windows").GetString());
        Assert.Equal("--minimized", app.GetProperty("arguments").GetString());
        Assert.True(app.GetProperty("is_dashboard_overlay").GetBoolean());
        Assert.Equal(iconPath, app.GetProperty("image_path").GetString());

        var zhName = app.GetProperty("strings").GetProperty("zh_cn").GetProperty("name").GetString();
        Assert.Contains("VRLyrics", zhName);
    }

    [Fact]
    public void EnsureManifestFile_WritesManifestAndThumbnailIconToTargetDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VRLyrics_ManifestTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var fakeExe = Path.Combine(tempDir, "VRLyrics.App.exe");
            var manifestPath = SteamVrManifestService.EnsureManifestFile(tempDir, fakeExe);

            Assert.True(File.Exists(manifestPath));
            Assert.True(File.Exists(Path.Combine(tempDir, "dashboard_thumb.png")));

            var status = SteamVrManifestService.QueryStatus(
                applications: null,
                configuredAutoStart: true,
                targetDirectory: tempDir,
                customExecutablePath: fakeExe);

            Assert.True(status.IsAutoLaunchEnabled);
            Assert.Equal(manifestPath, status.ManifestPath);
            Assert.Equal(fakeExe, status.ExecutablePath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SyncSteamVrConfigFilesDirectly_WritesAppConfigAndVrAppConfigFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "VRLyrics_SteamVrCfgTest_" + Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(tempRoot, "dist", "VRLyrics-win-x64");
        var cfgDir = Path.Combine(tempRoot, "steam_config");
        try
        {
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(cfgDir);

            var fakeExe = Path.Combine(appDir, "VRLyrics.exe");
            File.WriteAllText(fakeExe, "stub");
            var manifestPath = SteamVrManifestService.EnsureManifestFile(appDir, fakeExe);

            // Simulate an existing appconfig.json with another overlay app
            var appConfigPath = Path.Combine(cfgDir, "appconfig.json");
            File.WriteAllText(appConfigPath, """
            {
               "manifest_paths" : [
                  "D:\\OtherApp\\other.vrmanifest"
               ]
            }
            """);

            var (syncedInstalled, syncedAutoLaunch, effectivePath) = SteamVrManifestService.SyncSteamVrConfigFilesDirectly(
                manifestPath,
                registerManifest: true,
                enableAutoStart: true,
                customConfigDirectory: cfgDir);
            Assert.True(syncedInstalled);
            Assert.True(syncedAutoLaunch);
            Assert.Equal(manifestPath, effectivePath);

            var vrAppConfigPath = Path.Combine(cfgDir, "vrappconfig", $"{SteamVrManifestService.AppKey}.vrappconfig");
            Assert.True(File.Exists(vrAppConfigPath));

            var (installed, autoLaunch, registeredPath) = SteamVrManifestService.QuerySteamVrConfigFilesDirectly(cfgDir);
            Assert.True(installed);
            Assert.True(autoLaunch);
            Assert.Equal(manifestPath, registeredPath);

            // Verify disabling autoStart updates vrappconfig to autolaunch=false while preserving other manifests
            SteamVrManifestService.SyncSteamVrConfigFilesDirectly(
                manifestPath,
                registerManifest: true,
                enableAutoStart: false,
                customConfigDirectory: cfgDir);
            var (installedAfterDisable, autoLaunchAfterDisable, _) = SteamVrManifestService.QuerySteamVrConfigFilesDirectly(cfgDir);
            Assert.True(installedAfterDisable);
            Assert.False(autoLaunchAfterDisable);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void PerTrackOffsetsMs_CombinesWithGlobalOffsetInEffectivePosition()
    {
        var trackA = new TrackInfo("晴天", "周杰伦");
        var trackB = new TrackInfo("七里香", "周杰伦");

        var settings = new LyricsOverlaySettings
        {
            GlobalOffsetMs = 200,
            PerTrackOffsetsMs = new Dictionary<string, int>
            {
                [trackA.Key] = -350
            }
        };

        Assert.Equal(-350, settings.GetTrackOffsetMs(trackA));
        Assert.Equal(0, settings.GetTrackOffsetMs(trackB));
        Assert.Equal(-150, settings.GetTotalOffsetMs(trackA));
        Assert.Equal(200, settings.GetTotalOffsetMs(trackB));

        var frameA = new LyricsRenderFrame
        {
            Track = trackA,
            PlaybackPosition = TimeSpan.FromSeconds(10),
            Settings = settings,
        };
        Assert.Equal(TimeSpan.FromMilliseconds(9850), frameA.EffectivePosition);
    }

    [Fact]
    public async Task QqMusicLyricsProvider_ParsesBase64LrcAndTranslation()
    {
        const string rawLrc = "[00:01.00]故事的小黄花\n[00:04.50]从出生那年就飘着";
        const string rawTrans = "[00:01.00]The little yellow flower of the story\n[00:04.50]Drifting since the year we were born";

        var b64Lrc = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawLrc));
        var b64Trans = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawTrans));

        var searchJson = """
        {
          "req_1": {
            "data": {
              "body": {
                "song": {
                  "list": [
                    {
                      "mid": "0039MnYb0qxYhV",
                      "name": "晴天",
                      "interval": 269,
                      "singer": [{ "name": "周杰伦" }]
                    }
                  ]
                }
              }
            }
          }
        }
        """;

        var lyricJson = $$"""
        MusicJsonCallback({"retcode":0,"lyric":"{{b64Lrc}}","trans":"{{b64Trans}}"})
        """;

        using var http = new HttpClient(new StubHttpHandler(req =>
        {
            var body = req.RequestUri!.AbsoluteUri.Contains("musicu.fcg", StringComparison.OrdinalIgnoreCase)
                ? searchJson
                : lyricJson;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }));

        using var provider = new QqMusicLyricsProvider(http);
        var lyrics = await provider.GetLyricsAsync(new TrackInfo("晴天", "周杰伦", Duration: TimeSpan.FromSeconds(269)));

        Assert.NotNull(lyrics);
        Assert.Equal("QQMusic", lyrics.Source);
        Assert.Equal("0039MnYb0qxYhV", lyrics.SourceId);
        Assert.Equal(2, lyrics.Lines.Count);
        Assert.Equal("故事的小黄花", lyrics.Lines[0].Text);
        Assert.Equal("The little yellow flower of the story", lyrics.Lines[0].Translation);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
