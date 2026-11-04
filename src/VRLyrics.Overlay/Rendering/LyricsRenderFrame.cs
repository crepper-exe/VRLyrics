using VRLyrics.Core.Models;

namespace VRLyrics.Overlay.Rendering;

/// <summary>All inputs required to render one frame of the VR lyrics overlay.</summary>
public sealed record LyricsRenderFrame
{
    public TrackInfo? Track { get; init; }

    public Lyrics? Lyrics { get; init; }

    /// <summary>Raw playback position from the media session (before <see cref="LyricsOverlaySettings.GlobalOffsetMs"/>).</summary>
    public TimeSpan PlaybackPosition { get; init; }

    public bool IsPlaying { get; init; }

    /// <summary>Optional status text (e.g. searching, provider info, error).</summary>
    public string? StatusText { get; init; }

    /// <summary>Monotonic clock timestamp used for smooth UI animations.</summary>
    public TimeSpan FrameTimestamp { get; init; }

    /// <summary>True when a VR controller ray or hand is hovering over the world-locked overlay.</summary>
    public bool IsWorldGrabHovered { get; init; }

    /// <summary>True when a VR controller is actively dragging the world-locked overlay in 3D space.</summary>
    public bool IsWorldGrabActive { get; init; }

    public LyricsOverlaySettings Settings { get; init; } = new();

    /// <summary>Playback position with <see cref="LyricsOverlaySettings.GlobalOffsetMs"/> and per-track offset applied.</summary>
    public TimeSpan EffectivePosition => PlaybackPosition + TimeSpan.FromMilliseconds(Settings.GetTotalOffsetMs(Track));
}
