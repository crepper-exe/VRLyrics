using SkiaSharp;
using VRLyrics.Core.Models;
using VRLyrics.Overlay.Rendering;

namespace VRLyrics.Overlay.Dashboard;

/// <summary>Inputs required to render a frame of the SteamVR Dashboard control panel.</summary>
public sealed record DashboardRenderState
{
    public LyricsRenderFrame Frame { get; init; } = new();
    public bool IsDemoLoopEnabled { get; init; }
    public float? CursorX { get; init; }
    public float? CursorY { get; init; }
    public bool IsCursorPressed { get; init; }
    public DashboardAction? HoveredAction { get; init; }
}

/// <summary>
/// Renders the <c>1200 × 760</c> SteamVR Dashboard control panel and <c>128 × 128</c> thumbnail icon using SkiaSharp,
/// and provides button layout hit-testing for OpenVR laser-mouse events.
/// </summary>
public sealed class DashboardPanelRenderer : IDisposable
{
    public const int PanelWidth = 1200;
    public const int PanelHeight = 760;

    private static readonly string[] FallbackFonts =
        ["Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI"];

    private readonly SKBitmap _bitmap;
    private readonly SKCanvas _canvas;
    private readonly SKTypeface _typefaceBold;
    private readonly SKTypeface _typefaceRegular;
    private readonly List<DashboardButtonItem> _buttons = new(32);

    public DashboardPanelRenderer()
    {
        var info = new SKImageInfo(PanelWidth, PanelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        _bitmap = new SKBitmap(info);
        _canvas = new SKCanvas(_bitmap);
        _typefaceBold = ResolveTypeface(bold: true);
        _typefaceRegular = ResolveTypeface(bold: false);
    }

    public SKBitmap Bitmap => _bitmap;

    public IReadOnlyList<DashboardButtonItem> Buttons => _buttons;

    /// <summary>
    /// Converts OpenVR <c>VREvent_Mouse_t</c> coordinates (where 0,0 is bottom-left) into top-left SkiaSharp canvas coordinates.
    /// </summary>
    public static (float X, float Y) ConvertOpenVrMouseToCanvas(float openVrMouseX, float openVrMouseY) =>
        (Math.Clamp(openVrMouseX, 0f, PanelWidth), Math.Clamp(PanelHeight - openVrMouseY, 0f, PanelHeight));

    /// <summary>Finds the button containing canvas coordinates <c>(x, y)</c>, if any.</summary>
    public DashboardAction? HitTest(float canvasX, float canvasY, LyricsOverlaySettings? settings = null, bool isDemoLoop = false)
    {
        if (_buttons.Count == 0)
            BuildButtons(settings ?? new LyricsOverlaySettings(), isDemoLoop);

        for (var i = 0; i < _buttons.Count; i++)
        {
            if (_buttons[i].Bounds.Contains(canvasX, canvasY))
                return _buttons[i].Action;
        }
        return null;
    }

    public SKBitmap Render(DashboardRenderState state)
    {
        var s = state.Frame.Settings;
        BuildButtons(s, state.IsDemoLoopEnabled);

        _canvas.Clear(SKColors.Transparent);

        // 1. Main Panel Background
        var outerRect = new SKRect(8, 8, PanelWidth - 8, PanelHeight - 8);
        using (var bgShader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(PanelWidth, PanelHeight),
            [new SKColor(13, 19, 34, 248), new SKColor(9, 13, 24, 252)],
            null,
            SKShaderTileMode.Clamp))
        using (var bgPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = bgShader })
        using (var borderPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            Color = new SKColor(56, 189, 248, 120)
        })
        {
            _canvas.DrawRoundRect(outerRect, 20f, 20f, bgPaint);
            _canvas.DrawRoundRect(outerRect, 20f, 20f, borderPaint);
        }

        // 2. Top Header Card
        DrawHeader(_canvas, state);

        // 3. Section Cards & Titles
        DrawSectionCard(_canvas, new SKRect(24, 138, PanelWidth - 24, 346), "📍 显示位置模式与空间校准",
            $"当前宽度: {s.WidthInMeters:0.00}m  ·  不透明度: {s.Opacity * 100:0}%  ·  曲面: {s.Curvature * 100:0}%");

        DrawSectionCard(_canvas, new SKRect(24, 360, 590, 544), "⏱️ 歌词时间轴同步微调",
            $"当前偏移: {s.GlobalOffsetMs:+0;-0;0} ms ({s.GlobalOffsetMs / 1000.0:+0.00;-0.00;0.00}s)");

        var currentSource = state.Frame.Lyrics?.Source ?? "未加载";
        DrawSectionCard(_canvas, new SKRect(606, 360, PanelWidth - 24, 544), "🎵 歌词源偏好与重新搜索",
            $"偏好: {FormatProviderName(s.PreferredLyricsProvider)}  ·  当前命中: {currentSource}");

        DrawSectionCard(_canvas, new SKRect(24, 558, PanelWidth - 24, 738), "🎨 歌词内容开关与一键配色主题",
            $"主字号: {s.MainFontSize:0}px  ·  字体: {s.FontFamily}");

        // 4. Interactive Buttons
        var hovered = state.HoveredAction;
        if (hovered is null && state.CursorX is { } cx && state.CursorY is { } cy)
            hovered = HitTest(cx, cy, s, state.IsDemoLoopEnabled);

        foreach (var btn in _buttons)
        {
            var isHover = hovered == btn.Action;
            var isPressed = isHover && state.IsCursorPressed;
            DrawButton(_canvas, btn, isHover, isPressed);
        }

        // 5. Laser Pointer Cursor Dot
        if (state.CursorX is { } curX && state.CursorY is { } curY)
            DrawCursor(_canvas, curX, curY, state.IsCursorPressed);

        _canvas.Flush();
        return _bitmap;
    }

    /// <summary>Generates a <c>128 × 128</c> PNG thumbnail icon for the SteamVR Dashboard tab bar.</summary>
    public static byte[] RenderThumbnailPng()
    {
        using var bmp = new SKBitmap(new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        using var bgShader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(128, 128),
            [new SKColor(18, 30, 54), new SKColor(9, 14, 26)],
            null,
            SKShaderTileMode.Clamp);
        using var bgPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = bgShader };
        using var borderPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            Color = new SKColor(56, 189, 248)
        };

        var rect = new SKRect(6, 6, 122, 122);
        canvas.DrawRoundRect(rect, 28f, 28f, bgPaint);
        canvas.DrawRoundRect(rect, 28f, 28f, borderPaint);

        using var sungPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(124, 247, 255) };
        using var unsungPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(203, 213, 225, 200) };

        // Karaoke bars
        canvas.DrawRoundRect(new SKRect(24, 42, 76, 58), 6f, 6f, sungPaint);
        canvas.DrawRoundRect(new SKRect(80, 42, 104, 58), 6f, 6f, unsungPaint);
        canvas.DrawRoundRect(new SKRect(32, 72, 96, 84), 5f, 5f, unsungPaint);

        canvas.Flush();
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private void BuildButtons(LyricsOverlaySettings s, bool isDemoLoop)
    {
        _buttons.Clear();

        // --- Header right buttons ---
        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleEnabled,
            new SKRect(746, 34, 964, 110),
            s.Enabled ? "🟢 歌词显示：已开启" : "⚪ 歌词显示：已隐藏",
            s.Enabled ? "点击隐藏叠加层" : "点击恢复显示",
            IsSelected: s.Enabled,
            IsPrimary: true));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleDemoLoop,
            new SKRect(976, 34, 1160, 110),
            isDemoLoop ? "🎬 演示动画：开启" : "🎬 演示动画：关闭",
            "无歌/暂停时预览",
            IsSelected: isDemoLoop));

        // --- Section 1 Row 1: 4 Attachment Modes (Y: 184..254) ---
        var modeY1 = 184f;
        var modeY2 = 254f;
        var modeW = 270f;
        var modeGap = 12f;
        var modeStartX = 40f;

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SetModeHead,
            new SKRect(modeStartX + (modeW + modeGap) * 0, modeY1, modeStartX + modeW + (modeW + modeGap) * 0, modeY2),
            "👀 头部跟随",
            "锁定在视野下方",
            IsSelected: s.AttachmentMode == OverlayAttachmentMode.HeadLocked));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SetModeWorld,
            new SKRect(modeStartX + (modeW + modeGap) * 1, modeY1, modeStartX + modeW + (modeW + modeGap) * 1, modeY2),
            "🌍 空间固定",
            "悬浮于房间 / 可手柄拖拽",
            IsSelected: s.AttachmentMode == OverlayAttachmentMode.WorldLocked));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SetModeLeftHand,
            new SKRect(modeStartX + (modeW + modeGap) * 2, modeY1, modeStartX + modeW + (modeW + modeGap) * 2, modeY2),
            "⌚ 左手腕跟随",
            "抬左手查看歌词",
            IsSelected: s.AttachmentMode == OverlayAttachmentMode.LeftHand));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SetModeRightHand,
            new SKRect(modeStartX + (modeW + modeGap) * 3, modeY1, modeStartX + modeW + (modeW + modeGap) * 3, modeY2),
            "⌚ 右手腕跟随",
            "抬右手查看歌词",
            IsSelected: s.AttachmentMode == OverlayAttachmentMode.RightHand));

        // --- Section 1 Row 2: Spatial Quick Actions (Y: 268..332) ---
        var actY1 = 268f;
        var actY2 = 332f;
        _buttons.Add(new DashboardButtonItem(
            DashboardAction.RecenterWorldToHmd,
            new SKRect(40, actY1, 310, actY2),
            "📍 空间歌词移至当前面前",
            "重置到当前头显视线正前方",
            IsPrimary: true));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleWorldControllerGrab,
            new SKRect(322, actY1, 588, actY2),
            s.EnableWorldControllerGrab ? "✋ 手柄抓取拖动：已允许" : "🔒 手柄抓取拖动：已锁定",
            s.EnableWorldControllerGrab ? "按住侧键/扳机可拖动" : "防止激烈游戏中误触",
            IsSelected: s.EnableWorldControllerGrab));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.WidthMinus,
            new SKRect(600, actY1, 774, actY2),
            "➖ 宽度缩小",
            $"{s.WidthInMeters:0.00} m"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.WidthPlus,
            new SKRect(786, actY1, 960, actY2),
            "➕ 宽度放大",
            $"{s.WidthInMeters:0.00} m"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.CycleOpacity,
            new SKRect(972, actY1, 1160, actY2),
            $"🔆 不透明度 {s.Opacity * 100:0}%",
            "点击循环切换"));

        // --- Section 2 Left: Timeline Offset (X: 40..574, Y: 408..528) ---
        var offY1 = 412f;
        var offY2 = 524f;
        var offW = 99f;
        var offGap = 9f;
        var offStartX = 40f;

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetMinus500,
            new SKRect(offStartX + (offW + offGap) * 0, offY1, offStartX + offW + (offW + offGap) * 0, offY2),
            "-0.5s",
            "提前500ms"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetMinus100,
            new SKRect(offStartX + (offW + offGap) * 1, offY1, offStartX + offW + (offW + offGap) * 1, offY2),
            "-0.1s",
            "提前100ms"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetReset,
            new SKRect(offStartX + (offW + offGap) * 2, offY1, offStartX + offW + (offW + offGap) * 2, offY2),
            "重置 0s",
            $"{s.GlobalOffsetMs:+0;-0;0}ms",
            IsSelected: s.GlobalOffsetMs == 0));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetPlus100,
            new SKRect(offStartX + (offW + offGap) * 3, offY1, offStartX + offW + (offW + offGap) * 3, offY2),
            "+0.1s",
            "延后100ms"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetPlus500,
            new SKRect(offStartX + (offW + offGap) * 4, offY1, offStartX + offW + (offW + offGap) * 4, offY2),
            "+0.5s",
            "延后500ms"));

        // --- Section 2 Right: Lyrics Provider & Reload (X: 622..1160, Y: 412..524) ---
        var srcStartX = 622f;
        var srcW = 83f;
        var srcGap = 8f;
        var pref = s.PreferredLyricsProvider ?? "Auto";

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceAuto,
            new SKRect(srcStartX + (srcW + srcGap) * 0, offY1, srcStartX + srcW + (srcW + srcGap) * 0, offY2),
            "✨ 自动",
            "智能优选",
            IsSelected: string.Equals(pref, "Auto", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceNetease,
            new SKRect(srcStartX + (srcW + srcGap) * 1, offY1, srcStartX + srcW + (srcW + srcGap) * 1, offY2),
            "☁️ 网易云",
            "逐字+翻译",
            IsSelected: string.Equals(pref, "NetEase", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceQqMusic,
            new SKRect(srcStartX + (srcW + srcGap) * 2, offY1, srcStartX + srcW + (srcW + srcGap) * 2, offY2),
            "🎶 QQ音乐",
            "双语歌词",
            IsSelected: string.Equals(pref, "QQMusic", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceLrclib,
            new SKRect(srcStartX + (srcW + srcGap) * 3, offY1, srcStartX + srcW + (srcW + srcGap) * 3, offY2),
            "🌐 LRCLIB",
            "欧美曲库",
            IsSelected: string.Equals(pref, "LRCLIB", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceLocal,
            new SKRect(srcStartX + (srcW + srcGap) * 4, offY1, srcStartX + srcW + (srcW + srcGap) * 4, offY2),
            "📂 本地",
            "LRC目录",
            IsSelected: string.Equals(pref, "LocalLRC", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ReloadLyrics,
            new SKRect(srcStartX + (srcW + srcGap) * 5, offY1, srcStartX + srcW + (srcW + srcGap) * 5, offY2),
            "🔄 刷新",
            "重新抓取",
            IsPrimary: true));

        // --- Section 3 Row 1: Content & Typography Toggles (Y: 604..660) ---
        var cY1 = 604f;
        var cY2 = 660f;
        var cW = 214f;
        var cGap = 12f;
        var cStartX = 40f;

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleTranslation,
            new SKRect(cStartX + (cW + cGap) * 0, cY1, cStartX + cW + (cW + cGap) * 0, cY2),
            s.ShowTranslation ? "🌐 双语翻译：开启" : "🌐 双语翻译：关闭",
            IsSelected: s.ShowTranslation));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleContextLines,
            new SKRect(cStartX + (cW + cGap) * 1, cY1, cStartX + cW + (cW + cGap) * 1, cY2),
            s.ShowContextLines ? "📜 上下句预览：开启" : "📜 上下句预览：关闭",
            IsSelected: s.ShowContextLines));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleKaraoke,
            new SKRect(cStartX + (cW + cGap) * 2, cY1, cStartX + cW + (cW + cGap) * 2, cY2),
            s.EnableKaraokeHighlight ? "🎤 逐字高亮：开启" : "🎤 逐字高亮：关闭",
            IsSelected: s.EnableKaraokeHighlight));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.FontSizeMinus,
            new SKRect(cStartX + (cW + cGap) * 3, cY1, cStartX + cW + (cW + cGap) * 3, cY2),
            $"🔉 字号缩小 ({s.MainFontSize:0}px)"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.FontSizePlus,
            new SKRect(cStartX + (cW + cGap) * 4, cY1, cStartX + cW + (cW + cGap) * 4, cY2),
            $"🔊 字号放大 ({s.MainFontSize:0}px)"));

        // --- Section 3 Row 2: 5 Color Theme Presets (Y: 670..726) ---
        var tY1 = 670f;
        var tY2 = 726f;
        var presets = ColorThemePreset.All;
        for (var i = 0; i < Math.Min(5, presets.Count); i++)
        {
            var preset = presets[i];
            var action = (DashboardAction)((int)DashboardAction.ThemePreset0 + i);
            var topColor = ColorParseHelper.Parse(preset.SungTopColorHex, SKColors.Cyan);
            var botColor = ColorParseHelper.Parse(preset.SungBottomColorHex, SKColors.DeepSkyBlue);

            _buttons.Add(new DashboardButtonItem(
                action,
                new SKRect(cStartX + (cW + cGap) * i, tY1, cStartX + cW + (cW + cGap) * i, tY2),
                $"🎨 {preset.ShortName}",
                IsSelected: preset.Matches(s),
                SwatchTop: topColor,
                SwatchBottom: botColor));
        }
    }

    private void DrawHeader(SKCanvas canvas, DashboardRenderState state)
    {
        var headerRect = new SKRect(24, 22, PanelWidth - 24, 124);
        using var cardPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(19, 28, 48, 235) };
        using var borderPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(36, 52, 82) };
        canvas.DrawRoundRect(headerRect, 14f, 14f, cardPaint);
        canvas.DrawRoundRect(headerRect, 14f, 14f, borderPaint);

        using var titleFont = new SKFont(_typefaceBold, 26f);
        using var trackFont = new SKFont(_typefaceBold, 20f);
        using var subFont = new SKFont(_typefaceRegular, 15.5f);

        using var cyanPaint = new SKPaint { IsAntialias = true, Color = new SKColor(56, 189, 248) };
        using var whitePaint = new SKPaint { IsAntialias = true, Color = new SKColor(241, 245, 249) };
        using var mutedPaint = new SKPaint { IsAntialias = true, Color = new SKColor(148, 163, 184) };

        canvas.DrawText("VRLyrics", 44, 58, SKTextAlign.Left, titleFont, cyanPaint);
        canvas.DrawText("SteamVR 仪表盘控制中心", 164, 57, SKTextAlign.Left, subFont, mutedPaint);

        var f = state.Frame;
        var trackTitle = f.Track is { } t
            ? $"{ (f.IsPlaying ? "▶" : "⏸") }  {t.Title} - {t.Artist}   ({FormatTime(f.PlaybackPosition)} / {FormatTime(t.Duration)})"
            : "⏸  当前未检测到正在播放的曲目";

        canvas.DrawText(TruncateText(trackTitle, trackFont, 680f), 44, 88, SKTextAlign.Left, trackFont, whitePaint);

        var statusStr = !string.IsNullOrWhiteSpace(f.StatusText) ? f.StatusText! : "等待音乐播放…";
        canvas.DrawText(TruncateText(statusStr, subFont, 680f), 44, 112, SKTextAlign.Left, subFont, mutedPaint);
    }

    private void DrawSectionCard(SKCanvas canvas, SKRect rect, string title, string rightSummary)
    {
        using var bgPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(17, 25, 42, 225) };
        using var borderPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = new SKColor(34, 49, 77) };
        canvas.DrawRoundRect(rect, 14f, 14f, bgPaint);
        canvas.DrawRoundRect(rect, 14f, 14f, borderPaint);

        using var titleFont = new SKFont(_typefaceBold, 17.5f);
        using var sumFont = new SKFont(_typefaceRegular, 14.5f);
        using var titlePaint = new SKPaint { IsAntialias = true, Color = new SKColor(125, 211, 252) };
        using var sumPaint = new SKPaint { IsAntialias = true, Color = new SKColor(148, 163, 184) };

        canvas.DrawText(title, rect.Left + 18f, rect.Top + 29f, SKTextAlign.Left, titleFont, titlePaint);
        if (!string.IsNullOrWhiteSpace(rightSummary))
            canvas.DrawText(rightSummary, rect.Right - 18f, rect.Top + 28f, SKTextAlign.Right, sumFont, sumPaint);
    }

    private void DrawButton(SKCanvas canvas, DashboardButtonItem btn, bool isHover, bool isPressed)
    {
        var r = btn.Bounds;
        if (isPressed)
            r.Inflate(-2f, -2f);

        SKColor bgTop;
        SKColor bgBot;
        SKColor borderColor;
        float borderWidth;

        if (btn.IsSelected)
        {
            bgTop = isHover ? new SKColor(14, 116, 144) : new SKColor(12, 74, 110);
            bgBot = isHover ? new SKColor(3, 105, 161) : new SKColor(7, 89, 133);
            borderColor = isHover ? new SKColor(125, 211, 252) : new SKColor(56, 189, 248);
            borderWidth = isHover ? 2.6f : 2.0f;
        }
        else if (btn.IsPrimary)
        {
            bgTop = isHover ? new SKColor(30, 64, 110) : new SKColor(23, 47, 84);
            bgBot = isHover ? new SKColor(23, 50, 90) : new SKColor(17, 36, 66);
            borderColor = isHover ? new SKColor(125, 211, 252) : new SKColor(56, 189, 248, 180);
            borderWidth = isHover ? 2.4f : 1.6f;
        }
        else
        {
            bgTop = isHover ? new SKColor(38, 54, 82) : new SKColor(26, 37, 58);
            bgBot = isHover ? new SKColor(30, 43, 68) : new SKColor(20, 29, 46);
            borderColor = isHover ? new SKColor(96, 165, 250) : new SKColor(51, 65, 85);
            borderWidth = isHover ? 2.2f : 1.2f;
        }

        using (var shader = SKShader.CreateLinearGradient(
            new SKPoint(r.Left, r.Top),
            new SKPoint(r.Left, r.Bottom),
            [bgTop, bgBot],
            null,
            SKShaderTileMode.Clamp))
        using (var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = shader })
        using (var strokePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = borderWidth,
            Color = borderColor
        })
        {
            canvas.DrawRoundRect(r, 10f, 10f, fillPaint);
            canvas.DrawRoundRect(r, 10f, 10f, strokePaint);
        }

        // Optional color swatch circle for theme buttons
        var textCenterOffsetX = 0f;
        if (btn.SwatchTop is { } sTop && btn.SwatchBottom is { } sBot)
        {
            var swatchRadius = 10f;
            var cx = r.Left + 24f;
            var cy = r.MidY;
            using var swatchShader = SKShader.CreateLinearGradient(
                new SKPoint(cx, cy - swatchRadius),
                new SKPoint(cx, cy + swatchRadius),
                [sTop, sBot],
                null,
                SKShaderTileMode.Clamp);
            using var swatchFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = swatchShader };
            using var swatchBorder = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.White };
            canvas.DrawCircle(cx, cy, swatchRadius, swatchFill);
            canvas.DrawCircle(cx, cy, swatchRadius, swatchBorder);
            textCenterOffsetX = 10f;
        }

        using var labelFont = new SKFont(_typefaceBold, string.IsNullOrEmpty(btn.SubLabel) ? 16.5f : 16.0f);
        using var labelPaint = new SKPaint
        {
            IsAntialias = true,
            Color = btn.IsSelected ? new SKColor(224, 242, 254) : new SKColor(241, 245, 249)
        };

        if (string.IsNullOrWhiteSpace(btn.SubLabel))
        {
            canvas.DrawText(btn.Label, r.MidX + textCenterOffsetX, r.MidY + 6f, SKTextAlign.Center, labelFont, labelPaint);
        }
        else
        {
            using var subFont = new SKFont(_typefaceRegular, 12.5f);
            using var subPaint = new SKPaint
            {
                IsAntialias = true,
                Color = btn.IsSelected ? new SKColor(186, 230, 253) : new SKColor(148, 163, 184)
            };
            canvas.DrawText(btn.Label, r.MidX + textCenterOffsetX, r.MidY - 3f, SKTextAlign.Center, labelFont, labelPaint);
            canvas.DrawText(btn.SubLabel!, r.MidX + textCenterOffsetX, r.MidY + 16f, SKTextAlign.Center, subFont, subPaint);
        }
    }

    private static void DrawCursor(SKCanvas canvas, float x, float y, bool pressed)
    {
        var radius = pressed ? 7f : 9f;
        using var glowFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f);
        using var glowPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0, 210, 255, 200),
            MaskFilter = glowFilter
        };
        using var corePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = pressed ? new SKColor(56, 189, 248) : SKColors.White
        };
        using var ringPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.2f,
            Color = new SKColor(0, 210, 255)
        };

        canvas.DrawCircle(x, y, radius + 3f, glowPaint);
        canvas.DrawCircle(x, y, radius, corePaint);
        canvas.DrawCircle(x, y, radius, ringPaint);
    }

    private static string FormatProviderName(string? pref) => pref?.ToUpperInvariant() switch
    {
        "NETEASE" => "网易云音乐",
        "LRCLIB" => "LRCLIB",
        "LOCALLRC" => "本地 LRC",
        _ => "自动优选 (Auto)"
    };

    private static string FormatTime(TimeSpan? t) =>
        t is { } v ? $"{(int)v.TotalMinutes:00}:{v.Seconds:00}" : "--:--";

    private static string TruncateText(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
            return text;

        var span = text.AsSpan();
        while (span.Length > 4 && font.MeasureText(span) > maxWidth - 20f)
            span = span[..^1];
        return string.Concat(span, "…");
    }

    private static SKTypeface ResolveTypeface(bool bold)
    {
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        var fm = SKFontManager.Default;
        foreach (var family in FallbackFonts)
        {
            var tf = fm.MatchFamily(family, style);
            if (tf is not null) return tf;
        }
        return SKTypeface.Default;
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        _typefaceBold.Dispose();
        _typefaceRegular.Dispose();
    }
}
