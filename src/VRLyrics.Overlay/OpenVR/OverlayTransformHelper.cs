using System.Numerics;
using Valve.VR;
using VRLyrics.Core.Models;

namespace VRLyrics.Overlay.OpenVR;

/// <summary>Represents a 3D world-locked position and horizon-level orientation for the lyrics overlay.</summary>
public readonly record struct WorldOverlayPose(
    Vector3 Position,
    float YawDegrees,
    float PitchDegrees)
{
    public LyricsOverlaySettings ApplyTo(LyricsOverlaySettings settings) =>
        settings with
        {
            WorldPositionX = MathF.Round(Position.X, 3),
            WorldPositionY = MathF.Round(Position.Y, 3),
            WorldPositionZ = MathF.Round(Position.Z, 3),
            WorldYawDegrees = MathF.Round(YawDegrees, 1),
            WorldPitchDegrees = MathF.Round(PitchDegrees, 1),
        };
}

/// <summary>
/// Builds OpenVR right-handed 3x4 transform matrices (<see cref="HmdMatrix34_t"/>) from
/// <see cref="LyricsOverlaySettings"/> and provides 3D pose helpers for M5 spatial placement.
/// OpenVR coordinate convention: +X right, +Y up, -Z forward (overlay front face normal is +Z).
/// </summary>
public static class OverlayTransformHelper
{
    public static HmdMatrix34_t BuildTransform(LyricsOverlaySettings settings) =>
        settings.AttachmentMode switch
        {
            OverlayAttachmentMode.HeadLocked => BuildYawPitchRollTranslation(
                settings.HeadYawDegrees,
                settings.HeadPitchDegrees,
                0f,
                new Vector3(settings.HeadOffsetX, settings.HeadOffsetY, settings.HeadOffsetZ)),

            OverlayAttachmentMode.LeftHand or OverlayAttachmentMode.RightHand => BuildYawPitchRollTranslation(
                settings.HandYawDegrees,
                settings.HandPitchDegrees,
                settings.HandRollDegrees,
                new Vector3(settings.HandOffsetX, settings.HandOffsetY, settings.HandOffsetZ)),

            OverlayAttachmentMode.WorldLocked => BuildYawPitchRollTranslation(
                settings.WorldYawDegrees,
                settings.WorldPitchDegrees,
                0f,
                new Vector3(settings.WorldPositionX, settings.WorldPositionY, settings.WorldPositionZ)),

            _ => BuildYawPitchRollTranslation(0f, 0f, 0f, new Vector3(0f, -0.24f, -1.15f)),
        };

    public static HmdMatrix34_t BuildYawPitchTranslation(float yawDegrees, float pitchDegrees, Vector3 position) =>
        BuildYawPitchRollTranslation(yawDegrees, pitchDegrees, 0f, position);

    public static HmdMatrix34_t BuildYawPitchRollTranslation(
        float yawDegrees,
        float pitchDegrees,
        float rollDegrees,
        Vector3 position)
    {
        var yawRad = yawDegrees * (MathF.PI / 180f);
        var pitchRad = pitchDegrees * (MathF.PI / 180f);
        var rollRad = rollDegrees * (MathF.PI / 180f);

        // In System.Numerics, CreateFromYawPitchRoll rotates around Y (yaw), X (pitch), Z (roll).
        var rot = Matrix4x4.CreateFromYawPitchRoll(yawRad, pitchRad, rollRad);

        // System.Numerics Matrix4x4 is row-vector convention (v * R), while OpenVR HmdMatrix34_t
        // uses column-vector convention (R * v), where columns 0..2 are the basis vectors and column 3 is translation.
        return new HmdMatrix34_t
        {
            m0 = rot.M11, m1 = rot.M21, m2 = rot.M31, m3 = position.X,
            m4 = rot.M12, m5 = rot.M22, m6 = rot.M32, m7 = position.Y,
            m8 = rot.M13, m9 = rot.M23, m10 = rot.M33, m11 = position.Z,
        };
    }

    public static Vector3 ExtractPosition(in HmdMatrix34_t m) =>
        new(m.m3, m.m7, m.m11);

    public static Vector3 ExtractRight(in HmdMatrix34_t m) =>
        NormalizeOrDefault(new Vector3(m.m0, m.m4, m.m8), Vector3.UnitX);

    public static Vector3 ExtractUp(in HmdMatrix34_t m) =>
        NormalizeOrDefault(new Vector3(m.m1, m.m5, m.m9), Vector3.UnitY);

    /// <summary>Returns the forward (-Z) unit vector of an OpenVR device/overlay transform.</summary>
    public static Vector3 ExtractForward(in HmdMatrix34_t m) =>
        NormalizeOrDefault(new Vector3(-m.m2, -m.m6, -m.m10), -Vector3.UnitZ);

    /// <summary>Returns the front-face normal (+Z) unit vector of an OpenVR overlay transform.</summary>
    public static Vector3 ExtractNormal(in HmdMatrix34_t m) =>
        NormalizeOrDefault(new Vector3(m.m2, m.m6, m.m10), Vector3.UnitZ);

    /// <summary>
    /// Computes horizon-level <c>(YawDegrees, PitchDegrees)</c> so that the overlay at
    /// <paramref name="overlayPosition"/> faces <paramref name="viewerPosition"/> directly.
    /// </summary>
    public static (float YawDegrees, float PitchDegrees) ComputeLookAtYawPitch(
        Vector3 overlayPosition,
        Vector3 viewerPosition)
    {
        var toViewer = viewerPosition - overlayPosition;
        if (toViewer.LengthSquared() < 1e-6f)
            return (0f, 0f);

        var normal = Vector3.Normalize(toViewer);
        var yawRad = MathF.Atan2(normal.X, normal.Z);
        var pitchRad = MathF.Asin(Math.Clamp(-normal.Y, -1f, 1f));

        return (yawRad * (180f / MathF.PI), pitchRad * (180f / MathF.PI));
    }

    /// <summary>
    /// Computes a world-locked pose comfortably in front of <paramref name="hmdPose"/>.
    /// </summary>
    public static WorldOverlayPose ComputeWorldPoseInFrontOfHmd(
        in HmdMatrix34_t hmdPose,
        float distanceMeters = 1.25f,
        float verticalOffsetMeters = -0.20f)
    {
        var hmdPos = ExtractPosition(in hmdPose);
        var forward = ExtractForward(in hmdPose);

        // Project forward vector onto the horizontal XZ plane so the overlay stays at a natural height
        // even if the user happens to be looking slightly up or down.
        var horizForward = new Vector3(forward.X, 0f, forward.Z);
        horizForward = horizForward.LengthSquared() > 1e-4f
            ? Vector3.Normalize(horizForward)
            : -Vector3.UnitZ;

        var targetPos = hmdPos + horizForward * Math.Clamp(distanceMeters, 0.4f, 4.0f)
                        + new Vector3(0f, verticalOffsetMeters, 0f);
        targetPos.Y = Math.Clamp(targetPos.Y, 0.25f, 2.80f);

        var (yaw, pitch) = ComputeLookAtYawPitch(targetPos, hmdPos);
        return new WorldOverlayPose(targetPos, yaw, pitch);
    }

    /// <summary>
    /// Returns true if <paramref name="overlayPosition"/> is within the forward field of view of <paramref name="hmdPose"/>
    /// and within a reasonable distance (0.25m .. 4.5m).
    /// </summary>
    public static bool IsOverlayInFrontOfHmd(
        in HmdMatrix34_t hmdPose,
        Vector3 overlayPosition,
        float minCosAngle = 0.25f)
    {
        var hmdPos = ExtractPosition(in hmdPose);
        var toOverlay = overlayPosition - hmdPos;
        var dist = toOverlay.Length();
        if (dist is < 0.25f or > 4.5f)
            return false;

        var dir = toOverlay / dist;
        var forward = ExtractForward(in hmdPose);
        return Vector3.Dot(forward, dir) >= minCosAngle;
    }

    private static Vector3 NormalizeOrDefault(Vector3 v, Vector3 fallback) =>
        v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : fallback;
}
