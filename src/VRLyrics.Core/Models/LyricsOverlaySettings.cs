namespace VRLyrics.Core.Models;

/// <summary>Where the lyrics overlay is anchored in VR space.</summary>
public enum OverlayAttachmentMode
{
    /// <summary>Follows the HMD (defaults to lower field of view).</summary>
    HeadLocked = 0,

    /// <summary>Fixed at a 3D position in the standing/seated universe.</summary>
    WorldLocked = 1,

    /// <summary>Attached to the left controller / wrist.</summary>
    LeftHand = 2,

    /// <summary>Attached to the right controller / wrist.</summary>
    RightHand = 3,
}

/// <summary>
/// User-configurable appearance, layout, animation and placement settings for the VR lyrics overlay.
/// Serializable to JSON for M4 settings persistence.
/// </summary>
public sealed record LyricsOverlaySettings
{
    public const float DefaultHeadWorldWidthMeters = 1.00f;
    public const float DefaultHandWidthMeters = 0.45f;

    public bool Enabled { get; init; } = true;

    // --- Placement & physical dimensions ---
    public OverlayAttachmentMode AttachmentMode { get; init; } = OverlayAttachmentMode.HeadLocked;

    /// <summary>Physical width of the overlay quad in meters for the currently active attachment mode.</summary>
    public float WidthInMeters { get; init; } = DefaultHeadWorldWidthMeters;

    /// <summary>User preset width (in meters) used when in <see cref="OverlayAttachmentMode.HeadLocked"/> or <see cref="OverlayAttachmentMode.WorldLocked"/>.</summary>
    public float HeadWorldWidthInMeters { get; init; } = DefaultHeadWorldWidthMeters;

    /// <summary>User preset width (in meters) used when in <see cref="OverlayAttachmentMode.LeftHand"/> or <see cref="OverlayAttachmentMode.RightHand"/>.</summary>
    public float HandWidthInMeters { get; init; } = DefaultHandWidthMeters;

    /// <summary>Overall overlay alpha (0..1).</summary>
    public float Opacity { get; init; } = 0.96f;

    /// <summary>Overlay surface curvature (0 = flat plane, 0.05..0.40 = cylindrical wrap around viewer).</summary>
    public float Curvature { get; init; } = 0.08f;

    // Head-locked offset (in HMD local space: +X right, +Y up, -Z forward).
    public float HeadOffsetX { get; init; } = 0.0f;
    public float HeadOffsetY { get; init; } = -0.24f;
    public float HeadOffsetZ { get; init; } = -1.15f;
    public float HeadYawDegrees { get; init; } = 0.0f;
    public float HeadPitchDegrees { get; init; } = 12.0f;

    // Hand-locked offset (in controller local space).
    public float HandOffsetX { get; init; } = 0.0f;
    public float HandOffsetY { get; init; } = 0.10f;
    public float HandOffsetZ { get; init; } = -0.05f;
    public float HandPitchDegrees { get; init; } = -45.0f;
    public float HandYawDegrees { get; init; } = 0.0f;
    public float HandRollDegrees { get; init; } = 0.0f;

    // World-locked pose (in standing universe space).
    public float WorldPositionX { get; init; } = 0.0f;
    public float WorldPositionY { get; init; } = 1.30f;
    public float WorldPositionZ { get; init; } = -1.40f;
    public float WorldYawDegrees { get; init; } = 0.0f;
    public float WorldPitchDegrees { get; init; } = 0.0f;

    /// <summary>Allow grabbing and dragging the world-locked overlay using VR controllers (Grip / Trigger).</summary>
    public bool EnableWorldControllerGrab { get; init; } = true;

    /// <summary>Automatically orient the world-locked overlay toward the HMD when dragged or recentered.</summary>
    public bool WorldAutoFaceHmd { get; init; } = true;

    // --- Render surface ---
    public int TextureWidth { get; init; } = 1600;
    public int TextureHeight { get; init; } = 420;

    // --- Typography ---
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public float MainFontSize { get; init; } = 52.0f;
    public float TranslationFontSize { get; init; } = 30.0f;
    public float ContextFontSize { get; init; } = 32.0f;
    public bool Bold { get; init; } = true;

    // --- Content toggles ---
    public bool ShowTranslation { get; init; } = true;
    public bool ShowContextLines { get; init; } = true;
    public int PreviousLinesCount { get; init; } = 1;
    public int NextLinesCount { get; init; } = 1;
    public bool ShowTrackHeader { get; init; } = true;

    // --- Karaoke & animations (M3) ---
    public bool EnableKaraokeHighlight { get; init; } = true;
    public bool EnableWordLift { get; init; } = true;
    public bool EnableLineTransitionAnimation { get; init; } = true;
    public int TransitionDurationMs { get; init; } = 220;

    // --- Colors (#AARRGGBB or #RRGGBB) ---
    public string SungTopColorHex { get; init; } = "#FF7CF7FF";
    public string SungBottomColorHex { get; init; } = "#FF00C2FF";
    public string SungGlowColorHex { get; init; } = "#8800D0FF";
    public string UnsungTopColorHex { get; init; } = "#FFFFFFFF";
    public string UnsungBottomColorHex { get; init; } = "#FFD2DCE8";
    public string TranslationColorHex { get; init; } = "#EBDFE8F6";
    public string ContextColorHex { get; init; } = "#94CBD5E1";
    public string StrokeColorHex { get; init; } = "#E8090D16";
    public float StrokeWidth { get; init; } = 5.5f;
    public string ShadowColorHex { get; init; } = "#C8000000";
    public float ShadowBlur { get; init; } = 8.0f;
    public string BackgroundColorHex { get; init; } = "#380A0F1A";
    public float BackgroundCornerRadius { get; init; } = 26.0f;

    // --- Timing & Local Sources ---
    public int GlobalOffsetMs { get; init; } = 0;

    /// <summary>Per-track timing offsets (in ms), keyed by <see cref="TrackInfo.Key"/>.</summary>
    public IReadOnlyDictionary<string, int> PerTrackOffsetsMs { get; init; } = new Dictionary<string, int>();

    /// <summary>Preferred lyrics provider name (<c>"Auto"</c>, <c>"NetEase"</c>, <c>"QQMusic"</c>, <c>"LRCLIB"</c>, or <c>"LocalLRC"</c>).</summary>
    public string PreferredLyricsProvider { get; init; } = "Auto";

    /// <summary>Ignore SMTC playback sessions originating from web browsers (Edge, Chrome, Firefox, etc.).</summary>
    public bool IgnoreBrowserMediaSessions { get; init; } = true;

    /// <summary>Automatically hide the VR lyrics overlay when no playback activity is captured for <see cref="AutoHideDelaySeconds"/>.</summary>
    public bool AutoHideWhenIdle { get; init; } = true;

    /// <summary>Seconds of inactivity (paused or no active media session) before auto-hiding the VR lyrics overlay.</summary>
    public int AutoHideDelaySeconds { get; init; } = 5;

    /// <summary>Optional user-configured directories to scan for local <c>.lrc</c> files.</summary>
    public IReadOnlyList<string> LocalLyricsFolders { get; init; } = [];

    // --- SteamVR Lifecycle (M7) ---
    /// <summary>Register <c>vrlyrics.vrmanifest</c> and auto-launch VRLyrics when SteamVR starts.</summary>
    public bool AutoStartWithSteamVr { get; init; } = false;

    /// <summary>Automatically exit the VRLyrics desktop app when SteamVR quits.</summary>
    public bool ExitWithSteamVr { get; init; } = false;

    public int GetTrackOffsetMs(TrackInfo? track) =>
        track is not null && PerTrackOffsetsMs is not null && PerTrackOffsetsMs.TryGetValue(track.Key, out var ms)
            ? ms
            : 0;

    public int GetTotalOffsetMs(TrackInfo? track) =>
        GlobalOffsetMs + GetTrackOffsetMs(track);

    public static bool IsHandAttachmentMode(OverlayAttachmentMode mode) =>
        mode is OverlayAttachmentMode.LeftHand or OverlayAttachmentMode.RightHand;

    /// <summary>
    /// Updates <see cref="WidthInMeters"/> and persists the value into the active mode group's width preset
    /// (<see cref="HandWidthInMeters"/> for Left/Right hand modes, <see cref="HeadWorldWidthInMeters"/> for Head/World modes).
    /// </summary>
    public LyricsOverlaySettings WithWidthForCurrentMode(float widthMeters)
    {
        var clamped = MathF.Round(Math.Clamp(widthMeters, 0.25f, 2.40f), 2);
        return IsHandAttachmentMode(AttachmentMode)
            ? this with { WidthInMeters = clamped, HandWidthInMeters = clamped }
            : this with { WidthInMeters = clamped, HeadWorldWidthInMeters = clamped };
    }

    /// <summary>
    /// Switches <see cref="AttachmentMode"/> and automatically applies the saved width preset for the target mode group
    /// (<c>0.45m</c> default for Left/Right hand modes, <c>1.00m</c> default for Head/World modes, or user's custom preset).
    /// </summary>
    public LyricsOverlaySettings WithAttachmentMode(OverlayAttachmentMode targetMode)
    {
        var synced = WithWidthForCurrentMode(WidthInMeters);
        var targetWidth = IsHandAttachmentMode(targetMode)
            ? synced.HandWidthInMeters
            : synced.HeadWorldWidthInMeters;
        return synced with
        {
            AttachmentMode = targetMode,
            WidthInMeters = MathF.Round(Math.Clamp(targetWidth, 0.25f, 2.40f), 2),
        };
    }
}
