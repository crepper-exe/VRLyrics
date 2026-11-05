using SkiaSharp;

namespace VRLyrics.Overlay.Dashboard;

/// <summary>Interactive actions available on the in-VR SteamVR Dashboard control panel.</summary>
public enum DashboardAction
{
    ToggleEnabled,
    ToggleDemoLoop,

    SetModeHead,
    SetModeWorld,
    SetModeLeftHand,
    SetModeRightHand,

    RecenterWorldToHmd,
    ToggleWorldControllerGrab,
    WidthMinus,
    WidthPlus,
    CycleOpacity,

    OffsetMinus500,
    OffsetMinus100,
    OffsetReset,
    OffsetPlus100,
    OffsetPlus500,

    SourceAuto,
    SourceNetease,
    SourceQqMusic,
    SourceLrclib,
    SourceLocal,
    ReloadLyrics,

    ToggleTranslation,
    ToggleContextLines,
    ToggleKaraoke,
    FontSizeMinus,
    FontSizePlus,

    ThemePreset0,
    ThemePreset1,
    ThemePreset2,
    ThemePreset3,
    ThemePreset4,
}

/// <summary>Continuous pill-slider controls available on the in-VR SteamVR Dashboard control panel.</summary>
public enum DashboardSliderKind
{
    Width,
    Opacity,
    FontSize,
}

/// <summary>Bounding box and visual state for a mobile-volume-style pill slider on the Dashboard canvas.</summary>
public readonly record struct DashboardSliderItem(
    DashboardSliderKind Kind,
    SKRect Bounds,
    string Label,
    string ValueText,
    float FillRatio);

/// <summary>Bounding box and visual definition for a clickable button on the Dashboard canvas.</summary>
public readonly record struct DashboardButtonItem(
    DashboardAction Action,
    SKRect Bounds,
    string Label,
    string? SubLabel = null,
    bool IsSelected = false,
    bool IsPrimary = false,
    SKColor? SwatchTop = null,
    SKColor? SwatchBottom = null,
    bool IsSliderSegment = false);
