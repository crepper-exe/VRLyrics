using System.Diagnostics;
using SkiaSharp;
using VRLyrics.Core.Configuration;
using VRLyrics.Core.Models;
using VRLyrics.Core.Parsing;
using VRLyrics.Media;
using VRLyrics.Overlay;
using VRLyrics.Overlay.Dashboard;
using VRLyrics.Overlay.OpenVR;
using VRLyrics.Overlay.Rendering;
using VRLyrics.Providers;

namespace VRLyrics.App.Services;

/// <summary>
/// Central runtime host for the desktop application: coordinates <see cref="SettingsService"/>,
/// <see cref="MediaSessionService"/>, <see cref="LyricsService"/>, and <see cref="LyricsOverlayController"/>.
/// </summary>
public sealed class VRLyricsRuntimeHost : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Stopwatch _demoStopwatch = Stopwatch.StartNew();
    private readonly TrackInfo _demoTrack = new("晴天 (样式预览演示)", "周杰伦", "叶惠美", TimeSpan.FromSeconds(12));
    private readonly Lyrics _demoLyrics = CreateDemoLyrics();

    private CancellationTokenSource? _lookupCts;
    private Lyrics? _currentLyrics;
    private string _statusMessage = "正在等待音乐播放器会话…";
    private bool _forceDemoLoop;

    public VRLyricsRuntimeHost(SettingsService? settingsService = null)
    {
        Settings = settingsService ?? new SettingsService();
        Media = new MediaSessionService();
        LyricsService = new LyricsService(
            [
                new LocalLrcProvider(() => Settings.Current.LocalLyricsFolders),
                new NeteaseLyricsProvider(),
                new QqMusicLyricsProvider(),
                new LrclibLyricsProvider(),
            ],
            LyricsService.DefaultCacheDirectory);

        Overlay = new LyricsOverlayController(BuildRenderFrame);
        Overlay.WorldPoseCommitted += OnWorldPoseCommitted;
        Overlay.DashboardActionTriggered += ExecuteDashboardAction;
        Overlay.DashboardSliderValueChanged += ExecuteDashboardSliderChange;
        Media.TrackChanged += OnTrackChanged;
    }

    public SettingsService Settings { get; }

    public MediaSessionService Media { get; }

    public LyricsService LyricsService { get; }

    public LyricsOverlayController Overlay { get; }

    public Lyrics? CurrentLyrics
    {
        get { lock (_sync) return _currentLyrics; }
    }

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
    }

    /// <summary>
    /// When enabled, uses a continuous word-timed bilingual demo loop so users can preview
    /// karaoke/line animations even when no music player is playing.
    /// </summary>
    public bool ForceDemoLoop
    {
        get { lock (_sync) return _forceDemoLoop; }
        set
        {
            lock (_sync) _forceDemoLoop = value;
            Overlay.IsDemoLoopEnabled = value;
            StateChanged?.Invoke();
        }
    }

    public event Action? StateChanged;

    public async Task StartAsync()
    {
        // Immediately synchronize vrlyrics.vrmanifest, appconfig.json and vrlyrics.overlay.app.vrappconfig on disk
        Overlay.RegisterManifest(Settings.Current.AutoStartWithSteamVr);

        // Start VR overlay & desktop preview loop immediately without waiting on external services
        Overlay.Start();
        await Media.StartAsync().ConfigureAwait(false);
        if (Media.CurrentTrack is { } initial)
            _ = LoadLyricsForTrackAsync(initial, bypassCache: false);
    }

    public SteamVrManifestStatus SetAutoStartWithSteamVr(bool enabled)
    {
        Settings.Update(s => s with { AutoStartWithSteamVr = enabled });
        return Overlay.RegisterManifest(enabled);
    }

    public SteamVrManifestStatus UnregisterFromSteamVr()
    {
        Settings.Update(s => s with { AutoStartWithSteamVr = false });
        return Overlay.UnregisterManifest();
    }

    public LyricsOverlaySettings SetCurrentTrackOffsetMs(int offsetMs)
    {
        var track = Media.CurrentTrack;
        if (track is null)
            return Settings.Current;

        var clamped = Math.Clamp(offsetMs, -5000, 5000);
        return Settings.Update(s =>
        {
            var dict = new Dictionary<string, int>(s.PerTrackOffsetsMs ?? new Dictionary<string, int>(), StringComparer.Ordinal);
            if (clamped == 0)
                dict.Remove(track.Key);
            else
                dict[track.Key] = clamped;
            return s with { PerTrackOffsetsMs = dict };
        });
    }

    public void ReloadCurrentLyrics(bool bypassCache = true)
    {
        if (Media.CurrentTrack is { } track)
        {
            if (bypassCache)
                LyricsService.Invalidate(track);
            _ = LoadLyricsForTrackAsync(track, bypassCache);
        }
    }

    /// <summary>
    /// Switches the overlay attachment mode. When switching to <see cref="OverlayAttachmentMode.WorldLocked"/>,
    /// if the saved world coordinate is currently behind the user's HMD gaze, automatically brings it in front of the HMD.
    /// </summary>
    public LyricsOverlaySettings SwitchAttachmentMode(OverlayAttachmentMode mode)
    {
        if (mode == OverlayAttachmentMode.WorldLocked && !Overlay.IsSavedWorldPoseInFrontOfHmd(Settings.Current))
        {
            var pose = Overlay.ComputePoseInFrontOfHmd();
            return Settings.Update(s => pose.ApplyTo(s).WithAttachmentMode(mode));
        }

        return Settings.Update(s => s.WithAttachmentMode(mode));
    }

    /// <summary>
    /// Places the world-locked lyrics overlay directly in front of the user's current HMD view and persists the coordinates.
    /// </summary>
    public LyricsOverlaySettings RecenterWorldOverlayToHmd(float distanceMeters = 1.25f, float verticalOffsetMeters = -0.20f)
    {
        var pose = Overlay.ComputePoseInFrontOfHmd(distanceMeters, verticalOffsetMeters);
        return Settings.Update(s => pose.ApplyTo(s).WithAttachmentMode(OverlayAttachmentMode.WorldLocked));
    }

    /// <summary>Switches the preferred lyrics provider and immediately re-queries the current track.</summary>
    public LyricsOverlaySettings SwitchLyricsProvider(string preferredProvider)
    {
        var updated = Settings.Update(s => s with { PreferredLyricsProvider = preferredProvider });
        if (Media.CurrentTrack is { } track)
            _ = LoadLyricsForTrackAsync(track, bypassCache: false);
        return updated;
    }

    public void ExecuteDashboardAction(DashboardAction action)
    {
        switch (action)
        {
            case DashboardAction.MediaPrevious:
                _ = Media.SkipPreviousAsync();
                break;

            case DashboardAction.MediaPlayPause:
                _ = Media.TogglePlayPauseAsync();
                break;

            case DashboardAction.MediaNext:
                _ = Media.SkipNextAsync();
                break;

            case DashboardAction.ToggleEnabled:
                Settings.Update(s => s with { Enabled = !s.Enabled });
                break;

            case DashboardAction.ToggleDemoLoop:
                ForceDemoLoop = !ForceDemoLoop;
                break;

            case DashboardAction.SetModeHead:
                SwitchAttachmentMode(OverlayAttachmentMode.HeadLocked);
                break;

            case DashboardAction.SetModeWorld:
                SwitchAttachmentMode(OverlayAttachmentMode.WorldLocked);
                break;

            case DashboardAction.SetModeLeftHand:
                SwitchAttachmentMode(OverlayAttachmentMode.LeftHand);
                break;

            case DashboardAction.SetModeRightHand:
                SwitchAttachmentMode(OverlayAttachmentMode.RightHand);
                break;

            case DashboardAction.RecenterWorldToHmd:
                RecenterWorldOverlayToHmd();
                break;

            case DashboardAction.ToggleWorldControllerGrab:
                Settings.Update(s => s with { EnableWorldControllerGrab = !s.EnableWorldControllerGrab });
                break;

            case DashboardAction.WidthMinus:
                Settings.Update(s => s.WithWidthForCurrentMode(s.WidthInMeters - 0.10f));
                break;

            case DashboardAction.WidthPlus:
                Settings.Update(s => s.WithWidthForCurrentMode(s.WidthInMeters + 0.10f));
                break;

            case DashboardAction.CycleOpacity:
                Settings.Update(s =>
                {
                    var next = s.Opacity switch
                    {
                        >= 0.92f => 0.80f,
                        >= 0.72f => 0.60f,
                        >= 0.52f => 0.40f,
                        _ => 0.96f,
                    };
                    return s with { Opacity = next };
                });
                break;

            case DashboardAction.OffsetMinus500:
                Settings.Update(s => s with { GlobalOffsetMs = Math.Clamp(s.GlobalOffsetMs - 500, -5000, 5000) });
                break;

            case DashboardAction.OffsetMinus100:
                Settings.Update(s => s with { GlobalOffsetMs = Math.Clamp(s.GlobalOffsetMs - 100, -5000, 5000) });
                break;

            case DashboardAction.OffsetReset:
                Settings.Update(s => s with { GlobalOffsetMs = 0 });
                break;

            case DashboardAction.OffsetPlus100:
                Settings.Update(s => s with { GlobalOffsetMs = Math.Clamp(s.GlobalOffsetMs + 100, -5000, 5000) });
                break;

            case DashboardAction.OffsetPlus500:
                Settings.Update(s => s with { GlobalOffsetMs = Math.Clamp(s.GlobalOffsetMs + 500, -5000, 5000) });
                break;

            case DashboardAction.SourceAuto:
                SwitchLyricsProvider("Auto");
                break;

            case DashboardAction.SourceNetease:
                SwitchLyricsProvider("NetEase");
                break;

            case DashboardAction.SourceQqMusic:
                SwitchLyricsProvider("QQMusic");
                break;

            case DashboardAction.SourceLrclib:
                SwitchLyricsProvider("LRCLIB");
                break;

            case DashboardAction.SourceLocal:
                SwitchLyricsProvider("LocalLRC");
                break;

            case DashboardAction.ReloadLyrics:
                ReloadCurrentLyrics(bypassCache: true);
                break;

            case DashboardAction.ToggleTranslation:
                Settings.Update(s => s with { ShowTranslation = !s.ShowTranslation });
                break;

            case DashboardAction.ToggleContextLines:
                Settings.Update(s => s with { ShowContextLines = !s.ShowContextLines });
                break;

            case DashboardAction.ToggleKaraoke:
                Settings.Update(s => s with { EnableKaraokeHighlight = !s.EnableKaraokeHighlight });
                break;

            case DashboardAction.FontSizeMinus:
                Settings.Update(s => s with { MainFontSize = Math.Clamp(s.MainFontSize - 4f, 28f, 90f) });
                break;

            case DashboardAction.FontSizePlus:
                Settings.Update(s => s with { MainFontSize = Math.Clamp(s.MainFontSize + 4f, 28f, 90f) });
                break;

            case DashboardAction.ThemePreset0:
            case DashboardAction.ThemePreset1:
            case DashboardAction.ThemePreset2:
            case DashboardAction.ThemePreset3:
            case DashboardAction.ThemePreset4:
            {
                var idx = (int)action - (int)DashboardAction.ThemePreset0;
                if (idx >= 0 && idx < ColorThemePreset.All.Count)
                    Settings.Update(s => ColorThemePreset.All[idx].ApplyTo(s));
                break;
            }
        }
    }

    public void ExecuteDashboardSliderChange(DashboardSliderKind kind, float normalizedValue)
    {
        var t = Math.Clamp(normalizedValue, 0f, 1f);
        switch (kind)
        {
            case DashboardSliderKind.Width:
            {
                var width = MathF.Round(
                    DashboardPanelRenderer.MinWidthMeters + t * (DashboardPanelRenderer.MaxWidthMeters - DashboardPanelRenderer.MinWidthMeters),
                    2);
                Settings.Update(s => s.WithWidthForCurrentMode(width));
                break;
            }

            case DashboardSliderKind.Opacity:
            {
                var opacity = MathF.Round(
                    DashboardPanelRenderer.MinOpacity + t * (DashboardPanelRenderer.MaxOpacity - DashboardPanelRenderer.MinOpacity),
                    2);
                Settings.Update(s => s with { Opacity = Math.Clamp(opacity, DashboardPanelRenderer.MinOpacity, DashboardPanelRenderer.MaxOpacity) });
                break;
            }

            case DashboardSliderKind.FontSize:
            {
                var fontSize = MathF.Round(
                    DashboardPanelRenderer.MinFontSize + t * (DashboardPanelRenderer.MaxFontSize - DashboardPanelRenderer.MinFontSize));
                Settings.Update(s => s with { MainFontSize = Math.Clamp(fontSize, DashboardPanelRenderer.MinFontSize, DashboardPanelRenderer.MaxFontSize) });
                break;
            }
        }
    }

    private void OnWorldPoseCommitted(WorldOverlayPose pose)
    {
        Settings.Update(s => pose.ApplyTo(s));
        StateChanged?.Invoke();
    }

    private void OnTrackChanged(TrackInfo? track) =>
        _ = LoadLyricsForTrackAsync(track, bypassCache: false);

    private async Task LoadLyricsForTrackAsync(TrackInfo? track, bool bypassCache)
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            _lookupCts?.Cancel();
            _lookupCts = cts = new CancellationTokenSource();
            _currentLyrics = null;
            _statusMessage = track is null ? "当前没有正在播放的曲目" : $"正在搜索歌词：{track}…";
        }
        StateChanged?.Invoke();

        if (track is null)
            return;

        try
        {
            await Task.Delay(300, cts.Token).ConfigureAwait(false);
            track = Media.CurrentTrack ?? track;
            var prefProvider = Settings.Current.PreferredLyricsProvider;
            var result = await LyricsService.GetLyricsAsync(track, bypassCache, cts.Token, prefProvider).ConfigureAwait(false);
            lock (_sync)
            {
                if (cts.IsCancellationRequested) return;
                _currentLyrics = result.Lyrics;
                _statusMessage = result.Lyrics is not null
                    ? $"已匹配：{result.Lyrics.Source} #{result.Lyrics.SourceId} ({result.Lyrics.MatchedArtist} - {result.Lyrics.MatchedTitle})"
                    : "未找到匹配歌词 (" + string.Join(" / ", result.Log) + ")";
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _statusMessage = "歌词获取异常：" + ex.Message;
            }
        }

        StateChanged?.Invoke();
    }

    private LyricsRenderFrame BuildRenderFrame()
    {
        Lyrics? lyr;
        string status;
        bool demo;
        lock (_sync)
        {
            lyr = _currentLyrics;
            status = _statusMessage;
            demo = _forceDemoLoop;
        }

        var cfg = Settings.Current;
        var realTrack = Media.CurrentTrack;

        if (demo)
        {
            var demoMs = _demoStopwatch.Elapsed.TotalMilliseconds % 10500.0;
            return new LyricsRenderFrame
            {
                Track = _demoTrack,
                Lyrics = _demoLyrics,
                PlaybackPosition = TimeSpan.FromMilliseconds(demoMs) - TimeSpan.FromMilliseconds(cfg.GlobalOffsetMs),
                IsPlaying = true,
                MediaIsPlaying = realTrack is not null && Media.IsPlaying,
                StatusText = "样式调色演示模式（循环播放逐字卡拉OK与翻译预览）",
                Settings = cfg,
            };
        }

        return new LyricsRenderFrame
        {
            Track = realTrack,
            Lyrics = lyr,
            PlaybackPosition = Media.Position,
            IsPlaying = Media.IsPlaying,
            MediaIsPlaying = realTrack is not null && Media.IsPlaying,
            StatusText = status,
            Settings = cfg,
        };
    }

    private static Lyrics CreateDemoLyrics()
    {
        const string sampleYrc = """
            [0,3400](0,450,0)故(450,450,0)事(900,400,0)的(1300,600,0)小(1900,650,0)黄(2550,850,0)花
            [3400,3600](3400,450,0)从(3850,450,0)出(4300,450,0)生(4750,450,0)那(5200,500,0)年(5700,550,0)就(6250,600,0)飘(6850,550,0)着
            [7000,3500](7000,500,0)童(7500,500,0)年(8000,450,0)的(8450,650,0)荡(9100,650,0)秋(9750,750,0)千
            """;
        const string sampleTrans = """
            [00:00.00]The little yellow flower of the story
            [00:03.40]Drifting ever since the year we were born
            [00:07.00]The childhood swing swaying through memory
            """;

        return LyricsNormalizer.AttachTranslation(
            YrcParser.Parse(sampleYrc, "NetEase"),
            LrcParser.ParseLines(sampleTrans));
    }

    public async ValueTask DisposeAsync()
    {
        Overlay.DashboardActionTriggered -= ExecuteDashboardAction;
        Overlay.WorldPoseCommitted -= OnWorldPoseCommitted;
        Media.TrackChanged -= OnTrackChanged;
        lock (_sync)
        {
            _lookupCts?.Cancel();
            _lookupCts?.Dispose();
            _lookupCts = null;
        }
        await Overlay.DisposeAsync();
        await Media.DisposeAsync();
        Settings.Dispose();
    }
}
