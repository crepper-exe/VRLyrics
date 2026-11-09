using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VRLyrics.Media;

/// <summary>Snapshot of NetEase Cloud Music's internal playback state reconstructed from <c>cloudmusic.elog</c>.</summary>
public readonly record struct NeteasePlaybackSnapshot(
    long? SongId,
    TimeSpan Position,
    TimeSpan? Duration,
    bool IsPlaying,
    uint LastEventTickMs);

/// <summary>
/// NetEase Cloud Music 3.x (<c>cloudmusic.exe</c>) does not report <c>TimelineProperties</c> to Windows SMTC
/// (it always reports Position=00:00, EndTime=00:00). However, it writes millisecond-accurate playback,
/// pause, track-load, and seek events to <c>%LocalAppData%\NetEase\CloudMusic\cloudmusic.elog</c> using a
/// lightweight byte-nibble cipher and the system <c>Environment.TickCount</c> clock.
///
/// This probe tails <c>cloudmusic.elog</c> (initial tail replay + incremental reads) so VRLyrics can determine
/// the exact song progress even when launched mid-song or when the user drags the progress bar.
/// </summary>
public sealed partial class NeteaseCloudMusicLogProbe
{
    private const int InitialTailBytes = 1024 * 1024; // 1 MB covers ~30-60 minutes of playback history
    private static readonly byte[] DecodeTable = CreateDecodeTable();

    private readonly object _sync = new();
    private readonly string _logPath;
    private readonly Dictionary<long, TimeSpan> _knownDurations = new();

    private long _lastFileLength = -1;
    private long _readOffset;
    private string _carryOver = "";

    private int _activePid = -1;
    private long? _songId;
    private double _accumulatedPositionMs;
    private uint _lastPlayStartTickMs;
    private uint _lastEventTickMs;
    private bool _isPlaying;
    private bool _isNativePlaying;
    private bool _hasSeenNativeAudioEvents;
    private bool _hasState;

    public NeteaseCloudMusicLogProbe(string? logPath = null)
    {
        _logPath = logPath ?? DefaultLogPath;
    }

    public static string DefaultLogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase",
            "CloudMusic",
            "cloudmusic.elog");

    [GeneratedRegex(@"^\[(\d+):\d+:\d+/\d+:(\d+):")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"(?:OnLoad|DoLoad|Load):(\d+)_")]
    private static partial Regex LoadTrackRegex();

    [GeneratedRegex(@"OnEnd:(\d+)_")]
    private static partial Regex EndTrackRegex();

    [GeneratedRegex(@"(?:DoSeek pos:|OnSeek pos:|""setPlayingPosition"",\s*|""dragEnd,\s*setPosition::"",\s*)([0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex SeekRegex();

    [GeneratedRegex(@"DoQuerySongCacheInfo\s+songId(\d+).*\\?""time\\?"":(\d+)")]
    private static partial Regex CacheDurationRegex();

    [GeneratedRegex(@"""id"":""(\d+)""[^{}]*""duration"":(\d+)")]
    private static partial Regex TrackJsonDurationRegex();

    [GeneratedRegex(@"""id"":""(\d+)"".*""resource_time"":(\d+)")]
    private static partial Regex PlvDurationRegex();

    /// <summary>Decodes NetEase Cloud Music's <c>.elog</c> / <c>localdata</c> nibble cipher in-place.</summary>
    public static void DecodeInPlace(Span<byte> buffer)
    {
        var table = DecodeTable;
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = table[buffer[i]];
    }

    /// <summary>Encodes plain UTF-8 bytes with NetEase Cloud Music's nibble cipher (useful for tests).</summary>
    public static void EncodeInPlace(Span<byte> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            var x = buffer[i];
            var h = (x >> 4) & 0x0F;
            var l = x & 0x0F;
            var z = (l << 4) | (h ^ l);
            buffer[i] = (byte)(z ^ 0x3B);
        }
    }

    /// <summary>Polls <c>cloudmusic.elog</c> for any new events and returns the current extrapolated playback snapshot.</summary>
    public NeteasePlaybackSnapshot? Poll(uint? currentTickMs = null)
    {
        lock (_sync)
        {
            if (!File.Exists(_logPath))
                return null;

            try
            {
                using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var len = fs.Length;
                if (len == 0) return null;

                // Detect log rotation / restart or first read:
                if (_lastFileLength < 0 || len < _readOffset)
                {
                    ResetState();
                    _readOffset = Math.Max(0, len - InitialTailBytes);
                    fs.Seek(_readOffset, SeekOrigin.Begin);
                    var initialBytes = new byte[len - _readOffset];
                    var read = fs.Read(initialBytes, 0, initialBytes.Length);
                    DecodeInPlace(initialBytes.AsSpan(0, read));
                    var text = Encoding.UTF8.GetString(initialBytes, 0, read);

                    // If we started mid-line, skip the partial first line.
                    if (_readOffset > 0)
                    {
                        var firstNewline = text.IndexOf('\n');
                        if (firstNewline >= 0) text = text[(firstNewline + 1)..];
                    }

                    // Lock onto the latest PID present in the tail so old sessions before an app restart are ignored.
                    var latestPid = FindLatestPid(text);
                    if (latestPid > 0) _activePid = latestPid;

                    FeedText(text);
                    _readOffset = len;
                    _lastFileLength = len;
                }
                else if (len > _readOffset)
                {
                    var toRead = (int)Math.Min(len - _readOffset, InitialTailBytes);
                    fs.Seek(_readOffset, SeekOrigin.Begin);
                    var buf = new byte[toRead];
                    var read = fs.Read(buf, 0, toRead);
                    DecodeInPlace(buf.AsSpan(0, read));
                    FeedText(Encoding.UTF8.GetString(buf, 0, read));
                    _readOffset += read;
                    _lastFileLength = len;
                }
            }
            catch (IOException)
            {
                // Best-effort if the file is momentarily inaccessible.
            }

            return GetSnapshot(currentTickMs ?? (uint)Environment.TickCount);
        }
    }

    /// <summary>Feeds decoded log text into the state machine (also usable directly in unit tests).</summary>
    public void FeedDecodedLog(string decodedText, int? targetPid = null)
    {
        lock (_sync)
        {
            if (targetPid is { } p) _activePid = p;
            else if (_activePid < 0)
            {
                var latest = FindLatestPid(decodedText);
                if (latest > 0) _activePid = latest;
            }
            FeedText(decodedText);
        }
    }

    public NeteasePlaybackSnapshot? GetSnapshot(uint currentTickMs)
    {
        lock (_sync)
        {
            if (!_hasState) return null;

            var posMs = _accumulatedPositionMs;
            if (_isPlaying)
            {
                var delta = unchecked(currentTickMs - _lastPlayStartTickMs);
                if (delta <= 7200_000) // < 2 hours sanity bound
                    posMs += delta;
            }

            var duration = _songId is { } id && _knownDurations.TryGetValue(id, out var d) ? d : (TimeSpan?)null;
            if (duration is { TotalMilliseconds: > 0 } dur && posMs > dur.TotalMilliseconds)
                posMs = dur.TotalMilliseconds;

            return new NeteasePlaybackSnapshot(
                _songId,
                TimeSpan.FromMilliseconds(Math.Max(0, posMs)),
                duration,
                _isPlaying,
                _lastEventTickMs);
        }
    }

    private void ResetState()
    {
        _carryOver = "";
        _knownDurations.Clear();
        _activePid = -1;
        _songId = null;
        _accumulatedPositionMs = 0;
        _lastPlayStartTickMs = 0;
        _lastEventTickMs = 0;
        _isPlaying = false;
        _isNativePlaying = false;
        _hasSeenNativeAudioEvents = false;
        _hasState = false;
    }

    private static int FindLatestPid(string text)
    {
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var m = HeaderRegex().Match(lines[i]);
            if (m.Success && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var pid))
                return pid;
        }
        return -1;
    }

    private void FeedText(string chunk)
    {
        var combined = string.IsNullOrEmpty(_carryOver) ? chunk : _carryOver + chunk;
        var lastNewline = combined.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            _carryOver = combined;
            return;
        }

        _carryOver = combined[(lastNewline + 1)..];
        var lines = combined[..lastNewline].Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0) ProcessLine(line);
        }
    }

    private void ProcessLine(string line)
    {
        var hm = HeaderRegex().Match(line);
        if (!hm.Success) return;

        var pid = int.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture);
        if (_activePid > 0 && pid != _activePid) return;

        var tickMs = uint.Parse(hm.Groups[2].Value, CultureInfo.InvariantCulture);

        // 1) Track Load (new song starts at 00:00)
        var loadMatch = LoadTrackRegex().Match(line);
        if (loadMatch.Success && long.TryParse(loadMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var loadedSongId))
        {
            if (_songId != loadedSongId || line.Contains("OnLoad:", StringComparison.Ordinal))
            {
                _songId = loadedSongId;
                _accumulatedPositionMs = 0;
                _lastPlayStartTickMs = tickMs;
                _isPlaying = false;
                _isNativePlaying = false;
                _hasState = true;
                _lastEventTickMs = tickMs;
            }
            return;
        }

        // 2) Track End (e.g. before single-track loop restarts)
        var endMatch = EndTrackRegex().Match(line);
        if (endMatch.Success && long.TryParse(endMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var endedSongId))
        {
            if (_songId == endedSongId)
            {
                _accumulatedPositionMs = 0;
                _lastPlayStartTickMs = tickMs;
                _isPlaying = false;
                _isNativePlaying = false;
                _lastEventTickMs = tickMs;
            }
            return;
        }

        // 3) Track Duration metadata (can arrive during preload or track start)
        if (line.Contains("DoQuerySongCacheInfo", StringComparison.Ordinal))
        {
            var dm = CacheDurationRegex().Match(line);
            if (dm.Success &&
                long.TryParse(dm.Groups[1].Value, CultureInfo.InvariantCulture, out var durSongId) &&
                long.TryParse(dm.Groups[2].Value, CultureInfo.InvariantCulture, out var durMs) &&
                durMs is > 1000 and < 7200_000)
            {
                _knownDurations[durSongId] = TimeSpan.FromMilliseconds(durMs);
            }
            return;
        }

        if (line.Contains("\"duration\":", StringComparison.Ordinal))
        {
            var tm = TrackJsonDurationRegex().Match(line);
            if (tm.Success &&
                long.TryParse(tm.Groups[1].Value, CultureInfo.InvariantCulture, out var jsonSongId) &&
                long.TryParse(tm.Groups[2].Value, CultureInfo.InvariantCulture, out var durMs) &&
                durMs is > 1000 and < 7200_000)
            {
                _knownDurations[jsonSongId] = TimeSpan.FromMilliseconds(durMs);
            }
        }

        if (line.Contains("\"resource_time\":", StringComparison.Ordinal))
        {
            var pm = PlvDurationRegex().Match(line);
            if (pm.Success &&
                long.TryParse(pm.Groups[1].Value, CultureInfo.InvariantCulture, out var plvSongId) &&
                long.TryParse(pm.Groups[2].Value, CultureInfo.InvariantCulture, out var durSec) &&
                durSec is > 1 and < 7200 &&
                !_knownDurations.ContainsKey(plvSongId))
            {
                _knownDurations[plvSongId] = TimeSpan.FromSeconds(durSec);
            }
        }

        // 4) Seek / Progress Bar Drag
        var seekMatch = SeekRegex().Match(line);
        if (seekMatch.Success &&
            double.TryParse(seekMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawPos))
        {
            var currentDur = _songId is { } id && _knownDurations.TryGetValue(id, out var d) ? d : (TimeSpan?)null;
            var maxSec = currentDur?.TotalSeconds ?? 3600.0;
            var posMs = rawPos > maxSec + 5.0 ? rawPos : rawPos * 1000.0;
            _accumulatedPositionMs = Math.Max(0, posMs);
            _lastPlayStartTickMs = tickMs;
            _hasState = true;
            _lastEventTickMs = tickMs;
            return;
        }

        // 5) Native Audio Engine Play / Pause (authoritative audio buffer clock).
        // NetEase Cloud Music logs UI/SMTC pause ("[SMTCWrapper] SMTC status=1" / "【playing】,\"pause\"") immediately
        // when the user clicks Pause, but performs a ~610 ms volume fade-out before actually pausing the native
        // DirectSound buffer ("[INFO:audio_player.cpp(198)] Pause ..."). Stopping our clock on the early UI/SMTC pause
        // event would discard ~610 ms of real audio playback on every pause/resume cycle.
        if (IsNativePlayEvent(line))
        {
            _hasSeenNativeAudioEvents = true;
            if (!_isPlaying || !_isNativePlaying)
            {
                _isPlaying = true;
                _isNativePlaying = true;
                _lastPlayStartTickMs = tickMs;
            }
            _hasState = true;
            _lastEventTickMs = tickMs;
            return;
        }

        if (IsNativePauseEvent(line))
        {
            _hasSeenNativeAudioEvents = true;
            if (_isPlaying)
            {
                var elapsed = unchecked(tickMs - _lastPlayStartTickMs);
                if (elapsed < 3600_000)
                    _accumulatedPositionMs += elapsed;
                _isPlaying = false;
                _isNativePlaying = false;
                _lastPlayStartTickMs = tickMs;
            }
            _hasState = true;
            _lastEventTickMs = tickMs;
            return;
        }

        // 6) Fallback to SMTC wrapper status only if the log stream contains no native audio_player.cpp events.
        if (!_hasSeenNativeAudioEvents)
        {
            if (line.Contains("[SMTCWrapper] SMTC status=0", StringComparison.Ordinal))
            {
                if (!_isPlaying)
                {
                    _isPlaying = true;
                    _lastPlayStartTickMs = tickMs;
                }
                _hasState = true;
                _lastEventTickMs = tickMs;
                return;
            }

            if (line.Contains("[SMTCWrapper] SMTC status=1", StringComparison.Ordinal))
            {
                if (_isPlaying)
                {
                    var elapsed = unchecked(tickMs - _lastPlayStartTickMs);
                    if (elapsed < 3600_000)
                        _accumulatedPositionMs += elapsed;
                    _isPlaying = false;
                    _lastPlayStartTickMs = tickMs;
                }
                _hasState = true;
                _lastEventTickMs = tickMs;
            }
        }
    }

    private static bool IsNativePlayEvent(string line) =>
        line.Contains("audio_player.cpp", StringComparison.Ordinal) &&
        line.Contains("] Play ", StringComparison.Ordinal);

    private static bool IsNativePauseEvent(string line) =>
        line.Contains("audio_player.cpp", StringComparison.Ordinal) &&
        line.Contains("] Pause ", StringComparison.Ordinal);

    private static byte[] CreateDecodeTable()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var z = i ^ 0x3B;
            var low = (z >> 4) & 0x0F;
            var high = (z & 0x0F) ^ low;
            table[i] = (byte)((high << 4) | low);
        }
        return table;
    }
}
