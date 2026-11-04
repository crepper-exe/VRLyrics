namespace VRLyrics.Overlay.Rendering;

/// <summary>Computed line-transition animation state for a single render frame.</summary>
public readonly record struct LineAnimationState(
    int CurrentLineIndex,
    int PreviousLineIndex,
    float TransitionProgress,
    float EasedProgress)
{
    public bool IsTransitioning => TransitionProgress < 1f && PreviousLineIndex != CurrentLineIndex;
}

/// <summary>
/// Tracks lyric line changes across frames and computes smooth easing values for vertical scroll,
/// scale and opacity transitions (M3).
/// </summary>
public sealed class LyricsAnimator
{
    private string? _lastTrackKey;
    private int _currentLineIndex = -1;
    private int _previousLineIndex = -1;
    private TimeSpan _transitionStartTime;
    private bool _initialized;

    public void Reset()
    {
        _lastTrackKey = null;
        _currentLineIndex = -1;
        _previousLineIndex = -1;
        _initialized = false;
    }

    /// <summary>
    /// Updates the animator with the current track, active line index and a monotonic frame timestamp.
    /// </summary>
    public LineAnimationState Update(
        string? trackKey,
        int targetLineIndex,
        TimeSpan frameTimestamp,
        TimeSpan transitionDuration,
        bool enableAnimation)
    {
        if (!_initialized || trackKey != _lastTrackKey)
        {
            _lastTrackKey = trackKey;
            _currentLineIndex = targetLineIndex;
            _previousLineIndex = targetLineIndex;
            _transitionStartTime = frameTimestamp;
            _initialized = true;
            return new LineAnimationState(targetLineIndex, targetLineIndex, 1f, 1f);
        }

        if (targetLineIndex != _currentLineIndex)
        {
            // Only animate normal forward stepping (1 or 2 lines); seeks jump immediately.
            var delta = targetLineIndex - _currentLineIndex;
            if (enableAnimation && transitionDuration > TimeSpan.Zero && delta is >= 1 and <= 2 && _currentLineIndex >= -1)
            {
                _previousLineIndex = _currentLineIndex;
                _currentLineIndex = targetLineIndex;
                _transitionStartTime = frameTimestamp;
            }
            else
            {
                _previousLineIndex = targetLineIndex;
                _currentLineIndex = targetLineIndex;
                _transitionStartTime = frameTimestamp - transitionDuration;
            }
        }

        if (!enableAnimation || transitionDuration <= TimeSpan.Zero || _previousLineIndex == _currentLineIndex)
            return new LineAnimationState(_currentLineIndex, _currentLineIndex, 1f, 1f);

        var elapsed = frameTimestamp - _transitionStartTime;
        var raw = (float)Math.Clamp(elapsed / transitionDuration, 0.0, 1.0);
        var eased = EaseOutCubic(raw);
        if (raw >= 1f) _previousLineIndex = _currentLineIndex;

        return new LineAnimationState(_currentLineIndex, _previousLineIndex, raw, eased);
    }

    /// <summary>Cubic ease-out: fast start, smooth deceleration into the resting position.</summary>
    public static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    /// <summary>Smooth 0 -> 1 -> 0 bell curve over a word's duration for subtle active-word lift.</summary>
    public static float WordLiftCurve(double wordProgress)
    {
        var p = (float)Math.Clamp(wordProgress, 0.0, 1.0);
        return MathF.Sin(p * MathF.PI);
    }
}
