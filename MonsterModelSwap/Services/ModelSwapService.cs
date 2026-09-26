using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace MonsterModelSwap.Services;

/// <summary>
/// Owns the "desired look" (ModelChara id + height) and makes sure every actor that represents the
/// local player carries it. The game recreates the player actor on zoning, when entering/leaving
/// cutscenes and gpose, and sometimes spawns a separate cutscene copy in the 200+ object table range,
/// so we never hold onto a pointer: every check re-resolves actors through the object table.
/// </summary>
public sealed unsafe class ModelSwapService : IDisposable
{
    /// <summary>Frames to leave an actor alone after we forced a redraw so the game can rebuild its draw object.</summary>
    private const int RedrawCooldownFrames = 20;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly CameraScaleService camera;

    private readonly Dictionary<nint, int> redrawCooldown = new();
    private readonly List<nint> cooldownExpired = new();

    private bool pendingReapply;

    public ModelSwapService(
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IObjectTable objects,
        IPluginLog log,
        Configuration config,
        CameraScaleService camera)
    {
        this.camera = camera;
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.objects = objects;
        this.log = log;
        this.config = config;

        framework.Update += OnFrameworkUpdate;
        clientState.TerritoryChanged += OnTerritoryChanged;
        clientState.Login += OnLogin;
        condition.ConditionChange += OnConditionChange;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        clientState.Login -= OnLogin;
        condition.ConditionChange -= OnConditionChange;
        camera.Reset();
    }

    /// <summary>True while a swap is in effect and the persistence loop should run.</summary>
    public bool Active { get; private set; }

    /// <summary>ModelChara id we are enforcing. 0 means "player's own model".</summary>
    public int TargetModelId { get; private set; }

    /// <summary>ModelScale we are enforcing.</summary>
    public float TargetHeight { get; private set; } = 1.0f;

    /// <summary>Snapshot of the actor before the very first apply; used for a clean revert.</summary>
    public int? OriginalModelId { get; private set; }
    public float? OriginalHeight { get; private set; }

    public int ReapplyCount { get; private set; }
    public DateTime? LastReapply { get; private set; }
    public string LastReason { get; private set; } = string.Empty;

    /// <summary>Current ModelCharaId of the main local player actor, or null if unavailable.</summary>
    public int? CurrentModelId
    {
        get
        {
            var chara = GetLocalCharacter();
            return chara is null ? null : chara->ModelContainer.ModelCharaId;
        }
    }

    public float? CurrentHeight
    {
        get
        {
            var chara = GetLocalCharacter();
            return chara is null ? null : chara->ModelScale;
        }
    }

    public void Apply(int modelId, float height)
    {
        if (modelId < 0) return;

        var chara = GetLocalCharacter();
        if (chara is null)
        {
            log.Warning("Apply requested but local player is not available; will apply on next frame.");
        }
        else if (OriginalModelId is null)
        {
            OriginalModelId = chara->ModelContainer.ModelCharaId;
            OriginalHeight = chara->ModelScale;
            log.Information("Stored original model #{Model} scale {Scale}", OriginalModelId, OriginalHeight);
        }

        TargetModelId = modelId;
        TargetHeight = Math.Clamp(height, 0.05f, 20f);
        Active = true;
        pendingReapply = true;
        redrawCooldown.Clear();
        log.Information("Applying model #{Model} height {Height:0.00}", modelId, TargetHeight);
        ReapplyAll("manual apply", force: true);
    }

    /// <summary>Change only the height. Cheap: no redraw is required for a scale change.</summary>
    public void SetHeight(float height)
    {
        TargetHeight = Math.Clamp(height, 0.05f, 20f);
        if (!Active)
        {
            // Allow using the height slider standalone without a model swap.
            var chara = GetLocalCharacter();
            if (chara is null) return;
            OriginalModelId ??= chara->ModelContainer.ModelCharaId;
            OriginalHeight ??= chara->ModelScale;
            TargetModelId = chara->ModelContainer.ModelCharaId;
            Active = true;
        }

        ReapplyAll("height change", force: false);
    }

    public void Revert()
    {
        var model = OriginalModelId ?? 0;
        var height = OriginalHeight ?? 1.0f;
        log.Information("Reverting to original model #{Model} scale {Scale}", model, height);

        TargetModelId = model;
        TargetHeight = height;
        ReapplyAll("revert", force: true);

        camera.Reset();
        Active = false;
        pendingReapply = false;
        OriginalModelId = null;
        OriginalHeight = null;
        redrawCooldown.Clear();
    }

    /// <summary>Force a redraw of every matching actor with the current target (debug helper).</summary>
    public void ForceRedraw() => ReapplyAll("forced redraw", force: true);

    private void OnLogin()
    {
        if (config.ApplyOnLogin && config.SelectedModelId > 0)
        {
            log.Information("Login detected; scheduling apply of #{Model}", config.SelectedModelId);
            Apply(config.SelectedModelId, config.Height);
        }
    }

    private void OnTerritoryChanged(uint territory)
    {
        if (!Active) return;
        // The actor is torn down and rebuilt during zoning. Don't touch it now; mark for the next frame
        // where the local player resolves with a draw object.
        pendingReapply = true;
        redrawCooldown.Clear();
        log.Debug("Territory changed to {Territory}; re-apply pending.", territory);
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (!Active) return;
        switch (flag)
        {
            case ConditionFlag.WatchingCutscene:
            case ConditionFlag.WatchingCutscene78:
            case ConditionFlag.OccupiedInCutSceneEvent:
            case ConditionFlag.BetweenAreas:
            case ConditionFlag.BetweenAreas51:
            case ConditionFlag.BoundByDuty:
            case ConditionFlag.OccupiedInEvent:
            case ConditionFlag.OccupiedInQuestEvent:
                pendingReapply = true;
                redrawCooldown.Clear();
                log.Debug("Condition {Flag}={Value}; re-apply pending.", flag, value);
                break;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        TickCooldowns();

        if (!Active) return;

        if (config.ScaleCamera) camera.Update(TargetHeight);
        else camera.Reset();

        if (!config.Persist && !pendingReapply) return;
        if (config.PauseInDuty && condition[ConditionFlag.BoundByDuty]) return;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]) return;

        var reason = pendingReapply
            ? "actor recreated (zone/cutscene/condition)"
            : "per-frame mismatch";

        if (ReapplyAll(reason, force: false))
            pendingReapply = false;
    }

    /// <summary>
    /// Walks the object table, finds every actor that is "the local player" (main actor plus any
    /// cutscene/gpose copies) and writes the target model/scale to any that differ.
    /// Returns true when at least one actor with a draw object was inspected.
    /// </summary>
    private bool ReapplyAll(string reason, bool force)
    {
        var local = objects.LocalPlayer;
        if (local is null || local.Address == nint.Zero) return false;

        var localName = local.Name.TextValue;
        var localWorld = local.HomeWorld.RowId;
        var inspected = false;

        foreach (var obj in objects)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (!IsLocalPlayerCopy(pc, local, localName, localWorld)) continue;

            var chara = (Character*)pc.Address;
            if (chara is null) continue;

            // Actor exists but has no draw object yet (mid-spawn / mid-redraw) — come back next frame.
            if (chara->GameObject.DrawObject is null) continue;
            inspected = true;

            if (!force && redrawCooldown.ContainsKey(pc.Address)) continue;

            ApplyTo(chara, pc, reason, force);
        }

        return inspected;
    }

    private void ApplyTo(Character* chara, IPlayerCharacter pc, string reason, bool force)
    {
        var modelChanged = false;
        var scaleChanged = false;

        if (force || chara->ModelContainer.ModelCharaId != TargetModelId)
        {
            chara->ModelContainer.ModelCharaId = TargetModelId;
            modelChanged = true;
        }

        if (force || Math.Abs(chara->ModelScale - TargetHeight) > 0.0005f)
        {
            chara->ModelScale = TargetHeight;
            scaleChanged = true;
        }

        var draw = chara->GameObject.DrawObject;
        // The game may rebuild the draw object's transform from ModelScale on its own schedule; keep the
        // visible scale in sync silently so a slider drag shows up immediately without a redraw.
        if (draw is not null && Math.Abs(draw->Object.Scale.X - TargetHeight) > 0.0005f)
            draw->Object.Scale = new Vector3(TargetHeight);

        if (modelChanged)
        {
            // Same trick as Anamnesis' "actor refresh": tear the draw object down and let the game
            // rebuild it from the (now modified) Character fields.
            chara->GameObject.DisableDraw();
            chara->GameObject.EnableDraw();
            redrawCooldown[pc.Address] = RedrawCooldownFrames;
        }

        if (modelChanged || scaleChanged)
        {
            ReapplyCount++;
            LastReapply = DateTime.Now;
            LastReason = reason;
            if (config.LogReapplies)
            {
                log.Information(
                    "Re-applied to actor #{Index} @ {Addr:X} (model={Model} scale={Scale:0.00}) — {Reason}",
                    pc.ObjectIndex, pc.Address, TargetModelId, TargetHeight, reason);
            }
        }
    }

    private static bool IsLocalPlayerCopy(IPlayerCharacter pc, IPlayerCharacter local, string localName, uint localWorld)
    {
        if (pc.Address == local.Address) return true;
        if (pc.ObjectKind != ObjectKind.Pc) return false;

        // Cutscene copies live at index 200+ and share the player's entity id or, failing that, name+world.
        if (pc.EntityId != 0 && pc.EntityId != 0xE0000000 && pc.EntityId == local.EntityId) return true;
        return pc.ObjectIndex >= 200
            && pc.HomeWorld.RowId == localWorld
            && string.Equals(pc.Name.TextValue, localName, StringComparison.Ordinal);
    }

    private Character* GetLocalCharacter()
    {
        var local = objects.LocalPlayer;
        return local is null || local.Address == nint.Zero ? null : (Character*)local.Address;
    }

    private void TickCooldowns()
    {
        if (redrawCooldown.Count == 0) return;
        cooldownExpired.Clear();
        cooldownExpired.AddRange(redrawCooldown.Keys);
        foreach (var addr in cooldownExpired)
        {
            var frames = redrawCooldown[addr] - 1;
            if (frames <= 0) redrawCooldown.Remove(addr);
            else redrawCooldown[addr] = frames;
        }
    }
}
