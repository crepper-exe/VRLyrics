using System.Numerics;
using VRLyrics.Core.Models;
using VRLyrics.Overlay.OpenVR;
using VRLyrics.Overlay.Rendering;

namespace VRLyrics.Core.Tests;

public class SpatialPlacementTests
{
    [Fact]
    public void ComputeWorldPoseInFrontOfHmd_places_overlay_in_front_and_faces_viewer()
    {
        // Simulate HMD at (1.0, 1.65, 0.5) rotated +90 degrees yaw (facing along -X in OpenVR right-handed coords)
        var hmdPos = new Vector3(1.0f, 1.65f, 0.5f);
        var hmdPose = OverlayTransformHelper.BuildYawPitchTranslation(90f, 0f, hmdPos);

        var worldPose = OverlayTransformHelper.ComputeWorldPoseInFrontOfHmd(
            in hmdPose,
            distanceMeters: 1.20f,
            verticalOffsetMeters: -0.20f);

        // HMD forward for +90 deg yaw around +Y is (-1, 0, 0), so target X should be 1.0 - 1.20 = -0.20
        Assert.Equal(-0.20f, worldPose.Position.X, 2);
        Assert.Equal(1.45f, worldPose.Position.Y, 2);
        Assert.Equal(0.50f, worldPose.Position.Z, 2);

        // Overlay should be considered in front of HMD
        Assert.True(OverlayTransformHelper.IsOverlayInFrontOfHmd(in hmdPose, worldPose.Position));

        // Behind HMD (+X direction here) should return false
        Assert.False(OverlayTransformHelper.IsOverlayInFrontOfHmd(in hmdPose, new Vector3(2.5f, 1.45f, 0.5f)));

        // Verify the resulting overlay transform's +Z normal points back toward the HMD
        var overlayMat = OverlayTransformHelper.BuildYawPitchTranslation(
            worldPose.YawDegrees,
            worldPose.PitchDegrees,
            worldPose.Position);
        var normal = OverlayTransformHelper.ExtractNormal(in overlayMat);
        var expectedNormal = Vector3.Normalize(hmdPos - worldPose.Position);
        Assert.True(Vector3.Dot(normal, expectedNormal) > 0.999f);
    }

    [Fact]
    public void WorldOverlayGrabInteractor_handles_hover_rising_edge_drag_thumbstick_and_commit()
    {
        var interactor = new WorldOverlayGrabInteractor();
        var settings = new LyricsOverlaySettings
        {
            Enabled = true,
            AttachmentMode = OverlayAttachmentMode.WorldLocked,
            EnableWorldControllerGrab = true,
            WorldAutoFaceHmd = true,
            WidthInMeters = 1.0f,
            TextureWidth = 1600,
            TextureHeight = 420,
            WorldPositionX = 0f,
            WorldPositionY = 1.30f,
            WorldPositionZ = -1.40f,
            WorldYawDegrees = 0f,
            WorldPitchDegrees = 0f,
        };

        var hmdPose = OverlayTransformHelper.BuildYawPitchTranslation(0f, 0f, new Vector3(0f, 1.60f, 0f));

        // Controller at (0, 1.30, -0.40) pointing straight forward (-Z) towards the overlay at (0, 1.30, -1.40)
        var ctrlPose0 = OverlayTransformHelper.BuildYawPitchTranslation(0f, 0f, new Vector3(0f, 1.30f, -0.40f));

        // Frame 1: Hover without pressing grab button -> IsHovered = true, hover haptic triggered
        ReadOnlySpan<ControllerGrabInput> frame1 =
        [
            new ControllerGrabInput(1, ctrlPose0, IsGrabPressed: false, ThumbstickY: 0f)
        ];
        var r1 = interactor.Update(settings, hmdPose, frame1, 0.016f);
        Assert.True(r1.IsHovered);
        Assert.False(r1.IsGrabbing);
        Assert.Null(r1.CommittedPose);
        Assert.Equal(1u, r1.HapticDeviceIndex);

        // Frame 2: Press grab button (rising edge) -> IsGrabbing = true
        ReadOnlySpan<ControllerGrabInput> frame2 =
        [
            new ControllerGrabInput(1, ctrlPose0, IsGrabPressed: true, ThumbstickY: 0f)
        ];
        var r2 = interactor.Update(settings, hmdPose, frame2, 0.016f);
        Assert.True(r2.IsHovered);
        Assert.True(r2.IsGrabbing);
        Assert.Equal(1u, r2.HapticDeviceIndex);

        // Frame 3: Move controller right (+0.45m X) and up (+0.20m Y) while pushing thumbstick forward (+1.0)
        var ctrlPoseMoved = OverlayTransformHelper.BuildYawPitchTranslation(0f, 0f, new Vector3(0.45f, 1.50f, -0.40f));
        ReadOnlySpan<ControllerGrabInput> frame3 =
        [
            new ControllerGrabInput(1, ctrlPoseMoved, IsGrabPressed: true, ThumbstickY: 1.0f)
        ];
        var r3 = interactor.Update(settings, hmdPose, frame3, 0.10f);
        Assert.True(r3.IsGrabbing);
        Assert.Equal(0.45f, r3.CurrentPose.Position.X, 2);
        Assert.Equal(1.50f, r3.CurrentPose.Position.Y, 2);
        // Pushed farther away than initial -1.40m Z
        Assert.True(r3.CurrentPose.Position.Z < -1.45f);
        // Auto-facing HMD at X=0 from X=+0.45 means Yaw should be negative (angling left toward viewer)
        Assert.True(r3.CurrentPose.YawDegrees < -10f);

        // Frame 4: Release grab button -> commits final pose
        ReadOnlySpan<ControllerGrabInput> frame4 =
        [
            new ControllerGrabInput(1, ctrlPoseMoved, IsGrabPressed: false, ThumbstickY: 0f)
        ];
        var r4 = interactor.Update(settings, hmdPose, frame4, 0.016f);
        Assert.False(r4.IsGrabbing);
        Assert.NotNull(r4.CommittedPose);

        var updatedSettings = r4.CommittedPose.Value.ApplyTo(settings);
        Assert.Equal(0.45f, updatedSettings.WorldPositionX, 2);
        Assert.Equal(1.50f, updatedSettings.WorldPositionY, 2);
    }

    [Fact]
    public void WorldOverlayGrabInteractor_supports_direct_hand_proximity_grab()
    {
        var interactor = new WorldOverlayGrabInteractor();
        var settings = new LyricsOverlaySettings
        {
            Enabled = true,
            AttachmentMode = OverlayAttachmentMode.WorldLocked,
            EnableWorldControllerGrab = true,
            WorldPositionX = 0f,
            WorldPositionY = 1.30f,
            WorldPositionZ = -1.20f,
        };

        // Controller placed right on the overlay surface (within 0.10m proximity), even if pointing away (+90 deg yaw)
        var ctrlPoseNear = OverlayTransformHelper.BuildYawPitchTranslation(90f, 0f, new Vector3(0.05f, 1.30f, -1.12f));

        ReadOnlySpan<ControllerGrabInput> hoverFrame =
        [
            new ControllerGrabInput(2, ctrlPoseNear, IsGrabPressed: false, ThumbstickY: 0f)
        ];
        var rHover = interactor.Update(settings, null, hoverFrame, 0.016f);
        Assert.True(rHover.IsHovered);

        ReadOnlySpan<ControllerGrabInput> grabFrame =
        [
            new ControllerGrabInput(2, ctrlPoseNear, IsGrabPressed: true, ThumbstickY: 0f)
        ];
        var rGrab = interactor.Update(settings, null, grabFrame, 0.016f);
        Assert.True(rGrab.IsGrabbing);
    }

    [Fact]
    public void LyricsRenderer_renders_world_grab_visual_feedback_without_error()
    {
        using var renderer = new LyricsRenderer();
        var frame = new LyricsRenderFrame
        {
            Track = new TrackInfo("Test", "Artist"),
            IsWorldGrabHovered = true,
            IsWorldGrabActive = true,
            Settings = new LyricsOverlaySettings { TextureWidth = 800, TextureHeight = 240 },
        };

        var bmp = renderer.RenderFrame(frame);
        Assert.Equal(800, bmp.Width);
        Assert.True(renderer.IsAnimating);
    }
}
