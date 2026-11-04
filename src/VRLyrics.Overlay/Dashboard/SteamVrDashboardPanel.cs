using System.Runtime.InteropServices;
using Valve.VR;
using VRLyrics.Overlay.Direct3D;
using VRLyrics.Overlay.Rendering;

namespace VRLyrics.Overlay.Dashboard;

/// <summary>
/// Manages the SteamVR Dashboard tab overlay (<c>CreateDashboardOverlay</c>), polls laser-mouse input events,
/// and uploads the rendered <see cref="DashboardPanelRenderer"/> texture when the user opens the dashboard tab.
/// </summary>
public sealed class SteamVrDashboardPanel : IDisposable
{
    public const string DefaultDashboardKey = "vrlyrics.dashboard.panel";
    public const string DefaultDashboardName = "VRLyrics 歌词控制";

    private readonly DashboardPanelRenderer _renderer = new();
    private readonly D3D11TextureBridge _textureBridge = new();

    private ulong _dashboardHandle = Valve.VR.OpenVR.k_ulOverlayHandleInvalid;
    private ulong _thumbnailHandle = Valve.VR.OpenVR.k_ulOverlayHandleInvalid;
    private float? _cursorX;
    private float? _cursorY;
    private bool _isCursorPressed;
    private DashboardAction? _hoveredAction;
    private DashboardAction? _pressedAction;
    private bool _needsInitialRender = true;
    private DateTimeOffset _lastBackgroundSync = DateTimeOffset.MinValue;
    private bool _disposed;

    public bool IsCreated => _dashboardHandle != Valve.VR.OpenVR.k_ulOverlayHandleInvalid;

    public event Action<DashboardAction>? ActionTriggered;

    public bool Initialize(CVROverlay overlay, int adapterIndex = -1)
    {
        if (IsCreated) return true;

        var err = overlay.CreateDashboardOverlay(
            DefaultDashboardKey,
            DefaultDashboardName,
            ref _dashboardHandle,
            ref _thumbnailHandle);

        if (err != EVROverlayError.None || _dashboardHandle == Valve.VR.OpenVR.k_ulOverlayHandleInvalid)
        {
            _dashboardHandle = Valve.VR.OpenVR.k_ulOverlayHandleInvalid;
            _thumbnailHandle = Valve.VR.OpenVR.k_ulOverlayHandleInvalid;
            return false;
        }

        _textureBridge.InitializeDevice(adapterIndex);

        var bounds = new VRTextureBounds_t { uMin = 0f, vMin = 0f, uMax = 1f, vMax = 1f };
        overlay.SetOverlayTextureBounds(_dashboardHandle, ref bounds);
        overlay.SetOverlayWidthInMeters(_dashboardHandle, 2.15f);
        overlay.SetOverlayInputMethod(_dashboardHandle, VROverlayInputMethod.Mouse);

        var mouseScale = new HmdVector2_t
        {
            v0 = DashboardPanelRenderer.PanelWidth,
            v1 = DashboardPanelRenderer.PanelHeight
        };
        overlay.SetOverlayMouseScale(_dashboardHandle, ref mouseScale);

        try
        {
            overlay.SetOverlayFlag(_dashboardHandle, VROverlayFlags.EnableClickStabilization, true);
        }
        catch { }

        // Upload dashboard tab bar thumbnail icon
        if (_thumbnailHandle != Valve.VR.OpenVR.k_ulOverlayHandleInvalid)
        {
            try
            {
                var thumbDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VRLyrics");
                Directory.CreateDirectory(thumbDir);
                var thumbPath = Path.Combine(thumbDir, "dashboard_icon.png");
                File.WriteAllBytes(thumbPath, DashboardPanelRenderer.RenderThumbnailPng());
                overlay.SetOverlayFromFile(_thumbnailHandle, thumbPath);
            }
            catch { }
        }

        _needsInitialRender = true;
        return true;
    }

    public void UpdateAndRenderIfNeeded(
        CVROverlay overlay,
        LyricsRenderFrame currentFrame,
        bool isDemoLoopEnabled)
    {
        if (!IsCreated) return;

        var inputChanged = PollDashboardEvents(overlay, currentFrame, isDemoLoopEnabled);
        var isActiveTab = overlay.IsActiveDashboardOverlay(_dashboardHandle);
        var now = DateTimeOffset.UtcNow;

        // Render at full responsiveness while the VRLyrics dashboard tab is open in VR,
        // or once on startup / every 1.5s in the background so the thumbnail preview stays fresh.
        if (!isActiveTab && !_needsInitialRender && !inputChanged && (now - _lastBackgroundSync) < TimeSpan.FromMilliseconds(1500))
            return;

        _needsInitialRender = false;
        _lastBackgroundSync = now;

        var state = new DashboardRenderState
        {
            Frame = currentFrame,
            IsDemoLoopEnabled = isDemoLoopEnabled,
            CursorX = _cursorX,
            CursorY = _cursorY,
            IsCursorPressed = _isCursorPressed,
            HoveredAction = _hoveredAction,
        };

        var bmp = _renderer.Render(state);
        var nativeTex = _textureBridge.UploadBitmap(bmp);
        var tex = new Texture_t
        {
            handle = nativeTex,
            eType = ETextureType.DirectX,
            eColorSpace = EColorSpace.Auto,
        };
        overlay.SetOverlayTexture(_dashboardHandle, ref tex);
    }

    private bool PollDashboardEvents(
        CVROverlay overlay,
        LyricsRenderFrame currentFrame,
        bool isDemoLoopEnabled)
    {
        var changed = false;
        var eventSize = (uint)Marshal.SizeOf<VREvent_t>();
        var vrEvent = default(VREvent_t);

        while (overlay.PollNextOverlayEvent(_dashboardHandle, ref vrEvent, eventSize))
        {
            changed = true;
            var evtType = (EVREventType)vrEvent.eventType;
            switch (evtType)
            {
                case EVREventType.VREvent_MouseMove:
                {
                    var (cx, cy) = DashboardPanelRenderer.ConvertOpenVrMouseToCanvas(
                        vrEvent.data.mouse.x,
                        vrEvent.data.mouse.y);
                    _cursorX = cx;
                    _cursorY = cy;

                    var hit = _renderer.HitTest(cx, cy, currentFrame.Settings, isDemoLoopEnabled);
                    if (hit != _hoveredAction)
                    {
                        _hoveredAction = hit;
                        if (hit.HasValue)
                        {
                            try { overlay.TriggerLaserMouseHapticVibration(_dashboardHandle, 0.012f, 160f, 0.30f); }
                            catch { }
                        }
                    }
                    break;
                }

                case EVREventType.VREvent_MouseButtonDown:
                {
                    var (cx, cy) = DashboardPanelRenderer.ConvertOpenVrMouseToCanvas(
                        vrEvent.data.mouse.x,
                        vrEvent.data.mouse.y);
                    _cursorX = cx;
                    _cursorY = cy;
                    _isCursorPressed = true;
                    _hoveredAction = _renderer.HitTest(cx, cy, currentFrame.Settings, isDemoLoopEnabled);
                    _pressedAction = _hoveredAction;
                    break;
                }

                case EVREventType.VREvent_MouseButtonUp:
                {
                    var (cx, cy) = DashboardPanelRenderer.ConvertOpenVrMouseToCanvas(
                        vrEvent.data.mouse.x,
                        vrEvent.data.mouse.y);
                    _cursorX = cx;
                    _cursorY = cy;
                    _isCursorPressed = false;

                    var releasedHit = _renderer.HitTest(cx, cy, currentFrame.Settings, isDemoLoopEnabled);
                    var triggered = releasedHit ?? _pressedAction;
                    _pressedAction = null;

                    if (triggered is { } action)
                    {
                        try { overlay.TriggerLaserMouseHapticVibration(_dashboardHandle, 0.025f, 220f, 0.65f); }
                        catch { }
                        try { ActionTriggered?.Invoke(action); }
                        catch { }
                    }
                    break;
                }

                case EVREventType.VREvent_FocusLeave:
                case EVREventType.VREvent_OverlayHidden:
                    _cursorX = null;
                    _cursorY = null;
                    _isCursorPressed = false;
                    _hoveredAction = null;
                    _pressedAction = null;
                    break;
            }
        }

        return changed;
    }

    public void Reset(CVROverlay? overlay)
    {
        if (_dashboardHandle != Valve.VR.OpenVR.k_ulOverlayHandleInvalid)
        {
            try { overlay?.DestroyOverlay(_dashboardHandle); }
            catch { }
            _dashboardHandle = Valve.VR.OpenVR.k_ulOverlayHandleInvalid;
        }
        _thumbnailHandle = Valve.VR.OpenVR.k_ulOverlayHandleInvalid;
        _cursorX = null;
        _cursorY = null;
        _isCursorPressed = false;
        _hoveredAction = null;
        _pressedAction = null;
        _needsInitialRender = true;
        _textureBridge.Reset();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset(Valve.VR.OpenVR.Overlay);
        _textureBridge.Dispose();
        _renderer.Dispose();
    }
}
