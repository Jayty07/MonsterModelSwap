using System.Collections.Generic;
using System;
using Dalamud.Configuration;

namespace MonsterModelSwap;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>ModelChara row id currently selected in the browser. 0 = none.</summary>
    public int SelectedModelId { get; set; }

    /// <summary>Height / model scale multiplier written to Character.ModelScale. 1.0 = default.</summary>
    public float Height { get; set; } = 1.0f;

    /// <summary>Scale the third-person camera pivot height and zoom range together with Height.</summary>
    public bool ScaleCamera { get; set; } = true;

    /// <summary>Extra camera pivot height in world units, added on top of the scaled pivot.</summary>
    public float CameraHeightOffset { get; set; }

    /// <summary>Max zoom-out is never scaled below this multiple of the game's normal max distance.</summary>
    public float CameraMaxZoomFloor { get; set; } = 1f;

    /// <summary>Vertical shift (world units) applied to cutscene/dialogue cameras. 0 = off.</summary>
    public float CutsceneCameraOffset { get; set; }
    /// <summary>Shift only shots whose look-at point is within this XZ distance of the player.</summary>
    public float CutsceneCameraRange { get; set; } = 3f;
    /// <summary>Shift only shots whose look-at point is below feet + this many units (aimed at the floor).</summary>
    public float CutsceneCameraLowAim { get; set; } = 1f;

    /// <summary>Answer failed head/attach-bone lookups on the swapped actor with a bone of the monster skeleton.</summary>
    public bool FocusBoneEnabled { get; set; } = true;

    /// <summary>Only redirect while a cutscene/dialogue is active.</summary>
    public bool FocusBoneCutsceneOnly { get; set; } = true;

    /// <summary>Redirect every attach-bone lookup on the swapped actor, not just the ones the game reports missing.</summary>
    public bool FocusBoneRedirectAll { get; set; }

    /// <summary>Per-model focus bone name (ModelChara id -> havok bone name). Missing = auto-pick head-like bone.</summary>
    public Dictionary<int, string> FocusBones { get; set; } = new();

    /// <summary>Vertical offset (model units) added to the focus bone position.</summary>
    public float FocusBoneOffsetY { get; set; }

    /// <summary>When true the plugin keeps re-applying the selected model every frame.</summary>
    public bool Persist { get; set; } = true;

    /// <summary>Skip re-application while bound by duty (raids, dungeons, trials, etc.).</summary>
    public bool PauseInDuty { get; set; }

    /// <summary>Re-apply the selected model automatically on login/plugin load.</summary>
    public bool ApplyOnLogin { get; set; }

    /// <summary>Emit a log line every time a re-apply happens.</summary>
    public bool LogReapplies { get; set; } = true;

    /// <summary>Only list Type 3 (monster, "m") models in the browser.</summary>
    public bool MonstersOnly { get; set; } = true;

    /// <summary>Favorited ModelChara row ids.</summary>
    public HashSet<int> Favorites { get; set; } = new();
    public bool FavoritesOnly { get; set; }

    public List<AnimationBind> AnimationBinds { get; set; } = new();

    /// <summary>Open the main window when the plugin loads.</summary>
    public bool OpenOnStartup { get; set; }
}

[Serializable]
public sealed class AnimationBind
{
    /// <summary>ActionTimeline row to play.</summary>
    public ushort TimelineId { get; set; }

    /// <summary>ModelChara row this bind was created for (0 = fire regardless of active model).</summary>
    public int ModelId { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Play as looping base animation (replaces idle) instead of a one-shot.</summary>
    public bool Loop { get; set; }

    /// <summary>VirtualKey code; 0 = unbound.</summary>
    public int Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
}
