using SkiaSharp;
using VRLyrics.Core.Models;
using VRLyrics.Core.Timing;

namespace VRLyrics.Overlay.Rendering;

/// <summary>
/// Off-screen SkiaSharp renderer that draws the VR lyrics HUD (M2 + M3):
/// word-by-word karaoke gradient wipe, active-word lift, bilingual translation,
/// context lines, stroke/shadow/glow, and smooth line-transition animations.
/// </summary>
public sealed class LyricsRenderer : IDisposable
{
    private static readonly string[] FallbackCjkFonts =
        ["Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "PingFang SC", "Noto Sans CJK SC", "Segoe UI"];

    private readonly LyricsAnimator _animator = new();

    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    private SKTypeface? _typeface;
    private string? _cachedFontFamily;
    private bool _cachedBold;

    public SKBitmap Bitmap => _bitmap ?? throw new InvalidOperationException("Call RenderFrame at least once before accessing Bitmap.");

    /// <summary>True when the most recent frame is actively animating (line transition or active karaoke wipe).</summary>
    public bool IsAnimating { get; private set; }

    public void ResetAnimation() => _animator.Reset();

    /// <summary>Renders <paramref name="frame"/> into <see cref="Bitmap"/> and returns it.</summary>
    public SKBitmap RenderFrame(LyricsRenderFrame frame)
    {
        var settings = frame.Settings;
        var width = Math.Clamp(settings.TextureWidth, 320, 4096);
        var height = Math.Clamp(settings.TextureHeight, 120, 2160);

        EnsureSurface(width, height);
        EnsureTypeface(settings.FontFamily, settings.Bold);

        var canvas = _canvas!;
        canvas.Clear(SKColors.Transparent);

        DrawBackdrop(canvas, width, height, settings);
        if (frame.IsWorldGrabHovered || frame.IsWorldGrabActive)
            DrawWorldGrabFeedback(canvas, width, height, frame);

        if (settings.ShowTrackHeader)
            DrawTrackHeader(canvas, width, frame);

        var lyrics = frame.Lyrics;
        if (lyrics is null || lyrics.IsEmpty)
        {
            IsAnimating = frame.IsWorldGrabHovered || frame.IsWorldGrabActive;
            DrawIdleOrStatus(canvas, width, height, frame);
            canvas.Flush();
            return _bitmap!;
        }

        if (lyrics.IsUnsynced)
        {
            IsAnimating = frame.IsWorldGrabHovered || frame.IsWorldGrabActive;
            DrawUnsyncedLyrics(canvas, width, height, lyrics, settings);
            canvas.Flush();
            return _bitmap!;
        }

        var pos = frame.EffectivePosition;
        var state = LyricTimeline.GetState(lyrics, pos);
        var transitionDuration = TimeSpan.FromMilliseconds(Math.Clamp(settings.TransitionDurationMs, 0, 1500));
        var anim = _animator.Update(
            frame.Track?.Key ?? lyrics.SourceId ?? "default",
            state.LineIndex,
            frame.FrameTimestamp,
            transitionDuration,
            settings.EnableLineTransitionAnimation);

        IsAnimating = anim.IsTransitioning ||
                      (frame.IsPlaying && state.IsLineActive) ||
                      frame.IsWorldGrabHovered ||
                      frame.IsWorldGrabActive;

        DrawSyncedLyrics(canvas, width, height, lyrics, state, anim, settings);
        canvas.Flush();
        return _bitmap!;
    }

    /// <summary>Encodes the most recently rendered frame to PNG bytes (for tests and desktop preview).</summary>
    public byte[] EncodeToPng()
    {
        if (_bitmap is null) throw new InvalidOperationException("Nothing rendered yet.");
        using var image = SKImage.FromBitmap(_bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private void DrawBackdrop(SKCanvas canvas, int width, int height, LyricsOverlaySettings settings)
    {
        var bgColor = ColorParseHelper.Parse(settings.BackgroundColorHex, new SKColor(10, 15, 26, 56));
        if (bgColor.Alpha == 0) return;

        var marginX = 24f;
        var marginY = 14f;
        var rect = new SKRect(marginX, marginY, width - marginX, height - marginY);
        var radius = Math.Clamp(settings.BackgroundCornerRadius, 0f, height * 0.5f);

        using var rrect = new SKRoundRect(rect, radius, radius);
        using var fillPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = bgColor,
        };
        canvas.DrawRoundRect(rrect, fillPaint);

        // Subtle glass border so the HUD edge is clean in dark VR scenes.
        using var borderPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = new SKColor(255, 255, 255, (byte)Math.Min(48, (int)bgColor.Alpha)),
        };
        canvas.DrawRoundRect(rrect, borderPaint);
    }

    private void DrawWorldGrabFeedback(SKCanvas canvas, int width, int height, LyricsRenderFrame frame)
    {
        var marginX = 24f;
        var marginY = 14f;
        var rect = new SKRect(marginX, marginY, width - marginX, height - marginY);
        var radius = Math.Clamp(frame.Settings.BackgroundCornerRadius, 0f, height * 0.5f);
        using var rrect = new SKRoundRect(rect, radius, radius);

        var accent = frame.IsWorldGrabActive
            ? new SKColor(56, 189, 248, 235)   // Active sky-cyan glow
            : new SKColor(125, 211, 252, 165); // Soft hover border

        using (var glowFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, frame.IsWorldGrabActive ? 6f : 3.5f))
        using (var glowPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = frame.IsWorldGrabActive ? 4.5f : 3.0f,
            Color = accent,
            MaskFilter = glowFilter,
        })
        {
            canvas.DrawRoundRect(rrect, glowPaint);
        }

        using (var strokePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = frame.IsWorldGrabActive ? 3.0f : 2.0f,
            Color = accent,
        })
        {
            canvas.DrawRoundRect(rrect, strokePaint);
        }

        // Top-right interactive hint badge
        var hintText = frame.IsWorldGrabActive
            ? "✥ 正在拖动空间位置 · 松开自动保存"
            : "✋ 按住手柄侧键/扳机拖动 · 摇杆调远近";

        using var badgeFont = new SKFont(_typeface!, 20f);
        var textWidth = badgeFont.MeasureText(hintText);
        var pillWidth = textWidth + 28f;
        var pillHeight = 34f;
        var pillRight = width - marginX - 16f;
        var pillTop = marginY + 12f;
        var pillRect = new SKRect(pillRight - pillWidth, pillTop, pillRight, pillTop + pillHeight);

        using var pillBg = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = frame.IsWorldGrabActive ? new SKColor(8, 47, 73, 225) : new SKColor(15, 23, 42, 210),
        };
        using var pillBorder = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = accent,
        };
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = new SKColor(224, 242, 254, 250),
        };

        canvas.DrawRoundRect(pillRect, 17f, 17f, pillBg);
        canvas.DrawRoundRect(pillRect, 17f, 17f, pillBorder);
        canvas.DrawText(hintText, pillRect.MidX, pillRect.MidY + 7f, SKTextAlign.Center, badgeFont, textPaint);
    }

    private void DrawTrackHeader(SKCanvas canvas, int width, LyricsRenderFrame frame)
    {
        var trackText = frame.Track is { } t ? t.ToString() : null;
        var status = frame.StatusText;
        var header = !string.IsNullOrWhiteSpace(trackText)
            ? (!string.IsNullOrWhiteSpace(status) && frame.Lyrics is null ? $"{trackText}  ·  {status}" : trackText)
            : status;

        if (string.IsNullOrWhiteSpace(header)) return;

        using var font = new SKFont(_typeface!, Math.Max(18f, frame.Settings.ContextFontSize * 0.68f));
        var headerColor = new SKColor(210, 225, 245, 195);
        DrawOutlinedText(
            canvas,
            header,
            width * 0.5f,
            48f,
            font,
            headerColor,
            headerColor,
            ColorParseHelper.Parse(frame.Settings.StrokeColorHex, new SKColor(9, 13, 22, 220)).MultiplyAlpha(0.75f),
            Math.Max(2.5f, frame.Settings.StrokeWidth * 0.55f),
            SKColors.Transparent,
            0f,
            maxWidth: width - 120f);
    }

    private void DrawIdleOrStatus(SKCanvas canvas, int width, int height, LyricsRenderFrame frame)
    {
        var settings = frame.Settings;
        var centerY = height * 0.52f;
        var primary = frame.Track is { } t ? t.ToString() : "等待音乐播放…";
        var secondary = !string.IsNullOrWhiteSpace(frame.StatusText)
            ? frame.StatusText!
            : (frame.Track is null ? "在任意播放器（网易云 / QQ音乐 / Spotify / 浏览器）播放歌曲即可自动显示歌词" : "正在匹配歌词…");

        using var mainFont = new SKFont(_typeface!, settings.MainFontSize * 0.82f);
        using var subFont = new SKFont(_typeface!, settings.TranslationFontSize * 0.90f);

        var unsungTop = ColorParseHelper.Parse(settings.UnsungTopColorHex, SKColors.White);
        var unsungBottom = ColorParseHelper.Parse(settings.UnsungBottomColorHex, new SKColor(210, 220, 232));
        var strokeColor = ColorParseHelper.Parse(settings.StrokeColorHex, new SKColor(9, 13, 22, 232));
        var shadowColor = ColorParseHelper.Parse(settings.ShadowColorHex, new SKColor(0, 0, 0, 200));
        var subColor = ColorParseHelper.Parse(settings.TranslationColorHex, new SKColor(223, 232, 246, 220));

        DrawOutlinedText(canvas, primary, width * 0.5f, centerY - 12f, mainFont, unsungTop, unsungBottom, strokeColor, settings.StrokeWidth, shadowColor, settings.ShadowBlur, width - 96f);
        DrawOutlinedText(canvas, secondary, width * 0.5f, centerY + settings.TranslationFontSize + 14f, subFont, subColor, subColor, strokeColor.MultiplyAlpha(0.85f), settings.StrokeWidth * 0.7f, shadowColor, settings.ShadowBlur * 0.7f, width - 96f);
    }

    private void DrawUnsyncedLyrics(SKCanvas canvas, int width, int height, Lyrics lyrics, LyricsOverlaySettings settings)
    {
        using var font = new SKFont(_typeface!, settings.ContextFontSize);
        var strokeColor = ColorParseHelper.Parse(settings.StrokeColorHex, new SKColor(9, 13, 22, 232));
        var shadowColor = ColorParseHelper.Parse(settings.ShadowColorHex, new SKColor(0, 0, 0, 200));
        var fill = ColorParseHelper.Parse(settings.UnsungTopColorHex, SKColors.White);

        var y = 105f;
        foreach (var line in lyrics.Lines.Take(4))
        {
            DrawOutlinedText(canvas, line.Text, width * 0.5f, y, font, fill, fill, strokeColor, settings.StrokeWidth * 0.75f, shadowColor, settings.ShadowBlur * 0.7f, width - 96f);
            y += settings.ContextFontSize * 1.45f;
        }
    }

    private void DrawSyncedLyrics(
        SKCanvas canvas,
        int width,
        int height,
        Lyrics lyrics,
        LyricState state,
        LineAnimationState anim,
        LyricsOverlaySettings settings)
    {
        var lines = lyrics.Lines;
        var idx = state.LineIndex;
        var maxWidth = width - 96f;

        var strokeColor = ColorParseHelper.Parse(settings.StrokeColorHex, new SKColor(9, 13, 22, 232));
        var shadowColor = ColorParseHelper.Parse(settings.ShadowColorHex, new SKColor(0, 0, 0, 200));
        var contextColor = ColorParseHelper.Parse(settings.ContextColorHex, new SKColor(203, 213, 225, 148));
        var transColor = ColorParseHelper.Parse(settings.TranslationColorHex, new SKColor(223, 232, 246, 235));

        // Vertical anchor layout:
        var hasTrans = settings.ShowTranslation && idx >= 0 && idx < lines.Count && !string.IsNullOrWhiteSpace(lines[idx].Translation);
        var mainBaselineY = hasTrans ? height * 0.50f : height * 0.56f;
        var transBaselineY = mainBaselineY + settings.TranslationFontSize * 1.28f + 6f;
        var prevStep = settings.MainFontSize * 1.15f + 12f;
        var nextBaseY = (hasTrans ? transBaselineY : mainBaselineY) + settings.ContextFontSize * 1.32f + 10f;
        var nextStep = settings.ContextFontSize * 1.25f + 6f;

        // Smooth vertical slide during forward line transition:
        var slideOffset = anim.IsTransitioning ? (1f - anim.EasedProgress) * (settings.MainFontSize * 0.75f) : 0f;

        // 1) Previous context lines
        if (settings.ShowContextLines && settings.PreviousLinesCount > 0)
        {
            for (var p = 1; p <= settings.PreviousLinesCount; p++)
            {
                var prevIdx = idx - p;
                if (prevIdx < 0 || prevIdx >= lines.Count) continue;
                var prevText = lines[prevIdx].Text;
                if (string.IsNullOrWhiteSpace(prevText)) continue;

                var alphaFactor = p == 1 && anim.IsTransitioning
                    ? 0.55f + 0.45f * (1f - anim.EasedProgress)
                    : Math.Max(0.35f, 1f - (p - 1) * 0.35f);
                var y = mainBaselineY - prevStep * p + slideOffset;
                if (y < 68f) continue;

                using var ctxFont = new SKFont(_typeface!, settings.ContextFontSize);
                var c = contextColor.MultiplyAlpha(alphaFactor);
                DrawOutlinedText(
                    canvas,
                    prevText,
                    width * 0.5f,
                    y,
                    ctxFont,
                    c,
                    c,
                    strokeColor.MultiplyAlpha(alphaFactor * 0.8f),
                    settings.StrokeWidth * 0.68f,
                    shadowColor.MultiplyAlpha(alphaFactor * 0.7f),
                    settings.ShadowBlur * 0.65f,
                    maxWidth);
            }
        }

        // 2) Current active line (or intro preview when position precedes line 0)
        if (idx < 0)
        {
            var firstText = $"即将开始:  {lines[0].Text}";
            using var introFont = new SKFont(_typeface!, settings.MainFontSize * 0.88f);
            var unsungTop = ColorParseHelper.Parse(settings.UnsungTopColorHex, SKColors.White);
            var unsungBottom = ColorParseHelper.Parse(settings.UnsungBottomColorHex, new SKColor(210, 220, 232));
            DrawOutlinedText(
                canvas,
                firstText,
                width * 0.5f,
                mainBaselineY,
                introFont,
                unsungTop,
                unsungBottom,
                strokeColor,
                settings.StrokeWidth,
                shadowColor,
                settings.ShadowBlur,
                maxWidth);
        }
        else
        {
            var curLine = lines[idx];
            var entryScale = anim.IsTransitioning ? (0.88f + 0.12f * anim.EasedProgress) : 1.0f;
            var curY = mainBaselineY + slideOffset;

            DrawCurrentKaraokeLine(
                canvas,
                curLine,
                state,
                width * 0.5f,
                curY,
                maxWidth,
                entryScale,
                settings);

            // 3) Bilingual translation line
            if (hasTrans)
            {
                var transAlpha = anim.IsTransitioning ? (0.35f + 0.65f * anim.EasedProgress) : 1.0f;
                using var transFont = new SKFont(_typeface!, settings.TranslationFontSize * entryScale);
                var tc = transColor.MultiplyAlpha(transAlpha);
                DrawOutlinedText(
                    canvas,
                    curLine.Translation!,
                    width * 0.5f,
                    transBaselineY + slideOffset * 0.85f,
                    transFont,
                    tc,
                    tc,
                    strokeColor.MultiplyAlpha(transAlpha * 0.88f),
                    settings.StrokeWidth * 0.75f,
                    shadowColor.MultiplyAlpha(transAlpha * 0.8f),
                    settings.ShadowBlur * 0.75f,
                    maxWidth);
            }
        }

        // 4) Next context lines
        if (settings.ShowContextLines && settings.NextLinesCount > 0)
        {
            for (var n = 1; n <= settings.NextLinesCount; n++)
            {
                var nextIdx = (idx < 0 ? 0 : idx) + n;
                if (nextIdx < 0 || nextIdx >= lines.Count) continue;
                var nextText = lines[nextIdx].Text;
                if (string.IsNullOrWhiteSpace(nextText)) continue;

                var alphaFactor = Math.Max(0.35f, 1f - (n - 1) * 0.35f);
                if (anim.IsTransitioning && n == settings.NextLinesCount)
                    alphaFactor *= anim.EasedProgress;

                var y = nextBaseY + (n - 1) * nextStep + slideOffset;
                if (y > height - 28f) continue;

                using var ctxFont = new SKFont(_typeface!, settings.ContextFontSize);
                var c = contextColor.MultiplyAlpha(alphaFactor);
                DrawOutlinedText(
                    canvas,
                    nextText,
                    width * 0.5f,
                    y,
                    ctxFont,
                    c,
                    c,
                    strokeColor.MultiplyAlpha(alphaFactor * 0.8f),
                    settings.StrokeWidth * 0.68f,
                    shadowColor.MultiplyAlpha(alphaFactor * 0.7f),
                    settings.ShadowBlur * 0.65f,
                    maxWidth);
            }
        }
    }

    private void DrawCurrentKaraokeLine(
        SKCanvas canvas,
        LyricLine line,
        LyricState state,
        float centerX,
        float baselineY,
        float maxWidth,
        float scale,
        LyricsOverlaySettings settings)
    {
        var text = string.IsNullOrWhiteSpace(line.Text) ? "· · ·" : line.Text;
        var desiredSize = settings.MainFontSize * scale;
        using var font = new SKFont(_typeface!, desiredSize);

        // Measure either per-word segments (for exact karaoke wipe & word lift) or whole line.
        var hasWords = line.HasWordTiming && !string.IsNullOrWhiteSpace(line.Text);
        float totalWidth;
        float[]? wordWidths = null;

        if (hasWords)
        {
            wordWidths = new float[line.Words.Count];
            totalWidth = 0f;
            for (var i = 0; i < line.Words.Count; i++)
            {
                var w = font.MeasureText(line.Words[i].Text);
                wordWidths[i] = w;
                totalWidth += w;
            }
        }
        else
        {
            totalWidth = font.MeasureText(text);
        }

        // Auto-fit long lines so they never clip outside the VR overlay quad.
        if (totalWidth > maxWidth && totalWidth > 1f)
        {
            var fitRatio = maxWidth / totalWidth;
            font.Size = Math.Max(22f, desiredSize * fitRatio);
            if (hasWords && wordWidths is not null)
            {
                totalWidth = 0f;
                for (var i = 0; i < line.Words.Count; i++)
                {
                    var w = font.MeasureText(line.Words[i].Text);
                    wordWidths[i] = w;
                    totalWidth += w;
                }
            }
            else
            {
                totalWidth = font.MeasureText(text);
            }
        }

        var lineStartX = centerX - totalWidth * 0.5f;
        var topY = baselineY - font.Size;
        var bottomY = baselineY + font.Size * 0.35f;

        // Compute karaoke wipe width in pixels:
        float wipeWidth;
        if (!settings.EnableKaraokeHighlight)
        {
            wipeWidth = 0f;
        }
        else if (hasWords && wordWidths is not null)
        {
            wipeWidth = 0f;
            for (var i = 0; i < line.Words.Count; i++)
            {
                if (i < state.WordIndex)
                {
                    wipeWidth += wordWidths[i];
                }
                else if (i == state.WordIndex)
                {
                    wipeWidth += wordWidths[i] * (float)Math.Clamp(state.WordProgress, 0.0, 1.0);
                    break;
                }
                else
                {
                    break;
                }
            }
        }
        else
        {
            wipeWidth = totalWidth * (float)Math.Clamp(state.LineProgress, 0.0, 1.0);
        }

        var unsungTop = ColorParseHelper.Parse(settings.UnsungTopColorHex, SKColors.White);
        var unsungBottom = ColorParseHelper.Parse(settings.UnsungBottomColorHex, new SKColor(210, 220, 232));
        var sungTop = ColorParseHelper.Parse(settings.SungTopColorHex, new SKColor(124, 247, 255));
        var sungBottom = ColorParseHelper.Parse(settings.SungBottomColorHex, new SKColor(0, 194, 255));
        var sungGlow = ColorParseHelper.Parse(settings.SungGlowColorHex, new SKColor(0, 208, 255, 136));
        var strokeColor = ColorParseHelper.Parse(settings.StrokeColorHex, new SKColor(9, 13, 22, 232));
        var shadowColor = ColorParseHelper.Parse(settings.ShadowColorHex, new SKColor(0, 0, 0, 200));

        var pad = settings.StrokeWidth + settings.ShadowBlur + 16f;
        var leftBound = lineStartX - pad;
        var rightBound = lineStartX + totalWidth + pad;
        var splitX = (!settings.EnableKaraokeHighlight || wipeWidth <= 0.5f)
            ? leftBound
            : (wipeWidth >= totalWidth - 0.5f ? rightBound : lineStartX + wipeWidth);

        // Disjoint left (Sung) and right (Unsung) clips prevent double-blending on the active character
        // while keeping the outer neon glow un-clipped so there is no hard vertical cut through the glow.
        if (splitX > leftBound)
        {
            // Draw soft outer glow for the sung region with a slightly wider right bound so the glow feathers naturally.
            canvas.Save();
            canvas.ClipRect(new SKRect(leftBound, topY - pad, Math.Min(rightBound, splitX + 12f), bottomY + pad));
            DrawGlowOnly(
                canvas,
                text,
                hasWords ? line.Words : null,
                wordWidths,
                lineStartX,
                baselineY,
                font,
                state,
                settings.EnableWordLift && state.IsLineActive,
                sungGlow,
                settings.StrokeWidth,
                glowRadius: 7.0f);
            canvas.Restore();

            canvas.Save();
            canvas.ClipRect(new SKRect(leftBound, topY - pad, splitX, bottomY + pad));
            DrawLineSegments(
                canvas,
                text,
                hasWords ? line.Words : null,
                wordWidths,
                lineStartX,
                baselineY,
                topY,
                bottomY,
                font,
                state,
                settings.EnableWordLift && state.IsLineActive,
                sungTop,
                sungBottom,
                strokeColor,
                settings.StrokeWidth,
                shadowColor,
                settings.ShadowBlur,
                glowColor: SKColors.Transparent,
                glowRadius: 0f);
            canvas.Restore();
        }

        if (splitX < rightBound)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(splitX, topY - pad, rightBound, bottomY + pad));
            DrawLineSegments(
                canvas,
                text,
                hasWords ? line.Words : null,
                wordWidths,
                lineStartX,
                baselineY,
                topY,
                bottomY,
                font,
                state,
                settings.EnableWordLift && state.IsLineActive,
                unsungTop,
                unsungBottom,
                strokeColor,
                settings.StrokeWidth,
                shadowColor,
                settings.ShadowBlur,
                glowColor: SKColors.Transparent,
                glowRadius: 0f);
            canvas.Restore();
        }
    }

    private static void DrawGlowOnly(
        SKCanvas canvas,
        string fullText,
        IReadOnlyList<LyricWord>? words,
        float[]? wordWidths,
        float startX,
        float baselineY,
        SKFont font,
        LyricState state,
        bool enableWordLift,
        SKColor glowColor,
        float strokeWidth,
        float glowRadius)
    {
        if (glowRadius <= 0f || glowColor.Alpha == 0) return;

        using var glowFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, glowRadius);
        using var glowPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.StrokeAndFill,
            StrokeWidth = Math.Max(2f, strokeWidth * 0.85f),
            StrokeJoin = SKStrokeJoin.Round,
            Color = glowColor,
            MaskFilter = glowFilter,
        };

        if (words is null || wordWidths is null || words.Count == 0)
        {
            canvas.DrawText(fullText, startX, baselineY, SKTextAlign.Left, font, glowPaint);
            return;
        }

        var cursorX = startX;
        for (var i = 0; i < words.Count; i++)
        {
            var lift = 0f;
            if (enableWordLift && i == state.WordIndex)
                lift = -4.0f * LyricsAnimator.WordLiftCurve(state.WordProgress);

            canvas.DrawText(words[i].Text, cursorX, baselineY + lift, SKTextAlign.Left, font, glowPaint);
            cursorX += wordWidths[i];
        }
    }

    private static void DrawLineSegments(
        SKCanvas canvas,
        string fullText,
        IReadOnlyList<LyricWord>? words,
        float[]? wordWidths,
        float startX,
        float baselineY,
        float topY,
        float bottomY,
        SKFont font,
        LyricState state,
        bool enableWordLift,
        SKColor fillTop,
        SKColor fillBottom,
        SKColor strokeColor,
        float strokeWidth,
        SKColor shadowColor,
        float shadowBlur,
        SKColor glowColor,
        float glowRadius)
    {
        using var gradient = SKShader.CreateLinearGradient(
            new SKPoint(0, topY),
            new SKPoint(0, bottomY),
            [fillTop, fillBottom],
            null,
            SKShaderTileMode.Clamp);

        using var fillPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = gradient,
        };

        using var strokePaint = strokeWidth > 0f && strokeColor.Alpha > 0
            ? new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                Color = strokeColor,
            }
            : null;

        using var shadowFilter = shadowBlur > 0f && shadowColor.Alpha > 0
            ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, shadowBlur * 0.5f)
            : null;
        using var shadowPaint = shadowFilter is not null
            ? new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.StrokeAndFill,
                StrokeWidth = strokeWidth,
                StrokeJoin = SKStrokeJoin.Round,
                Color = shadowColor,
                MaskFilter = shadowFilter,
            }
            : null;

        using var glowFilter = glowRadius > 0f && glowColor.Alpha > 0
            ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, glowRadius)
            : null;
        using var glowPaint = glowFilter is not null
            ? new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.StrokeAndFill,
                StrokeWidth = Math.Max(2f, strokeWidth * 0.8f),
                StrokeJoin = SKStrokeJoin.Round,
                Color = glowColor,
                MaskFilter = glowFilter,
            }
            : null;

        void DrawPiece(string piece, float x, float y)
        {
            if (shadowPaint is not null)
                canvas.DrawText(piece, x, y + 2.5f, SKTextAlign.Left, font, shadowPaint);
            if (glowPaint is not null)
                canvas.DrawText(piece, x, y, SKTextAlign.Left, font, glowPaint);
            if (strokePaint is not null)
                canvas.DrawText(piece, x, y, SKTextAlign.Left, font, strokePaint);
            canvas.DrawText(piece, x, y, SKTextAlign.Left, font, fillPaint);
        }

        if (words is null || wordWidths is null || words.Count == 0)
        {
            DrawPiece(fullText, startX, baselineY);
            return;
        }

        var cursorX = startX;
        for (var i = 0; i < words.Count; i++)
        {
            var lift = 0f;
            if (enableWordLift && i == state.WordIndex)
                lift = -4.0f * LyricsAnimator.WordLiftCurve(state.WordProgress);

            DrawPiece(words[i].Text, cursorX, baselineY + lift);
            cursorX += wordWidths[i];
        }
    }

    private static void DrawOutlinedText(
        SKCanvas canvas,
        string text,
        float centerX,
        float baselineY,
        SKFont font,
        SKColor fillTop,
        SKColor fillBottom,
        SKColor strokeColor,
        float strokeWidth,
        SKColor shadowColor,
        float shadowBlur,
        float maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var originalSize = font.Size;
        var measured = font.MeasureText(text);
        if (measured > maxWidth && measured > 1f)
        {
            font.Size = Math.Max(16f, originalSize * (maxWidth / measured));
            measured = font.MeasureText(text);
        }

        var leftX = centerX - measured * 0.5f;
        var topY = baselineY - font.Size;
        var bottomY = baselineY + font.Size * 0.3f;

        DrawLineSegments(
            canvas,
            text,
            words: null,
            wordWidths: null,
            leftX,
            baselineY,
            topY,
            bottomY,
            font,
            LyricState.None,
            enableWordLift: false,
            fillTop,
            fillBottom,
            strokeColor,
            strokeWidth,
            shadowColor,
            shadowBlur,
            glowColor: SKColors.Transparent,
            glowRadius: 0f);

        font.Size = originalSize;
    }

    private void EnsureSurface(int width, int height)
    {
        if (_bitmap is not null && _bitmap.Width == width && _bitmap.Height == height)
            return;

        _canvas?.Dispose();
        _bitmap?.Dispose();

        // Rgba8888 matches DXGI_FORMAT_R8G8B8A8_UNORM directly for zero-copy row upload.
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _bitmap = new SKBitmap(info);
        _canvas = new SKCanvas(_bitmap);
    }

    private void EnsureTypeface(string fontFamily, bool bold)
    {
        if (_typeface is not null &&
            string.Equals(_cachedFontFamily, fontFamily, StringComparison.Ordinal) &&
            _cachedBold == bold)
            return;

        _typeface?.Dispose();
        _cachedFontFamily = fontFamily;
        _cachedBold = bold;

        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        var fm = SKFontManager.Default;

        var tf = !string.IsNullOrWhiteSpace(fontFamily) ? fm.MatchFamily(fontFamily, style) : null;
        if (tf is null)
        {
            foreach (var fallback in FallbackCjkFonts)
            {
                tf = fm.MatchFamily(fallback, style);
                if (tf is not null) break;
            }
        }
        _typeface = tf ?? SKTypeface.Default;
    }

    public void Dispose()
    {
        _canvas?.Dispose();
        _bitmap?.Dispose();
        _typeface?.Dispose();
    }
}
