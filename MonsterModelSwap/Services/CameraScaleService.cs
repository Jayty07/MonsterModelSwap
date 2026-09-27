using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;

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
    private const int SceneCameraUpdateRenderVfIndex = 4;

    private delegate void GetCameraPositionDelegate(Camera* camera, GameObject* target, Vector3* position, byte swapPerson);
    private delegate void SceneCameraUpdateRenderDelegate(SceneCamera* camera);

    private Hook<SceneCameraUpdateRenderDelegate>? updateRenderHook;

    /// <summary>Vertical world-space shift applied to every scene camera while <see cref="CutsceneActive"/> is true.</summary>
    public float CutsceneOffset { get; set; }
    public bool CutsceneActive { get; set; }
    /// <summary>Only shots whose look-at point lies within this XZ distance of a local actor are shifted.</summary>
    public float CutsceneRange { get; set; } = 3f;
    /// <summary>Only shots whose look-at point is below feet + this height are shifted (i.e. aimed at the floor).</summary>
    public float CutsceneLowAim { get; set; } = 1f;
    /// <summary>World positions of the local actor and its cutscene copies, refreshed every tick.</summary>
    public readonly System.Collections.Generic.List<Vector3> LocalActorPositions = new();
    private long cutsceneRenderCalls;
    private long cutsceneShiftedCalls;
    private nint lastSceneCamera;

    private readonly IPluginLog log;
    private readonly IObjectTable objects;
    private readonly IGameInteropProvider interop;

    private Hook<GetCameraPositionDelegate>? getCameraPositionHook;

    private bool engaged;
    private float factor = 1f;
    private float appliedFactor = 1f;
    private float appliedMaxFactor = 1f;

    /// <summary>Max zoom-out is never scaled below this multiple of the game's normal max distance.</summary>
    public float MaxZoomFloor { get; set; } = 1f;
    private float baselineMin;
    private float baselineMax;

    public CameraScaleService(IPluginLog log, IObjectTable objects, IGameInteropProvider interop)
    {
        this.log = log;
        this.objects = objects;
        this.interop = interop;
    }

    public bool Engaged => engaged;

    /// <summary>Install the camera hook as soon as the world camera exists (needed for ExtraHeight even without a swap).</summary>
    public void Tick()
    {
        var cam = GetWorldCamera();
        if (cam is not null) EnsureHook(cam);
    }

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

        var maxFactor = Math.Max(factor, MaxZoomFloor);
        if (Math.Abs(factor - appliedFactor) > 0.0005f || Math.Abs(maxFactor - appliedMaxFactor) > 0.0005f)
        {
            var ratio = factor / appliedFactor;
            cam->MinDistance = baselineMin * factor;
            cam->MaxDistance = baselineMax * maxFactor;
            cam->Distance = Math.Clamp(cam->Distance * ratio, cam->MinDistance, cam->MaxDistance);
            cam->InterpDistance = cam->Distance;
            appliedFactor = factor;
            appliedMaxFactor = maxFactor;
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
        appliedMaxFactor = 1f;
        log.Debug("Camera scaling reset");
    }

    public void Dispose()
    {
        Reset();
        getCameraPositionHook?.Dispose();
        getCameraPositionHook = null;
        updateRenderHook?.Dispose();
        updateRenderHook = null;
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
            log.Information("Hooked Camera::GetCameraPosition @ {Addr:X}", vtbl[GetCameraPositionVfIndex]);

            var sceneVtbl = (nint*)cam->SceneCamera.VirtualTable;
            if (sceneVtbl is not null)
            {
                updateRenderHook = interop.HookFromAddress<SceneCameraUpdateRenderDelegate>(sceneVtbl[SceneCameraUpdateRenderVfIndex], UpdateRenderDetour);
                updateRenderHook.Enable();
                log.Information("Hooked Scene::Camera::UpdateRender @ {Addr:X}", sceneVtbl[SceneCameraUpdateRenderVfIndex]);
            }
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

        hookCalls++;
        if (position is null || target is null) return;

        var local = objects.LocalPlayer;
        if (local is null || (nint)target != local.Address) return;

        var feetY = target->Position.Y;
        var pivotAboveFeet = position->Y - feetY;
        lastPivotAboveFeet = pivotAboveFeet;
        lastFieldOffset = *(float*)((byte*)camera + LookAtHeightOffsetOffset);

        if (!engaged && Math.Abs(ExtraHeight) < 0.0005f) return;

        // The game's pivot is computed from the unscaled skeleton: scale its height above the feet
        // by the model factor, then add the user's manual offset.
        position->Y = feetY + pivotAboveFeet * factor + ExtraHeight;
    }

    /// <summary>
    /// Cutscene cameras drive the scene camera directly (not via Camera::GetCameraPosition), so the whole
    /// camera is shifted vertically for the duration of the render update and restored afterwards.
    /// </summary>
    private void UpdateRenderDetour(SceneCamera* camera)
    {
        if (!CutsceneActive || Math.Abs(CutsceneOffset) < 0.0005f || camera is null)
        {
            updateRenderHook!.Original(camera);
            return;
        }

        cutsceneRenderCalls++;
        lastSceneCamera = (nint)camera;
        var pos = camera->Position;
        var look = camera->LookAtVector;
        if (!TargetsLocalActor(look))
        {
            updateRenderHook!.Original(camera);
            return;
        }

        cutsceneShiftedCalls++;
        camera->Position.Y += CutsceneOffset;
        camera->LookAtVector.Y += CutsceneOffset;
        updateRenderHook!.Original(camera);
        camera->Position = pos;
        camera->LookAtVector = look;
    }

    private bool TargetsLocalActor(FFXIVClientStructs.FFXIV.Common.Math.Vector3 look)
    {
        var r2 = CutsceneRange * CutsceneRange;
        foreach (var p in LocalActorPositions)
        {
            var dx = look.X - p.X;
            var dz = look.Z - p.Z;
            if (dx * dx + dz * dz > r2) continue;
            if (look.Y - p.Y > CutsceneLowAim) continue;
            return true;
        }
        return false;
    }

    /// <summary>Additional manual camera pivot offset (world units) applied on top of the scaling.</summary>
    public float ExtraHeight { get; set; }

    private long hookCalls;
    private float lastPivotAboveFeet;
    private float lastFieldOffset;

    public string DebugInfo()
    {
        var cam = GetWorldCamera();
        return $"hook={(getCameraPositionHook is null ? "none" : getCameraPositionHook.IsEnabled ? "enabled" : "disabled")} " +
               $"calls={hookCalls} engaged={engaged} factor={factor:0.###} extra={ExtraHeight:0.###} zoomFloor={MaxZoomFloor:0.###} " +
               $"pivotAboveFeet={lastPivotAboveFeet:0.###} field0x234={lastFieldOffset:0.###} " +
               $"renderHook={(updateRenderHook is null ? "none" : updateRenderHook.IsEnabled ? "enabled" : "disabled")} cutscene={CutsceneActive} cutOffset={CutsceneOffset:0.###} cutCalls={cutsceneRenderCalls} shifted={cutsceneShiftedCalls} range={CutsceneRange:0.##} lowAim={CutsceneLowAim:0.##} actors={LocalActorPositions.Count} lastSceneCam={lastSceneCamera:X} " +
               $"dist={(cam is null ? -1 : cam->Distance):0.##} min={(cam is null ? -1 : cam->MinDistance):0.##} max={(cam is null ? -1 : cam->MaxDistance):0.##}";
    }

    private static Camera* GetWorldCamera()
    {
        var manager = CameraManager.Instance();
        return manager is null ? null : manager->Camera;
    }
}
