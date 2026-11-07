using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace SkyrimHelper;

/// <summary>
/// Record conflicts down to the field: what each plugin changed in a record — its version held against the version it was
/// made from, the last of its masters' — and which of those changes the winning version lost. A change is a field's value,
/// or an item a list gained or lost: lists are compared item by item, in any order, as a leveled list's entries or an item's
/// keywords are what a merge puts together (the Bashed Patch's and xEdit's way). Pure functions over the records' project
/// text (Spriggit's YAML); the versions are gathered by Session.FindConflicts and Session's merge in addPatch.
/// </summary>
internal static class Conflicts
{
    /// <summary>Lines each version carries for itself, which say nothing of the record: the Creation Kit's stamps.</summary>
    private static readonly HashSet<string> Stamps = new(StringComparer.Ordinal) { "FormKey", "VersionControl", "FormVersion", "Version2" };

    /// <summary>
    /// Fields the game reads nothing from, which differ between versions all the same: the Creation Kit writes what its memory
    /// held into a condition's Unknown1 and Unknown2 — `Unknown1: 0x709300, Unknown2: 11986` in Dawnguard's copy of a game
    /// idle — and every condition of a copy then read as a change (the test instance's DLCs, 2026-11-07).
    /// </summary>
    private static bool Unread(string key) => System.Text.RegularExpressions.Regex.IsMatch(key, @"^Unknown\d*$");

    /// <summary>
    /// Fields of the world's records that the Creation Kit writes anew each save and no patch merges: an exterior cell's grid
    /// flags, whose bits beyond its four land quadrants hold what its memory held — every Creation Club plugin's copy of
    /// Riverwood's cells had others — and a worldspace's height map, which a plugin's copy of Tamriel leaves out (the test
    /// instance's Creation Club plugins, 2026-11-07).
    /// </summary>
    private static readonly HashSet<string> UnreadPaths = new(StringComparer.Ordinal) { "Grid.Flags", "MaxHeight", "OffsetData" };

    /// <summary>A record's fields by path: each value at its dotted path, each list as its items' text.</summary>
    internal sealed record Shape(Dictionary<string, string> Values, Dictionary<string, List<string>> Lists, Dictionary<string, List<YamlNode>> ListNodes);

    /// <summary>One change of a plugin's that the winner does not carry.</summary>
    /// <param name="Kind">"value": a field's value; "added" or "removed": an item of the list at Path.</param>
    /// <param name="Clash">The winner changed the field too, to a value of its own: which one is meant is a choice.</param>
    internal sealed record Change(string Path, string Kind, string? Was, string? Mine, string? Winners, bool Clash);

    public static Shape Flatten(string text)
    {
        var shape = new Shape(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode root) Walk(root, "", shape);
        return shape;
    }

    private static void Walk(YamlMappingNode node, string prefix, Shape shape)
    {
        foreach (var (keyNode, value) in node.Children)
        {
            var key = ((YamlScalarNode)keyNode).Value ?? "";
            if (prefix.Length == 0 && Stamps.Contains(key) || Unread(key)) continue;
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            if (UnreadPaths.Contains(path)) continue;
            switch (value)
            {
                case YamlMappingNode mapping: Walk(mapping, path, shape); break;
                case YamlSequenceNode sequence:
                    shape.Lists[path] = sequence.Children.Select(Text).ToList();
                    shape.ListNodes[path] = sequence.Children.ToList();
                    break;
                case YamlScalarNode scalar: shape.Values[path] = scalar.Value ?? ""; break;
            }
        }
    }

    /// <summary>A node as one line: a list item compared whole.</summary>
    public static string Text(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value ?? "",
        YamlSequenceNode sequence => $"[{string.Join(", ", sequence.Children.Select(Text))}]",
        YamlMappingNode mapping => $"{{{string.Join(", ", mapping.Children.Where(c => !Unread(((YamlScalarNode)c.Key).Value ?? ""))
            .Select(c => $"{((YamlScalarNode)c.Key).Value}: {Text(c.Value)}"))}}}",
        _ => ""
    };

    /// <summary>
    /// The changes <paramref name="mine"/> made to <paramref name="made"/>, the version it was made from, that
    /// <paramref name="winner"/> does not carry.
    /// </summary>
    public static List<Change> Lost(Shape made, Shape mine, Shape winner)
    {
        var lost = new List<Change>();
        foreach (var path in made.Values.Keys.Union(mine.Values.Keys).Order(StringComparer.Ordinal))
        {
            var was = made.Values.GetValueOrDefault(path);
            var mineValue = mine.Values.GetValueOrDefault(path);
            if (was == mineValue) continue;
            var winners = winner.Values.GetValueOrDefault(path);
            if (winners == mineValue) continue;
            lost.Add(new Change(path, "value", was, mineValue, winners, winners != was));
        }
        foreach (var path in made.Lists.Keys.Union(mine.Lists.Keys).Order(StringComparer.Ordinal))
        {
            var was = made.Lists.GetValueOrDefault(path) ?? [];
            var items = mine.Lists.GetValueOrDefault(path) ?? [];
            var winners = winner.Lists.GetValueOrDefault(path) ?? [];
            foreach (var added in Without(items, was).Where(item => !winners.Contains(item)).Distinct())
                lost.Add(new Change(path, "added", null, added, null, false));
            foreach (var removed in Without(was, items).Where(winners.Contains).Distinct())
                lost.Add(new Change(path, "removed", removed, null, null, false));
        }
        return lost;
    }

    /// <summary>The items of a list that another does not hold, counted: two of an item where the other has one leave one.</summary>
    private static List<string> Without(List<string> items, List<string> other)
    {
        var left = other.GroupBy(i => i).ToDictionary(g => g.Key, g => g.Count());
        var rest = new List<string>();
        foreach (var item in items)
        {
            if (left.TryGetValue(item, out var count) && count > 0) left[item] = count - 1;
            else rest.Add(item);
        }
        return rest;
    }

    /// <summary>A change as a reader takes it: `BasicStats.Damage: 12 (the winner has 9)`, `Keywords + WeapMaterialSilver`.</summary>
    public static string Said(Change change, Func<string, string> named) => change.Kind switch
    {
        "added" => $"{change.Path} + {Short(named(change.Mine!))}",
        "removed" => $"{change.Path} − {Short(named(change.Was!))}",
        _ => $"{change.Path}: {Short(named(change.Mine ?? "(none)"))} (the winner has {Short(named(change.Winners ?? "none"))}" +
             (change.Clash ? $", changed from {Short(named(change.Was ?? "none"))} too" : "") + ")"
    };

    private static string Short(string text) => text.Length > 120 ? text[..117] + "…" : text;

    /// <summary>
    /// The edits that put each plugin's lost changes into the winner's copy, as editRecord's `set` takes them — a field's value
    /// at its path, a list written whole: the winner's items, each plugin's lost additions put in and lost removals taken out —
    /// and the fields two of them changed to values of their own, the later plugin's kept.
    /// </summary>
    public static (JsonObject Set, List<string> Clashes) Merge(Shape winner, IReadOnlyList<(string Plugin, Shape Made, Shape Mine, List<Change> Lost)> losers,
        Func<string, string> named)
    {
        var set = new JsonObject();
        var clashes = new List<string>();
        var values = new Dictionary<string, (string Plugin, string? Value)>(StringComparer.Ordinal);
        var lists = new Dictionary<string, List<YamlNode>>(StringComparer.Ordinal);
        foreach (var (plugin, _, mine, lost) in losers)
        {
            foreach (var change in lost.Where(c => c.Kind == "value"))
            {
                if (values.TryGetValue(change.Path, out var earlier) && earlier.Value != change.Mine)
                    clashes.Add($"{change.Path}: {plugin} has {Short(named(change.Mine ?? "none"))}, {earlier.Plugin} {Short(named(earlier.Value ?? "none"))} — {plugin}'s kept");
                else if (change.Clash)
                    clashes.Add($"{change.Path}: {plugin} has {Short(named(change.Mine ?? "none"))}, the winner {Short(named(change.Winners ?? "none"))} — {plugin}'s kept");
                values[change.Path] = (plugin, change.Mine);
            }
            foreach (var path in lost.Where(c => c.Kind != "value").Select(c => c.Path).Distinct())
            {
                var items = lists.TryGetValue(path, out var merged) ? merged : (winner.ListNodes.GetValueOrDefault(path) ?? []).ToList();
                foreach (var change in lost.Where(c => c.Path == path))
                {
                    if (change.Kind == "added" && mine.ListNodes.GetValueOrDefault(path)?.FirstOrDefault(n => Text(n) == change.Mine) is { } node
                        && !items.Any(n => Text(n) == change.Mine))
                        items.Add(node);
                    else if (change.Kind == "removed" && items.FindIndex(n => Text(n) == change.Was) is >= 0 and var at)
                        items.RemoveAt(at);
                }
                lists[path] = items;
            }
        }
        foreach (var (path, (_, value)) in values) set[path] = value is null ? null : Json(new YamlScalarNode(value));
        foreach (var (path, items) in lists) set[path] = new JsonArray(items.Select(Json).ToArray());
        return (set, clashes);
    }

    /// <summary>A YAML node as editRecord's JSON value: a number as a number, other scalars as text, lists and parts as such.</summary>
    public static JsonNode? Json(YamlNode node) => node switch
    {
        YamlScalarNode scalar when double.TryParse(scalar.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && !(scalar.Value ?? "").StartsWith("0x", StringComparison.OrdinalIgnoreCase) => JsonValue.Create(number),
        YamlScalarNode scalar => JsonValue.Create(scalar.Value ?? ""),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(Json).ToArray()),
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value ?? "", Json(c.Value)))),
        _ => null
    };
}
