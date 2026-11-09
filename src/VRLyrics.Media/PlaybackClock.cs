using System.Diagnostics;

namespace VRLyrics.Media;

/// <summary>
/// Local playback clock that is periodically corrected by position reports from SMTC or player-specific
/// probes (such as <see cref="NeteaseCloudMusicLogProbe"/>). Between reports the position is extrapolated
/// smoothly with a monotonic stopwatch.
/// </summary>
public sealed class PlaybackClock
{
    private readonly object _lock = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private TimeSpan _anchorPosition;
    private TimeSpan _anchorTime; // stopwatch time at anchor
    private bool _playing;
    private double _rate = 1.0;
    private TimeSpan? _duration;

    // Last raw SMTC report, used to detect whether a report actually carries new information.
    private TimeSpan _lastReportedPosition = TimeSpan.MinValue;
    private DateTimeOffset _lastReportedUpdate = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPauseTimeUtc = DateTimeOffset.MinValue;
    private bool _hasSyncedPlayingReport;

    public bool IsPlaying { get { lock (_lock) return _playing; } }

    public TimeSpan? Duration { get { lock (_lock) return _duration; } }

    public TimeSpan Position
    {
        get
        {
            lock (_lock) return ComputeUnlocked(_stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Feeds a standard SMTC timeline report.
    /// Windows SMTC players often report <c>Position</c> + <c>LastUpdatedTime</c> only once when the song
    /// starts or seeks, so <c>DateTimeOffset.UtcNow - lastUpdated</c> may legitimately be several minutes old
    /// when VRLyrics is launched mid-song.
    /// </summary>
    public void ReportTimeline(TimeSpan position, DateTimeOffset lastUpdated, TimeSpan? duration)
    {
        lock (_lock)
        {
            if (duration is { TotalSeconds: > 0 }) _duration = duration;

            var isNewReport = position != _lastReportedPosition || lastUpdated != _lastReportedUpdate;

            // Once we have locked onto a playing report (or paused at least once on the current track),
            // never re-extrapolate an unchanged pre-pause SMTC report—otherwise the time spent paused
            // would be added to (UtcNow - lastUpdated) when playback resumes.
            if (!isNewReport && (_hasSyncedPlayingReport || _lastPauseTimeUtc != DateTimeOffset.MinValue))
                return;

            var now = _stopwatch.Elapsed;
            var estimated = EstimatePosition(position, lastUpdated);

            var current = ComputeUnlocked(now);
            var drift = (estimated - current).Duration();

            if (!isNewReport && drift < TimeSpan.FromMilliseconds(200))
                return;

            _lastReportedPosition = position;
            _lastReportedUpdate = lastUpdated;
            _anchorPosition = estimated;
            _anchorTime = now;
            if (_playing)
                _hasSyncedPlayingReport = true;
        }
    }

    /// <summary>
    /// Updates the clock from a direct player probe (e.g. NetEase Cloud Music log probe).
    /// Only snaps the anchor if the drift exceeds <paramref name="driftThreshold"/> so 60fps rendering stays smooth.
    /// </summary>
    public void SyncFromProbe(TimeSpan probePosition, TimeSpan? duration, TimeSpan? driftThreshold = null)
    {
        lock (_lock)
        {
            if (duration is { TotalSeconds: > 0 }) _duration = duration;
            var threshold = driftThreshold ?? TimeSpan.FromMilliseconds(45);
            var now = _stopwatch.Elapsed;
            var current = ComputeUnlocked(now);
            if ((probePosition - current).Duration() >= threshold)
            {
                _anchorPosition = probePosition;
                _anchorTime = now;
            }
        }
    }

    public void SetPlaying(bool playing, double rate = 1.0)
    {
        lock (_lock)
        {
            var wasPlaying = _playing;
            var now = _stopwatch.Elapsed;
            _anchorPosition = ComputeUnlocked(now);
            _anchorTime = now;
            _playing = playing;
            _rate = rate > 0 ? rate : 1.0;

            if (wasPlaying && !playing)
            {
                _lastPauseTimeUtc = DateTimeOffset.UtcNow;
            }
            else if (_playing &&
                     !_hasSyncedPlayingReport &&
                     _lastPauseTimeUtc == DateTimeOffset.MinValue &&
                     _lastReportedPosition != TimeSpan.MinValue &&
                     _lastReportedUpdate.Year > 2000)
            {
                // Mid-song startup: if ReportTimeline was called while _playing was still false,
                // extrapolate once when _playing turns true for the first time.
                _anchorPosition = EstimatePosition(_lastReportedPosition, _lastReportedUpdate);
                _hasSyncedPlayingReport = true;
            }
        }
    }

    /// <summary>Hard reset, e.g. on track change.</summary>
    public void Reset(TimeSpan position, TimeSpan? duration)
    {
        lock (_lock)
        {
            _anchorPosition = position;
            _anchorTime = _stopwatch.Elapsed;
            _duration = duration;
            _lastReportedPosition = TimeSpan.MinValue;
            _lastReportedUpdate = DateTimeOffset.MinValue;
            _lastPauseTimeUtc = DateTimeOffset.MinValue;
            _hasSyncedPlayingReport = false;
        }
    }

    private TimeSpan EstimatePosition(TimeSpan reportedPosition, DateTimeOffset lastUpdated)
    {
        var estimated = reportedPosition;
        if (_playing && lastUpdated.Year > 2000 && lastUpdated > _lastPauseTimeUtc)
        {
            var age = DateTimeOffset.UtcNow - lastUpdated.ToUniversalTime();
            var maxAge = (_duration is { TotalSeconds: > 0 } d ? d : TimeSpan.FromHours(2)) + TimeSpan.FromMinutes(2);
            if (age > TimeSpan.Zero && age <= maxAge)
                estimated += age * _rate;
        }
        if (estimated < TimeSpan.Zero) estimated = TimeSpan.Zero;
        if (_duration is { TotalSeconds: > 0 } dur && estimated > dur) estimated = dur;
        return estimated;
    }

    private TimeSpan ComputeUnlocked(TimeSpan now)
    {
        var pos = _playing ? _anchorPosition + (now - _anchorTime) * _rate : _anchorPosition;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (_duration is { } d && d > TimeSpan.Zero && pos > d) pos = d;
        return pos;
    }
}
