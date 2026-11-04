using System.Numerics;
using Valve.VR;
using VRLyrics.Core.Models;

namespace VRLyrics.Overlay.OpenVR;

/// <summary>Snapshot of a single VR controller's pose and button/thumbstick state for a frame.</summary>
public readonly record struct ControllerGrabInput(
    uint DeviceIndex,
    HmdMatrix34_t Pose,
    bool IsGrabPressed,
    float ThumbstickY);

/// <summary>Per-frame result of <see cref="WorldOverlayGrabInteractor.Update"/>.</summary>
public readonly record struct WorldGrabUpdateResult(
    bool IsHovered,
    bool IsGrabbing,
    WorldOverlayPose CurrentPose,
    WorldOverlayPose? CommittedPose,
    uint? HapticDeviceIndex,
    ushort HapticDurationMicroSec);

/// <summary>
/// Handles VR controller hover detection (both laser-ray intersection and direct hand proximity),
/// rising-edge grip/trigger grabbing, 6DoF dragging, thumbstick push/pull distance adjustment,
/// and release pose commit for <see cref="OverlayAttachmentMode.WorldLocked"/>.
/// </summary>
public sealed class WorldOverlayGrabInteractor
{
    private const float MaxRayDistanceMeters = 4.2f;
    private const float MinGrabDistanceMeters = 0.35f;
    private const float MaxGrabDistanceMeters = 4.0f;
    private const float ProximityThresholdMeters = 0.22f;
    private const float HoverPaddingMeters = 0.04f;
    private const float ThumbstickDeadzone = 0.22f;
    private const float ThumbstickPushPullSpeedMetersPerSec = 1.35f;

    // Standard ergonomic pointer ray pitch (-30 degrees downward from controller -Z axis).
    private static readonly Vector3 LocalErgonomicRayDir = Vector3.Normalize(new Vector3(
        0f,
        -MathF.Sin(30f * MathF.PI / 180f),
        -MathF.Cos(30f * MathF.PI / 180f)));

    private static readonly Vector3 LocalStraightRayDir = -Vector3.UnitZ;

    private readonly Dictionary<uint, bool> _prevButtonPressedByDevice = [];

    private bool _wasHovered;
    private uint? _grabbingDeviceIndex;
    private Vector3 _grabLocalRayDir;
    private float _grabDistance;
    private Vector3 _grabLocalCenterOffset;
    private WorldOverlayPose _activeGrabPose;

    public bool IsHovered { get; private set; }
    public bool IsGrabbing => _grabbingDeviceIndex.HasValue;

    public void Reset()
    {
        _prevButtonPressedByDevice.Clear();
        _wasHovered = false;
        _grabbingDeviceIndex = null;
        IsHovered = false;
    }

    public WorldGrabUpdateResult Update(
        LyricsOverlaySettings settings,
        HmdMatrix34_t? hmdPose,
        ReadOnlySpan<ControllerGrabInput> controllers,
        float deltaTimeSeconds,
        bool isDashboardOpen = false)
    {
        var configuredPose = new WorldOverlayPose(
            new Vector3(settings.WorldPositionX, settings.WorldPositionY, settings.WorldPositionZ),
            settings.WorldYawDegrees,
            settings.WorldPitchDegrees);

        if (!settings.Enabled ||
            settings.AttachmentMode != OverlayAttachmentMode.WorldLocked ||
            !settings.EnableWorldControllerGrab ||
            isDashboardOpen)
        {
            Reset();
            return new WorldGrabUpdateResult(false, false, configuredPose, null, null, 0);
        }

        var dt = Math.Clamp(deltaTimeSeconds, 0.001f, 0.1f);
        var widthMeters = Math.Clamp(settings.WidthInMeters, 0.15f, 3.5f);
        var aspect = Math.Clamp((float)settings.TextureHeight / Math.Max(1, settings.TextureWidth), 0.12f, 1.0f);
        var heightMeters = widthMeters * aspect;

        // 1) If currently grabbing, continue or finish the active drag.
        if (_grabbingDeviceIndex is { } activeDev)
        {
            ControllerGrabInput? activeInput = null;
            for (var i = 0; i < controllers.Length; i++)
            {
                if (controllers[i].DeviceIndex == activeDev)
                {
                    activeInput = controllers[i];
                    break;
                }
            }

            UpdatePrevButtonStates(controllers);

            if (activeInput is { IsGrabPressed: true } input)
            {
                if (MathF.Abs(input.ThumbstickY) > ThumbstickDeadzone)
                {
                    _grabDistance = Math.Clamp(
                        _grabDistance + input.ThumbstickY * ThumbstickPushPullSpeedMetersPerSec * dt,
                        MinGrabDistanceMeters,
                        MaxGrabDistanceMeters);
                }

                var ctrlPose = input.Pose;
                var ctrlPos = OverlayTransformHelper.ExtractPosition(in ctrlPose);
                var worldRayDir = TransformDirection(in ctrlPose, _grabLocalRayDir);
                var worldOffset = TransformDirection(in ctrlPose, _grabLocalCenterOffset);
                var newPos = ctrlPos + worldRayDir * _grabDistance + worldOffset;
                newPos.Y = Math.Clamp(newPos.Y, 0.15f, 3.00f);

                var viewerPos = (settings.WorldAutoFaceHmd && hmdPose.HasValue)
                    ? OverlayTransformHelper.ExtractPosition(hmdPose.Value)
                    : ctrlPos;

                var (yaw, pitch) = OverlayTransformHelper.ComputeLookAtYawPitch(newPos, viewerPos);
                _activeGrabPose = new WorldOverlayPose(newPos, yaw, pitch);
                IsHovered = true;

                return new WorldGrabUpdateResult(
                    IsHovered: true,
                    IsGrabbing: true,
                    CurrentPose: _activeGrabPose,
                    CommittedPose: null,
                    HapticDeviceIndex: null,
                    HapticDurationMicroSec: 0);
            }
            else
            {
                // Released grab button -> commit the final dragged pose!
                _grabbingDeviceIndex = null;
                _wasHovered = false;
                IsHovered = false;
                return new WorldGrabUpdateResult(
                    IsHovered: false,
                    IsGrabbing: false,
                    CurrentPose: _activeGrabPose,
                    CommittedPose: _activeGrabPose,
                    HapticDeviceIndex: activeDev,
                    HapticDurationMicroSec: 1200);
            }
        }

        // 2) Not currently grabbing: check all controllers for hover and rising-edge grab start.
        var overlayTransform = OverlayTransformHelper.BuildYawPitchTranslation(
            configuredPose.YawDegrees,
            configuredPose.PitchDegrees,
            configuredPose.Position);

        var anyHovered = false;
        uint? hoveredDevice = null;

        for (var i = 0; i < controllers.Length; i++)
        {
            var ctrl = controllers[i];
            var wasPressed = _prevButtonPressedByDevice.TryGetValue(ctrl.DeviceIndex, out var prev) && prev;
            var risingEdge = !wasPressed && ctrl.IsGrabPressed;

            var ctrlPose = ctrl.Pose;
            if (TryHitOverlay(in ctrlPose, in overlayTransform, widthMeters, heightMeters,
                    out var localRayDir, out var hitDistance, out var hitPointWorld))
            {
                anyHovered = true;
                hoveredDevice ??= ctrl.DeviceIndex;

                if (risingEdge)
                {
                    _grabbingDeviceIndex = ctrl.DeviceIndex;
                    _grabLocalRayDir = localRayDir;
                    _grabDistance = Math.Clamp(hitDistance, 0.0f, MaxGrabDistanceMeters);
                    var worldCenterOffset = configuredPose.Position - hitPointWorld;
                    _grabLocalCenterOffset = InverseTransformDirection(in ctrlPose, worldCenterOffset);
                    _activeGrabPose = configuredPose;
                    IsHovered = true;
                    _wasHovered = true;
                    UpdatePrevButtonStates(controllers);

                    return new WorldGrabUpdateResult(
                        IsHovered: true,
                        IsGrabbing: true,
                        CurrentPose: _activeGrabPose,
                        CommittedPose: null,
                        HapticDeviceIndex: ctrl.DeviceIndex,
                        HapticDurationMicroSec: 2200);
                }
            }
        }

        UpdatePrevButtonStates(controllers);

        var triggerHoverHaptic = anyHovered && !_wasHovered;
        _wasHovered = anyHovered;
        IsHovered = anyHovered;

        return new WorldGrabUpdateResult(
            IsHovered: anyHovered,
            IsGrabbing: false,
            CurrentPose: configuredPose,
            CommittedPose: null,
            HapticDeviceIndex: triggerHoverHaptic ? hoveredDevice : null,
            HapticDurationMicroSec: triggerHoverHaptic ? (ushort)750 : (ushort)0);
    }

    /// <summary>
    /// Tests whether a controller at <paramref name="controllerPose"/> intersects the 3D overlay quad
    /// either via direct hand proximity or via straight/ergonomic pointer rays.
    /// </summary>
    public static bool TryHitOverlay(
        in HmdMatrix34_t controllerPose,
        in HmdMatrix34_t overlayTransform,
        float widthMeters,
        float heightMeters,
        out Vector3 matchedLocalRayDir,
        out float hitDistance,
        out Vector3 hitPointWorld)
    {
        var ctrlPos = OverlayTransformHelper.ExtractPosition(in controllerPose);
        var overlayCenter = OverlayTransformHelper.ExtractPosition(in overlayTransform);
        var overlayRight = OverlayTransformHelper.ExtractRight(in overlayTransform);
        var overlayUp = OverlayTransformHelper.ExtractUp(in overlayTransform);
        var overlayNormal = OverlayTransformHelper.ExtractNormal(in overlayTransform);

        var halfW = widthMeters * 0.5f + HoverPaddingMeters;
        var halfH = heightMeters * 0.5f + HoverPaddingMeters;

        // 1) Direct hand proximity check (grabbing the overlay directly like a physical board)
        var rel = ctrlPos - overlayCenter;
        var normalDist = MathF.Abs(Vector3.Dot(rel, overlayNormal));
        var projX = MathF.Abs(Vector3.Dot(rel, overlayRight));
        var projY = MathF.Abs(Vector3.Dot(rel, overlayUp));
        if (normalDist <= ProximityThresholdMeters && projX <= halfW && projY <= halfH)
        {
            matchedLocalRayDir = LocalStraightRayDir;
            hitDistance = 0f;
            hitPointWorld = ctrlPos;
            return true;
        }

        // 2) Ray-quad intersection: test both straight (-Z) ray and ergonomic (-30 deg pitch) pointer ray,
        // picking whichever hits closer to the quad center.
        var hitStraight = TryIntersectRayWithQuad(
            ctrlPos,
            TransformDirection(in controllerPose, LocalStraightRayDir),
            overlayCenter,
            overlayRight,
            overlayUp,
            overlayNormal,
            halfW,
            halfH,
            out var distStraight,
            out var ptStraight,
            out var normOffsetStraight);

        var hitErgo = TryIntersectRayWithQuad(
            ctrlPos,
            TransformDirection(in controllerPose, LocalErgonomicRayDir),
            overlayCenter,
            overlayRight,
            overlayUp,
            overlayNormal,
            halfW,
            halfH,
            out var distErgo,
            out var ptErgo,
            out var normOffsetErgo);

        if (hitStraight && (!hitErgo || normOffsetStraight <= normOffsetErgo))
        {
            matchedLocalRayDir = LocalStraightRayDir;
            hitDistance = distStraight;
            hitPointWorld = ptStraight;
            return true;
        }

        if (hitErgo)
        {
            matchedLocalRayDir = LocalErgonomicRayDir;
            hitDistance = distErgo;
            hitPointWorld = ptErgo;
            return true;
        }

        matchedLocalRayDir = LocalStraightRayDir;
        hitDistance = 0f;
        hitPointWorld = Vector3.Zero;
        return false;
    }

    private static bool TryIntersectRayWithQuad(
        Vector3 rayOrigin,
        Vector3 rayDir,
        Vector3 quadCenter,
        Vector3 quadRight,
        Vector3 quadUp,
        Vector3 quadNormal,
        float halfWidth,
        float halfHeight,
        out float distance,
        out Vector3 hitPoint,
        out float normalizedCenterDistance)
    {
        distance = 0f;
        hitPoint = Vector3.Zero;
        normalizedCenterDistance = float.MaxValue;

        var denom = Vector3.Dot(rayDir, quadNormal);
        if (MathF.Abs(denom) < 1e-4f)
            return false;

        var t = Vector3.Dot(quadCenter - rayOrigin, quadNormal) / denom;
        if (t is < 0.05f or > MaxRayDistanceMeters)
            return false;

        var p = rayOrigin + rayDir * t;
        var offset = p - quadCenter;
        var localX = Vector3.Dot(offset, quadRight);
        var localY = Vector3.Dot(offset, quadUp);

        if (MathF.Abs(localX) > halfWidth || MathF.Abs(localY) > halfHeight)
            return false;

        distance = t;
        hitPoint = p;
        var nx = localX / Math.Max(0.05f, halfWidth);
        var ny = localY / Math.Max(0.05f, halfHeight);
        normalizedCenterDistance = nx * nx + ny * ny;
        return true;
    }

    private void UpdatePrevButtonStates(ReadOnlySpan<ControllerGrabInput> controllers)
    {
        for (var i = 0; i < controllers.Length; i++)
            _prevButtonPressedByDevice[controllers[i].DeviceIndex] = controllers[i].IsGrabPressed;
    }

    private static Vector3 TransformDirection(in HmdMatrix34_t m, Vector3 localDir)
    {
        var right = OverlayTransformHelper.ExtractRight(in m);
        var up = OverlayTransformHelper.ExtractUp(in m);
        var back = OverlayTransformHelper.ExtractNormal(in m);
        var world = right * localDir.X + up * localDir.Y + back * localDir.Z;
        return world.LengthSquared() > 1e-6f ? Vector3.Normalize(world) : world;
    }

    private static Vector3 InverseTransformDirection(in HmdMatrix34_t m, Vector3 worldVec)
    {
        var right = OverlayTransformHelper.ExtractRight(in m);
        var up = OverlayTransformHelper.ExtractUp(in m);
        var back = OverlayTransformHelper.ExtractNormal(in m);
        return new Vector3(
            Vector3.Dot(worldVec, right),
            Vector3.Dot(worldVec, up),
            Vector3.Dot(worldVec, back));
    }
}
