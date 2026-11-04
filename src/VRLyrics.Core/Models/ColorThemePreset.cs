namespace VRLyrics.Core.Models;

public sealed record ColorThemePreset(
    string Name,
    string ShortName,
    string Description,
    string SungTopColorHex,
    string SungBottomColorHex,
    string SungGlowColorHex,
    string UnsungTopColorHex,
    string UnsungBottomColorHex,
    string TranslationColorHex,
    string ContextColorHex,
    string StrokeColorHex,
    string BackgroundColorHex)
{
    public LyricsOverlaySettings ApplyTo(LyricsOverlaySettings current) =>
        current with
        {
            SungTopColorHex = SungTopColorHex,
            SungBottomColorHex = SungBottomColorHex,
            SungGlowColorHex = SungGlowColorHex,
            UnsungTopColorHex = UnsungTopColorHex,
            UnsungBottomColorHex = UnsungBottomColorHex,
            TranslationColorHex = TranslationColorHex,
            ContextColorHex = ContextColorHex,
            StrokeColorHex = StrokeColorHex,
            BackgroundColorHex = BackgroundColorHex,
        };

    public bool Matches(LyricsOverlaySettings current) =>
        string.Equals(current.SungTopColorHex, SungTopColorHex, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(current.SungBottomColorHex, SungBottomColorHex, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ColorThemePreset> All { get; } =
    [
        new(
            "霓虹青蓝 (默认)",
            "霓虹青蓝",
            "SteamVR 科技感青蓝发光卡拉OK，对比度清晰",
            SungTopColorHex: "#FF7CF7FF",
            SungBottomColorHex: "#FF00C2FF",
            SungGlowColorHex: "#8800D0FF",
            UnsungTopColorHex: "#FFFFFFFF",
            UnsungBottomColorHex: "#FFD2DCE8",
            TranslationColorHex: "#EBDFE8F6",
            ContextColorHex: "#94CBD5E1",
            StrokeColorHex: "#E8090D16",
            BackgroundColorHex: "#380A0F1A"),

        new(
            "流金暖阳",
            "流金暖阳",
            "KTV 经典流金暖黄高亮，适合流行与抒情慢歌",
            SungTopColorHex: "#FFFFF29E",
            SungBottomColorHex: "#FFFF9D00",
            SungGlowColorHex: "#88FF9900",
            UnsungTopColorHex: "#FFFFFFFF",
            UnsungBottomColorHex: "#FFEAE0D5",
            TranslationColorHex: "#EBF7E6C4",
            ContextColorHex: "#94D6C7B2",
            StrokeColorHex: "#E8140D06",
            BackgroundColorHex: "#38140F0A"),

        new(
            "樱花粉紫",
            "樱花粉紫",
            "柔和霓虹粉紫渐变，适合 ACG、电音与日系曲风",
            SungTopColorHex: "#FFFFA6F6",
            SungBottomColorHex: "#FFFF389C",
            SungGlowColorHex: "#88FF38B8",
            UnsungTopColorHex: "#FFFFFFFF",
            UnsungBottomColorHex: "#FFF0D9EA",
            TranslationColorHex: "#EBF5D5ED",
            ContextColorHex: "#94DEC2D8",
            StrokeColorHex: "#E8160814",
            BackgroundColorHex: "#38160A18"),

        new(
            "翡翠律动",
            "翡翠律动",
            "清透薄荷翠绿高光，暗场与亮场游戏均不刺眼",
            SungTopColorHex: "#FF98FFD1",
            SungBottomColorHex: "#FF00D67F",
            SungGlowColorHex: "#8800E088",
            UnsungTopColorHex: "#FFFFFFFF",
            UnsungBottomColorHex: "#FFD4ECE2",
            TranslationColorHex: "#EBD8F5E8",
            ContextColorHex: "#94BBD9CC",
            StrokeColorHex: "#E806140E",
            BackgroundColorHex: "#3808140F"),

        new(
            "纯净极简白",
            "极简纯白",
            "无底板低打扰纯白高亮，适合沉浸式 VR 观影/社交",
            SungTopColorHex: "#FFFFFFFF",
            SungBottomColorHex: "#FFCCE8FF",
            SungGlowColorHex: "#7080C8FF",
            UnsungTopColorHex: "#CCBAC8D8",
            UnsungBottomColorHex: "#998A99AD",
            TranslationColorHex: "#D8CBD5E1",
            ContextColorHex: "#7894A3B8",
            StrokeColorHex: "#E8090D16",
            BackgroundColorHex: "#00000000"),
    ];
}
