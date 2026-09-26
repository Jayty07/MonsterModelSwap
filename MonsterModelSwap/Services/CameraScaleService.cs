using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace MonsterModelSwap.Services;

/// <summary>
/// Scales the third-person camera with the player's height so the pivot stays on the (now taller or
/// shorter) model and zoom range grows/shrinks with it. Multiplies the game's own look-at height offset
/// and the min/max/current zoom distances by the height factor; the game only recomputes the look-at
/// offset when the target skeleton changes (tracked via <see cref="Camera.SavedModelSkeletonId"/>), so
/// the write sticks between changes. Writes happen every frame and are undone by <see cref="Reset"/>.
/// </summary>
public sealed unsafe class CameraScaleService
{
    // Not yet mapped in FFXIVClientStructs; same offset Cammy uses for the target look-at height offset
    // (sits just before Camera.SavedModelSkeletonId @ 0x23C, which is mapped and used as a layout sanity check).
    private const int LookAtHeightOffsetOffset = 0x234;

    private readonly IPluginLog log;

    private bool engaged;
    private float appliedFactor = 1f;
    private uint baselineSkeleton;
    private float baselineLookAt;
    private float lastWrittenLookAt;
    private float baselineMin;
    private float baselineMax;

    public CameraScaleService(IPluginLog log) => this.log = log;

    public bool Engaged => engaged;

    /// <summary>Called every frame with the height factor currently enforced on the player.</summary>
    public void Update(float factor)
    {
        var cam = GetWorldCamera();
        if (cam is null) return;

        var lookAt = (float*)((byte*)cam + LookAtHeightOffsetOffset);

        if (!engaged)
        {
            if (Math.Abs(factor - 1f) < 0.0005f) return;
            baselineSkeleton = cam->SavedModelSkeletonId;
            baselineLookAt = *lookAt;
            lastWrittenLookAt = baselineLookAt;
            baselineMin = cam->MinDistance;
            baselineMax = cam->MaxDistance;
            appliedFactor = 1f;
            engaged = true;
            log.Debug("Camera scaling engaged (lookAt={LookAt:0.###} min={Min} max={Max})", baselineLookAt, baselineMin, baselineMax);
        }

        // The game rewrote the look-at offset (new skeleton, or some transition reset it): re-capture the
        // unscaled value before scaling it again.
        if (cam->SavedModelSkeletonId != baselineSkeleton || Math.Abs(*lookAt - lastWrittenLookAt) > 0.0005f)
        {
            baselineSkeleton = cam->SavedModelSkeletonId;
            baselineLookAt = *lookAt;
        }

        lastWrittenLookAt = baselineLookAt * factor;
        *lookAt = lastWrittenLookAt;

        if (Math.Abs(factor - appliedFactor) > 0.0005f)
        {
            var ratio = factor / appliedFactor;
            cam->MinDistance = baselineMin * factor;
            cam->MaxDistance = baselineMax * factor;
            cam->Distance = Math.Clamp(cam->Distance * ratio, cam->MinDistance, cam->MaxDistance);
            cam->InterpDistance = cam->Distance;
            appliedFactor = factor;
        }
    }

    /// <summary>Restore the game's own camera values.</summary>
    public void Reset()
    {
        if (!engaged) return;
        engaged = false;

        var cam = GetWorldCamera();
        if (cam is null) return;

        var lookAt = (float*)((byte*)cam + LookAtHeightOffsetOffset);
        *lookAt = baselineLookAt;
        cam->MinDistance = baselineMin;
        cam->MaxDistance = baselineMax;
        cam->Distance = Math.Clamp(cam->Distance / appliedFactor, baselineMin, baselineMax);
        cam->InterpDistance = cam->Distance;
        appliedFactor = 1f;
        log.Debug("Camera scaling reset");
    }

    private static Camera* GetWorldCamera()
    {
        var manager = CameraManager.Instance();
        return manager is null ? null : manager->Camera;
    }
}
