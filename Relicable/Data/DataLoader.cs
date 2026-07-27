using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin;
using Relicable.Model;

namespace Relicable.Data;

// Loads objective JSON files from Data/relics. Static stages (Novus, upgrades)
// ship as authored files; the bulk Animus and Mahatma entries are emitted by the
// generator from the in-game book definitions (DESIGN.md section 7) into the same
// folder, so both are loaded uniformly here.
public static class DataLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static IReadOnlyList<RelicObjective> LoadAll(IDalamudPluginInterface pi)
    {
        var result = new List<RelicObjective>();
        var dir = Path.Combine(pi.AssemblyLocation.DirectoryName ?? ".", "Data", "relics");
        if (!Directory.Exists(dir))
            return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllText(file);
                var obj = JsonSerializer.Deserialize<RelicObjective>(json, Options);
                if (obj != null)
                    result.Add(obj);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Relicable: failed to load {file}: {ex.Message}");
            }
        }

        return result;
    }
}
