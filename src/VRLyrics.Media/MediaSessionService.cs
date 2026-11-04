using VRLyrics.Core.Models;
using Windows.Media.Control;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace VRLyrics.Media;

/// <summary>
/// Tracks the active Windows media session (SMTC) — works with any player that integrates with the
/// Windows media overlay: Spotify, NetEase Cloud Music, QQ Music, browsers, foobar2000, ...
/// Also includes a dedicated real-time progress probe for NetEase Cloud Music 3.x (<c>cloudmusic.exe</c>),
/// which does not report timeline positions to SMTC.
/// </summary>
public sealed class MediaSessionService : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly PlaybackClock _clock = new();
    private readonly NeteaseCloudMusicLogProbe _neteaseProbe = new();
    private readonly CancellationTokenSource _cts = new();

    private SessionManager? _manager;
    private Session? _session;
    private Task? _pollTask;
    private TrackInfo? _track;

    /// <summary>Raised (on a background thread) when the track changes. Argument may be null when nothing plays.</summary>
    public event Action<TrackInfo?>? TrackChanged;

    /// <summary>Raised when play/pause state changes.</summary>
    public event Action<bool>? PlaybackStateChanged;

    /// <summary>Optional filter: only accept sessions whose app id contains one of these strings (case-insensitive).</summary>
    public IReadOnlyList<string> AppFilter { get; set; } = [];

    /// <summary>How often to poll the timeline and player probes.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public TrackInfo? CurrentTrack { get { lock (_lock) return _track; } }

    public string? CurrentAppId { get { lock (_lock) return _session?.SourceAppUserModelId; } }

    public bool IsPlaying => _clock.IsPlaying;

    /// <summary>Current interpolated playback position.</summary>
    public TimeSpan Position => _clock.Position;

    public async Task StartAsync()
    {
        await TryInitializeManagerAsync().ConfigureAwait(false);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    private async Task TryInitializeManagerAsync()
    {
        if (_manager is not null) return;
        try
        {
            // Run WinRT SMTC RequestAsync on MTA thread pool with a timeout so it never hangs STA UI startup
            var requestTask = Task.Run(async () => await SessionManager.RequestAsync().AsTask().ConfigureAwait(false));
            var completed = await Task.WhenAny(requestTask, Task.Delay(3000, _cts.Token)).ConfigureAwait(false);
            if (completed != requestTask) return;

            var mgr = await requestTask.ConfigureAwait(false);
            _manager = mgr;
            mgr.CurrentSessionChanged += (_, _) => _ = RefreshSessionAsync();
            mgr.SessionsChanged += (_, _) => _ = RefreshSessionAsync();
            await RefreshSessionAsync().ConfigureAwait(false);
        }
        catch
        {
            // Will retry periodically inside PollLoopAsync
        }
    }

    /// <summary>Lists app ids of all current media sessions (for the settings UI).</summary>
    public IReadOnlyList<string> GetSessionAppIds() =>
        _manager?.GetSessions().Select(s => s.SourceAppUserModelId).ToList() ?? [];

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        var tick = 0;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (_manager is null)
                {
                    if (++tick % 8 == 0)
                        await TryInitializeManagerAsync().ConfigureAwait(false);
                    continue;
                }

                // Re-evaluate which session to follow every ~2 s (another player may have started).
                if (++tick % 8 == 0) await RefreshSessionAsync().ConfigureAwait(false);

                Session? s;
                lock (_lock) s = _session;
                if (s is null) continue;
                SyncSessionState(s);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sessions can vanish at any moment (COMException). Just retry next tick.
            }
        }
    }

    private async Task RefreshSessionAsync()
    {
        var manager = _manager;
        if (manager is null) return;

        Session? chosen;
        try
        {
            chosen = ChooseSession(manager);
        }
        catch (Exception)
        {
            return;
        }

        lock (_lock)
        {
            if (ReferenceEquals(chosen, _session)) return;
            if (chosen is not null && _session is not null &&
                chosen.SourceAppUserModelId == _session.SourceAppUserModelId) return;

            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            _session = chosen;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
        }

        if (chosen is null)
        {
            SetTrack(null);
            _clock.SetPlaying(false);
            return;
        }

        // Update playback & timeline BEFORE and AFTER reading media properties so mid-song startup
        // immediately locks onto the current position instead of staying at 00:00.
        SyncSessionState(chosen);
        await UpdateMediaPropertiesAsync(chosen).ConfigureAwait(false);
        SyncSessionState(chosen);
    }

    private void SyncSessionState(Session s)
    {
        UpdatePlayback(s);
        UpdateTimeline(s);
        UpdatePlayerSpecificProbe(s);
    }

    private Session? ChooseSession(SessionManager manager)
    {
        var sessions = manager.GetSessions().Where(Accept).ToList();
        if (sessions.Count == 0) return null;

        var playing = sessions.Where(IsSessionPlaying).ToList();
        var current = manager.GetCurrentSession();
        Session? previous;
        lock (_lock) previous = _session;

        if (playing.Count > 0)
        {
            return playing.FirstOrDefault(s => s.SourceAppUserModelId == previous?.SourceAppUserModelId)
                   ?? playing.FirstOrDefault(s => s.SourceAppUserModelId == current?.SourceAppUserModelId)
                   ?? playing[0];
        }

        return sessions.FirstOrDefault(s => s.SourceAppUserModelId == previous?.SourceAppUserModelId)
               ?? sessions.FirstOrDefault(s => s.SourceAppUserModelId == current?.SourceAppUserModelId)
               ?? sessions[0];
    }

    private bool Accept(Session s)
    {
        if (AppFilter.Count == 0) return true;
        var id = s.SourceAppUserModelId ?? "";
        return AppFilter.Any(f => id.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSessionPlaying(Session s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing; }
        catch (Exception) { return false; }
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        _ = UpdateMediaPropertiesAsync(sender);

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args)
    {
        try { SyncSessionState(sender); } catch (Exception) { }
    }

    private void OnTimelinePropertiesChanged(Session sender, TimelinePropertiesChangedEventArgs args)
    {
        try { SyncSessionState(sender); } catch (Exception) { }
    }

    private async Task UpdateMediaPropertiesAsync(Session s)
    {
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (props is null) return;
            lock (_lock) if (!ReferenceEquals(s, _session)) return;

            var title = props.Title?.Trim() ?? "";
            var artist = props.Artist?.Trim() ?? "";
            if (string.IsNullOrEmpty(artist)) artist = props.AlbumArtist?.Trim() ?? "";
            TimeSpan? duration = null;
            try
            {
                var tl = s.GetTimelineProperties();
                var d = tl.EndTime - tl.StartTime;
                if (d > TimeSpan.Zero) duration = d;
            }
            catch (Exception) { }

            long? neteaseSongId = null;
            if (IsNeteaseSession(s) && _neteaseProbe.Poll() is { } snap)
            {
                neteaseSongId = snap.SongId;
                duration ??= snap.Duration;
            }

            SetTrack(string.IsNullOrEmpty(title)
                ? null
                : new TrackInfo(title, artist, props.AlbumTitle, duration, s.SourceAppUserModelId, neteaseSongId));

            // Re-sync timeline/probe immediately after SetTrack so mid-song startup never stays at 00:00.
            SyncSessionState(s);
        }
        catch (Exception)
        {
            // Session went away mid-call.
        }
    }

    private void SetTrack(TrackInfo? track)
    {
        lock (_lock)
        {
            var previous = _track;
            if (previous is not null && track is not null && previous.Key == track.Key)
            {
                var updated = previous;
                if (track.Duration is not null && previous.Duration is null)
                    updated = updated with { Duration = track.Duration };
                if (track.NeteaseSongId is not null && previous.NeteaseSongId != track.NeteaseSongId)
                    updated = updated with { NeteaseSongId = track.NeteaseSongId };
                _track = updated;
                return;
            }
            if (previous is null && track is null) return;
            _track = track;
        }

        _clock.Reset(TimeSpan.Zero, track?.Duration);
        TrackChanged?.Invoke(track);
    }

    private void UpdatePlayback(Session s)
    {
        var info = s.GetPlaybackInfo();
        if (info is null) return;
        var playing = info.PlaybackStatus == PlaybackStatus.Playing;
        var rate = info.PlaybackRate ?? 1.0;
        var was = _clock.IsPlaying;
        if (was != playing)
        {
            _clock.SetPlaying(playing, rate);
            PlaybackStateChanged?.Invoke(playing);
        }
    }

    private void UpdateTimeline(Session s)
    {
        var tl = s.GetTimelineProperties();
        if (tl is null) return;
        var duration = tl.EndTime - tl.StartTime;
        // Apps like NetEase Cloud Music report all-zero timelines; don't let that overwrite probe positions.
        if (tl.Position == TimeSpan.Zero && tl.EndTime == TimeSpan.Zero) return;
        _clock.ReportTimeline(tl.Position - tl.StartTime, tl.LastUpdatedTime, duration > TimeSpan.Zero ? duration : null);

        lock (_lock)
        {
            if (_track is not null && _track.Duration is null && duration > TimeSpan.Zero)
                _track = _track with { Duration = duration };
        }
    }

    private void UpdatePlayerSpecificProbe(Session s)
    {
        if (!IsNeteaseSession(s)) return;
        if (_neteaseProbe.Poll() is not { } snap) return;

        _clock.SyncFromProbe(snap.Position, snap.Duration);

        lock (_lock)
        {
            if (_track is not null)
            {
                var updated = _track;
                if (snap.Duration is { TotalSeconds: > 0 } d && _track.Duration is null)
                    updated = updated with { Duration = d };
                if (snap.SongId is > 0 and { } id && _track.NeteaseSongId != id)
                    updated = updated with { NeteaseSongId = id };
                _track = updated;
            }
        }
    }

    private static bool IsNeteaseSession(Session s)
    {
        var appId = s.SourceAppUserModelId;
        return !string.IsNullOrEmpty(appId) &&
               appId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_pollTask is not null)
        {
            try { await _pollTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
