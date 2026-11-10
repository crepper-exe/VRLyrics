using System.Diagnostics;
using SkiaSharp;
using VRLyrics.Core.Models;
using VRLyrics.Overlay.Dashboard;
using VRLyrics.Overlay.OpenVR;
using VRLyrics.Overlay.Rendering;

namespace VRLyrics.Overlay;

/// <summary>
/// High-level loop that renders lyrics frames with <see cref="LyricsRenderer"/> and submits them
/// to <see cref="SteamVrOverlayService"/> at 60 FPS during active playback/animations (throttled when idle).
/// Automatically retries connecting to SteamVR when SteamVR is not yet running.
/// </summary>
public sealed class LyricsOverlayController : IAsyncDisposable
{
    private readonly Func<LyricsRenderFrame> _frameProvider;
    private readonly LyricsRenderer _renderer = new();
    private readonly SteamVrOverlayService _vrService = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();

    private Task? _loopTask;
    private DateTimeOffset _lastConnectAttempt = DateTimeOffset.MinValue;
    private TimeSpan _lastLoopTimestamp;
    private LyricsOverlaySettings? _lastSettings;
    private TimeSpan? _lastActivityTimestamp;
    private string? _lastTrackKey;
    private TimeSpan _lastPlaybackPosition;
    private bool _hasLastPlaybackPosition;
    private volatile bool _isAutoHidden;

    public LyricsOverlayController(Func<LyricsRenderFrame> frameProvider)
    {
        _frameProvider = frameProvider;
    }

    public bool IsVrConnected => _vrService.IsConnected;

    public string VrStatusMessage => _vrService.LastStatusMessage;

    public SteamVrManifestStatus? LastManifestStatus => _vrService.LastManifestStatus;

    public bool IsWorldGrabHovered => _vrService.IsWorldGrabHovered;

    public bool IsWorldGrabActive => _vrService.IsWorldGrabActive;

    /// <summary>True when the VR overlay is currently hidden after <see cref="LyricsOverlaySettings.AutoHideDelaySeconds"/> of no playback activity.</summary>
    public bool IsAutoHidden => _isAutoHidden;

    public bool IsDemoLoopEnabled { get; set; }

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>
    /// Raised on the render loop thread immediately after each frame is rendered into the internal <see cref="SKBitmap"/>.
    /// Subscribers (such as the WPF live preview) should copy pixel data before returning.
    /// </summary>
    public event Action<LyricsRenderFrame, SKBitmap>? FrameRendered;

    public event Action? VrQuitRequested
    {
        add => _vrService.QuitRequested += value;
        remove => _vrService.QuitRequested -= value;
    }

    public event Action? VrElevationRequired
    {
        add => _vrService.ElevationRequired += value;
        remove => _vrService.ElevationRequired -= value;
    }

    /// <summary>
    /// Raised when the user finishes dragging the world-locked overlay with a VR controller
    /// so the updated 3D pose can be persisted to <c>settings.json</c>.
    /// </summary>
    public event Action<WorldOverlayPose>? WorldPoseCommitted
    {
        add => _vrService.WorldPoseCommitted += value;
        remove => _vrService.WorldPoseCommitted -= value;
    }

    /// <summary>Raised when the user clicks a control on the in-VR SteamVR Dashboard panel.</summary>
    public event Action<DashboardAction>? DashboardActionTriggered
    {
        add => _vrService.DashboardActionTriggered += value;
        remove => _vrService.DashboardActionTriggered -= value;
    }

    /// <summary>Raised when the user clicks or drags a mobile-volume-style pill slider on the in-VR SteamVR Dashboard panel.</summary>
    public event Action<DashboardSliderKind, float>? DashboardSliderValueChanged
    {
        add => _vrService.DashboardSliderValueChanged += value;
        remove => _vrService.DashboardSliderValueChanged -= value;
    }

    public WorldOverlayPose ComputePoseInFrontOfHmd(float distanceMeters = 1.25f, float verticalOffsetMeters = -0.20f) =>
        _vrService.ComputePoseInFrontOfHmd(distanceMeters, verticalOffsetMeters);

    public bool IsSavedWorldPoseInFrontOfHmd(LyricsOverlaySettings settings) =>
        _vrService.IsSavedWorldPoseInFrontOfHmd(settings);

    public SteamVrManifestStatus RegisterManifest(bool enableAutoStart) =>
        _vrService.RegisterManifest(enableAutoStart);

    public SteamVrManifestStatus UnregisterManifest() =>
        _vrService.UnregisterManifest();

    public SteamVrManifestStatus QueryManifestStatus(bool configuredAutoStart) =>
        _vrService.QueryManifestStatus(configuredAutoStart);

    public void Start()
    {
        if (_loopTask is not null) return;
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>Renders a single frame immediately (e.g. for desktop preview or snapshot export).</summary>
    public SKBitmap RenderSingleFrame(LyricsRenderFrame frame) =>
        _renderer.RenderFrame(frame with { FrameTimestamp = _clock.Elapsed });

    /// <summary>
    /// Evaluates whether the VR lyrics overlay should auto-hide after <see cref="LyricsOverlaySettings.AutoHideDelaySeconds"/>
    /// without playback activity, and computes a smooth opacity fade-out scale.
    /// </summary>
    public (bool IsAutoHidden, float OpacityScale) UpdateAutoHideState(
        LyricsRenderFrame rawFrame,
        TimeSpan timestamp,
        bool settingsChanged = false,
        bool isGrabInteracting = false)
    {
        var trackChanged = rawFrame.Track is not null &&
                           !string.Equals(rawFrame.Track.Key, _lastTrackKey, StringComparison.Ordinal);
        var positionJumped = rawFrame.Track is not null &&
                             _hasLastPlaybackPosition &&
                             (rawFrame.PlaybackPosition - _lastPlaybackPosition).Duration() > TimeSpan.FromMilliseconds(80);

        _lastTrackKey = rawFrame.Track?.Key;
        _lastPlaybackPosition = rawFrame.PlaybackPosition;
        _hasLastPlaybackPosition = rawFrame.Track is not null;

        var hasActivity = rawFrame.IsPlaying ||
                          IsDemoLoopEnabled ||
                          trackChanged ||
                          positionJumped ||
                          isGrabInteracting ||
                          settingsChanged;

        if (!rawFrame.Settings.AutoHideWhenIdle || rawFrame.Settings.AutoHideDelaySeconds <= 0 || hasActivity)
        {
            _lastActivityTimestamp = timestamp;
            _isAutoHidden = false;
            return (false, 1.0f);
        }

        _lastActivityTimestamp ??= timestamp;
        var idleElapsed = timestamp - _lastActivityTimestamp.Value;
        var delay = TimeSpan.FromSeconds(Math.Clamp(rawFrame.Settings.AutoHideDelaySeconds, 1, 3600));
        const double fadeDurationSeconds = 0.25;

        if (idleElapsed < delay)
        {
            _isAutoHidden = false;
            return (false, 1.0f);
        }

        var fadeElapsed = (idleElapsed - delay).TotalSeconds;
        if (fadeElapsed < fadeDurationSeconds)
        {
            _isAutoHidden = false;
            var scale = Math.Clamp(1.0f - (float)(fadeElapsed / fadeDurationSeconds), 0.01f, 1.0f);
            return (false, scale);
        }

        _isAutoHidden = true;
        return (true, 0.0f);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frameStart = _clock.Elapsed;
            var dtSeconds = (float)Math.Clamp((frameStart - _lastLoopTimestamp).TotalSeconds, 0.001, 0.1);
            _lastLoopTimestamp = frameStart;

            try
            {
                if (!_vrService.IsConnected)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastConnectAttempt >= TimeSpan.FromSeconds(3))
                    {
                        _lastConnectAttempt = now;
                        _vrService.TryConnect();
                    }
                }

                var rawFrame = _frameProvider();
                var settingsChanged = _lastSettings is not null && !Equals(_lastSettings, rawFrame.Settings);
                _lastSettings = rawFrame.Settings;

                var isGrabInteracting = _vrService.IsWorldGrabHovered || _vrService.IsWorldGrabActive;
                var (isAutoHidden, opacityScale) = UpdateAutoHideState(
                    rawFrame,
                    frameStart,
                    settingsChanged,
                    isGrabInteracting);

                var vrActive = _vrService.IsConnected && _vrService.PollEvents();
                if (vrActive)
                    _vrService.ApplySettings(rawFrame.Settings, dtSeconds, isAutoHidden, opacityScale);

                var frame = rawFrame with
                {
                    FrameTimestamp = frameStart,
                    IsWorldGrabHovered = _vrService.IsWorldGrabHovered,
                    IsWorldGrabActive = _vrService.IsWorldGrabActive,
                    IsAutoHidden = isAutoHidden,
                };

                var bitmap = _renderer.RenderFrame(frame);

                if (vrActive)
                {
                    if (frame.Settings.Enabled && !isAutoHidden)
                        _vrService.SubmitFrame(bitmap);

                    _vrService.UpdateDashboard(frame, IsDemoLoopEnabled);
                }

                try
                {
                    FrameRendered?.Invoke(frame, bitmap);
                }
                catch
                {
                    // Never allow a UI preview listener exception to disrupt the VR overlay loop.
                }

                // 60 FPS (~16.6 ms) when playing, animating, fading out, tweaking settings, interacting with the VR Dashboard,
                // or polling VR controller grab in WorldLocked mode.
                var worldGrabListening = vrActive &&
                                         !isAutoHidden &&
                                         frame.Settings.AttachmentMode == OverlayAttachmentMode.WorldLocked &&
                                         frame.Settings.EnableWorldControllerGrab;
                var isFadingOut = !isAutoHidden && opacityScale < 1.0f;

                var targetFrameTime = (_renderer.IsAnimating ||
                                       frame.IsPlaying ||
                                       isFadingOut ||
                                       settingsChanged ||
                                       worldGrabListening ||
                                       _vrService.IsDashboardActive)
                    ? TimeSpan.FromMilliseconds(16.6)
                    : TimeSpan.FromMilliseconds(80);

                var elapsed = _clock.Elapsed - frameStart;
                var delay = targetFrameTime - elapsed;
                if (delay > TimeSpan.FromMilliseconds(1))
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _vrService.Dispose();
        _renderer.Dispose();
    }
}
