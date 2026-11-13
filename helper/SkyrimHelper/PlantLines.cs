using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Skyrim;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SkyrimHelper;

/// <summary>
/// What a plant gives when picked written as a plant gives it: its one `Ingredient:` line, the ingredient's editor ID.
/// </summary>
public static class PlantLines
{
    /// <summary>The fields a plant's ingredient was written under: drops, yields, a harvest, a list of ingredients.</summary>
    private static readonly HashSet<string> GiveKeys = new(["Drops", "Drop", "Gives", "Give", "Yields", "Yield", "Harvest", "Harvests",
        "HarvestItem", "HarvestIngredient", "HarvestedIngredient", "Produces", "Produce", "ProducedItem", "Ingredients", "Item", "Items",
        "Loot", "PickedItem", "Picked"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The keys an item of such a list names the ingredient by.</summary>
    private static readonly string[] ItemKeys = ["Ingredient", "Item", "Reference", "Object", "EditorID", "Name"];

    /// <summary>
    /// A Tree's or a Flora's ingredient written under another field — `Drops: [- Ingredient: MoonpetalLeaf]` — where it names one
    /// and the file has no `Ingredient:` line, written as its `Ingredient:` line. Told to delete Drops with the ingredient's own
    /// fields beside it, eval round 145's flora+detailed #1 deleted it, and built a Moonpetal that gives nothing when picked.
    /// </summary>
    public static List<(string File, string Change)> Gives(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir).Where(f => f.Type == typeof(Tree) || f.Type == typeof(Flora)))
        {
            var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Any(l => Regex.IsMatch(l, @"^Ingredient:"))) continue;
            var at = lines.FindIndex(l => Regex.Match(l, @"^(?<key>[A-Za-z]+):") is { Success: true } m && GiveKeys.Contains(m.Groups["key"].Value));
            if (at < 0) continue;
            var count = RecordFiles.BlockLength(lines, at);
            JsonNode? value;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(string.Join('\n', lines.Skip(at).Take(count))));
                value = ((YamlMappingNode)stream.Documents[0].RootNode).Children.Values.FirstOrDefault() is { } node ? RecordEdits.FromYaml(node) : null;
            }
            catch (Exception e) when (e is YamlException or InvalidCastException) { continue; }
            if (Named(value) is not { Length: > 0 } ingredient) continue;
            var key = Regex.Match(lines[at], @"^(?<key>[A-Za-z]+):").Groups["key"].Value;
            lines.RemoveRange(at, count);
            lines.Insert(at, $"Ingredient: {ingredient}");
            File.WriteAllText(file, string.Join('\n', lines));
            changes.Add((relative, $"wrote `{key}:` as `Ingredient: {ingredient}` — a plant gives what it is picked for by its one `Ingredient:` " +
                $"line, the ingredient's editor ID, as the game's TreeFloraMountainFlower01Blue has it; {RecordFiles.Article(type.Name)} has no {key}"));
        }
        return changes;
    }

    /// <summary>The one ingredient a value names: a name, a list of one, or an item naming it under one of ItemKeys.</summary>
    private static string? Named(JsonNode? value) => value switch
    {
        JsonValue one => one.ToString().Trim(),
        JsonArray { Count: 1 } list => Named(list[0]),
        JsonObject item => ItemKeys.Select(k => item[k]).OfType<JsonValue>().Select(v => v.ToString().Trim()).FirstOrDefault(),
        _ => null
    };
}
