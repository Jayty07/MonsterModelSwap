using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using CsMatrix = FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4;
using CsVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace MonsterModelSwap.Services;

/// <summary>
/// Cutscene/dialogue cameras and other systems locate points on an actor through the draw object's
/// "attach bone" virtuals (HasAttachBone / GetAttachBoneWorldLocation / GetAttachBoneWorldTransform),
/// indexed by the human skeleton's attach-point table. Monster skeletons do not carry those entries,
/// so the lookup fails and the game falls back to the model origin — the floor. This service hooks those
/// virtuals on the swapped draw object's vtable and, for the local player's actors only, answers the
/// failed lookups with a real bone of the monster skeleton (the "focus bone", normally its head).
/// </summary>
public sealed unsafe class SkeletonService : IDisposable
{
    private const int WorldTransformVfIndex = 0x88 / 8;
    private const int WorldLocationVfIndex = 0x90 / 8;
    private const int HasAttachBoneVfIndex = 0xA8 / 8;

    private delegate CsMatrix* WorldTransformDelegate(DrawObject* self, CsMatrix* result, int attachIndex);
    private delegate CsVector3* WorldLocationDelegate(DrawObject* self, int attachIndex);
    private delegate byte HasAttachBoneDelegate(DrawObject* self, int attachIndex);

    private static readonly string[] PreferredHeadBones =
    {
        "j_kao", "j_kubi", "j_sebo_c", "j_sebo_b", "j_sebo_a",
    };

    private readonly IPluginLog log;
    private readonly IGameInteropProvider interop;

    private Hook<WorldTransformDelegate>? worldTransformHook;
    private Hook<WorldLocationDelegate>? worldLocationHook;
    private Hook<HasAttachBoneDelegate>? hasAttachBoneHook;
    private nint hookedVtable;

    private readonly CsVector3* locationBuffer = (CsVector3*)NativeMemory.AllocZeroed((nuint)sizeof(CsVector3) * 4);

    private readonly HashSet<nint> localDrawObjects = new();
    private readonly Dictionary<nint, string[]> boneNameCache = new();
    private readonly Dictionary<(nint, string), int> boneIndexCache = new();

    private readonly object debugLock = new();
    private readonly Dictionary<int, (long calls, bool has, long redirected)> requested = new();
    private long totalCalls;
    private long redirectedCalls;

    /// <summary>Master switch for redirecting attach-bone lookups.</summary>
    public bool Enabled { get; set; }
    /// <summary>Redirect only while a cutscene/dialogue condition is active.</summary>
    public bool CutsceneOnly { get; set; } = true;
    public bool CutsceneActive { get; set; }
    /// <summary>When false, only lookups the game itself reports as missing are redirected; when true, every lookup is.</summary>
    public bool RedirectAll { get; set; }
    /// <summary>Bone name to answer redirected lookups with; empty = auto-pick a head-like bone.</summary>
    public string FocusBone { get; set; } = string.Empty;
    /// <summary>Extra vertical offset (model units, pre-scale) added to the focus bone position.</summary>
    public float FocusOffsetY { get; set; }

    public SkeletonService(IPluginLog log, IGameInteropProvider interop)
    {
        this.log = log;
        this.interop = interop;
    }

    public bool Hooked => hasAttachBoneHook is not null;

    public void Dispose()
    {
        worldTransformHook?.Dispose();
        worldLocationHook?.Dispose();
        hasAttachBoneHook?.Dispose();
        worldTransformHook = null;
        worldLocationHook = null;
        hasAttachBoneHook = null;
        NativeMemory.Free(locationBuffer);
    }

    /// <summary>Called every frame with the draw objects of the local player and its cutscene copies.</summary>
    public void SetLocalDrawObjects(IEnumerable<nint> drawObjects)
    {
        localDrawObjects.Clear();
        foreach (var d in drawObjects)
        {
            if (d == nint.Zero) continue;
            localDrawObjects.Add(d);
            EnsureHooks((DrawObject*)d);
        }
    }

    /// <summary>Names of every havok bone on the given draw object's primary skeleton.</summary>
    public string[] BoneNames(nint drawObject)
    {
        var pose = GetPose((DrawObject*)drawObject);
        if (pose is null || pose->Skeleton is null) return Array.Empty<string>();
        return BoneNamesFor(pose->Skeleton);
    }

    /// <summary>Names in the draw object's attach-point table (what the game resolves attach indices against).</summary>
    public string[] AttachBoneNames(nint drawObject)
    {
        var cb = (CharacterBase*)drawObject;
        if (cb is null || cb->Skeleton is null) return Array.Empty<string>();
        var skel = cb->Skeleton;
        var count = (int)Math.Min(skel->AttachBoneCount, 256u);
        if (count <= 0 || skel->AttachBones is null) return Array.Empty<string>();
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var b = skel->AttachBones + i;
            names[i] = $"{StdToString(b->BoneName.AsSpan())} (bone {b->BoneIndex})";
        }
        return names;
    }

    /// <summary>The bone that redirected lookups currently resolve to on this draw object, or null.</summary>
    public string? ResolvedFocusBone(nint drawObject)
    {
        var pose = GetPose((DrawObject*)drawObject);
        if (pose is null || pose->Skeleton is null) return null;
        var idx = ResolveFocusBoneIndex(pose);
        if (idx < 0) return null;
        var names = BoneNamesFor(pose->Skeleton);
        return idx < names.Length ? names[idx] : null;
    }

    public string DebugInfo()
    {
        var sb = new StringBuilder();
        sb.Append($"hooks={(Hooked ? "enabled" : "none")} vtable={hookedVtable:X} enabled={Enabled} cutsceneOnly={CutsceneOnly} cutscene={CutsceneActive} redirectAll={RedirectAll} focus='{FocusBone}' offY={FocusOffsetY:0.###} localDrawObjects={localDrawObjects.Count} calls={totalCalls} redirected={redirectedCalls}");
        lock (debugLock)
        {
            sb.Append(" indices=[");
            foreach (var (idx, v) in requested)
                sb.Append($"{idx}:calls={v.calls},has={v.has},redir={v.redirected}; ");
            sb.Append(']');
        }
        return sb.ToString();
    }

    private void EnsureHooks(DrawObject* draw)
    {
        if (draw is null || hasAttachBoneHook is not null) return;
        var cb = (CharacterBase*)draw;
        if (cb->GetModelType() != CharacterBase.ModelType.Monster) return;

        var vtbl = (nint*)draw->VirtualTable;
        if (vtbl is null) return;

        try
        {
            hasAttachBoneHook = interop.HookFromAddress<HasAttachBoneDelegate>(vtbl[HasAttachBoneVfIndex], HasAttachBoneDetour);
            worldLocationHook = interop.HookFromAddress<WorldLocationDelegate>(vtbl[WorldLocationVfIndex], WorldLocationDetour);
            worldTransformHook = interop.HookFromAddress<WorldTransformDelegate>(vtbl[WorldTransformVfIndex], WorldTransformDetour);
            hasAttachBoneHook.Enable();
            worldLocationHook.Enable();
            worldTransformHook.Enable();
            hookedVtable = (nint)vtbl;
            log.Information("Hooked Monster attach-bone virtuals (vtable {V:X}: has={A:X} loc={B:X} xform={C:X})",
                (nint)vtbl, vtbl[HasAttachBoneVfIndex], vtbl[WorldLocationVfIndex], vtbl[WorldTransformVfIndex]);
        }
        catch (Exception e)
        {
            log.Error(e, "Failed to hook attach-bone virtuals; cutscene focus bone redirect unavailable");
            worldTransformHook?.Dispose();
            worldLocationHook?.Dispose();
            hasAttachBoneHook?.Dispose();
            worldTransformHook = null;
            worldLocationHook = null;
            hasAttachBoneHook = null;
        }
    }

    private bool ShouldRedirect(DrawObject* self, int attachIndex, out bool originalHas)
    {
        originalHas = hasAttachBoneHook!.Original(self, attachIndex) != 0;
        if (!localDrawObjects.Contains((nint)self)) return false;

        totalCalls++;
        Record(attachIndex, originalHas);

        if (!Enabled) return false;
        if (CutsceneOnly && !CutsceneActive) return false;
        return RedirectAll || !originalHas;
    }

    private byte HasAttachBoneDetour(DrawObject* self, int attachIndex)
    {
        if (!ShouldRedirect(self, attachIndex, out var has)) return (byte)(has ? 1 : 0);
        var pose = GetPose(self);
        if (pose is null || ResolveFocusBoneIndex(pose) < 0) return (byte)(has ? 1 : 0);
        Redirected(attachIndex);
        return 1;
    }

    private CsVector3* WorldLocationDetour(DrawObject* self, int attachIndex)
    {
        if (!ShouldRedirect(self, attachIndex, out _)) return worldLocationHook!.Original(self, attachIndex);
        if (!TryFocusBoneWorld(self, out var pos, out _)) return worldLocationHook!.Original(self, attachIndex);

        Redirected(attachIndex);
        locationBuffer->X = pos.X;
        locationBuffer->Y = pos.Y;
        locationBuffer->Z = pos.Z;
        return locationBuffer;
    }

    private CsMatrix* WorldTransformDetour(DrawObject* self, CsMatrix* result, int attachIndex)
    {
        if (!ShouldRedirect(self, attachIndex, out _)) return worldTransformHook!.Original(self, result, attachIndex);
        if (result is null || !TryFocusBoneWorld(self, out var pos, out var rot)) return worldTransformHook!.Original(self, result, attachIndex);

        Redirected(attachIndex);
        var m = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
        *(Matrix4x4*)result = m;
        return result;
    }

    private bool TryFocusBoneWorld(DrawObject* self, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.Identity;

        var cb = (CharacterBase*)self;
        var skel = cb->Skeleton;
        var pose = GetPose(self);
        if (skel is null || pose is null) return false;

        var boneIdx = ResolveFocusBoneIndex(pose);
        if (boneIdx < 0) return false;

        var t = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
        if (t is null) return false;

        var local = new Vector3(t->Translation.X, t->Translation.Y + FocusOffsetY, t->Translation.Z);
        var tr = skel->Transform;
        var scale = new Vector3(tr.Scale.X, tr.Scale.Y, tr.Scale.Z);
        var rot = new Quaternion(tr.Rotation.X, tr.Rotation.Y, tr.Rotation.Z, tr.Rotation.W);
        position = new Vector3(tr.Position.X, tr.Position.Y, tr.Position.Z) + Vector3.Transform(local * scale, rot);
        rotation = rot * new Quaternion(t->Rotation.X, t->Rotation.Y, t->Rotation.Z, t->Rotation.W);
        return true;
    }

    private static hkaPose* GetPose(DrawObject* self)
    {
        if (self is null) return null;
        var cb = (CharacterBase*)self;
        var skel = cb->Skeleton;
        if (skel is null || skel->PartialSkeletonCount == 0 || skel->PartialSkeletons is null) return null;
        return skel->PartialSkeletons[0].GetHavokPose(0);
    }

    private int ResolveFocusBoneIndex(hkaPose* pose)
    {
        var hk = pose->Skeleton;
        if (hk is null) return -1;
        var key = ((nint)hk, FocusBone);
        if (boneIndexCache.TryGetValue(key, out var cached)) return cached;

        var names = BoneNamesFor(hk);
        var idx = -1;
        if (!string.IsNullOrWhiteSpace(FocusBone))
            idx = Array.FindIndex(names, n => string.Equals(n, FocusBone, StringComparison.OrdinalIgnoreCase));

        if (idx < 0)
        {
            foreach (var pref in PreferredHeadBones)
            {
                idx = Array.IndexOf(names, pref);
                if (idx >= 0) break;
            }
        }

        if (idx < 0) idx = Array.FindIndex(names, n => ContainsAny(n, "kao", "head", "atama"));
        if (idx < 0) idx = Array.FindIndex(names, n => ContainsAny(n, "kubi", "neck"));
        if (idx < 0) idx = HighestBone(pose, names.Length);

        boneIndexCache[key] = idx;
        return idx;
    }

    private static int HighestBone(hkaPose* pose, int count)
    {
        var best = -1;
        var bestY = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            var t = pose->AccessBoneModelSpace(i, hkaPose.PropagateOrNot.DontPropagate);
            if (t is null) continue;
            if (t->Translation.Y > bestY)
            {
                bestY = t->Translation.Y;
                best = i;
            }
        }
        return best;
    }

    private string[] BoneNamesFor(hkaSkeleton* hk)
    {
        if (boneNameCache.TryGetValue((nint)hk, out var cached)) return cached;
        var count = Math.Clamp(hk->Bones.Length, 0, 2048);
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = hk->Bones[i].Name.String ?? string.Empty;
        boneNameCache[(nint)hk] = names;
        return names;
    }

    private static bool ContainsAny(string s, params string[] parts)
    {
        foreach (var p in parts)
            if (s.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string StdToString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private void Record(int attachIndex, bool has)
    {
        lock (debugLock)
        {
            requested.TryGetValue(attachIndex, out var v);
            requested[attachIndex] = (v.calls + 1, has, v.redirected);
        }
    }

    private void Redirected(int attachIndex)
    {
        redirectedCalls++;
        lock (debugLock)
        {
            if (requested.TryGetValue(attachIndex, out var v))
                requested[attachIndex] = (v.calls, v.has, v.redirected + 1);
        }
    }
}
