using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using VRLyrics.App.Models;
using VRLyrics.App.Services;
using VRLyrics.Core.Models;
using VRLyrics.Overlay.Rendering;

namespace VRLyrics.App;

public partial class MainWindow : Window
{
    private readonly VRLyricsRuntimeHost _runtime;
    private readonly Action _onMinimizeToTray;
    private readonly Action _onRequestExit;
    private readonly DispatcherTimer _statusTimer;

    private WriteableBitmap? _previewBitmap;
    private byte[]? _bgraBuffer;
    private long _lastPreviewTickMs;
    private int _previewUpdateInFlight;
    private volatile bool _isPreviewActive = true;
    private bool _suppressUiEvents;
    private bool _allowClose;

    public MainWindow(VRLyricsRuntimeHost runtime, Action onMinimizeToTray, Action onRequestExit)
    {
        _runtime = runtime;
        _onMinimizeToTray = onMinimizeToTray;
        _onRequestExit = onRequestExit;

        InitializeComponent();
        IsVisibleChanged += (_, _) => _isPreviewActive = IsVisible && WindowState != WindowState.Minimized;
        StateChanged += (_, _) => _isPreviewActive = IsVisible && WindowState != WindowState.Minimized;
        PopulateFontFamilies();
        BuildPresetThemeButtons();
        LoadSettingsIntoControls(_runtime.Settings.Current);

        TxtSettingsFilePath.Text = $"配置文件路径：{_runtime.Settings.SettingsPath}";

        _runtime.Settings.SettingsChanged += OnExternalSettingsChanged;
        _runtime.Overlay.FrameRendered += OnOverlayFrameRendered;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();
        UpdateStatusBar();
    }

    public void AllowPermanentClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            _onMinimizeToTray();
            return;
        }

        _statusTimer.Stop();
        _runtime.Settings.SettingsChanged -= OnExternalSettingsChanged;
        _runtime.Overlay.FrameRendered -= OnOverlayFrameRendered;
        base.OnClosing(e);
    }

    private void PopulateFontFamilies()
    {
        var preferred = new[]
        {
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "DengXian",
            "SimHei",
            "KaiTi",
            "SimSun",
            "FangSong",
            "Segoe UI",
            "Arial"
        };

        var installed = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ordered = preferred
            .Concat(installed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        CmbFontFamily.ItemsSource = ordered;
    }

    private void BuildPresetThemeButtons()
    {
        PresetButtonsPanel.Children.Clear();
        foreach (var preset in ColorThemePreset.All)
        {
            var btn = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("ActionButtonStyle"),
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = preset.Description,
                Tag = preset,
            };

            var stack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var dot = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 7, 0),
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(1),
                Background = CreateGradientPreviewBrush(preset.SungTopColorHex, preset.SungBottomColorHex)
            };
            stack.Children.Add(dot);
            stack.Children.Add(new TextBlock { Text = preset.Name, VerticalAlignment = VerticalAlignment.Center });
            btn.Content = stack;
            btn.Click += OnPresetThemeButtonClick;
            PresetButtonsPanel.Children.Add(btn);
        }
    }

    private void OnPresetThemeButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: ColorThemePreset preset })
        {
            var updated = _runtime.Settings.Update(s => preset.ApplyTo(s));
            LoadSettingsIntoControls(updated);
        }
    }

    private void OnExternalSettingsChanged(LyricsOverlaySettings settings)
    {
        Dispatcher.BeginInvoke(() => LoadSettingsIntoControls(settings));
    }

    private void LoadSettingsIntoControls(LyricsOverlaySettings s)
    {
        _suppressUiEvents = true;
        try
        {
            ChkOverlayEnabled.IsChecked = s.Enabled;
            ChkDemoLoop.IsChecked = _runtime.ForceDemoLoop;

            RbModeHead.IsChecked = s.AttachmentMode == OverlayAttachmentMode.HeadLocked;
            RbModeWorld.IsChecked = s.AttachmentMode == OverlayAttachmentMode.WorldLocked;
            RbModeLeftHand.IsChecked = s.AttachmentMode == OverlayAttachmentMode.LeftHand;
            RbModeRightHand.IsChecked = s.AttachmentMode == OverlayAttachmentMode.RightHand;

            UpdatePlacementPanelsVisibility(s.AttachmentMode);

            SldWidthMeters.Value = s.WidthInMeters;
            LblWidthMeters.Text = $"{s.WidthInMeters:0.00} m";

            SldOpacity.Value = s.Opacity;
            LblOpacity.Text = $"{s.Opacity * 100:0}%";

            SldCurvature.Value = s.Curvature;
            LblCurvature.Text = $"{s.Curvature * 100:0}%";

            SldHeadX.Value = s.HeadOffsetX;
            LblHeadX.Text = $"{s.HeadOffsetX:+0.00;-0.00;0.00} m";
            SldHeadY.Value = s.HeadOffsetY;
            LblHeadY.Text = $"{s.HeadOffsetY:+0.00;-0.00;0.00} m";
            SldHeadZ.Value = s.HeadOffsetZ;
            LblHeadZ.Text = $"{s.HeadOffsetZ:0.00} m";
            SldHeadPitch.Value = s.HeadPitchDegrees;
            LblHeadPitch.Text = $"{s.HeadPitchDegrees:+0.0;-0.0;0.0}°";
            SldHeadYaw.Value = s.HeadYawDegrees;
            LblHeadYaw.Text = $"{s.HeadYawDegrees:+0.0;-0.0;0.0}°";

            ChkWorldControllerGrab.IsChecked = s.EnableWorldControllerGrab;
            ChkWorldAutoFaceHmd.IsChecked = s.WorldAutoFaceHmd;

            SldWorldX.Value = s.WorldPositionX;
            LblWorldX.Text = $"{s.WorldPositionX:+0.00;-0.00;0.00} m";
            SldWorldY.Value = s.WorldPositionY;
            LblWorldY.Text = $"{s.WorldPositionY:0.00} m";
            SldWorldZ.Value = s.WorldPositionZ;
            LblWorldZ.Text = $"{s.WorldPositionZ:0.00} m";
            SldWorldYaw.Value = s.WorldYawDegrees;
            LblWorldYaw.Text = $"{s.WorldYawDegrees:+0.0;-0.0;0.0}°";
            SldWorldPitch.Value = s.WorldPitchDegrees;
            LblWorldPitch.Text = $"{s.WorldPitchDegrees:+0.0;-0.0;0.0}°";

            SldHandX.Value = s.HandOffsetX;
            LblHandX.Text = $"{s.HandOffsetX:+0.00;-0.00;0.00} m";
            SldHandY.Value = s.HandOffsetY;
            LblHandY.Text = $"{s.HandOffsetY:+0.00;-0.00;0.00} m";
            SldHandZ.Value = s.HandOffsetZ;
            LblHandZ.Text = $"{s.HandOffsetZ:+0.00;-0.00;0.00} m";
            SldHandPitch.Value = s.HandPitchDegrees;
            LblHandPitch.Text = $"{s.HandPitchDegrees:+0.0;-0.0;0.0}°";
            SldHandYaw.Value = s.HandYawDegrees;
            LblHandYaw.Text = $"{s.HandYawDegrees:+0.0;-0.0;0.0}°";
            SldHandRoll.Value = s.HandRollDegrees;
            LblHandRoll.Text = $"{s.HandRollDegrees:+0.0;-0.0;0.0}°";

            CmbFontFamily.Text = s.FontFamily;
            ChkBold.IsChecked = s.Bold;

            SldMainFontSize.Value = s.MainFontSize;
            LblMainFontSize.Text = $"{s.MainFontSize:0} px";
            SldTransFontSize.Value = s.TranslationFontSize;
            LblTransFontSize.Text = $"{s.TranslationFontSize:0} px";
            SldContextFontSize.Value = s.ContextFontSize;
            LblContextFontSize.Text = $"{s.ContextFontSize:0} px";

            ChkShowTranslation.IsChecked = s.ShowTranslation;
            ChkShowContextLines.IsChecked = s.ShowContextLines;
            ChkShowTrackHeader.IsChecked = s.ShowTrackHeader;

            ChkKaraokeHighlight.IsChecked = s.EnableKaraokeHighlight;
            ChkWordLift.IsChecked = s.EnableWordLift;
            ChkLineTransition.IsChecked = s.EnableLineTransitionAnimation;

            SldTransitionMs.Value = s.TransitionDurationMs;
            LblTransitionMs.Text = $"{s.TransitionDurationMs} ms";
            SldStrokeWidth.Value = s.StrokeWidth;
            LblStrokeWidth.Text = $"{s.StrokeWidth:0.0} px";
            SldShadowBlur.Value = s.ShadowBlur;
            LblShadowBlur.Text = $"{s.ShadowBlur:0.0} px";
            SldCornerRadius.Value = s.BackgroundCornerRadius;
            LblCornerRadius.Text = $"{s.BackgroundCornerRadius:0} px";

            SetColorControl(BtnColorSungTop, TxtColorSungTop, s.SungTopColorHex);
            SetColorControl(BtnColorSungBottom, TxtColorSungBottom, s.SungBottomColorHex);
            SetColorControl(BtnColorSungGlow, TxtColorSungGlow, s.SungGlowColorHex);
            SetColorControl(BtnColorUnsungTop, TxtColorUnsungTop, s.UnsungTopColorHex);
            SetColorControl(BtnColorUnsungBottom, TxtColorUnsungBottom, s.UnsungBottomColorHex);
            SetColorControl(BtnColorTranslation, TxtColorTranslation, s.TranslationColorHex);
            SetColorControl(BtnColorContext, TxtColorContext, s.ContextColorHex);
            SetColorControl(BtnColorStroke, TxtColorStroke, s.StrokeColorHex);
            SetColorControl(BtnColorBackground, TxtColorBackground, s.BackgroundColorHex);

            SldGlobalOffset.Value = s.GlobalOffsetMs;
            LblGlobalOffset.Text = $"{s.GlobalOffsetMs:+0;-0;0} ms ({s.GlobalOffsetMs / 1000.0:+0.00;-0.00;0.00}s)";

            UpdateTrackOffsetControls(s);

            var pref = s.PreferredLyricsProvider ?? "Auto";
            RbSourceNetEase.IsChecked = string.Equals(pref, "NetEase", StringComparison.OrdinalIgnoreCase);
            RbSourceQqMusic.IsChecked = string.Equals(pref, "QQMusic", StringComparison.OrdinalIgnoreCase);
            RbSourceLrclib.IsChecked = string.Equals(pref, "LRCLIB", StringComparison.OrdinalIgnoreCase);
            RbSourceLocal.IsChecked = string.Equals(pref, "LocalLRC", StringComparison.OrdinalIgnoreCase);
            RbSourceAuto.IsChecked = RbSourceNetEase.IsChecked != true
                                     && RbSourceQqMusic.IsChecked != true
                                     && RbSourceLrclib.IsChecked != true
                                     && RbSourceLocal.IsChecked != true;

            ChkAutoStartWithSteamVr.IsChecked = s.AutoStartWithSteamVr;
            ChkExitWithSteamVr.IsChecked = s.ExitWithSteamVr;
            UpdateManifestStatusText();

            LstLocalFolders.ItemsSource = s.LocalLyricsFolders.ToList();
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void UpdateStatusBar()
    {
        var vrConnected = _runtime.Overlay.IsVrConnected;
        var vrMsg = _runtime.Overlay.VrStatusMessage;
        VrStatusText.Text = vrConnected ? $"● {vrMsg}" : $"○ {vrMsg}";
        VrStatusBadge.ToolTip = vrMsg;
        VrStatusBadge.Background = new SolidColorBrush(vrConnected
            ? System.Windows.Media.Color.FromRgb(14, 52, 46)
            : System.Windows.Media.Color.FromRgb(42, 34, 20));
        VrStatusBadge.BorderBrush = new SolidColorBrush(vrConnected
            ? System.Windows.Media.Color.FromRgb(16, 185, 129)
            : System.Windows.Media.Color.FromRgb(217, 119, 6));
        VrStatusText.Foreground = new SolidColorBrush(vrConnected
            ? System.Windows.Media.Color.FromRgb(110, 231, 183)
            : System.Windows.Media.Color.FromRgb(252, 211, 77));

        var app = _runtime.Media.CurrentAppId;
        var track = _runtime.Media.CurrentTrack;
        var pos = _runtime.Media.Position;
        var dur = track?.Duration;
        var stateIcon = _runtime.Media.IsPlaying ? "▶" : "⏸";

        PlayerStatusText.Text = string.IsNullOrEmpty(app)
            ? "播放器：未检测"
            : $"播放器：{app}  {stateIcon} {FormatTime(pos)} / {FormatTime(dur)}";

        TrackAndLyricsStatusText.Text = track is null
            ? _runtime.StatusMessage
            : $"正在跟随：{track}   |   {_runtime.StatusMessage}";

        UpdateTrackOffsetControls(_runtime.Settings.Current);
        UpdateManifestStatusText();
    }

    private void UpdateTrackOffsetControls(LyricsOverlaySettings s)
    {
        var track = _runtime.Media.CurrentTrack;
        var trackOffsetMs = s.GetTrackOffsetMs(track);
        var prevSuppress = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            SldTrackOffset.IsEnabled = track is not null;
            SldTrackOffset.Value = trackOffsetMs;
            LblTrackOffset.Text = $"{trackOffsetMs:+0;-0;0} ms ({trackOffsetMs / 1000.0:+0.00;-0.00;0.00}s)";
            LblTrackOffsetTarget.Text = track is null
                ? "当前曲目：未在播放（播放歌曲时可单独微调并自动记忆）"
                : $"当前曲目：{track}（已记忆 {s.PerTrackOffsetsMs?.Count ?? 0} 首歌）";
        }
        finally
        {
            _suppressUiEvents = prevSuppress;
        }
    }

    private void UpdateManifestStatusText()
    {
        var st = _runtime.Overlay.LastManifestStatus;
        if (st is null)
        {
            TxtManifestStatus.Text = "清单状态：初始化中…";
            return;
        }
        TxtManifestStatus.Text = $"状态：{st.StatusMessage}\n清单文件：{st.ManifestPath}";
    }

    private void OnOverlayFrameRendered(LyricsRenderFrame frame, SKBitmap bitmap)
    {
        if (!_isPreviewActive)
            return;

        var nowTick = Environment.TickCount64;
        if (nowTick - _lastPreviewTickMs < 32) // ~30 FPS desktop preview cap
            return;

        if (Interlocked.CompareExchange(ref _previewUpdateInFlight, 1, 0) != 0)
            return;

        _lastPreviewTickMs = nowTick;

        try
        {
            var width = bitmap.Width;
            var height = bitmap.Height;
            var byteCount = width * height * 4;

            if (_bgraBuffer is null || _bgraBuffer.Length != byteCount)
                _bgraBuffer = new byte[byteCount];

            // Convert RGBA8888 (Premul) -> BGRA8888 (Pbgra32) for WPF WriteableBitmap
            Marshal.Copy(bitmap.GetPixels(), _bgraBuffer, 0, byteCount);
            var pixels = MemoryMarshal.Cast<byte, uint>(_bgraBuffer.AsSpan());
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                pixels[i] = (p & 0xFF00FF00u) | ((p & 0x00FF0000u) >> 16) | ((p & 0x000000FFu) << 16);
            }

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_previewBitmap is null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
                    {
                        _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
                        PreviewImage.Source = _previewBitmap;
                    }

                    _previewBitmap.WritePixels(new Int32Rect(0, 0, width, height), _bgraBuffer, width * 4, 0);
                }
                finally
                {
                    Interlocked.Exchange(ref _previewUpdateInFlight, 0);
                }
            }, DispatcherPriority.Render);
        }
        catch
        {
            Interlocked.Exchange(ref _previewUpdateInFlight, 0);
        }
    }

    // ==================== EVENT HANDLERS ====================

    private void OnOverlayEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        var enabled = ChkOverlayEnabled.IsChecked == true;
        _runtime.Settings.Update(s => s with { Enabled = enabled });
    }

    private void OnDemoLoopChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        _runtime.ForceDemoLoop = ChkDemoLoop.IsChecked == true;
    }

    private void OnReloadLyricsClick(object sender, RoutedEventArgs e) =>
        _runtime.ReloadCurrentLyrics(bypassCache: true);

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e)
    {
        Hide();
        _onMinimizeToTray();
    }

    private void OnExitAppClick(object sender, RoutedEventArgs e) =>
        _onRequestExit();

    private void OnAttachmentModeRadioChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        var mode = RbModeWorld.IsChecked == true ? OverlayAttachmentMode.WorldLocked
            : RbModeLeftHand.IsChecked == true ? OverlayAttachmentMode.LeftHand
            : RbModeRightHand.IsChecked == true ? OverlayAttachmentMode.RightHand
            : OverlayAttachmentMode.HeadLocked;

        var updated = _runtime.SwitchAttachmentMode(mode);
        LoadSettingsIntoControls(updated);
    }

    private void UpdatePlacementPanelsVisibility(OverlayAttachmentMode mode)
    {
        PanelHeadMode.Visibility = mode == OverlayAttachmentMode.HeadLocked ? Visibility.Visible : Visibility.Collapsed;
        PanelWorldMode.Visibility = mode == OverlayAttachmentMode.WorldLocked ? Visibility.Visible : Visibility.Collapsed;
        PanelHandMode.Visibility = mode is OverlayAttachmentMode.LeftHand or OverlayAttachmentMode.RightHand
            ? Visibility.Visible
            : Visibility.Collapsed;

        LblHandModeTitle.Text = mode == OverlayAttachmentMode.LeftHand
            ? "⌚ 左手腕 / 左手柄跟随位置与角度"
            : "⌚ 右手腕 / 右手柄跟随位置与角度";
    }

    private void OnPlacementSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !IsLoaded) return;

        var width = (float)Math.Round(SldWidthMeters.Value, 2);
        var opacity = (float)Math.Round(SldOpacity.Value, 2);
        var curvature = (float)Math.Round(SldCurvature.Value, 2);

        var hx = (float)Math.Round(SldHeadX.Value, 2);
        var hy = (float)Math.Round(SldHeadY.Value, 2);
        var hz = (float)Math.Round(SldHeadZ.Value, 2);
        var hp = (float)Math.Round(SldHeadPitch.Value, 1);
        var hyaw = (float)Math.Round(SldHeadYaw.Value, 1);

        var wx = (float)Math.Round(SldWorldX.Value, 2);
        var wy = (float)Math.Round(SldWorldY.Value, 2);
        var wz = (float)Math.Round(SldWorldZ.Value, 2);
        var wyaw = (float)Math.Round(SldWorldYaw.Value, 1);
        var wpitch = (float)Math.Round(SldWorldPitch.Value, 1);

        var handX = (float)Math.Round(SldHandX.Value, 2);
        var handY = (float)Math.Round(SldHandY.Value, 2);
        var handZ = (float)Math.Round(SldHandZ.Value, 2);
        var handP = (float)Math.Round(SldHandPitch.Value, 1);
        var handYaw = (float)Math.Round(SldHandYaw.Value, 1);
        var handRoll = (float)Math.Round(SldHandRoll.Value, 1);

        LblWidthMeters.Text = $"{width:0.00} m";
        LblOpacity.Text = $"{opacity * 100:0}%";
        LblCurvature.Text = $"{curvature * 100:0}%";

        LblHeadX.Text = $"{hx:+0.00;-0.00;0.00} m";
        LblHeadY.Text = $"{hy:+0.00;-0.00;0.00} m";
        LblHeadZ.Text = $"{hz:0.00} m";
        LblHeadPitch.Text = $"{hp:+0.0;-0.0;0.0}°";
        LblHeadYaw.Text = $"{hyaw:+0.0;-0.0;0.0}°";

        LblWorldX.Text = $"{wx:+0.00;-0.00;0.00} m";
        LblWorldY.Text = $"{wy:0.00} m";
        LblWorldZ.Text = $"{wz:0.00} m";
        LblWorldYaw.Text = $"{wyaw:+0.0;-0.0;0.0}°";
        LblWorldPitch.Text = $"{wpitch:+0.0;-0.0;0.0}°";

        LblHandX.Text = $"{handX:+0.00;-0.00;0.00} m";
        LblHandY.Text = $"{handY:+0.00;-0.00;0.00} m";
        LblHandZ.Text = $"{handZ:+0.00;-0.00;0.00} m";
        LblHandPitch.Text = $"{handP:+0.0;-0.0;0.0}°";
        LblHandYaw.Text = $"{handYaw:+0.0;-0.0;0.0}°";
        LblHandRoll.Text = $"{handRoll:+0.0;-0.0;0.0}°";

        _runtime.Settings.Update(s => s with
        {
            WidthInMeters = width,
            Opacity = opacity,
            Curvature = curvature,
            HeadOffsetX = hx,
            HeadOffsetY = hy,
            HeadOffsetZ = hz,
            HeadPitchDegrees = hp,
            HeadYawDegrees = hyaw,
            WorldPositionX = wx,
            WorldPositionY = wy,
            WorldPositionZ = wz,
            WorldYawDegrees = wyaw,
            WorldPitchDegrees = wpitch,
            HandOffsetX = handX,
            HandOffsetY = handY,
            HandOffsetZ = handZ,
            HandPitchDegrees = handP,
            HandYawDegrees = handYaw,
            HandRollDegrees = handRoll,
        });
    }

    private void OnHeadPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag }) return;
        var updated = tag switch
        {
            "Top" => _runtime.Settings.Update(s => s with
            {
                HeadOffsetX = 0.0f,
                HeadOffsetY = 0.25f,
                HeadOffsetZ = -1.15f,
                HeadPitchDegrees = -12.0f,
                HeadYawDegrees = 0.0f,
            }),
            "BottomLeft" => _runtime.Settings.Update(s => s with
            {
                HeadOffsetX = -0.32f,
                HeadOffsetY = -0.22f,
                HeadOffsetZ = -1.15f,
                HeadPitchDegrees = 10.0f,
                HeadYawDegrees = 15.0f,
            }),
            "BottomRight" => _runtime.Settings.Update(s => s with
            {
                HeadOffsetX = 0.32f,
                HeadOffsetY = -0.22f,
                HeadOffsetZ = -1.15f,
                HeadPitchDegrees = 10.0f,
                HeadYawDegrees = -15.0f,
            }),
            _ => _runtime.Settings.Update(s => s with
            {
                HeadOffsetX = 0.0f,
                HeadOffsetY = -0.24f,
                HeadOffsetZ = -1.15f,
                HeadPitchDegrees = 12.0f,
                HeadYawDegrees = 0.0f,
            })
        };
        LoadSettingsIntoControls(updated);
    }

    private void OnRecenterWorldToHmdClick(object sender, RoutedEventArgs e)
    {
        var updated = _runtime.RecenterWorldOverlayToHmd();
        LoadSettingsIntoControls(updated);
    }

    private void OnWorldGrabCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        _runtime.Settings.Update(s => s with
        {
            EnableWorldControllerGrab = ChkWorldControllerGrab.IsChecked == true,
            WorldAutoFaceHmd = ChkWorldAutoFaceHmd.IsChecked == true,
        });
    }

    private void OnHandPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag }) return;
        var isRight = _runtime.Settings.Current.AttachmentMode == OverlayAttachmentMode.RightHand;
        var sign = isRight ? -1f : 1f;

        var updated = tag switch
        {
            "InnerWrist" => _runtime.Settings.Update(s => s with
            {
                WidthInMeters = Math.Min(s.WidthInMeters, 0.45f),
                HandOffsetX = 0.06f * sign,
                HandOffsetY = -0.02f,
                HandOffsetZ = 0.09f,
                HandPitchDegrees = -25.0f,
                HandYawDegrees = 80.0f * sign,
                HandRollDegrees = -65.0f * sign,
            }),
            "OuterWrist" => _runtime.Settings.Update(s => s with
            {
                WidthInMeters = Math.Min(s.WidthInMeters, 0.50f),
                HandOffsetX = -0.05f * sign,
                HandOffsetY = 0.04f,
                HandOffsetZ = 0.08f,
                HandPitchDegrees = -40.0f,
                HandYawDegrees = -20.0f * sign,
                HandRollDegrees = 35.0f * sign,
            }),
            _ => _runtime.Settings.Update(s => s with
            {
                HandOffsetX = 0.0f,
                HandOffsetY = 0.10f,
                HandOffsetZ = -0.05f,
                HandPitchDegrees = -45.0f,
                HandYawDegrees = 0.0f,
                HandRollDegrees = 0.0f,
            })
        };
        LoadSettingsIntoControls(updated);
    }

    private void OnResetPlacementClick(object sender, RoutedEventArgs e)
    {
        var def = new LyricsOverlaySettings();
        var updated = _runtime.Settings.Update(s => s with
        {
            WidthInMeters = def.WidthInMeters,
            Opacity = def.Opacity,
            Curvature = def.Curvature,
            HeadOffsetX = def.HeadOffsetX,
            HeadOffsetY = def.HeadOffsetY,
            HeadOffsetZ = def.HeadOffsetZ,
            HeadPitchDegrees = def.HeadPitchDegrees,
            HeadYawDegrees = def.HeadYawDegrees,
            WorldPositionX = def.WorldPositionX,
            WorldPositionY = def.WorldPositionY,
            WorldPositionZ = def.WorldPositionZ,
            WorldYawDegrees = def.WorldYawDegrees,
            WorldPitchDegrees = def.WorldPitchDegrees,
            EnableWorldControllerGrab = def.EnableWorldControllerGrab,
            WorldAutoFaceHmd = def.WorldAutoFaceHmd,
            HandOffsetX = def.HandOffsetX,
            HandOffsetY = def.HandOffsetY,
            HandOffsetZ = def.HandOffsetZ,
            HandPitchDegrees = def.HandPitchDegrees,
            HandYawDegrees = def.HandYawDegrees,
            HandRollDegrees = def.HandRollDegrees,
        });
        LoadSettingsIntoControls(updated);
    }

    private void OnFontFamilySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        if (CmbFontFamily.SelectedItem is string font && !string.IsNullOrWhiteSpace(font))
            _runtime.Settings.Update(s => s with { FontFamily = font.Trim() });
    }

    private void OnFontFamilyLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        var font = CmbFontFamily.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(font))
            _runtime.Settings.Update(s => s with { FontFamily = font });
    }

    private void OnTypographyCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        _runtime.Settings.Update(s => s with
        {
            Bold = ChkBold.IsChecked == true,
            ShowTranslation = ChkShowTranslation.IsChecked == true,
            ShowContextLines = ChkShowContextLines.IsChecked == true,
            ShowTrackHeader = ChkShowTrackHeader.IsChecked == true,
        });
    }

    private void OnTypographySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        var mainSz = (float)Math.Round(SldMainFontSize.Value);
        var transSz = (float)Math.Round(SldTransFontSize.Value);
        var ctxSz = (float)Math.Round(SldContextFontSize.Value);

        LblMainFontSize.Text = $"{mainSz:0} px";
        LblTransFontSize.Text = $"{transSz:0} px";
        LblContextFontSize.Text = $"{ctxSz:0} px";

        _runtime.Settings.Update(s => s with
        {
            MainFontSize = mainSz,
            TranslationFontSize = transSz,
            ContextFontSize = ctxSz,
        });
    }

    private void OnAnimationCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        _runtime.Settings.Update(s => s with
        {
            EnableKaraokeHighlight = ChkKaraokeHighlight.IsChecked == true,
            EnableWordLift = ChkWordLift.IsChecked == true,
            EnableLineTransitionAnimation = ChkLineTransition.IsChecked == true,
        });
    }

    private void OnEffectsSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        var transMs = (int)Math.Round(SldTransitionMs.Value);
        var strokeW = (float)Math.Round(SldStrokeWidth.Value, 1);
        var shadowB = (float)Math.Round(SldShadowBlur.Value, 1);
        var radius = (float)Math.Round(SldCornerRadius.Value);

        LblTransitionMs.Text = $"{transMs} ms";
        LblStrokeWidth.Text = $"{strokeW:0.0} px";
        LblShadowBlur.Text = $"{shadowB:0.0} px";
        LblCornerRadius.Text = $"{radius:0} px";

        _runtime.Settings.Update(s => s with
        {
            TransitionDurationMs = transMs,
            StrokeWidth = strokeW,
            ShadowBlur = shadowB,
            BackgroundCornerRadius = radius,
        });
    }

    private void OnColorSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string key }) return;
        var currentHex = GetColorHexByKey(_runtime.Settings.Current, key);
        var skCurrent = ColorParseHelper.Parse(currentHex, SKColors.White);

        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(skCurrent.Red, skCurrent.Green, skCurrent.Blue)
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            // Preserve existing alpha byte
            var newHex = $"#{skCurrent.Alpha:X2}{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            var updated = _runtime.Settings.Update(s => WithColorHexByKey(s, key, newHex));
            LoadSettingsIntoControls(updated);
        }
    }

    private void OnColorTextKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox tb)
            ApplyColorTextBox(tb);
    }

    private void OnColorTextLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
            ApplyColorTextBox(tb);
    }

    private void ApplyColorTextBox(System.Windows.Controls.TextBox tb)
    {
        if (_suppressUiEvents || tb.Tag is not string key) return;
        var raw = tb.Text?.Trim() ?? "";
        if (!raw.StartsWith('#')) raw = "#" + raw;
        if (raw.Length is not (7 or 9)) return;

        var updated = _runtime.Settings.Update(s => WithColorHexByKey(s, key, raw.ToUpperInvariant()));
        LoadSettingsIntoControls(updated);
    }

    private void OnGlobalOffsetSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        var offsetMs = (int)(Math.Round(SldGlobalOffset.Value / 10.0) * 10);
        LblGlobalOffset.Text = $"{offsetMs:+0;-0;0} ms ({offsetMs / 1000.0:+0.00;-0.00;0.00}s)";
        _runtime.Settings.Update(s => s with { GlobalOffsetMs = offsetMs });
    }

    private void OnOffsetStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string deltaStr } &&
            int.TryParse(deltaStr, CultureInfo.InvariantCulture, out var delta))
        {
            var updated = _runtime.Settings.Update(s =>
                s with { GlobalOffsetMs = Math.Clamp(s.GlobalOffsetMs + delta, -5000, 5000) });
            LoadSettingsIntoControls(updated);
        }
    }

    private void OnOffsetResetClick(object sender, RoutedEventArgs e)
    {
        var updated = _runtime.Settings.Update(s => s with { GlobalOffsetMs = 0 });
        LoadSettingsIntoControls(updated);
    }

    private void OnTrackOffsetSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        var offsetMs = (int)(Math.Round(SldTrackOffset.Value / 10.0) * 10);
        LblTrackOffset.Text = $"{offsetMs:+0;-0;0} ms ({offsetMs / 1000.0:+0.00;-0.00;0.00}s)";
        _runtime.SetCurrentTrackOffsetMs(offsetMs);
    }

    private void OnTrackOffsetStepClick(object sender, RoutedEventArgs e)
    {
        if (_runtime.Media.CurrentTrack is not { } track) return;
        if (sender is System.Windows.Controls.Button { Tag: string deltaStr } &&
            int.TryParse(deltaStr, CultureInfo.InvariantCulture, out var delta))
        {
            var cur = _runtime.Settings.Current.GetTrackOffsetMs(track);
            var updated = _runtime.SetCurrentTrackOffsetMs(cur + delta);
            LoadSettingsIntoControls(updated);
        }
    }

    private void OnTrackOffsetResetClick(object sender, RoutedEventArgs e)
    {
        var updated = _runtime.SetCurrentTrackOffsetMs(0);
        LoadSettingsIntoControls(updated);
    }

    private void OnLyricsSourceRadioChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        var provider = RbSourceNetEase.IsChecked == true ? "NetEase"
            : RbSourceQqMusic.IsChecked == true ? "QQMusic"
            : RbSourceLrclib.IsChecked == true ? "LRCLIB"
            : RbSourceLocal.IsChecked == true ? "LocalLRC"
            : "Auto";
        _runtime.SwitchLyricsProvider(provider);
    }

    private void OnSteamVrLifecycleCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        var autoStart = ChkAutoStartWithSteamVr.IsChecked == true;
        var exitWithVr = ChkExitWithSteamVr.IsChecked == true;
        _runtime.Settings.Update(s => s with
        {
            AutoStartWithSteamVr = autoStart,
            ExitWithSteamVr = exitWithVr,
        });
        _runtime.SetAutoStartWithSteamVr(autoStart);
        UpdateManifestStatusText();
    }

    private void OnRegisterManifestClick(object sender, RoutedEventArgs e)
    {
        var autoStart = ChkAutoStartWithSteamVr.IsChecked == true;
        _runtime.SetAutoStartWithSteamVr(autoStart);
        UpdateManifestStatusText();
    }

    private void OnUnregisterManifestClick(object sender, RoutedEventArgs e)
    {
        _runtime.UnregisterFromSteamVr();
        LoadSettingsIntoControls(_runtime.Settings.Current);
    }

    private void OnAddLocalFolderClick(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择包含本地 .lrc 歌词文件的文件夹",
            UseDescriptionForTitle = true,
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            var folder = dlg.SelectedPath.Trim();
            var updated = _runtime.Settings.Update(s =>
            {
                if (s.LocalLyricsFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    return s;
                return s with { LocalLyricsFolders = [.. s.LocalLyricsFolders, folder] };
            });
            LoadSettingsIntoControls(updated);
            _runtime.ReloadCurrentLyrics(bypassCache: false);
        }
    }

    private void OnRemoveLocalFolderClick(object sender, RoutedEventArgs e)
    {
        if (LstLocalFolders.SelectedItem is not string selected) return;
        var updated = _runtime.Settings.Update(s => s with
        {
            LocalLyricsFolders = s.LocalLyricsFolders
                .Where(f => !string.Equals(f, selected, StringComparison.OrdinalIgnoreCase))
                .ToList()
        });
        LoadSettingsIntoControls(updated);
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.GetDirectoryName(_runtime.Settings.SettingsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
    }

    private void OnResetAllSettingsClick(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            this,
            "确定要将所有字体、颜色、位置与偏移恢复为默认设置吗？",
            "恢复默认设置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            var reset = _runtime.Settings.ResetToDefaults();
            LoadSettingsIntoControls(reset);
        }
    }

    // ==================== HELPERS ====================

    private static void SetColorControl(System.Windows.Controls.Button swatch, System.Windows.Controls.TextBox textBox, string hex)
    {
        textBox.Text = hex;
        var sk = ColorParseHelper.Parse(hex, SKColors.White);
        swatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(sk.Alpha, sk.Red, sk.Green, sk.Blue));
        swatch.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
        swatch.BorderThickness = new Thickness(1);
    }

    private static System.Windows.Media.Brush CreateGradientPreviewBrush(string topHex, string bottomHex)
    {
        var top = ColorParseHelper.Parse(topHex, SKColors.Cyan);
        var bot = ColorParseHelper.Parse(bottomHex, SKColors.DeepSkyBlue);
        return new LinearGradientBrush(
            System.Windows.Media.Color.FromRgb(top.Red, top.Green, top.Blue),
            System.Windows.Media.Color.FromRgb(bot.Red, bot.Green, bot.Blue),
            90.0);
    }

    private static string GetColorHexByKey(LyricsOverlaySettings s, string key) => key switch
    {
        "SungTop" => s.SungTopColorHex,
        "SungBottom" => s.SungBottomColorHex,
        "SungGlow" => s.SungGlowColorHex,
        "UnsungTop" => s.UnsungTopColorHex,
        "UnsungBottom" => s.UnsungBottomColorHex,
        "Translation" => s.TranslationColorHex,
        "Context" => s.ContextColorHex,
        "Stroke" => s.StrokeColorHex,
        "Background" => s.BackgroundColorHex,
        _ => "#FFFFFFFF"
    };

    private static LyricsOverlaySettings WithColorHexByKey(LyricsOverlaySettings s, string key, string hex) => key switch
    {
        "SungTop" => s with { SungTopColorHex = hex },
        "SungBottom" => s with { SungBottomColorHex = hex },
        "SungGlow" => s with { SungGlowColorHex = hex },
        "UnsungTop" => s with { UnsungTopColorHex = hex },
        "UnsungBottom" => s with { UnsungBottomColorHex = hex },
        "Translation" => s with { TranslationColorHex = hex },
        "Context" => s with { ContextColorHex = hex },
        "Stroke" => s with { StrokeColorHex = hex },
        "Background" => s with { BackgroundColorHex = hex },
        _ => s
    };

    private static string FormatTime(TimeSpan? t) =>
        t is { } v ? $"{(int)v.TotalMinutes:00}:{v.Seconds:00}.{v.Milliseconds / 100}" : "--:--";
}
