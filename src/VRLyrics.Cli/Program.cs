using System.Globalization;
using System.Text;
using VRLyrics.Core.Configuration;
using VRLyrics.Core.Models;
using VRLyrics.Core.Parsing;
using VRLyrics.Core.Timing;
using VRLyrics.Media;
using VRLyrics.Overlay;
using VRLyrics.Overlay.Direct3D;
using VRLyrics.Overlay.Rendering;
using VRLyrics.Providers;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

using var settingsService = new SettingsService();
var service = new LyricsService(
    [
        new LocalLrcProvider(() => settingsService.Current.LocalLyricsFolders),
        new NeteaseLyricsProvider(),
        new LrclibLyricsProvider(),
    ],
    LyricsService.DefaultCacheDirectory);

if (args.Length >= 1 && args[0] == "search")
    return await SearchCommand(args[1..]);

if (args.Length >= 1 && args[0] == "status")
    return await StatusCommand();

if (args.Length >= 1 && args[0] == "render-preview")
    return await RenderPreviewCommand(args[1..]);

return await LiveCommand();

// ---------------------------------------------------------------------------------------------
// vrlyrics render-preview [outputPath.png] — renders a sample karaoke + bilingual frame & tests D3D11 upload
// ---------------------------------------------------------------------------------------------
async Task<int> RenderPreviewCommand(string[] a)
{
    var outPath = a.Length >= 1
        ? Path.GetFullPath(a[0])
        : Path.Combine(AppContext.BaseDirectory, "overlay_preview.png");

    var sampleYrc = """
        [10000,3200](10000,400,0)故(10400,400,0)事(10800,350,0)的(11150,600,0)小(11750,650,0)黄(12400,800,0)花
        [13200,3600](13200,450,0)从(13650,450,0)出(14100,450,0)生(14550,450,0)那(15000,500,0)年(15500,550,0)就(16050,750,0)飘(16400,400,0)着
        [16800,3500](16800,500,0)童(17300,500,0)年(17800,400,0)的(18200,600,0)荡(18800,700,0)秋(19500,800,0)千
        """;
    var sampleTrans = """
        [00:10.00]The little yellow flower of the story
        [00:13.20]Drifting ever since the year we were born
        [00:16.80]The childhood swing swaying through memory
        """;

    var lyrics = LyricsNormalizer.AttachTranslation(
        YrcParser.Parse(sampleYrc, "NetEase"),
        LrcParser.ParseLines(sampleTrans));

    var settings = new LyricsOverlaySettings();
    using var renderer = new LyricsRenderer();

    // Render mid-word on line 1 ("从出生那年就飘着" at 14.75s -> midway through "那")
    var frame = new LyricsRenderFrame
    {
        Track = new TrackInfo("晴天", "周杰伦", "叶惠美", TimeSpan.FromSeconds(269)),
        Lyrics = lyrics,
        PlaybackPosition = TimeSpan.FromMilliseconds(14750),
        IsPlaying = true,
        FrameTimestamp = TimeSpan.FromSeconds(1.0),
        Settings = settings,
    };

    var bitmap = renderer.RenderFrame(frame);
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    await File.WriteAllBytesAsync(outPath, renderer.EncodeToPng());

    using var d3d = new D3D11TextureBridge();
    var texPtr = d3d.UploadBitmap(bitmap);

    Console.WriteLine($"Preview rendered: {outPath} ({bitmap.Width}x{bitmap.Height})");
    Console.WriteLine($"D3D11 texture upload OK: handle=0x{texPtr:X}");
    await Task.CompletedTask;
    return 0;
}

// ---------------------------------------------------------------------------------------------
// vrlyrics status — print SMTC sessions and the followed track a few times (non-interactive)
// ---------------------------------------------------------------------------------------------
async Task<int> StatusCommand()
{
    await using var media = new MediaSessionService();
    await media.StartAsync();
    Console.WriteLine("Sessions: " + string.Join(", ", media.GetSessionAppIds()));
    for (var i = 0; i < 6; i++)
    {
        Console.WriteLine($"[{i * 0.5:0.0}s] app={media.CurrentAppId ?? "-"} track={media.CurrentTrack?.ToString() ?? "-"} " +
                          $"dur={Fmt(media.CurrentTrack?.Duration)} pos={Fmt(media.Position)} playing={media.IsPlaying}");
        await Task.Delay(500);
    }
    return 0;
}

// ---------------------------------------------------------------------------------------------
// vrlyrics search "<title>" "<artist>" [durationSeconds]  — test providers without a player
// ---------------------------------------------------------------------------------------------
async Task<int> SearchCommand(string[] a)
{
    if (a.Length < 1)
    {
        Console.WriteLine("usage: VRLyrics.Cli search \"<title>\" [\"<artist>\"] [durationSeconds]");
        return 1;
    }
    var track = new TrackInfo(a[0], a.Length > 1 ? a[1] : "", null,
        a.Length > 2 ? TimeSpan.FromSeconds(double.Parse(a[2], CultureInfo.InvariantCulture)) : null);

    var result = await service.GetLyricsAsync(track, bypassCache: true);
    foreach (var l in result.Log) Console.WriteLine($"  [{l}]");
    if (result.Lyrics is not { } lyr)
    {
        Console.WriteLine("No lyrics found.");
        return 2;
    }

    Console.WriteLine($"Source: {lyr.Source} #{lyr.SourceId}  matched: {lyr.MatchedArtist} - {lyr.MatchedTitle}");
    Console.WriteLine($"Lines: {lyr.Lines.Count}  word-timed: {lyr.HasWordTiming}  translated: {lyr.HasTranslation}  unsynced: {lyr.IsUnsynced}");
    Console.WriteLine();
    foreach (var line in lyr.Lines.Take(25))
    {
        Console.WriteLine($"[{line.Start:mm\\:ss\\.ff} - {line.End:mm\\:ss\\.ff}] {line.Text}{(line.HasWordTiming ? $"  ({line.Words.Count} words)" : "")}");
        if (line.Translation is not null) Console.WriteLine($"                      {line.Translation}");
    }
    if (lyr.Lines.Count > 25) Console.WriteLine($"... ({lyr.Lines.Count - 25} more)");
    return 0;
}

// ---------------------------------------------------------------------------------------------
// Live mode: follow the Windows media session, print synced lyrics, and drive the SteamVR overlay.
// ---------------------------------------------------------------------------------------------
async Task<int> LiveCommand()
{
    await using var media = new MediaSessionService();

    Lyrics? lyrics = null;
    string status = "waiting for a media session…";
    CancellationTokenSource? lookupCts = null;
    var sync = new object();

    async Task LoadAsync(TrackInfo? track, bool bypassCache)
    {
        CancellationTokenSource cts;
        lock (sync)
        {
            lookupCts?.Cancel();
            lookupCts = cts = new CancellationTokenSource();
            lyrics = null;
            status = track is null ? "nothing playing" : "searching…";
        }
        if (track is null) return;

        try
        {
            await Task.Delay(400, cts.Token);
            track = media.CurrentTrack ?? track;
            var result = await service.GetLyricsAsync(track, bypassCache, cts.Token);
            lock (sync)
            {
                if (cts.IsCancellationRequested) return;
                lyrics = result.Lyrics;
                status = string.Join(" | ", result.Log);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (sync) status = "error: " + ex.Message;
        }
    }

    media.TrackChanged += t => _ = LoadAsync(t, false);
    await media.StartAsync();
    if (media.CurrentTrack is { } initial) _ = LoadAsync(initial, false);

    await using var overlayController = new LyricsOverlayController(() =>
    {
        Lyrics? lyr;
        string st;
        lock (sync)
        {
            lyr = lyrics;
            st = status;
        }
        return new LyricsRenderFrame
        {
            Track = media.CurrentTrack,
            Lyrics = lyr,
            PlaybackPosition = media.Position,
            IsPlaying = media.IsPlaying,
            StatusText = st,
            Settings = settingsService.Current,
        };
    });

    overlayController.Start();

    Console.Write("\x1b[?25l\x1b[2J");
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync())
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.Q or ConsoleKey.Escape:
                        return 0;
                    case ConsoleKey.OemPlus or ConsoleKey.Add:
                        settingsService.Update(s => s with { GlobalOffsetMs = s.GlobalOffsetMs + 100 });
                        break;
                    case ConsoleKey.OemMinus or ConsoleKey.Subtract:
                        settingsService.Update(s => s with { GlobalOffsetMs = s.GlobalOffsetMs - 100 });
                        break;
                    case ConsoleKey.D0:
                        settingsService.Update(s => s with { GlobalOffsetMs = 0 });
                        break;
                    case ConsoleKey.M:
                        settingsService.Update(s => s with { AttachmentMode = (OverlayAttachmentMode)(((int)s.AttachmentMode + 1) % 4) });
                        break;
                    case ConsoleKey.T:
                        settingsService.Update(s => s with { ShowTranslation = !s.ShowTranslation });
                        break;
                    case ConsoleKey.K:
                        settingsService.Update(s => s with { EnableKaraokeHighlight = !s.EnableKaraokeHighlight });
                        break;
                    case ConsoleKey.C:
                        settingsService.Update(s => s with { ShowContextLines = !s.ShowContextLines });
                        break;
                    case ConsoleKey.R:
                        if (media.CurrentTrack is { } t) { service.Invalidate(t); _ = LoadAsync(t, true); }
                        break;
                }
            }

            Lyrics? lyr;
            string st;
            lock (sync) { lyr = lyrics; st = status; }
            RenderConsole(media, lyr, st, settingsService.Current, overlayController.VrStatusMessage);
        }
    }
    finally
    {
        Console.Write("\x1b[0m\x1b[?25h\n");
    }
    return 0;
}

static void RenderConsole(MediaSessionService media, Lyrics? lyrics, string status, LyricsOverlaySettings settings, string vrStatus)
{
    const string Reset = "\x1b[0m", Dim = "\x1b[90m", Sung = "\x1b[1;96m", Unsung = "\x1b[97m", Trans = "\x1b[36m", Head = "\x1b[1;33m";
    var width = Math.Max(20, Console.WindowWidth - 1);
    var sb = new StringBuilder("\x1b[H");
    void Line(string s = "") => sb.Append(s).Append(Reset).Append("\x1b[K\n");

    var track = media.CurrentTrack;
    var pos = media.Position;
    var offset = TimeSpan.FromMilliseconds(settings.GlobalOffsetMs);
    var lyricPos = pos + offset;

    Line($"{Head}VRLyrics (M2+M3){Reset}  {Dim}[+/-]偏移 [0]重置 [m]位置({settings.AttachmentMode}) [k]卡拉OK [t]翻译 [c]上下文 [r]重载 [q]退出");
    Line($"{Dim}vr:{Reset} {vrStatus}  {Dim}| app:{Reset} {media.CurrentAppId ?? "-"}");
    Line($"{Dim}track:{Reset} {Truncate(track?.ToString() ?? "-", width - 8)}");
    Line($"{Dim}time:{Reset} {Fmt(pos)} / {Fmt(track?.Duration)}  {(media.IsPlaying ? "▶ playing" : "⏸ paused")}  {Dim}offset:{Reset} {offset.TotalSeconds:+0.0;-0.0;0.0}s");
    Line($"{Dim}lyrics:{Reset} {(lyrics is null ? "-" : $"{lyrics.Source} #{lyrics.SourceId} {lyrics.MatchedArtist} - {lyrics.MatchedTitle}")}");
    Line($"{Dim}{Truncate(status, width)}");
    Line(new string('─', Math.Min(width, 80)));

    if (lyrics is null || lyrics.IsEmpty)
    {
        Line(); Line($"{Dim}(no lyrics)");
    }
    else if (lyrics.IsUnsynced)
    {
        Line($"{Dim}(unsynced lyrics)");
        foreach (var l in lyrics.Lines.Take(8)) Line(Truncate(l.Text, width));
    }
    else
    {
        var state = LyricTimeline.GetState(lyrics, lyricPos);
        var idx = state.LineIndex;

        for (var i = idx - 2; i <= idx + 3; i++)
        {
            if (i < 0 || i >= lyrics.Lines.Count) { Line(); continue; }
            var l = lyrics.Lines[i];
            var text = Truncate(l.Text, width - 2);
            if (i == idx)
            {
                var sungCount = (int)Math.Clamp(Math.Floor(state.SungCharacters), 0, text.Length);
                var marker = state.IsLineActive ? "▶ " : "  ";
                Line($"{marker}{Sung}{text[..sungCount]}{Reset}{Unsung}{text[sungCount..]}");
                Line($"  {Trans}{Truncate(settings.ShowTranslation ? (l.Translation ?? "") : "", width - 2)}");
            }
            else if (settings.ShowContextLines)
            {
                Line($"  {Dim}{text}");
            }
            else
            {
                Line();
            }
        }
        var cur = idx >= 0 ? lyrics.Lines[idx] : null;
        Line();
        Line($"{Dim}line {idx + 1}/{lyrics.Lines.Count}  progress {state.LineProgress:P0}  word {state.WordIndex}{(cur?.HasWordTiming == true ? $"/{cur.Words.Count}" : " (line-timed)")}");
    }

    sb.Append("\x1b[J");
    Console.Write(sb.ToString());
}

static string Fmt(TimeSpan? t) => t is { } v ? $"{(int)v.TotalMinutes:00}:{v.Seconds:00}.{v.Milliseconds / 100}" : "--:--";

static string Truncate(string s, int maxWidth)
{
    var w = 0;
    for (var i = 0; i < s.Length; i++)
    {
        w += s[i] >= 0x2E80 ? 2 : 1;
        if (w > maxWidth) return s[..Math.Max(0, i - 1)] + "…";
    }
    return s;
}
