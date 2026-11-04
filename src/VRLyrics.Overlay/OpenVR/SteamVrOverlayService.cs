using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Valve.VR;
using VRLyrics.Core.Models;
using VRLyrics.Overlay.Dashboard;
using VRLyrics.Overlay.Direct3D;
using VRLyrics.Overlay.Rendering;

namespace VRLyrics.Overlay.OpenVR;

public enum VrServerProcessStatus
{
    NotRunning,
    Accessible,
    AccessDeniedNeedsElevation
}

/// <summary>
/// Manages the lifecycle of the SteamVR overlay, uploads SkiaSharp frames to it via D3D11 textures,
/// and handles M5 spatial placement (head-locked, wrist-mounted, world-locked with controller grab-and-drag).
/// </summary>
public sealed class SteamVrOverlayService : IDisposable
{
    public const string DefaultOverlayKey = "vrlyrics.overlay.main";
    public const string DefaultOverlayName = "VRLyrics";

    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const int ERROR_ACCESS_DENIED = 5;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private readonly D3D11TextureBridge _textureBridge = new();
    private readonly WorldOverlayGrabInteractor _grabInteractor = new();
    private readonly SteamVrDashboardPanel _dashboardPanel = new();
    private readonly TrackedDevicePose_t[] _trackedPoses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];

    private ulong _overlayHandle = Valve.VR.OpenVR.k_ulOverlayHandleInvalid;
    private bool _vrInitialized;
    private bool? _lastSyncedAutoStart;
    private bool _disposed;

    public bool IsConnected => _vrInitialized && _overlayHandle != Valve.VR.OpenVR.k_ulOverlayHandleInvalid;

    public string LastStatusMessage { get; private set; } = "未连接 SteamVR";

    public SteamVrManifestStatus? LastManifestStatus { get; private set; }

    public bool IsWorldGrabHovered => _grabInteractor.IsHovered;

    public bool IsWorldGrabActive => _grabInteractor.IsGrabbing;

    /// <summary>Raised when SteamVR requests the overlay process to quit (<c>VREvent_Quit</c>).</summary>
    public event Action? QuitRequested;

    /// <summary>
    /// Raised when <c>vrserver.exe</c> is running as Administrator while <c>VRLyrics.exe</c> is running
    /// without Administrator privileges, which would otherwise fail OpenVR IPC with <c>ERROR_ACCESS_DENIED (5)</c>.
    /// </summary>
    public event Action? ElevationRequired;

    /// <summary>
    /// Raised when the user finishes dragging the world-locked overlay with a VR controller
    /// so the updated 3D pose can be persisted to <c>settings.json</c>.
    /// </summary>
    public event Action<WorldOverlayPose>? WorldPoseCommitted;

    /// <summary>Raised when the user clicks a button on the in-VR SteamVR Dashboard control panel.</summary>
    public event Action<DashboardAction>? DashboardActionTriggered
    {
        add => _dashboardPanel.ActionTriggered += value;
        remove => _dashboardPanel.ActionTriggered -= value;
    }

    /// <summary>
    /// Checks whether <c>vrserver.exe</c> is currently running and whether the current process has sufficient
    /// privileges to open its handle (<c>PROCESS_DUP_HANDLE</c>) as required by <c>openvr_api.dll</c>.
    /// </summary>
    public static VrServerProcessStatus CheckVrServerProcessAccess()
    {
        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName("vrserver");
        }
        catch
        {
            return VrServerProcessStatus.NotRunning;
        }

        if (procs.Length == 0)
            return VrServerProcessStatus.NotRunning;

        var sawAccessDenied = false;
        foreach (var proc in procs)
        {
            try
            {
                var hProc = OpenProcess(PROCESS_DUP_HANDLE, bInheritHandle: false, (uint)proc.Id);
                if (hProc != IntPtr.Zero)
                {
                    CloseHandle(hProc);
                    return VrServerProcessStatus.Accessible;
                }

                if (Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
                    sawAccessDenied = true;
            }
            catch { }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }

        return sawAccessDenied
            ? VrServerProcessStatus.AccessDeniedNeedsElevation
            : VrServerProcessStatus.NotRunning;
    }

    /// <summary>
    /// Attempts to initialize OpenVR in overlay mode and create the main lyrics overlay.
    /// Returns false if SteamVR is not running yet.
    /// </summary>
    public bool TryConnect(string overlayKey = DefaultOverlayKey, string overlayName = DefaultOverlayName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected) return true;

        var serverStatus = CheckVrServerProcessAccess();
        if (serverStatus == VrServerProcessStatus.NotRunning)
        {
            LastStatusMessage = "未连接 SteamVR（等待 SteamVR 启动…）";
            return false;
        }

        if (serverStatus == VrServerProcessStatus.AccessDeniedNeedsElevation)
        {
            LastStatusMessage = "SteamVR 正以管理员权限运行，正在请求管理员权限连接…";
            try { ElevationRequired?.Invoke(); } catch { }
            return false;
        }

        try
        {
            var initError = EVRInitError.None;
            var system = Valve.VR.OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Overlay);
            if (initError != EVRInitError.None || system is null)
            {
                LastStatusMessage = $"SteamVR 未就绪: {Valve.VR.OpenVR.GetStringForHmdError(initError)} ({initError})";
                return false;
            }
            _vrInitialized = true;

            var overlay = Valve.VR.OpenVR.Overlay;
            if (overlay is null)
            {
                LastStatusMessage = "IVROverlay 接口不可用";
                ShutdownOpenVr();
                return false;
            }

            var err = overlay.CreateOverlay(overlayKey, overlayName, ref _overlayHandle);
            if (err != EVROverlayError.None)
            {
                LastStatusMessage = $"创建叠加层失败: {err}";
                ShutdownOpenVr();
                return false;
            }

            // Explicit DirectX UV bounds (top-left = 0,0, bottom-right = 1,1).
            var bounds = new VRTextureBounds_t { uMin = 0f, vMin = 0f, uMax = 1f, vMax = 1f };
            overlay.SetOverlayTextureBounds(_overlayHandle, ref bounds);
            overlay.SetOverlaySortOrder(_overlayHandle, 100);

            var adapterIndex = -1;
            system.GetDXGIOutputInfo(ref adapterIndex);
            _textureBridge.InitializeDevice(adapterIndex);
            overlay.ShowOverlay(_overlayHandle);

            _dashboardPanel.Initialize(overlay, adapterIndex);

            LastStatusMessage = "已连接 SteamVR 叠加层与仪表盘";
            return true;
        }
        catch (Exception ex)
        {
            LastStatusMessage = $"连接 SteamVR 异常: {ex.Message}";
            ShutdownOpenVr();
            return false;
        }
    }

    /// <summary>
    /// Updates overlay visibility, width, curvature, opacity and 3D transform according to <paramref name="settings"/>,
    /// including live VR controller grab-and-drag when in <see cref="OverlayAttachmentMode.WorldLocked"/> mode.
    /// </summary>
    public void ApplySettings(LyricsOverlaySettings settings, float deltaTimeSeconds = 1f / 60f)
    {
        if (!IsConnected) return;
        var overlay = Valve.VR.OpenVR.Overlay;
        if (overlay is null) return;

        if (_lastSyncedAutoStart != settings.AutoStartWithSteamVr)
        {
            _lastSyncedAutoStart = settings.AutoStartWithSteamVr;
            try
            {
                LastManifestStatus = SteamVrManifestService.ApplyRegistration(
                    Valve.VR.OpenVR.Applications,
                    settings.AutoStartWithSteamVr);
            }
            catch { }
        }

        if (!settings.Enabled)
        {
            _grabInteractor.Reset();
            overlay.HideOverlay(_overlayHandle);
            return;
        }

        overlay.ShowOverlay(_overlayHandle);
        overlay.SetOverlayWidthInMeters(_overlayHandle, Math.Clamp(settings.WidthInMeters, 0.15f, 3.5f));
        overlay.SetOverlayAlpha(_overlayHandle, Math.Clamp(settings.Opacity, 0.05f, 1.0f));
        overlay.SetOverlayCurvature(_overlayHandle, Math.Clamp(settings.Curvature, 0.0f, 0.45f));

        switch (settings.AttachmentMode)
        {
            case OverlayAttachmentMode.HeadLocked:
            {
                _grabInteractor.Reset();
                var transform = OverlayTransformHelper.BuildTransform(settings);
                overlay.SetOverlayTransformTrackedDeviceRelative(
                    _overlayHandle,
                    Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd,
                    ref transform);
                break;
            }

            case OverlayAttachmentMode.LeftHand:
            case OverlayAttachmentMode.RightHand:
            {
                _grabInteractor.Reset();
                var role = settings.AttachmentMode == OverlayAttachmentMode.LeftHand
                    ? ETrackedControllerRole.LeftHand
                    : ETrackedControllerRole.RightHand;
                var devIdx = ResolveControllerDeviceIndex(role);
                if (devIdx != Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
                {
                    var transform = OverlayTransformHelper.BuildTransform(settings);
                    overlay.SetOverlayTransformTrackedDeviceRelative(_overlayHandle, devIdx, ref transform);
                }
                else
                {
                    // Fallback to HMD if the chosen controller isn't turned on yet.
                    var fallback = OverlayTransformHelper.BuildTransform(settings with { AttachmentMode = OverlayAttachmentMode.HeadLocked });
                    overlay.SetOverlayTransformTrackedDeviceRelative(
                        _overlayHandle,
                        Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd,
                        ref fallback);
                }
                break;
            }

            case OverlayAttachmentMode.WorldLocked:
            {
                var worldTransform = UpdateWorldLockedTransform(settings, overlay, deltaTimeSeconds);
                overlay.SetOverlayTransformAbsolute(
                    _overlayHandle,
                    ETrackingUniverseOrigin.TrackingUniverseStanding,
                    ref worldTransform);
                break;
            }
        }
    }

    /// <summary>
    /// Computes a world-locked pose directly in front of the current HMD gaze in <c>TrackingUniverseStanding</c>.
    /// Falls back to default standing coordinates if SteamVR is not connected or HMD pose is unavailable.
    /// </summary>
    public WorldOverlayPose ComputePoseInFrontOfHmd(float distanceMeters = 1.25f, float verticalOffsetMeters = -0.20f)
    {
        if (TryGetHmdStandingPose(out var hmdPose))
            return OverlayTransformHelper.ComputeWorldPoseInFrontOfHmd(in hmdPose, distanceMeters, verticalOffsetMeters);

        return new WorldOverlayPose(new Vector3(0f, 1.30f, -Math.Clamp(distanceMeters, 0.5f, 3.0f)), 0f, 0f);
    }

    /// <summary>
    /// Returns true if the world-locked position in <paramref name="settings"/> is currently within the HMD's forward view
    /// (or if HMD tracking is not active, in which case we don't override saved coordinates).
    /// </summary>
    public bool IsSavedWorldPoseInFrontOfHmd(LyricsOverlaySettings settings)
    {
        if (!TryGetHmdStandingPose(out var hmdPose))
            return true;

        var worldPos = new Vector3(settings.WorldPositionX, settings.WorldPositionY, settings.WorldPositionZ);
        return OverlayTransformHelper.IsOverlayInFrontOfHmd(in hmdPose, worldPos);
    }

    public SteamVrManifestStatus RegisterManifest(bool enableAutoStart)
    {
        _lastSyncedAutoStart = enableAutoStart;
        var status = SteamVrManifestService.ApplyRegistration(
            IsConnected ? Valve.VR.OpenVR.Applications : null,
            enableAutoStart);
        LastManifestStatus = status;
        return status;
    }

    public SteamVrManifestStatus UnregisterManifest()
    {
        _lastSyncedAutoStart = false;
        var status = SteamVrManifestService.Unregister(
            IsConnected ? Valve.VR.OpenVR.Applications : null);
        LastManifestStatus = status;
        return status;
    }

    public SteamVrManifestStatus QueryManifestStatus(bool configuredAutoStart)
    {
        var status = SteamVrManifestService.QueryStatus(
            IsConnected ? Valve.VR.OpenVR.Applications : null,
            configuredAutoStart);
        LastManifestStatus = status;
        return status;
    }

    /// <summary>Uploads <paramref name="bitmap"/> to the D3D11 texture and submits it to OpenVR.</summary>
    public bool SubmitFrame(SKBitmap bitmap)
    {
        if (!IsConnected) return false;
        var overlay = Valve.VR.OpenVR.Overlay;
        if (overlay is null) return false;

        var nativeTex = _textureBridge.UploadBitmap(bitmap);
        var tex = new Texture_t
        {
            handle = nativeTex,
            eType = ETextureType.DirectX,
            eColorSpace = EColorSpace.Auto,
        };

        var err = overlay.SetOverlayTexture(_overlayHandle, ref tex);
        if (err != EVROverlayError.None)
        {
            LastStatusMessage = $"SetOverlayTexture: {err}";
            return false;
        }
        return true;
    }

    /// <summary>Polls and renders the SteamVR Dashboard control panel if it is open or needs an initial frame.</summary>
    public void UpdateDashboard(LyricsRenderFrame currentFrame, bool isDemoLoopEnabled = false)
    {
        if (!IsConnected || Valve.VR.OpenVR.Overlay is not { } overlay)
            return;

        _dashboardPanel.UpdateAndRenderIfNeeded(overlay, currentFrame, isDemoLoopEnabled);
    }

    /// <summary>Polls OpenVR system and overlay events. Returns false if SteamVR is quitting.</summary>
    public bool PollEvents()
    {
        if (!IsConnected) return false;
        var eventSize = (uint)Marshal.SizeOf<VREvent_t>();
        var vrEvent = default(VREvent_t);

        var system = Valve.VR.OpenVR.System;
        while (system is not null && system.PollNextEvent(ref vrEvent, eventSize))
        {
            if ((EVREventType)vrEvent.eventType == EVREventType.VREvent_Quit)
            {
                system.AcknowledgeQuit_Exiting();
                LastStatusMessage = "SteamVR 已退出";
                ShutdownOpenVr();
                QuitRequested?.Invoke();
                return false;
            }
        }

        var overlay = Valve.VR.OpenVR.Overlay;
        while (overlay is not null && overlay.PollNextOverlayEvent(_overlayHandle, ref vrEvent, eventSize))
        {
            if ((EVREventType)vrEvent.eventType == EVREventType.VREvent_Quit)
            {
                ShutdownOpenVr();
                QuitRequested?.Invoke();
                return false;
            }
        }

        return true;
    }

    private HmdMatrix34_t UpdateWorldLockedTransform(
        LyricsOverlaySettings settings,
        CVROverlay overlay,
        float deltaTimeSeconds)
    {
        var system = Valve.VR.OpenVR.System;
        if (system is null || !settings.EnableWorldControllerGrab)
        {
            _grabInteractor.Reset();
            return OverlayTransformHelper.BuildTransform(settings);
        }

        system.GetDeviceToAbsoluteTrackingPose(
            ETrackingUniverseOrigin.TrackingUniverseStanding,
            0f,
            _trackedPoses);

        HmdMatrix34_t? hmdPose = _trackedPoses[Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd] is { bPoseIsValid: true } hmd
            ? hmd.mDeviceToAbsoluteTracking
            : null;

        Span<ControllerGrabInput> controllers = stackalloc ControllerGrabInput[8];
        var controllerCount = CollectControllerInputs(system, controllers);

        var grabResult = _grabInteractor.Update(
            settings,
            hmdPose,
            controllers[..controllerCount],
            deltaTimeSeconds,
            isDashboardOpen: overlay.IsDashboardVisible());

        if (grabResult.HapticDeviceIndex is { } hapticDev && grabResult.HapticDurationMicroSec > 0)
        {
            try { system.TriggerHapticPulse(hapticDev, 0, grabResult.HapticDurationMicroSec); }
            catch { }
        }

        if (grabResult.CommittedPose is { } committed)
        {
            try { WorldPoseCommitted?.Invoke(committed); }
            catch { }
        }

        return OverlayTransformHelper.BuildYawPitchTranslation(
            grabResult.CurrentPose.YawDegrees,
            grabResult.CurrentPose.PitchDegrees,
            grabResult.CurrentPose.Position);
    }

    private int CollectControllerInputs(CVRSystem system, Span<ControllerGrabInput> buffer)
    {
        var count = 0;
        var stateSize = (uint)Marshal.SizeOf<VRControllerState_t>();

        for (uint devIdx = 1; devIdx < Valve.VR.OpenVR.k_unMaxTrackedDeviceCount && count < buffer.Length; devIdx++)
        {
            ref readonly var tracked = ref _trackedPoses[devIdx];
            if (!tracked.bDeviceIsConnected || !tracked.bPoseIsValid)
                continue;

            var devClass = system.GetTrackedDeviceClass(devIdx);
            if (devClass != ETrackedDeviceClass.Controller)
                continue;

            var ctrlState = default(VRControllerState_t);
            var hasState = system.GetControllerState(devIdx, ref ctrlState, stateSize);

            const ulong gripMask = 1UL << (int)EVRButtonId.k_EButton_Grip;
            const ulong triggerMask = 1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger;

            var isGrabPressed = hasState &&
                ((ctrlState.ulButtonPressed & (gripMask | triggerMask)) != 0 ||
                 ctrlState.rAxis1.x >= 0.78f);

            var thumbstickY = hasState ? ctrlState.rAxis0.y : 0f;

            buffer[count++] = new ControllerGrabInput(
                devIdx,
                tracked.mDeviceToAbsoluteTracking,
                isGrabPressed,
                thumbstickY);
        }

        return count;
    }

    private static uint ResolveControllerDeviceIndex(ETrackedControllerRole role)
    {
        var system = Valve.VR.OpenVR.System;
        if (system is null)
            return Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;

        var idx = system.GetTrackedDeviceIndexForControllerRole(role);
        if (idx != Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid && system.IsTrackedDeviceConnected(idx))
            return idx;

        // Fallback: if SteamVR hasn't assigned Left/Right hand roles yet, pick the 1st/2nd connected controller.
        var desiredOrder = role == ETrackedControllerRole.LeftHand ? 0 : 1;
        var found = 0;
        for (uint dev = 1; dev < Valve.VR.OpenVR.k_unMaxTrackedDeviceCount; dev++)
        {
            if (system.IsTrackedDeviceConnected(dev) && system.GetTrackedDeviceClass(dev) == ETrackedDeviceClass.Controller)
            {
                if (found == desiredOrder)
                    return dev;
                found++;
            }
        }

        return Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
    }

    private bool TryGetHmdStandingPose(out HmdMatrix34_t hmdPose)
    {
        hmdPose = default;
        if (!IsConnected || Valve.VR.OpenVR.System is not { } system)
            return false;

        try
        {
            system.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding,
                0f,
                _trackedPoses);

            ref readonly var hmd = ref _trackedPoses[Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (hmd.bDeviceIsConnected && hmd.bPoseIsValid)
            {
                hmdPose = hmd.mDeviceToAbsoluteTracking;
                return true;
            }
        }
        catch { }

        return false;
    }

    public void ShutdownOpenVr()
    {
        _grabInteractor.Reset();
        _dashboardPanel.Reset(Valve.VR.OpenVR.Overlay);
        _lastSyncedAutoStart = null;

        if (_overlayHandle != Valve.VR.OpenVR.k_ulOverlayHandleInvalid)
        {
            try { Valve.VR.OpenVR.Overlay?.DestroyOverlay(_overlayHandle); }
            catch (Exception) { }
            _overlayHandle = Valve.VR.OpenVR.k_ulOverlayHandleInvalid;
        }

        if (_vrInitialized)
        {
            try { Valve.VR.OpenVR.Shutdown(); }
            catch (Exception) { }
            _vrInitialized = false;
        }

        _textureBridge.Reset();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ShutdownOpenVr();
        _dashboardPanel.Dispose();
        _textureBridge.Dispose();
    }
}
