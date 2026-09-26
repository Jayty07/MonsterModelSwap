using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MonsterModelSwap.Data;

/// <summary>A single ModelChara row, decorated with a display name when one is known.</summary>
public sealed record ModelEntry(int Id, byte Type, ushort Model, byte Base, byte Variant, string? Name)
{
    public bool IsMonster => Type == 3;

    /// <summary>Game-path style code, e.g. m0001 / d1001 / c0101.</summary>
    public string Code => Type switch
    {
        1 => $"c{Model:D4}",
        2 => $"d{Model:D4}",
        3 => $"m{Model:D4}",
        4 => $"w{Model:D4}",
        _ => $"?{Model:D4}",
    };

    public string Label => Name is null
        ? $"{Code} b{Base:D4} v{Variant:D4}  (#{Id})"
        : $"{Name}  [{Code} b{Base:D4} v{Variant:D4}]  (#{Id})";

    public bool Matches(string needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return true;
        return Id.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase)
            || Code.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

/// <summary>
/// Builds the browsable model list from the game's ModelChara sheet and decorates it
/// with names from the shipped Data/models.json (ModelChara row id -> display name).
/// </summary>
public sealed class ModelDatabase
{
    private readonly Dictionary<int, string> names = new();
    private readonly List<ModelEntry> entries = new();
    private readonly Dictionary<int, ModelEntry> byId = new();

    public IReadOnlyList<ModelEntry> Entries => entries;
    public int NamedCount => names.Count;

    public ModelDatabase(IDalamudPluginInterface pi, IDataManager data, IPluginLog log)
    {
        LoadNames(pi, log);

        var sheet = data.GetExcelSheet<ModelChara>();
        if (sheet is null)
        {
            log.Error("ModelChara sheet unavailable; falling back to named ids only.");
            foreach (var (id, name) in names)
                Add(new ModelEntry(id, 3, 0, 0, 0, name));
            return;
        }

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.Type == 0) continue;
            names.TryGetValue((int)row.RowId, out var name);
            Add(new ModelEntry((int)row.RowId, row.Type, row.Model, row.Base, row.Variant, name));
        }

        log.Information("Loaded {Count} ModelChara rows ({Named} named).", entries.Count, names.Count);
    }

    public ModelEntry? Get(int id) => byId.GetValueOrDefault(id);

    public string Describe(int id) => Get(id)?.Label ?? $"#{id}";

    private void Add(ModelEntry e)
    {
        entries.Add(e);
        byId[e.Id] = e;
    }

    private void LoadNames(IDalamudPluginInterface pi, IPluginLog log)
    {
        var path = Path.Combine(pi.AssemblyLocation.DirectoryName!, "Data", "models.json");
        if (!File.Exists(path))
        {
            log.Warning("models.json not found at {Path}; only raw ids will be shown.", path);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.GetProperty("models").EnumerateObject())
            {
                if (int.TryParse(prop.Name, out var id) && prop.Value.GetString() is { Length: > 0 } name)
                    names[id] = name;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to parse models.json");
        }
    }
}
