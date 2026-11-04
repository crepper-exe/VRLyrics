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
/// Uses the same low-saturation charcoal/slate palette, spacious rhythm, and Google Sans + CJK mixed typography as the desktop control hub.
/// </summary>
public sealed class DashboardPanelRenderer : IDisposable
{
    public const int PanelWidth = 1200;
    public const int PanelHeight = 760;

    private const string SteamLogoSvgPath =
        "M496 256c0 137-111.2 248-248.4 248-113.8 0-209.6-76.3-239-180.4l95.2 39.3c6.4 32.1 34.9 56.4 68.9 56.4 39.2 0 71.9-32.4 70.2-73.5l84.5-60.2c52.1 1.3 95.8-40.9 95.8-93.5 0-51.6-42-93.5-93.7-93.5s-93.7 42-93.7 93.5v1.2L176.6 279c-15.5-.9-30.7 3.4-43.5 12.1L0 236.1C10.2 108.4 117.1 8 247.6 8 384.8 8 496 119 496 256zM155.7 384.3l-30.5-12.6a52.79 52.79 0 0 0 27.2 25.8c26.9 11.2 57.8-1.6 69-28.4 5.4-13 5.5-27.3.1-40.3-5.4-13-15.5-23.2-28.5-28.6-12.9-5.4-26.7-5.2-38.9-.6l31.5 13c19.8 8.2 29.2 30.9 20.9 50.7-8.3 19.9-31 29.2-50.8 21zm173.8-129.9c-34.4 0-62.4-28-62.4-62.3s28-62.3 62.4-62.3 62.4 28 62.4 62.3-27.9 62.3-62.4 62.3zm.1-15.6c25.9 0 46.9-21 46.9-46.8 0-25.9-21-46.8-46.9-46.8s-46.9 21-46.9 46.8c.1 25.8 21.1 46.8 46.9 46.8z";

    private static readonly string[] FallbackFonts =
        ["Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI"];

    // Low-saturation theme palette matching Themes/Theme.xaml
    private static readonly SKColor ColorBg = new(20, 22, 25, 250);
    private static readonly SKColor ColorCard = new(28, 31, 36, 255);
    private static readonly SKColor ColorInset = new(21, 23, 27, 255);
    private static readonly SKColor ColorBorder = new(41, 45, 52, 255);
    private static readonly SKColor ColorBorderStrong = new(56, 61, 70, 255);

    private static readonly SKColor ColorText1 = new(228, 230, 234, 255);
    private static readonly SKColor ColorText2 = new(166, 172, 182, 255);
    private static readonly SKColor ColorText3 = new(135, 142, 153, 255);

    private static readonly SKColor ColorAccent = new(142, 163, 191, 255);
    private static readonly SKColor ColorAccentHover = new(157, 177, 203, 255);
    private static readonly SKColor ColorAccentSoft = new(44, 51, 61, 255);
    private static readonly SKColor ColorAccentSoftHover = new(52, 60, 72, 255);
    private static readonly SKColor ColorAccentSoftBorder = new(78, 91, 110, 255);
    private static readonly SKColor ColorOnAccent = new(19, 22, 26, 255);

    private readonly SKBitmap _bitmap;
    private readonly SKCanvas _canvas;
    private readonly SKTypeface _cjkBold;
    private readonly SKTypeface _cjkRegular;
    private readonly SKTypeface _latinBold;
    private readonly SKTypeface _latinMedium;
    private readonly List<DashboardButtonItem> _buttons = new(32);

    public DashboardPanelRenderer()
    {
        var info = new SKImageInfo(PanelWidth, PanelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        _bitmap = new SKBitmap(info);
        _canvas = new SKCanvas(_bitmap);
        _cjkBold = ResolveCjkTypeface(bold: true);
        _cjkRegular = ResolveCjkTypeface(bold: false);
        _latinBold = LoadEmbeddedTypeface("VRLyrics.Overlay.GoogleSans-Bold.ttf") ?? _cjkBold;
        _latinMedium = LoadEmbeddedTypeface("VRLyrics.Overlay.GoogleSans-Medium.ttf") ?? _cjkRegular;
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

        // 1. Main Low-Saturation Panel Surface
        var outerRect = new SKRect(8, 8, PanelWidth - 8, PanelHeight - 8);
        using (var bgPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ColorBg })
        using (var borderPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = ColorBorderStrong
        })
        {
            _canvas.DrawRoundRect(outerRect, 22f, 22f, bgPaint);
            _canvas.DrawRoundRect(outerRect, 22f, 22f, borderPaint);
        }

        // 2. Top Header Card
        DrawHeader(_canvas, state);

        // 3. Section Cards & Titles (Spacious layout with 16px gaps)
        DrawSectionCard(_canvas, new SKRect(28, 134, PanelWidth - 28, 336), "显示与空间锚定",
            $"物理宽度 {s.WidthInMeters:0.00} m   ·   不透明度 {s.Opacity * 100:0}%   ·   曲面 {s.Curvature * 100:0}%");

        // Inset segmented track container for the 4 attachment modes
        var modeTrackRect = new SKRect(44, 180, PanelWidth - 44, 246);
        using (var trackFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ColorInset })
        using (var trackStroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = ColorBorder })
        {
            _canvas.DrawRoundRect(modeTrackRect, 11f, 11f, trackFill);
            _canvas.DrawRoundRect(modeTrackRect, 11f, 11f, trackStroke);
        }

        DrawSectionCard(_canvas, new SKRect(28, 352, 592, 532), "时间轴偏移微调",
            $"全局偏移 {s.GlobalOffsetMs:+0;-0;0} ms ({s.GlobalOffsetMs / 1000.0:+0.00;-0.00;0.00}s)");

        var currentSource = state.Frame.Lyrics?.Source ?? "未加载";
        DrawSectionCard(_canvas, new SKRect(608, 352, PanelWidth - 28, 532), "歌词源优先级",
            $"偏好 {FormatProviderName(s.PreferredLyricsProvider)}   ·   当前 {currentSource}");

        DrawSectionCard(_canvas, new SKRect(28, 548, PanelWidth - 28, 736), "排版显示与配色方案",
            $"主字号 {s.MainFontSize:0} px   ·   字体 {s.FontFamily}");

        // 4. Interactive Buttons (Native SteamVR laser pointer provides cursor; no duplicate drawn cursor)
        var hovered = state.HoveredAction;
        if (hovered is null && state.CursorX is { } cx && state.CursorY is { } cy)
            hovered = HitTest(cx, cy, s, state.IsDemoLoopEnabled);

        foreach (var btn in _buttons)
        {
            var isHover = hovered == btn.Action;
            var isPressed = isHover && state.IsCursorPressed;
            DrawButton(_canvas, btn, isHover, isPressed);
        }

        _canvas.Flush();
        return _bitmap;
    }

    /// <summary>Generates a <c>128 × 128</c> PNG thumbnail icon for the SteamVR Dashboard tab bar.</summary>
    public static byte[] RenderThumbnailPng()
    {
        using var bmp = new SKBitmap(new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        var drewLogo = false;
        try
        {
            using var stream = typeof(DashboardPanelRenderer).Assembly.GetManifestResourceStream("VRLyrics.Overlay.logo.png");
            if (stream is not null)
            {
                using var logoBmp = SKBitmap.Decode(stream);
                if (logoBmp is not null)
                {
                    var srcRect = new SKRect(4f, 74f, 1019f, 502f);
                    const float pad = 3f;
                    var avail = 128f - pad * 2f;
                    var scale = Math.Min(avail / srcRect.Width, avail / srcRect.Height);
                    var drawW = srcRect.Width * scale;
                    var drawH = srcRect.Height * scale;
                    var drawX = (128f - drawW) * 0.5f;
                    var drawY = (128f - drawH) * 0.5f;
                    var dstRect = new SKRect(drawX, drawY, drawX + drawW, drawY + drawH);

                    using var imgFromLogo = SKImage.FromBitmap(logoBmp);
                    using var logoPaint = new SKPaint { IsAntialias = true };
                    canvas.DrawImage(imgFromLogo, srcRect, dstRect, new SKSamplingOptions(SKCubicResampler.Mitchell), logoPaint);
                    drewLogo = true;
                }
            }
        }
        catch { }

        if (!drewLogo)
        {
            using var sungPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ColorAccent };
            using var unsungPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ColorText1 };

            canvas.DrawRoundRect(new SKRect(24, 42, 76, 58), 6f, 6f, sungPaint);
            canvas.DrawRoundRect(new SKRect(80, 42, 104, 58), 6f, 6f, unsungPaint);
            canvas.DrawRoundRect(new SKRect(32, 72, 96, 84), 5f, 5f, unsungPaint);
        }

        canvas.Flush();
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private void BuildButtons(LyricsOverlaySettings s, bool isDemoLoop)
    {
        _buttons.Clear();

        // --- Header right toggle switches (Y: 36..106) ---
        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleEnabled,
            new SKRect(740, 36, 946, 106),
            "显示 VR 歌词",
            s.Enabled ? "已开启" : "已隐藏",
            IsSelected: s.Enabled,
            IsPrimary: true));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleDemoLoop,
            new SKRect(960, 36, 1154, 106),
            "样式演示动画",
            isDemoLoop ? "已开启" : "已关闭",
            IsSelected: isDemoLoop));

        // --- Section 1 Row 1: 4 Attachment Modes inside Segmented Track (Y: 185..241) ---
        var modeY1 = 185f;
        var modeY2 = 241f;
        var modeW = 266f;
        var modeGap = 12f;
        var modeStartX = 50f;

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SetModeHead,
            new SKRect(modeStartX + (modeW + modeGap) * 0, modeY1, modeStartX + modeW + (modeW + modeGap) * 0, modeY2),
            "头部跟随 (视野下方)",
            IsSelected: s.AttachmentMode == OverlayAttachmentMode.HeadLocked));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SetModeWorld,
            new SKRect(modeStartX + (modeW + modeGap) * 1, modeY1, modeStartX + modeW + (modeW + modeGap) * 1, modeY2),
            "空间固定 (手柄拖拽)",
            IsSelected: s.AttachmentMode == OverlayAttachmentMode.WorldLocked));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SetModeLeftHand,
            new SKRect(modeStartX + (modeW + modeGap) * 2, modeY1, modeStartX + modeW + (modeW + modeGap) * 2, modeY2),
            "左手腕跟随",
            IsSelected: s.AttachmentMode == OverlayAttachmentMode.LeftHand));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SetModeRightHand,
            new SKRect(modeStartX + (modeW + modeGap) * 3, modeY1, modeStartX + modeW + (modeW + modeGap) * 3, modeY2),
            "右手腕跟随",
            IsSelected: s.AttachmentMode == OverlayAttachmentMode.RightHand));

        // --- Section 1 Row 2: Spatial Quick Actions (Y: 260..318) ---
        var actY1 = 260f;
        var actY2 = 318f;
        _buttons.Add(new DashboardButtonItem(
            DashboardAction.RecenterWorldToHmd,
            new SKRect(46, actY1, 306, actY2),
            "移至当前头显视野正前方",
            IsPrimary: true));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleWorldControllerGrab,
            new SKRect(320, actY1, 580, actY2),
            s.EnableWorldControllerGrab ? "手柄抓取拖动：已允许" : "手柄抓取拖动：已锁定",
            IsSelected: s.EnableWorldControllerGrab));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.WidthMinus,
            new SKRect(594, actY1, 768, actY2),
            $"宽度缩小 ({s.WidthInMeters:0.00} m)"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.WidthPlus,
            new SKRect(782, actY1, 956, actY2),
            $"宽度放大 ({s.WidthInMeters:0.00} m)"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.CycleOpacity,
            new SKRect(970, actY1, 1154, actY2),
            $"不透明度 {s.Opacity * 100:0}%"));

        // --- Section 2 Left: Timeline Offset (X: 46..574, Y: 416..508) ---
        var offY1 = 416f;
        var offY2 = 508f;
        var offW = 97f;
        var offGap = 10f;
        var offStartX = 46f;

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetMinus500,
            new SKRect(offStartX + (offW + offGap) * 0, offY1, offStartX + offW + (offW + offGap) * 0, offY2),
            "-500 ms",
            "提前 0.5s"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetMinus100,
            new SKRect(offStartX + (offW + offGap) * 1, offY1, offStartX + offW + (offW + offGap) * 1, offY2),
            "-100 ms",
            "提前 0.1s"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetReset,
            new SKRect(offStartX + (offW + offGap) * 2, offY1, offStartX + offW + (offW + offGap) * 2, offY2),
            "重置 0 ms",
            $"{s.GlobalOffsetMs:+0;-0;0} ms",
            IsSelected: s.GlobalOffsetMs == 0));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetPlus100,
            new SKRect(offStartX + (offW + offGap) * 3, offY1, offStartX + offW + (offW + offGap) * 3, offY2),
            "+100 ms",
            "延后 0.1s"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.OffsetPlus500,
            new SKRect(offStartX + (offW + offGap) * 4, offY1, offStartX + offW + (offW + offGap) * 4, offY2),
            "+500 ms",
            "延后 0.5s"));

        // --- Section 2 Right: Lyrics Provider & Reload (X: 626..1154, Y: 416..508) ---
        var srcStartX = 626f;
        var srcW = 80f;
        var srcGap = 9f;
        var pref = s.PreferredLyricsProvider ?? "Auto";

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceAuto,
            new SKRect(srcStartX + (srcW + srcGap) * 0, offY1, srcStartX + srcW + (srcW + srcGap) * 0, offY2),
            "自动优选",
            "Auto",
            IsSelected: string.Equals(pref, "Auto", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceNetease,
            new SKRect(srcStartX + (srcW + srcGap) * 1, offY1, srcStartX + srcW + (srcW + srcGap) * 1, offY2),
            "网易云",
            "逐字歌词",
            IsSelected: string.Equals(pref, "NetEase", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceQqMusic,
            new SKRect(srcStartX + (srcW + srcGap) * 2, offY1, srcStartX + srcW + (srcW + srcGap) * 2, offY2),
            "QQ 音乐",
            "双语歌词",
            IsSelected: string.Equals(pref, "QQMusic", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceLrclib,
            new SKRect(srcStartX + (srcW + srcGap) * 3, offY1, srcStartX + srcW + (srcW + srcGap) * 3, offY2),
            "LRCLIB",
            "全球曲库",
            IsSelected: string.Equals(pref, "LRCLIB", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.SourceLocal,
            new SKRect(srcStartX + (srcW + srcGap) * 4, offY1, srcStartX + srcW + (srcW + srcGap) * 4, offY2),
            "本地词库",
            ".lrc",
            IsSelected: string.Equals(pref, "LocalLRC", StringComparison.OrdinalIgnoreCase)));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ReloadLyrics,
            new SKRect(srcStartX + (srcW + srcGap) * 5, offY1, srcStartX + srcW + (srcW + srcGap) * 5, offY2),
            "重新抓取",
            "刷新缓存",
            IsPrimary: true));

        // --- Section 3 Row 1: Content & Typography Toggles (Y: 600..654) ---
        var cY1 = 600f;
        var cY2 = 654f;
        var cW = 210f;
        var cGap = 14f;
        var cStartX = 46f;

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleTranslation,
            new SKRect(cStartX + (cW + cGap) * 0, cY1, cStartX + cW + (cW + cGap) * 0, cY2),
            s.ShowTranslation ? "双语翻译行：开启" : "双语翻译行：关闭",
            IsSelected: s.ShowTranslation));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleContextLines,
            new SKRect(cStartX + (cW + cGap) * 1, cY1, cStartX + cW + (cW + cGap) * 1, cY2),
            s.ShowContextLines ? "上下句预览：开启" : "上下句预览：关闭",
            IsSelected: s.ShowContextLines));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.ToggleKaraoke,
            new SKRect(cStartX + (cW + cGap) * 2, cY1, cStartX + cW + (cW + cGap) * 2, cY2),
            s.EnableKaraokeHighlight ? "逐字擦除高亮：开启" : "逐字擦除高亮：关闭",
            IsSelected: s.EnableKaraokeHighlight));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.FontSizeMinus,
            new SKRect(cStartX + (cW + cGap) * 3, cY1, cStartX + cW + (cW + cGap) * 3, cY2),
            $"字号缩小 ({s.MainFontSize:0} px)"));

        _buttons.Add(new DashboardButtonItem(
            DashboardAction.FontSizePlus,
            new SKRect(cStartX + (cW + cGap) * 4, cY1, cStartX + cW + (cW + cGap) * 4, cY2),
            $"字号放大 ({s.MainFontSize:0} px)"));

        // --- Section 3 Row 2: 5 Color Theme Presets (Y: 666..718) ---
        var tY1 = 666f;
        var tY2 = 718f;
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
                preset.ShortName,
                IsSelected: preset.Matches(s),
                SwatchTop: topColor,
                SwatchBottom: botColor));
        }
    }

    private void DrawHeader(SKCanvas canvas, DashboardRenderState state)
    {
        var headerRect = new SKRect(28, 22, PanelWidth - 28, 120);
        using var cardPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ColorCard };
        using var borderPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = ColorBorder };
        canvas.DrawRoundRect(headerRect, 14f, 14f, cardPaint);
        canvas.DrawRoundRect(headerRect, 14f, 14f, borderPaint);

        using var titleLatin = new SKFont(_latinBold, 23f);
        using var titleCjk = new SKFont(_cjkBold, 23f);
        using var trackLatin = new SKFont(_latinMedium, 17.5f);
        using var trackCjk = new SKFont(_cjkBold, 17.5f);
        using var subLatin = new SKFont(_latinMedium, 13.5f);
        using var subCjk = new SKFont(_cjkRegular, 13.5f);

        using var text1Paint = new SKPaint { IsAntialias = true, Color = ColorText1 };
        using var text2Paint = new SKPaint { IsAntialias = true, Color = ColorText2 };
        using var text3Paint = new SKPaint { IsAntialias = true, Color = ColorText3 };

        // Brand title & subtitle in Google Sans
        DrawMixedText(canvas, "VRLyrics", 48, 56, SKTextAlign.Left, titleLatin, titleCjk, text1Paint);
        DrawMixedText(canvas, "SteamVR Lyrics Studio", 160, 55, SKTextAlign.Left, subLatin, subCjk, text3Paint);

        // SteamVR Connected status pill with vector Steam logo
        var badgeRect = new SKRect(326, 36, 466, 62);
        using (var badgeBg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(29, 38, 33) })
        using (var badgeBorder = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(67, 94, 77) })
        using (var badgeFg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(180, 203, 187) })
        {
            canvas.DrawRoundRect(badgeRect, 6f, 6f, badgeBg);
            canvas.DrawRoundRect(badgeRect, 6f, 6f, badgeBorder);

            DrawSteamLogo(canvas, badgeRect.Left + 10f, badgeRect.MidY - 6.5f, 13f, badgeFg.Color);

            using var badgeLatin = new SKFont(_latinMedium, 12.5f);
            using var badgeCjk = new SKFont(_cjkRegular, 12.5f);
            DrawMixedText(canvas, "已连接 SteamVR", badgeRect.Left + 29f, badgeRect.MidY + 4.5f, SKTextAlign.Left, badgeLatin, badgeCjk, badgeFg);
        }

        var f = state.Frame;
        DrawPlayPauseIcon(canvas, 48f, 73f, 12f, f.Track is not null && f.IsPlaying, f.Track is not null ? ColorAccent : ColorText3);

        var trackTitle = f.Track is { } t
            ? $"{t.Title} - {t.Artist}   ({FormatTime(f.PlaybackPosition)} / {FormatTime(t.Duration)})"
            : "当前未检测到正在播放的曲目";

        DrawMixedText(canvas, TruncateText(trackTitle, trackCjk, 640f), 68, 86, SKTextAlign.Left, trackLatin, trackCjk, text1Paint);

        var statusStr = !string.IsNullOrWhiteSpace(f.StatusText) ? f.StatusText! : "等待音乐播放…";
        DrawMixedText(canvas, TruncateText(statusStr, subCjk, 660f), 48, 108, SKTextAlign.Left, subLatin, subCjk, text3Paint);
    }

    private static void DrawPlayPauseIcon(SKCanvas canvas, float x, float y, float size, bool isPlaying, SKColor color)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        if (isPlaying)
        {
            using var path = new SKPath();
            path.MoveTo(x + 1f, y);
            path.LineTo(x + size, y + size * 0.5f);
            path.LineTo(x + 1f, y + size);
            path.Close();
            canvas.DrawPath(path, paint);
        }
        else
        {
            var barW = 3.2f;
            canvas.DrawRoundRect(new SKRect(x + 1f, y, x + 1f + barW, y + size), 1f, 1f, paint);
            canvas.DrawRoundRect(new SKRect(x + size - 1f - barW, y, x + size - 1f, y + size), 1f, 1f, paint);
        }
    }

    private static void DrawSteamLogo(SKCanvas canvas, float x, float y, float size, SKColor color)
    {
        using var path = SKPath.ParseSvgPathData(SteamLogoSvgPath);
        if (path is null) return;

        canvas.Save();
        canvas.Translate(x, y);
        var scale = size / 512f;
        canvas.Scale(scale, scale);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }

    private void DrawSectionCard(SKCanvas canvas, SKRect rect, string title, string rightSummary)
    {
        using var bgPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ColorCard };
        using var borderPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = ColorBorder };
        canvas.DrawRoundRect(rect, 14f, 14f, bgPaint);
        canvas.DrawRoundRect(rect, 14f, 14f, borderPaint);

        using var titleLatin = new SKFont(_latinBold, 16.5f);
        using var titleCjk = new SKFont(_cjkBold, 16.5f);
        using var sumLatin = new SKFont(_latinMedium, 13f);
        using var sumCjk = new SKFont(_cjkRegular, 13f);
        using var titlePaint = new SKPaint { IsAntialias = true, Color = ColorText1 };
        using var sumPaint = new SKPaint { IsAntialias = true, Color = ColorText3 };

        DrawMixedText(canvas, title, rect.Left + 20f, rect.Top + 31f, SKTextAlign.Left, titleLatin, titleCjk, titlePaint);
        if (!string.IsNullOrWhiteSpace(rightSummary))
            DrawMixedText(canvas, rightSummary, rect.Right - 20f, rect.Top + 30f, SKTextAlign.Right, sumLatin, sumCjk, sumPaint);
    }

    private void DrawButton(SKCanvas canvas, DashboardButtonItem btn, bool isHover, bool isPressed)
    {
        var r = btn.Bounds;
        if (isPressed)
            r.Inflate(-1.5f, -1.5f);

        SKColor fillColor;
        SKColor borderColor;
        float borderWidth;

        if (btn.IsSelected)
        {
            fillColor = isHover ? ColorAccentSoftHover : ColorAccentSoft;
            borderColor = isHover ? ColorAccentHover : ColorAccent;
            borderWidth = 1.5f;
        }
        else if (btn.IsPrimary)
        {
            fillColor = isHover ? new SKColor(46, 53, 64) : new SKColor(38, 44, 54);
            borderColor = isHover ? ColorAccent : ColorAccentSoftBorder;
            borderWidth = 1.2f;
        }
        else
        {
            fillColor = isHover ? new SKColor(43, 48, 56) : new SKColor(35, 39, 46);
            borderColor = isHover ? new SKColor(78, 86, 98) : ColorBorderStrong;
            borderWidth = 1f;
        }

        using (var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = fillColor })
        using (var strokePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = borderWidth,
            Color = borderColor
        })
        {
            canvas.DrawRoundRect(r, 9f, 9f, fillPaint);
            canvas.DrawRoundRect(r, 9f, 9f, strokePaint);
        }

        // Special layout for header toggle switches (ToggleEnabled / ToggleDemoLoop): draw pill switch on right
        if (btn.Action is DashboardAction.ToggleEnabled or DashboardAction.ToggleDemoLoop)
        {
            DrawHeaderToggleButtonContent(canvas, r, btn);
            return;
        }

        // Optional color swatch circle for theme buttons
        var textCenterOffsetX = 0f;
        if (btn.SwatchTop is { } sTop && btn.SwatchBottom is { } sBot)
        {
            var swatchRadius = 8.5f;
            var cx = r.Left + 24f;
            var cy = r.MidY;
            using var swatchShader = SKShader.CreateLinearGradient(
                new SKPoint(cx, cy - swatchRadius),
                new SKPoint(cx, cy + swatchRadius),
                [sTop, sBot],
                null,
                SKShaderTileMode.Clamp);
            using var swatchFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = swatchShader };
            using var swatchBorder = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(90, 98, 110) };
            canvas.DrawCircle(cx, cy, swatchRadius, swatchFill);
            canvas.DrawCircle(cx, cy, swatchRadius, swatchBorder);
            textCenterOffsetX = 9f;
        }

        var fontSize = string.IsNullOrEmpty(btn.SubLabel) ? 15.0f : 15.0f;
        using var labelLatin = new SKFont(btn.IsSelected ? _latinBold : _latinMedium, fontSize);
        using var labelCjk = new SKFont(btn.IsSelected ? _cjkBold : _cjkRegular, fontSize);
        using var labelPaint = new SKPaint
        {
            IsAntialias = true,
            Color = btn.IsSelected ? ColorText1 : new SKColor(210, 215, 222)
        };

        if (string.IsNullOrWhiteSpace(btn.SubLabel))
        {
            DrawMixedText(canvas, btn.Label, r.MidX + textCenterOffsetX, r.MidY + 5.5f, SKTextAlign.Center, labelLatin, labelCjk, labelPaint);
        }
        else
        {
            using var subLatin = new SKFont(_latinMedium, 12f);
            using var subCjk = new SKFont(_cjkRegular, 12f);
            using var subPaint = new SKPaint
            {
                IsAntialias = true,
                Color = btn.IsSelected ? new SKColor(188, 198, 211) : ColorText3
            };
            DrawMixedText(canvas, btn.Label, r.MidX + textCenterOffsetX, r.MidY - 3f, SKTextAlign.Center, labelLatin, labelCjk, labelPaint);
            DrawMixedText(canvas, btn.SubLabel!, r.MidX + textCenterOffsetX, r.MidY + 16f, SKTextAlign.Center, subLatin, subCjk, subPaint);
        }
    }

    private void DrawHeaderToggleButtonContent(SKCanvas canvas, SKRect r, DashboardButtonItem btn)
    {
        using var labelLatin = new SKFont(_latinBold, 14.5f);
        using var labelCjk = new SKFont(_cjkBold, 14.5f);
        using var subLatin = new SKFont(_latinMedium, 12f);
        using var subCjk = new SKFont(_cjkRegular, 12f);
        using var labelPaint = new SKPaint { IsAntialias = true, Color = ColorText1 };
        using var subPaint = new SKPaint { IsAntialias = true, Color = btn.IsSelected ? new SKColor(188, 198, 211) : ColorText3 };

        DrawMixedText(canvas, btn.Label, r.Left + 16f, r.MidY - 3f, SKTextAlign.Left, labelLatin, labelCjk, labelPaint);
        if (!string.IsNullOrWhiteSpace(btn.SubLabel))
            DrawMixedText(canvas, btn.SubLabel!, r.Left + 16f, r.MidY + 16f, SKTextAlign.Left, subLatin, subCjk, subPaint);

        // Draw low-saturation pill switch on the right side
        var swW = 40f;
        var swH = 20f;
        var swRect = new SKRect(r.Right - 16f - swW, r.MidY - swH * 0.5f, r.Right - 16f, r.MidY + swH * 0.5f);
        if (btn.IsSelected)
        {
            using var trackPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ColorAccent };
            using var thumbPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ColorOnAccent };
            canvas.DrawRoundRect(swRect, 10f, 10f, trackPaint);
            canvas.DrawCircle(swRect.Right - 10f, swRect.MidY, 6f, thumbPaint);
        }
        else
        {
            using var trackStroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(91, 98, 109) };
            using var thumbPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(154, 161, 172) };
            canvas.DrawRoundRect(swRect, 10f, 10f, trackStroke);
            canvas.DrawCircle(swRect.Left + 10f, swRect.MidY, 5f, thumbPaint);
        }
    }

    /// <summary>
    /// Renders mixed Latin/numeric (Google Sans) and CJK (Microsoft YaHei UI) text runs with proper alignment.
    /// </summary>
    private static void DrawMixedText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign align,
        SKFont latinFont,
        SKFont cjkFont,
        SKPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Measure total width across runs
        var totalWidth = 0f;
        var i = 0;
        while (i < text.Length)
        {
            var isLat = IsLatinRunChar(text[i]);
            var start = i;
            i++;
            while (i < text.Length && IsLatinRunChar(text[i]) == isLat)
                i++;

            var span = text.AsSpan(start, i - start);
            totalWidth += (isLat ? latinFont : cjkFont).MeasureText(span);
        }

        var curX = align switch
        {
            SKTextAlign.Center => x - totalWidth * 0.5f,
            SKTextAlign.Right => x - totalWidth,
            _ => x
        };

        i = 0;
        while (i < text.Length)
        {
            var isLat = IsLatinRunChar(text[i]);
            var start = i;
            i++;
            while (i < text.Length && IsLatinRunChar(text[i]) == isLat)
                i++;

            var run = text[start..i];
            var font = isLat ? latinFont : cjkFont;
            canvas.DrawText(run, curX, y, SKTextAlign.Left, font, paint);
            curX += font.MeasureText(run);
        }
    }

    private static bool IsLatinRunChar(char ch) =>
        (ch >= 0x0020 && ch <= 0x007E) || ch is '·' or '×' or '°';

    private static string FormatProviderName(string? pref) => pref?.ToUpperInvariant() switch
    {
        "NETEASE" => "网易云音乐",
        "QQMUSIC" => "QQ 音乐",
        "LRCLIB" => "LRCLIB",
        "LOCALLRC" => "本地 .lrc",
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

    private static SKTypeface? LoadEmbeddedTypeface(string resourceName)
    {
        try
        {
            using var stream = typeof(DashboardPanelRenderer).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            return SKTypeface.FromStream(ms);
        }
        catch
        {
            return null;
        }
    }

    private static SKTypeface ResolveCjkTypeface(bool bold)
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
        _cjkBold.Dispose();
        _cjkRegular.Dispose();
        if (!ReferenceEquals(_latinBold, _cjkBold)) _latinBold.Dispose();
        if (!ReferenceEquals(_latinMedium, _cjkRegular)) _latinMedium.Dispose();
    }
}
