using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using MonsterModelSwap.Data;

namespace MonsterModelSwap.Services;

public sealed record AnimationEntry(ushort Id, string Key, byte Slot, byte Type, bool IsLoop)
{
    public string Label => $"{Key}  (#{Id}{(IsLoop ? ", loop" : string.Empty)})";

    public bool Matches(string needle) =>
        string.IsNullOrWhiteSpace(needle)
        || Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || Id.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Lists the ActionTimeline rows a given ModelChara skeleton can actually play (its .pap exists),
/// plays them on the local player, and fires keybinds.
/// </summary>
public sealed unsafe class AnimationService : IDisposable
{
    private const ushort IdleTimeline = 3; // "normal/idle"

    private readonly IDataManager data;
    private readonly IKeyState keys;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ModelSwapService swap;
    private readonly ModelDatabase db;

    private readonly List<AnimationEntry> allRows = new();
    private readonly Dictionary<int, List<AnimationEntry>> cache = new();
    private readonly HashSet<VirtualKey> down = new();
    private CancellationTokenSource? scanCts;

    public IReadOnlyList<AnimationEntry> Available { get; private set; } = Array.Empty<AnimationEntry>();
    public int ScannedModelId { get; private set; } = -1;
    public bool Scanning { get; private set; }
    public string? ScanError { get; private set; }
    public ushort LastPlayed { get; private set; }

    /// <summary>Set by the UI each frame so binds don't fire while typing in a text field.</summary>
    public bool SuppressKeybinds { get; set; }

    /// <summary>Non-null while the UI is waiting for the user to press a key for this bind.</summary>
    public AnimationBind? Capturing { get; set; }

    /// <summary>Raised after a keybind capture completes so the config can be saved.</summary>
    public event System.Action? BindsChanged;

    public AnimationService(IDataManager data, IKeyState keys, IPluginLog log, Configuration config,
        ModelSwapService swap, ModelDatabase db)
    {
        this.data = data;
        this.keys = keys;
        this.log = log;
        this.config = config;
        this.swap = swap;
        this.db = db;

        var sheet = data.GetExcelSheet<ActionTimeline>();
        if (sheet is null)
        {
            log.Error("ActionTimeline sheet unavailable; animation browser disabled.");
            return;
        }

        foreach (var row in sheet)
        {
            var key = row.Key.ExtractText();
            if (row.RowId == 0 || row.RowId > ushort.MaxValue || string.IsNullOrEmpty(key)) continue;
            allRows.Add(new AnimationEntry((ushort)row.RowId, key, row.Slot, row.Type, row.IsLoop));
        }

        log.Information("Loaded {Count} ActionTimeline rows.", allRows.Count);
    }

    public void Dispose()
    {
        scanCts?.Cancel();
        scanCts?.Dispose();
    }

    /// <summary>Kick off a background scan of which timelines exist for this model (no-op if cached/in progress).</summary>
    public void EnsureScanned(int modelId)
    {
        if (modelId == ScannedModelId || allRows.Count == 0) return;

        if (cache.TryGetValue(modelId, out var cached))
        {
            Available = cached;
            ScannedModelId = modelId;
            ScanError = null;
            return;
        }

        var entry = db.Get(modelId);
        var folder = AnimationFolder(entry);
        ScannedModelId = modelId;
        Available = Array.Empty<AnimationEntry>();

        if (folder is null)
        {
            ScanError = entry is null ? "Unknown ModelChara row." : "Unsupported model type for animation lookup.";
            return;
        }

        ScanError = null;
        Scanning = true;
        scanCts?.Cancel();
        scanCts = new CancellationTokenSource();
        var ct = scanCts.Token;

        Task.Run(() =>
        {
            var found = new List<AnimationEntry>();
            try
            {
                foreach (var row in allRows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (data.FileExists($"{folder}{row.Key}.pap"))
                        found.Add(row);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Error(ex, "Animation scan failed for model {Model}", modelId);
                ScanError = ex.Message;
            }

            if (ct.IsCancellationRequested) return;
            found.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            cache[modelId] = found;
            Available = found;
            Scanning = false;
            log.Information("Model #{Model}: {Count} playable animations.", modelId, found.Count);
        }, ct);
    }

    private static string? AnimationFolder(ModelEntry? e) => e?.Type switch
    {
        1 => $"chara/human/c{e.Model:D4}/animation/a0001/bt_common/",
        2 => $"chara/demihuman/d{e.Model:D4}/animation/a0001/bt_common/",
        3 => $"chara/monster/m{e.Model:D4}/animation/a0001/bt_common/",
        _ => null,
    };

    public AnimationEntry? Find(ushort id) => allRows.FirstOrDefault(r => r.Id == id);

    /// <summary>Play a one-shot timeline on the local player (and cutscene copies).</summary>
    public void Play(ushort timelineId)
    {
        var n = 0;
        foreach (var addr in swap.LocalCharacterAddresses())
        {
            ((Character*)addr)->Timeline.TimelineSequencer.PlayTimeline(timelineId, null);
            n++;
        }

        LastPlayed = timelineId;
        if (n > 0) log.Debug("Played timeline #{Id} on {Count} actor(s).", timelineId, n);
    }

    /// <summary>Replace the idle animation with a looping timeline (0 clears).</summary>
    public void SetLoop(ushort timelineId)
    {
        foreach (var addr in swap.LocalCharacterAddresses())
            ((Character*)addr)->Timeline.BaseOverride = timelineId;
        LastPlayed = timelineId;
    }

    public void Stop()
    {
        foreach (var addr in swap.LocalCharacterAddresses())
        {
            var chara = (Character*)addr;
            chara->Timeline.BaseOverride = 0;
            chara->Timeline.TimelineSequencer.PlayTimeline(IdleTimeline, null);
        }

        LastPlayed = 0;
    }

    /// <summary>Called every framework tick on the main thread.</summary>
    public void Tick()
    {
        var pressed = new List<VirtualKey>();
        foreach (var key in keys.GetValidVirtualKeys())
        {
            var isDown = keys[key];
            if (isDown && !down.Contains(key)) pressed.Add(key);
            if (isDown) down.Add(key);
            else down.Remove(key);
        }

        if (pressed.Count == 0) return;

        var ctrl = down.Contains(VirtualKey.CONTROL);
        var shift = down.Contains(VirtualKey.SHIFT);
        var alt = down.Contains(VirtualKey.MENU);

        if (Capturing is { } cap)
        {
            var key = pressed.FirstOrDefault(k => k is not (VirtualKey.CONTROL or VirtualKey.SHIFT or VirtualKey.MENU
                or VirtualKey.LCONTROL or VirtualKey.RCONTROL or VirtualKey.LSHIFT or VirtualKey.RSHIFT
                or VirtualKey.LMENU or VirtualKey.RMENU));
            if (key == default) return;

            if (key == VirtualKey.ESCAPE)
            {
                Capturing = null;
                return;
            }

            cap.Key = (int)key;
            cap.Ctrl = ctrl;
            cap.Shift = shift;
            cap.Alt = alt;
            Capturing = null;
            keys[key] = false;
            BindsChanged?.Invoke();
            return;
        }

        if (SuppressKeybinds) return;

        foreach (var bind in config.AnimationBinds)
        {
            if (bind.Key == 0 || bind.Ctrl != ctrl || bind.Shift != shift || bind.Alt != alt) continue;
            if (!pressed.Contains((VirtualKey)bind.Key)) continue;
            if (bind.ModelId != 0 && bind.ModelId != swap.TargetModelId) continue;

            if (bind.Loop) SetLoop(bind.TimelineId);
            else Play(bind.TimelineId);
            keys[(VirtualKey)bind.Key] = false;
        }
    }

    public static string Describe(AnimationBind b)
    {
        if (b.Key == 0) return "unbound";
        var k = ((VirtualKey)b.Key).GetFancyName();
        return $"{(b.Ctrl ? "Ctrl+" : "")}{(b.Shift ? "Shift+" : "")}{(b.Alt ? "Alt+" : "")}{k}";
    }
}
