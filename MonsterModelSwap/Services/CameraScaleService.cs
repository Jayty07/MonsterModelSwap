using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace MonsterModelSwap.Services;

/// <summary>
/// Scales the third-person camera with the player's height. The look-at point is raised/lowered by
/// hooking the camera's GetCameraPosition virtual (the game recomputes its look-at height offset every
/// frame, so writing the field directly does nothing) and the min/max/current zoom distances are
/// multiplied by the height factor each frame. Undone by <see cref="Reset"/>.
/// </summary>
public sealed unsafe class CameraScaleService : IDisposable
{
    // Not yet mapped in FFXIVClientStructs; same offsets Cammy uses. LookAtHeightOffset sits just before
    // Camera.SavedModelSkeletonId @ 0x23C (mapped, used as a layout sanity check).
    private const int LookAtHeightOffsetOffset = 0x234;
    private const int GetCameraPositionVfIndex = 16;

    private delegate void GetCameraPositionDelegate(Camera* camera, GameObject* target, Vector3* position, byte swapPerson);

    private readonly IPluginLog log;
    private readonly IObjectTable objects;
    private readonly IGameInteropProvider interop;

    private Hook<GetCameraPositionDelegate>? getCameraPositionHook;

    private bool engaged;
    private float factor = 1f;
    private float appliedFactor = 1f;
    private float baselineMin;
    private float baselineMax;

    public CameraScaleService(IPluginLog log, IObjectTable objects, IGameInteropProvider interop)
    {
        this.log = log;
        this.objects = objects;
        this.interop = interop;
    }

    public bool Engaged => engaged;

    /// <summary>Called every frame with the height factor currently enforced on the player.</summary>
    public void Update(float newFactor)
    {
        var cam = GetWorldCamera();
        if (cam is null) return;

        EnsureHook(cam);

        if (!engaged)
        {
            if (Math.Abs(newFactor - 1f) < 0.0005f) return;
            baselineMin = cam->MinDistance;
            baselineMax = cam->MaxDistance;
            appliedFactor = 1f;
            engaged = true;
            log.Debug("Camera scaling engaged (min={Min} max={Max})", baselineMin, baselineMax);
        }

        factor = newFactor;

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
        factor = 1f;

        var cam = GetWorldCamera();
        if (cam is null) return;

        cam->MinDistance = baselineMin;
        cam->MaxDistance = baselineMax;
        cam->Distance = Math.Clamp(cam->Distance / appliedFactor, baselineMin, baselineMax);
        cam->InterpDistance = cam->Distance;
        appliedFactor = 1f;
        log.Debug("Camera scaling reset");
    }

    public void Dispose()
    {
        Reset();
        getCameraPositionHook?.Dispose();
        getCameraPositionHook = null;
    }

    private void EnsureHook(Camera* cam)
    {
        if (getCameraPositionHook is not null) return;

        var vtbl = (nint*)cam->VirtualTable;
        if (vtbl is null) return;

        try
        {
            getCameraPositionHook = interop.HookFromAddress<GetCameraPositionDelegate>(vtbl[GetCameraPositionVfIndex], GetCameraPositionDetour);
            getCameraPositionHook.Enable();
            log.Debug("Hooked Camera::GetCameraPosition @ {Addr:X}", vtbl[GetCameraPositionVfIndex]);
        }
        catch (Exception e)
        {
            log.Error(e, "Failed to hook Camera::GetCameraPosition; camera pivot will not follow height");
            getCameraPositionHook = null;
        }
    }

    private void GetCameraPositionDetour(Camera* camera, GameObject* target, Vector3* position, byte swapPerson)
    {
        getCameraPositionHook!.Original(camera, target, position, swapPerson);

        if (!engaged || position is null || target is null) return;

        var local = objects.LocalPlayer;
        if (local is null || (nint)target != local.Address) return;

        // The game's look-at height is derived from the unscaled skeleton: add the extra height the
        // scaled model actually has.
        var baseOffset = *(float*)((byte*)camera + LookAtHeightOffsetOffset);
        position->Y += baseOffset * (factor - 1f);
    }

    private static Camera* GetWorldCamera()
    {
        var manager = CameraManager.Instance();
        return manager is null ? null : manager->Camera;
    }
}
