# Monster Model Swap

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin that swaps the local player's model to any
monster / NPC `ModelChara` and **keeps it applied across every scene transition** — zoning, cutscenes,
gpose, duty entry — by re-resolving the player actor every frame and re-writing the model whenever the
game recreates it.

> **Client-side only.** Nobody else sees the swap. This writes to the same actor memory Anamnesis does
> and carries the same Terms-of-Service risk as Anamnesis / Glamourer / Penumbra: use at your own
> discretion and never in a way that is visible to other players (e.g. streaming while abusing it).

## Why actor-level instead of a Penumbra file swap?

Penumbra swaps *files*, so a monster's meshes get forced onto the **player's** skeleton and animation
set, which is why file-swapped monsters usually look broken. This plugin changes the actor's
`ModelCharaId` (the field Anamnesis calls *Model Type*) and forces a redraw, so the game builds the
monster's **own skeleton, animations, VFX and scale**. You walk, idle and emote as that creature does.

## Features

- **Creature browser** — searchable list of every `ModelChara` row (default filter: Type 3 "monster"
  models `m0001 … m9998`; untick *Monsters only* for demihumans/humans/etc.). ~1,800 rows carry a
  display name from `Data/models.json`; everything else falls back to the raw `mXXXX bYYYY vZZZZ (#id)`
  code. Double-click a row or press **Apply**.
- **Favorites** — star any row to pin it; tick *Favorites* to list only starred models.
- **Height slider** (`x0.10 … x5.00`) — writes `Character.ModelScale` and the draw object's scale.
  Works standalone (no model swap needed) and is persisted alongside the model.
- **Scale camera** (on by default) — raises/lowers the third-person camera's look-at point (via a hook on
  the camera's position function) and multiplies min/max/current zoom distance by the height factor, so the camera pivots on the resized model instead
  of at its knees/over its head. Restored on Revert / unload.
- **Persist toggle** — while on, the plugin:
  - re-applies on `IClientState.TerritoryChanged` (zoning),
  - re-applies on `ICondition.ConditionChange` for cutscene / between-areas / duty / event flags,
  - checks **every `IFramework.Update`** whether any actor that represents you (the main actor *and*
    any cutscene copy at object-table index ≥ 200) has a `ModelCharaId` or `ModelScale` different from
    the target, and rewrites + redraws it. The old pointer is never reused — actors are re-resolved via
    the object table each tick, so the "cutscene spawned a brand-new actor" case and the
    "cutscene actor despawned, normal actor respawned" case are both covered.
- **Clean revert** — the original `ModelCharaId` / `ModelScale` are captured on the first apply and
  restored by **Revert** (also on plugin unload).
- **Pause in duty** — optional; skips the re-apply loop while `BoundByDuty` if you see instability.
- **Logging** — every re-apply logs the actor index, address, target and the reason
  (`/xllog`, toggle with *Log re-applies*).
- **Apply on login** — optional auto-apply of the saved selection.

### Commands

| Command | Effect |
| --- | --- |
| `/mms` | Toggle the window |
| `/mms apply [id]` | Apply the given (or currently selected) `ModelChara` id |
| `/mms revert` | Restore the original model / height |
| `/mms height 1.5` | Set the height multiplier |
| `/mms camoffset 2` | Extra camera pivot height (world units) |
| `/mms camdebug` | Log camera hook diagnostics |
| `/mms persist [on\|off]` | Toggle the persistence loop |

## How the swap works

```text
Character* chara = (Character*)objectTable.LocalPlayer.Address;   // re-resolved every frame
chara->ModelContainer.ModelCharaId = targetId;                     // Anamnesis "ModelType", offset 0x1B38 at time of writing
chara->ModelScale                  = targetHeight;
chara->GameObject.DisableDraw();                                   // Anamnesis "actor refresh"
chara->GameObject.EnableDraw();                                    // game rebuilds the DrawObject from the modified fields
```

After a redraw the actor is left alone for ~20 frames so the game can finish rebuilding the draw object;
actors with a null `DrawObject` (mid-spawn) are skipped and picked up on a later frame.

## Install

The plugin is not in the official Dalamud repository, so it is loaded as a **dev plugin**.

1. Get a **compiled** build — the source code in this repo does *not* contain the DLL:
   - download `MonsterModelSwap-plugin.zip` from the
     [latest release](https://github.com/Jayty07/MonsterModelSwap/releases/latest), **or**
   - build it yourself (see [Building](#building)); the output is `MonsterModelSwap/bin/Release/`.
2. Extract the zip. You get a `MonsterModelSwap` folder containing `MonsterModelSwap.dll`,
   `MonsterModelSwap.json` and `Data\models.json`. Move that folder somewhere permanent, e.g.
   `%AppData%\XIVLauncher\devPlugins\MonsterModelSwap\`.
3. Launch the game through XIVLauncher, then in game type `/xlsettings`.
4. **Experimental** tab → *Dev Plugin Locations* → click **+**, paste the full path to
   `MonsterModelSwap.dll` (or the folder), tick **Enabled**, then **Save and Close**.
5. Open `/xlplugins` → **Dev Tools** / *Installed Plugins* → enable **Monster Model Swap**
   (if it does not appear, click *Scan Dev Plugins* or restart the game).
6. Type `/mms` to open the window, pick a model, press **Apply**.

To update, replace the files in the same folder and reload the plugin from `/xlplugins`.
To uninstall, disable it in `/xlplugins` and remove the dev plugin location again.

## Building

Requires the .NET 10 SDK and a Dalamud dev install (the `Dalamud.NET.Sdk` looks in
`%APPDATA%\XIVLauncher\addon\Hooks\dev` by default). To point at another copy:

```sh
dotnet build MonsterModelSwap/MonsterModelSwap.csproj -c Release -p:DalamudLibPath=/path/to/dalamud/
# or
DALAMUD_HOME=/path/to/dalamud dotnet build MonsterModelSwap/MonsterModelSwap.csproj -c Release
```

The output (`MonsterModelSwap/bin/Release/MonsterModelSwap.dll` + `MonsterModelSwap.json` + `Data/`) can
be loaded via *Dalamud Settings → Experimental → Dev Plugin Locations*.

## Data

`MonsterModelSwap/Data/models.json` maps `ModelChara` row id → display name. It was generated by joining
`BNpcBase.ModelChara` with `BNpcName` (names resolved through Anamnesis'
[`NpcNames.json`](https://github.com/imchillin/Anamnesis), MIT). When several enemies share a model the
first three names are listed. Feel free to edit it — unknown ids simply show their raw code.

## Known limitations

- Some models are not valid for the player actor (e.g. `ModelChara` rows with no skeleton) and will
  render invisible or crash on redraw. Revert (or reload the plugin) if that happens.
- The player's hitbox, camera height and gear are untouched; weapons are hidden by most monster models.
- Height is a multiplier on `ModelScale`; the game may clamp extreme values for some skeletons.
- Camera scaling hooks the world camera's `GetCameraPosition` virtual (vtable slot 16) and reads the
  look-at height offset at a fixed offset (`0x234`), both as used by Cammy and not yet mapped in
  FFXIVClientStructs; if a game patch moves them, untick *Scale camera*.
- Not tested with Glamourer/Penumbra redraws at the same time; both plugins redraw the same actor and
  may fight over it if they have their own model-type overrides active.
