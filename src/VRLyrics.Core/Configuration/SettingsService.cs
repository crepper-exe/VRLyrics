using System.Text.Json;
using System.Text.Json.Serialization;
using VRLyrics.Core.Models;

namespace VRLyrics.Core.Configuration;

/// <summary>
/// Manages loading, saving, atomic persistence, and hot-reloading of <see cref="LyricsOverlaySettings"/>
/// from <c>%AppData%\VRLyrics\settings.json</c>.
/// </summary>
public sealed class SettingsService : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly string _settingsPath;
    private FileSystemWatcher? _watcher;
    private LyricsOverlaySettings _current;
    private DateTimeOffset _lastSelfWriteUtc = DateTimeOffset.MinValue;

    public SettingsService(string? settingsPath = null, bool enableFileWatcher = true)
    {
        _settingsPath = settingsPath ?? DefaultSettingsPath;
        _current = LoadFromDiskOrCreateDefault();

        if (enableFileWatcher)
            StartWatcher();
    }

    public static string DefaultSettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRLyrics");

    public static string DefaultSettingsPath =>
        Path.Combine(DefaultSettingsDirectory, "settings.json");

    public string SettingsPath => _settingsPath;

    public LyricsOverlaySettings Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Fired whenever <see cref="Current"/> changes (either via code or external file modification).</summary>
    public event Action<LyricsOverlaySettings>? SettingsChanged;

    /// <summary>Atomically updates <see cref="Current"/>, raises <see cref="SettingsChanged"/>, and persists to disk.</summary>
    public LyricsOverlaySettings Update(Func<LyricsOverlaySettings, LyricsOverlaySettings> mutator, bool persist = true)
    {
        LyricsOverlaySettings updated;
        lock (_lock)
        {
            updated = mutator(_current);
            if (Equals(_current, updated))
                return _current;
            _current = updated;
        }

        if (persist)
            SaveToDisk(updated);

        SettingsChanged?.Invoke(updated);
        return updated;
    }

    /// <summary>Replaces <see cref="Current"/> with <paramref name="settings"/> and persists to disk.</summary>
    public void Replace(LyricsOverlaySettings settings, bool persist = true) =>
        Update(_ => settings, persist);

    /// <summary>Resets all settings to factory defaults and persists to disk.</summary>
    public LyricsOverlaySettings ResetToDefaults(bool persist = true) =>
        Update(_ => new LyricsOverlaySettings(), persist);

    /// <summary>Forces a reload from disk if the file exists.</summary>
    public LyricsOverlaySettings ReloadFromDisk()
    {
        if (!File.Exists(_settingsPath))
            return Current;

        try
        {
            var json = ReadAllTextShared(_settingsPath);
            if (string.IsNullOrWhiteSpace(json))
                return Current;

            var loaded = JsonSerializer.Deserialize<LyricsOverlaySettings>(json, JsonOptions);
            if (loaded is null)
                return Current;

            lock (_lock)
            {
                _current = loaded;
            }

            SettingsChanged?.Invoke(loaded);
            return loaded;
        }
        catch
        {
            return Current;
        }
    }

    public void SaveToDisk(LyricsOverlaySettings? settings = null)
    {
        var toSave = settings ?? Current;
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(toSave, JsonOptions);
            lock (_lock)
            {
                _lastSelfWriteUtc = DateTimeOffset.UtcNow;
                using var fs = new FileStream(_settingsPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.Write(json);
            }
        }
        catch
        {
            // Ignore transient IO failures during rapid slider dragging.
        }
    }

    private LyricsOverlaySettings LoadFromDiskOrCreateDefault()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = ReadAllTextShared(_settingsPath);
                var parsed = JsonSerializer.Deserialize<LyricsOverlaySettings>(json, JsonOptions);
                if (parsed is not null)
                    return parsed;
            }
        }
        catch
        {
            // Fall back to defaults if JSON is malformed.
        }

        var defaults = new LyricsOverlaySettings();
        SaveToDisk(defaults);
        return defaults;
    }

    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private void StartWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            var file = Path.GetFileName(_settingsPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                return;

            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnWatchedFileChanged;
            _watcher.Created += OnWatchedFileChanged;
        }
        catch
        {
            // Watcher is optional enhancement.
        }
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow - _lastSelfWriteUtc < TimeSpan.FromMilliseconds(350))
                return;
        }

        // Small delay so the writer finishes flushing before we read.
        _ = Task.Run(async () =>
        {
            await Task.Delay(80).ConfigureAwait(false);
            ReloadFromDisk();
        });
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
