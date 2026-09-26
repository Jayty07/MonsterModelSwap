using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using MonsterModelSwap.Data;
using MonsterModelSwap.Services;

namespace MonsterModelSwap.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Warn = new(1f, 0.75f, 0.2f, 1f);
    private static readonly Vector4 Ok = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly IDalamudPluginInterface pi;
    private readonly Configuration config;
    private readonly ModelDatabase db;
    private readonly ModelSwapService swap;

    private string search = string.Empty;
    private string lastSearch = "\0";
    private bool lastMonstersOnly;
    private readonly List<ModelEntry> filtered = new();
    private int manualId;
    private float height;

    public MainWindow(IDalamudPluginInterface pi, Configuration config, ModelDatabase db, ModelSwapService swap)
        : base("Monster Model Swap###MonsterModelSwapMain")
    {
        this.pi = pi;
        this.config = config;
        this.db = db;
        this.swap = swap;

        manualId = config.SelectedModelId;
        height = config.Height;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(620, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawControls();
        ImGui.Separator();
        DrawBrowser();
    }

    private void DrawStatus()
    {
        if (swap.Active)
        {
            ImGui.TextColored(Ok, $"Active: {db.Describe(swap.TargetModelId)}  height x{swap.TargetHeight:0.00}");
            ImGui.TextColored(Dim, $"Original: #{swap.OriginalModelId ?? 0}  |  re-applies: {swap.ReapplyCount}" +
                                   (swap.LastReapply is { } t ? $"  |  last: {t:HH:mm:ss} ({swap.LastReason})" : string.Empty));
        }
        else
        {
            ImGui.TextColored(Dim, "Inactive — select a model and press Apply.");
        }

        var current = swap.CurrentModelId;
        ImGui.TextColored(Dim, current is null
            ? "Local player: not available"
            : $"Local player actor model: #{current}  scale {swap.CurrentHeight:0.00}");
    }

    private void DrawControls()
    {
        var selected = db.Get(config.SelectedModelId);
        ImGui.TextUnformatted("Selected: ");
        ImGui.SameLine();
        if (selected is null)
            ImGui.TextColored(Warn, config.SelectedModelId > 0 ? $"#{config.SelectedModelId} (unknown row)" : "none");
        else
            ImGui.TextUnformatted(selected.Label);

        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("ModelChara id", ref manualId, 0, 0))
        {
            manualId = Math.Max(0, manualId);
            config.SelectedModelId = manualId;
            pi.SavePluginConfig(config);
        }

        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat("Height", ref height, 0.1f, 5f, "x%.2f"))
        {
            height = Math.Clamp(height, 0.1f, 5f);
            config.Height = height;
            pi.SavePluginConfig(config);
            swap.SetHeight(height);
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset##height"))
        {
            height = 1f;
            config.Height = height;
            pi.SavePluginConfig(config);
            swap.SetHeight(height);
        }

        ImGui.SameLine();
        var scaleCamera = config.ScaleCamera;
        if (ImGui.Checkbox("Scale camera", ref scaleCamera))
        {
            config.ScaleCamera = scaleCamera;
            pi.SavePluginConfig(config);
        }

        ImGui.SetNextItemWidth(260);
        var camOffset = config.CameraHeightOffset;
        if (ImGui.SliderFloat("Camera height offset", ref camOffset, -5f, 20f, "%.2f"))
        {
            config.CameraHeightOffset = camOffset;
            pi.SavePluginConfig(config);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("0"))
        {
            config.CameraHeightOffset = 0f;
            pi.SavePluginConfig(config);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset camera height offset");

        ImGui.SetNextItemWidth(260);
        var zoomFloor = config.CameraMaxZoomFloor;
        if (ImGui.SliderFloat("Min zoom-out range", ref zoomFloor, 0.1f, 5f, "x%.2f"))
        {
            config.CameraMaxZoomFloor = Math.Clamp(zoomFloor, 0.1f, 5f);
            pi.SavePluginConfig(config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Max zoom-out distance never shrinks below this multiple of the game's normal max,\neven when Height is set very low. x1.00 = always at least the normal zoom range.");

        ImGui.BeginDisabled(config.SelectedModelId <= 0);
        if (ImGui.Button("Apply", new Vector2(100, 0)))
            swap.Apply(config.SelectedModelId, config.Height);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!swap.Active);
        if (ImGui.Button("Revert", new Vector2(100, 0)))
        {
            swap.Revert();
            height = swap.TargetHeight;
        }

        ImGui.SameLine();
        if (ImGui.Button("Force redraw"))
            swap.ForceRedraw();
        ImGui.EndDisabled();

        var persist = config.Persist;
        if (ImGui.Checkbox("Persist (re-apply on zone / cutscene / every frame)", ref persist))
        {
            config.Persist = persist;
            pi.SavePluginConfig(config);
        }

        var pauseDuty = config.PauseInDuty;
        if (ImGui.Checkbox("Pause re-apply while in a duty", ref pauseDuty))
        {
            config.PauseInDuty = pauseDuty;
            pi.SavePluginConfig(config);
        }

        ImGui.SameLine();
        var applyOnLogin = config.ApplyOnLogin;
        if (ImGui.Checkbox("Apply on login", ref applyOnLogin))
        {
            config.ApplyOnLogin = applyOnLogin;
            pi.SavePluginConfig(config);
        }

        ImGui.SameLine();
        var logReapplies = config.LogReapplies;
        if (ImGui.Checkbox("Log re-applies", ref logReapplies))
        {
            config.LogReapplies = logReapplies;
            pi.SavePluginConfig(config);
        }
    }

    private void DrawBrowser()
    {
        ImGui.SetNextItemWidth(-160);
        ImGui.InputTextWithHint("##search", "Search by name, code (m0001) or id", ref search, 64);
        ImGui.SameLine();
        var monstersOnly = config.MonstersOnly;
        if (ImGui.Checkbox("Monsters only", ref monstersOnly))
        {
            config.MonstersOnly = monstersOnly;
            pi.SavePluginConfig(config);
        }

        RefreshFilter();
        ImGui.TextColored(Dim, $"{filtered.Count} models ({db.NamedCount} named)");

        if (!ImGui.BeginChild("##list", new Vector2(-1, -1), true))
        {
            ImGui.EndChild();
            return;
        }

        unsafe
        {
            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(filtered.Count);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var e = filtered[i];
                    var isSelected = e.Id == config.SelectedModelId;
                    if (ImGui.Selectable($"{e.Label}##{e.Id}", isSelected, ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        config.SelectedModelId = e.Id;
                        manualId = e.Id;
                        pi.SavePluginConfig(config);
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                            swap.Apply(e.Id, config.Height);
                    }
                }
            }

            clipper.End();
            clipper.Destroy();
        }

        ImGui.EndChild();
    }

    private void RefreshFilter()
    {
        if (search == lastSearch && config.MonstersOnly == lastMonstersOnly && filtered.Count > 0) return;
        lastSearch = search;
        lastMonstersOnly = config.MonstersOnly;
        filtered.Clear();
        foreach (var e in db.Entries)
        {
            if (config.MonstersOnly && !e.IsMonster) continue;
            if (!e.Matches(search)) continue;
            filtered.Add(e);
        }
    }
}
