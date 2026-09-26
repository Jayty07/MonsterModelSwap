# Plan: monster animations for abilities and cutscenes

Status: proposal, no code yet. Applies to MonsterModelSwap >= 0.3.0.

## 1. Why it doesn't work today

- The swap only changes `Character.ModelContainer.ModelCharaId`. The game then builds the
  draw object from the monster's skeleton (`chara/monster/mXXXX/skeleton/base/b0001/skl_m..sklb`),
  so idle/walk/run come from the monster (those are "resident" animations in the monster's
  own `bt_common` set).
- Everything else is driven by **ActionTimeline rows** (Excel sheet `ActionTimeline`, ~ 10k rows).
  Each row has a `Key` such as `ws/s01/wsn_s01_009` (weaponskill) or `emote/joy` and a `Slot`
  (base / upper-body / facial / add / lips). When a row plays on a character the scheduler
  loads `chara/<race-or-monster>/<skel>/animation/a0001/bt_common/<Key>.pap`.
- Player actions (`Action.AnimationStart`, `Action.AnimationEnd`, `Action.ActionTimelineHit`)
  reference **human** keys (`ws/...`, `magic/...`, `battle/...`). For a monster skeleton those
  `.pap` files do not exist, so the load fails and the actor stays in its idle/battle pose.
- Cutscenes are `SchedulerTimeline`s (`.cutb` via `ScheduleManagement.CutSceneController`) whose
  actor tracks also reference human `ActionTimeline` keys (`emote/...`, `cutscene`-type rows,
  `ex/` rows). Same failure: the monster has no such file, so the cutscene copy idles.
- Monsters' own attacks are separate rows with keys such as `mon_sp/mXXXX/...` (the exact
  key format must be confirmed against the sheet at runtime; see step 2).

Conclusion: the fix is **timeline substitution**, not file redirection. Redirecting the
human `.pap` to a monster `.pap` (Penumbra style) is not viable because `.pap` data is bound
to the skeleton's bone set; a human file on a monster skeleton is garbage.

## 2. Build a per-monster animation catalogue (offline + runtime)

Goal: for each `ModelChara` monster, know which `ActionTimeline` rows it can actually play.

1. At plugin load, iterate `ActionTimeline` through `IDataManager.GetExcelSheet<ActionTimeline>()`.
2. For every row, compute the monster path for the selected model
   (`chara/monster/m{ModelId:D4}/animation/a{AnimBase:D4}/bt_common/{Key}.pap`) and test with
   `IDataManager.FileExists(path)`. Cache the result per model (dictionary `modelId -> HashSet<ushort rowId>`).
   Cost: ~10k `FileExists` per model, fast enough to run once per selection on a background thread
   (pure Lumina index lookup, no game memory).
3. Categorise the available rows by `Key` prefix / `Type` / `Slot`: idle, battle idle, attack
   (`mon_sp` rows), casting, hit-react, death, "special" (emote-like) rows. Persist the
   classification to `Data/anim-<model>.json` only as an optional cache; the runtime scan is the
   source of truth so new patches keep working.
4. Expose the list in the UI (new "Animations" tab): click a row to preview it with
   `Character.Timeline.PlayActionTimeline(rowId, ...)` — this is the same call Brio/Ktisis use and
   is useful on its own for screenshots.

Deliverable: catalogue service + preview tab. Estimated 1 session.

## 3. Ability animations (combat)

### 3a. Hook point

Hook `TimelineContainer.PlayActionTimeline(ushort timelineId, ushort param, void* a3)`
(or, one level down, `ActionTimelineSequencer.PlayTimeline`). Both are exported by
FFXIVClientStructs so they can be hooked through `IGameInteropProvider.HookFromAddress`
using `Addresses`/`MemberFunctionAttribute` signatures. Every action, emote, hit reaction and
cutscene actor track ends up here, so one hook covers everything.

Detour logic, executed only when `container->OwnerObject` is the local player or a
local-player cutscene copy (reuse `IsLocalPlayerCopy` from `ModelSwapService`):

```
if (!swapActive) return Original(...);
if (catalogue.CanPlay(model, timelineId)) return Original(...);   // monster already has it
var mapped = mapper.Map(timelineId);                              // see 3b
if (mapped != 0) return Original(container, mapped, param, a3);
return;                                                           // swallow: stay in idle instead of freezing mid-load
```

### 3b. Mapping human timeline -> monster timeline

Layered lookup, first match wins:

1. **User override**: `Dictionary<ushort human, ushort monster>` per model, edited in the UI
   (drag a monster animation onto an action, or "record next action" mode that captures the
   next `timelineId` and lets you pick a replacement). Saved in config.
2. **Category default**: classify the incoming row by `Key` prefix:
   - `ws/` , `battle/` attack rows -> monster "attack" pool (round-robin or random among
     `mon_sp` rows tagged as melee).
   - `magic/`, `cast/` rows (and `Action.AnimationStart` with cast bar) -> monster "cast" pool.
   - `battle/hit*`, `damage` -> monster hit-react row if present.
   - `dead`, `battle/dead*` -> monster death row.
   - `emote/` -> monster special/emote row if any, else idle.
3. **Fallback**: monster battle idle (or `0` = swallow).

Category pools come from the catalogue in step 2; defaults are guessed by key naming and can
be corrected per model in the UI.

### 3c. Timing and hit effects

- Action VFX/hit timing is driven by the *human* row's `.tmb` (`ActionTimelineHit`). After
  substitution the monster `.pap` plays but hit VFX may fire at the original timestamp. Accept
  this in v1; optionally scale `TimelineContainer.OverallSpeed` so the monster clip length
  matches the human clip length (needs clip durations, obtainable from the `.pap` header).
- Weapon timelines (`ActionTimeline.WeaponTimeline`) are irrelevant for monsters; leave alone.
- Upper-body slot rows (slot 1, e.g. `ws` while moving) must map to a full-body monster row or
  be swallowed, otherwise the sequencer can lock the upper slot. Guard: only map slot 0/1, pass
  facial/lips slots through unchanged (monsters usually ignore them).

Deliverable: hook + mapper + override UI + `/mms anim map <human> <monster>`. Estimated 1-2
sessions, plus in-game iteration with the user (the mapping quality is a tuning problem).

## 4. Cutscene animations

Cutscene copies (`ObjectIndex >= 200`) receive their tracks through the same
`PlayActionTimeline` path, so the section 3 hook already applies. Differences to handle:

1. **Speaking / facial tracks**: cutscene lips (`LipsOverride`) and facial rows target human
   face bones; pass them through (they will silently no-op on the monster).
2. **Emote-heavy content**: most cutscene tracks are `emote/`-type rows with no monster
   equivalent. Options, exposed as a per-user setting:
   - `Idle` (default): swallow unknown rows so the monster stands in idle (current behaviour,
     but deterministic).
   - `Random special`: play a random monster special/emote row for each unknown track so the
     monster "does something" during dialogue.
   - `Off`: leave cutscene copies unmapped (only main-actor combat is remapped).
3. **Base override**: for long scenes where the copy is meant to stand/sit, set
   `TimelineContainer.BaseOverride` to the monster idle row for the copy so cutscene
   `ModelState` changes don't leave it in a broken pose; clear it when the copy despawns
   (already tracked by `redrawCooldown` / `IsLocalPlayerCopy` logic).
4. **Height scaling** stays as in v0.2.5; nothing changes there.

Deliverable: cutscene mode setting + `BaseOverride` handling. Estimated 0.5 session on top of
section 3.

## 5. Risks and unknowns (verify in-game before committing to the design)

- Whether `PlayActionTimeline` is the right hook for **networked** action playback for the
  local player (it should be: `ActionEffect` handling ends in the sequencer), or whether some
  actions go through `ActionTimelineSequencer.SetSlotTimeline` directly. Plan: log both for one
  play session with `/mms animdebug` before writing the mapper.
- Monster `mon_sp` rows are often BNpc-specific (`mXXXX` in the key). Need to confirm the key
  format from the live sheet; if keys don't embed the model id, fall back purely on
  `FileExists` (which works regardless).
- Substituted `.pap` may reference `.tmb` VFX/sound; harmless but may look odd.
- Anti-cheat / ToS: same as the model swap (client-side only, nobody else sees it).
- Hook stability across patches: FFXIVClientStructs signatures for these functions are
  maintained upstream, but a broken signature must fail soft (log + disable feature, plugin
  still loads) — same pattern as the existing camera hook.

## 6. Suggested order

1. Catalogue + preview tab (also gives an immediate visible win).
2. `/mms animdebug` logging to confirm the hook sees ability and cutscene tracks.
3. Combat mapper with category defaults + user overrides.
4. Cutscene mode setting + `BaseOverride`.
5. Optional: speed matching, per-model shipped default mappings for popular monsters.

Total: roughly 3 sessions of implementation, with in-game verification by the user after
steps 1, 2 and 3 since the dev environment cannot run the client.
