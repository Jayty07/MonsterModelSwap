using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
    private readonly AnimationService anim;

    private string animSearch = string.Empty;
    private readonly List<AnimationEntry> animFiltered = new();
    private string animLastSearch = "\0";
    private int animLastCount = -1;
    private AnimationEntry? animSelected;

    private string search = string.Empty;
    private string lastSearch = "\0";
    private bool lastMonstersOnly;
    private bool lastFavoritesOnly;
    private int lastFavoritesCount;
    private readonly List<ModelEntry> filtered = new();
    private int manualId;
    private float height;

    public MainWindow(IDalamudPluginInterface pi, Configuration config, ModelDatabase db, ModelSwapService swap, AnimationService anim)
        : base("Monster Model Swap###MonsterModelSwapMain")
    {
        this.pi = pi;
        this.config = config;
        this.db = db;
        this.swap = swap;
        this.anim = anim;

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
        anim.SuppressKeybinds = ImGui.GetIO().WantTextInput;

        DrawStatus();
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##tabs")) return;

        if (ImGui.BeginTabItem("Models"))
        {
            DrawControls();
            ImGui.Separator();
            DrawBrowser();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Animations"))
        {
            DrawAnimations();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawAnimations()
    {
        var modelId = swap.Active ? swap.TargetModelId : config.SelectedModelId;
        if (modelId <= 0)
        {
            ImGui.TextColored(Dim, "Select (or apply) a model first to list the animations its skeleton can play.");
            return;
        }

        anim.EnsureScanned(modelId);

        ImGui.TextUnformatted($"Skeleton: {db.Describe(modelId)}");
        if (!swap.Active)
            ImGui.TextColored(Warn, "Model not applied — animations will play on your normal model and may not exist for it.");

        if (anim.Scanning) ImGui.TextColored(Dim, "Scanning game files...");
        else if (anim.ScanError is { } err) ImGui.TextColored(Warn, err);
        else ImGui.TextColored(Dim, $"{anim.Available.Count} playable animations");

        ImGui.SameLine();
        if (ImGui.Button("Stop / idle")) anim.Stop();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear the loop override and return to idle");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##animsearch", "Search by key (e.g. mon_sp, battle, emote) or id", ref animSearch, 64);
        RefreshAnimFilter();

        var bindsHeight = Math.Min(160f, 30f + config.AnimationBinds.Count * 26f);
        if (ImGui.BeginChild("##animlist", new Vector2(-1, -bindsHeight - 8), true))
        {
            unsafe
            {
                var clipper = ImGui.ImGuiListClipper();
                clipper.Begin(animFiltered.Count);
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var e = animFiltered[i];
                        ImGui.PushID(e.Id);

                        if (ImGui.SmallButton("Play")) anim.Play(e.Id);
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Loop")) anim.SetLoop(e.Id);
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Use as looping idle replacement");
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Bind"))
                        {
                            var b = new AnimationBind { TimelineId = e.Id, ModelId = modelId, Label = e.Key };
                            config.AnimationBinds.Add(b);
                            anim.Capturing = b;
                            pi.SavePluginConfig(config);
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add a keybind for this animation");
                        ImGui.SameLine();

                        var selected = animSelected == e || anim.LastPlayed == e.Id;
                        if (ImGui.Selectable(e.Label, selected, ImGuiSelectableFlags.AllowDoubleClick))
                        {
                            animSelected = e;
                            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) anim.Play(e.Id);
                        }

                        ImGui.PopID();
                    }
                }

                clipper.End();
                clipper.Destroy();
            }
        }
        ImGui.EndChild();

        ImGui.TextColored(Dim, $"Keybinds ({config.AnimationBinds.Count})");
        if (ImGui.BeginChild("##binds", new Vector2(-1, -1), true))
        {
            AnimationBind? remove = null;
            for (var i = 0; i < config.AnimationBinds.Count; i++)
            {
                var b = config.AnimationBinds[i];
                ImGui.PushID(i);

                var capturing = anim.Capturing == b;
                ImGui.PushStyleColor(ImGuiCol.Text, capturing ? Warn : b.Key == 0 ? Dim : Ok);
                if (ImGui.Button(capturing ? "press a key..." : AnimationService.Describe(b), new Vector2(130, 0)))
                    anim.Capturing = capturing ? null : b;
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click, then press the key (with Ctrl/Shift/Alt). Esc cancels.");

                ImGui.SameLine();
                var loop = b.Loop;
                if (ImGui.Checkbox("Loop", ref loop))
                {
                    b.Loop = loop;
                    pi.SavePluginConfig(config);
                }

                ImGui.SameLine();
                var anyModel = b.ModelId == 0;
                if (ImGui.Checkbox("Any model", ref anyModel))
                {
                    b.ModelId = anyModel ? 0 : modelId;
                    pi.SavePluginConfig(config);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(anyModel ? "Fires whatever model is active" : $"Only fires while model #{b.ModelId} is applied");

                ImGui.SameLine();
                if (ImGui.SmallButton("Play")) { if (b.Loop) anim.SetLoop(b.TimelineId); else anim.Play(b.TimelineId); }
                ImGui.SameLine();
                if (ImGui.SmallButton("X")) remove = b;
                ImGui.SameLine();
                ImGui.TextUnformatted($"{b.Label} (#{b.TimelineId})");

                ImGui.PopID();
            }

            if (remove is not null)
            {
                if (anim.Capturing == remove) anim.Capturing = null;
                config.AnimationBinds.Remove(remove);
                pi.SavePluginConfig(config);
            }
        }
        ImGui.EndChild();
    }

    private void RefreshAnimFilter()
    {
        if (animSearch == animLastSearch && anim.Available.Count == animLastCount) return;
        animLastSearch = animSearch;
        animLastCount = anim.Available.Count;
        animFiltered.Clear();
        foreach (var e in anim.Available)
            if (e.Matches(animSearch)) animFiltered.Add(e);
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
        ImGui.SetNextItemWidth(-290);
        ImGui.InputTextWithHint("##search", "Search by name, code (m0001) or id", ref search, 64);
        ImGui.SameLine();
        var monstersOnly = config.MonstersOnly;
        if (ImGui.Checkbox("Monsters only", ref monstersOnly))
        {
            config.MonstersOnly = monstersOnly;
            pi.SavePluginConfig(config);
        }
        ImGui.SameLine();
        var favoritesOnly = config.FavoritesOnly;
        if (ImGui.Checkbox($"Favorites ({config.Favorites.Count})", ref favoritesOnly))
        {
            config.FavoritesOnly = favoritesOnly;
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
                    var isFav = config.Favorites.Contains(e.Id);

                    ImGui.PushStyleColor(ImGuiCol.Text, isFav ? Warn : Dim);
                    ImGui.PushFont(UiBuilder.IconFont);
                    var favClicked = ImGui.SmallButton($"{FontAwesomeIcon.Star.ToIconString()}##fav{e.Id}");
                    ImGui.PopFont();
                    if (favClicked)
                    {
                        if (isFav) config.Favorites.Remove(e.Id);
                        else config.Favorites.Add(e.Id);
                        pi.SavePluginConfig(config);
                    }
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(isFav ? "Remove from favorites" : "Add to favorites");
                    ImGui.SameLine();

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
        if (search == lastSearch
            && config.MonstersOnly == lastMonstersOnly
            && config.FavoritesOnly == lastFavoritesOnly
            && (!config.FavoritesOnly || config.Favorites.Count == lastFavoritesCount)
            && filtered.Count > 0) return;
        lastSearch = search;
        lastMonstersOnly = config.MonstersOnly;
        lastFavoritesOnly = config.FavoritesOnly;
        lastFavoritesCount = config.Favorites.Count;
        filtered.Clear();
        foreach (var e in db.Entries)
        {
            if (config.FavoritesOnly && !config.Favorites.Contains(e.Id)) continue;
            if (config.MonstersOnly && !config.FavoritesOnly && !e.IsMonster) continue;
            if (!e.Matches(search)) continue;
            filtered.Add(e);
        }
    }
}
