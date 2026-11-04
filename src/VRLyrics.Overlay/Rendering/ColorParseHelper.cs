using System.Globalization;
using SkiaSharp;

namespace VRLyrics.Overlay.Rendering;

/// <summary>Parses hex color strings (#RRGGBB or #AARRGGBB) into <see cref="SKColor"/>.</summary>
public static class ColorParseHelper
{
    public static SKColor Parse(string? hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var span = hex.AsSpan().Trim();
        if (span.StartsWith("#")) span = span[1..];

        if (span.Length == 6 &&
            uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return new SKColor(
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF),
                0xFF);
        }

        if (span.Length == 8 &&
            uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return new SKColor(
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF),
                (byte)((argb >> 24) & 0xFF));
        }

        return fallback;
    }

    public static SKColor MultiplyAlpha(this SKColor color, float factor)
    {
        var a = (int)Math.Round(color.Alpha * Math.Clamp(factor, 0f, 1f));
        return color.WithAlpha((byte)Math.Clamp(a, 0, 255));
    }
}
