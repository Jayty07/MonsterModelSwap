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

    /// <summary>Open the main window when the plugin loads.</summary>
    public bool OpenOnStartup { get; set; }
}
