using System.Globalization;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkyrimHelper;

/// <summary>
/// What is placed in a cell, for readRecord to show beside the cell's own record: each plugin's version of a cell holds the
/// references that plugin places there, so they are gathered from every version, the winning one of each reference kept.
/// The cell's text alone said nothing of them, and no eval round 125 or 126 world run placed a chest: five wrote it into the
/// cell's file under fields of their own, one looked for a placed reference in a mod's text, and one answered that a
/// reference is nothing the tools can write. The objects one would place beside — containers, furniture, doors, activators,
/// NPCs, items — are listed with their positions; the rest is counted by its kind. And the lines a new reference is written
/// with, in the file it goes in.
/// </summary>
internal static class CellContents
{
    /// <summary>The most references listed; the rest are counted.</summary>
    private const int Listed = 40;

    /// <summary>The kinds of record a reference is listed by, in this order: what a new object is placed beside.</summary>
    private static readonly string[] Shown = ["Container", "Furniture", "Door", "Activator", "Npc", "TalkingActivator", "Flora",
        "Book", "Weapon", "Armor", "Ingestible", "MiscItem", "Ammunition", "Ingredient", "Key", "SoulGem", "Scroll"];

    public static JsonObject? Of(IReadOnlyList<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> contexts,
        RecordIndex index, string? cellFile)
    {
        if (contexts.Count == 0 || contexts[0].Record is not ICellGetter winner) return null;
        var seen = new HashSet<FormKey>();
        var placed = new List<(IPlacedGetter Reference, bool Persistent)>();
        foreach (var context in contexts)
        {
            if (context.Record is not ICellGetter cell) continue;
            foreach (var reference in cell.Persistent) if (seen.Add(reference.FormKey)) placed.Add((reference, true));
            foreach (var reference in cell.Temporary) if (seen.Add(reference.FormKey)) placed.Add((reference, false));
        }

        var rows = placed.Select(p => (p.Reference, p.Persistent, Base: BaseOf(p.Reference), Placement: PlacementOf(p.Reference)))
            .Select(r => (r.Reference, r.Persistent, r.Placement,
                Base: r.Base is { } key && index.ByFormKey.TryGetValue(key, out var entry) ? entry : null))
            .ToList();
        var byKind = rows.GroupBy(r => r.Base?.Type ?? "unknown").OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
        var listed = rows.Where(r => r.Base is not null && Array.IndexOf(Shown, r.Base.Type) >= 0 && r.Placement is not null)
            .OrderBy(r => Array.IndexOf(Shown, r.Base!.Type)).Take(Listed).ToList();

        var references = new JsonArray(listed.Select(r => (JsonNode)new JsonObject
        {
            ["formId"] = Session.FormKeyText(r.Reference.FormKey),
            ["editorId"] = r.Reference.EditorID,
            ["base"] = $"{r.Base!.EditorId ?? Session.FormKeyText(r.Base.FormKey)}{(r.Base.Name is { Length: > 0 } name ? $" \"{name}\"" : "")}, " +
                $"{RecordFiles.Article(r.Base.Type)} ({r.Base.Signature})",
            ["position"] = Triple(r.Placement!.Position),
            ["rotation"] = Triple(r.Placement.Rotation),
            ["persistent"] = r.Persistent
        }).ToArray());

        // A new reference beside the first object listed, moved a little along x — its rotation kept, so it faces the same way.
        var near = listed.FirstOrDefault();
        var position = near.Placement is { } at ? Triple(new P3Float(at.Position.X + 64, at.Position.Y, at.Position.Z)) : "0, 0, 0";
        var rotation = near.Placement is { } turned ? Triple(turned.Rotation) : "0, 0, 0";
        var folder = cellFile is null ? null : cellFile[..cellFile.LastIndexOf('/')];
        var name = winner.EditorID ?? "MyCell";
        return new JsonObject
        {
            ["count"] = placed.Count,
            ["byKind"] = new JsonObject(byKind.Select(k => KeyValuePair.Create(k.Key, (JsonNode?)k.Value))),
            ["references"] = references,
            ["newReference"] = new JsonObject
            {
                ["pathInPluginFolder"] = $"{folder ?? $"Cells/<block>/<sub-block>/{name}"}/Temporary/<EditorID>.yaml",
                ["text"] = $"MutagenObjectType: PlacedObject\nEditorID: <a name of its own>\nBase: <the record placed: its editor ID or FormID>\n" +
                    $"Placement:\n  Position: {position}\n  Rotation: {rotation}\n",
                ["note"] = "A reference placed in a cell is a file of its own in the cell's Temporary folder — its kind on its first line, " +
                    "PlacedObject for an object, PlacedNpc for an NPC — beside the copy of the cell addPatch makes, which keeps " +
                    $"{name}'s own fields; the cell's file holds none of them. Position is in game units, x and y across the floor, " +
                    "z up, rotation in radians about each axis" +
                    (near.Base is { } by ? $"; the position above is {by.EditorId ?? Session.FormKeyText(by.FormKey)}'s, 64 units along x" : "") +
                    ". A Persistent reference — in the Persistent folder — is one a quest or script names; an object placed for the player is Temporary." +
                    (GridOf(winner) is { } grid
                        ? $" {name} lies outdoors, cell {grid.X}, {grid.Y} of its worldspace: a reference placed in it stands between x {grid.X * CellSize} and " +
                          $"{(grid.X + 1) * CellSize}, y {grid.Y * CellSize} and {(grid.Y + 1) * CellSize}, on the ground beside what is placed there — the z of " +
                          "the objects near it, since the ground rises and falls."
                        : "")
            }
        };
    }

    /// <summary>The length of an exterior cell's side, in game units.</summary>
    internal const int CellSize = 4096;

    /// <summary>An exterior cell's grid — the cell x, y of its worldspace — or null for an interior cell.</summary>
    internal static P2Int? GridOf(ICellGetter cell) =>
        cell.Flags.HasFlag(Cell.Flag.IsInteriorCell) || cell.Grid is not { } grid ? null : grid.Point;

    /// <summary>The exterior cell a position lies in, by its grid.</summary>
    internal static P2Int GridAt(P3Float position) =>
        new((int)Math.Floor(position.X / CellSize), (int)Math.Floor(position.Y / CellSize));

    /// <summary>How far below the lowest container, furniture, door or activator a cell places a reference may be, in game units.</summary>
    private const float Below = 32;
    /// <summary>How far beyond them across the floor.</summary>
    private const float Beyond = 96;

    /// <summary>
    /// The project's references placed in a load-order cell away from what the cell's own plugins place there — below its lowest
    /// container, furniture, door or activator, or beyond them across the floor: under the floor, or outside the walls, where the
    /// player never finds it — as notes beside a build that succeeds, each with the call that places it beside the first container.
    /// Eval round 128's world #4 placed its chest at 0, 0, 0 in Breezehome, 64 units below its ground floor and 110 east of the
    /// easternmost thing placed there, and its build was a plain success.
    /// </summary>
    public static List<string> PlacedNotes(ISkyrimModGetter mod,
        Func<FormKey, List<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>> contexts, RecordIndex index, string pluginDir)
    {
        var notes = new List<string>();
        var files = RecordFiles.CellRecords(pluginDir).Where(c => c.Type is not null && c.Type != typeof(Cell))
            .Select(c => (c.Relative, Identity: RecordFiles.Identity(File.ReadAllText(c.File)))).ToList();
        foreach (var cell in mod.EnumerateMajorRecords<ICellGetter>())
        {
            var own = cell.Persistent.Concat(cell.Temporary).Where(r => r.FormKey.ModKey == mod.ModKey).ToList();
            if (own.Count == 0) continue;
            // Outdoors no walls and no floor: the ground rises and falls, and what is placed lies far apart. A reference belongs to
            // the cell its position lies in — one placed beyond it the game shows with a cell it does not stand in, loaded and
            // unloaded with the wrong ground.
            if (GridOf(cell) is { } grid)
            {
                notes.AddRange(OutsideGrid(cell, grid, own, files, contexts, pluginDir));
                continue;
            }
            var seen = new HashSet<FormKey>();
            var points = new List<(P3Float At, string Name, string Type)>();
            foreach (var context in contexts(cell.FormKey).Where(c => c.ModKey != mod.ModKey))
            {
                if (context.Record is not ICellGetter version) continue;
                foreach (var reference in version.Persistent.Concat(version.Temporary))
                    if (seen.Add(reference.FormKey) && BaseOf(reference) is { } key && index.ByFormKey.TryGetValue(key, out var entry)
                        && Array.IndexOf(Shown, entry.Type) is >= 0 and < 4 && PlacementOf(reference) is { } placement)
                        points.Add((placement.Position, entry.EditorId ?? Session.FormKeyText(entry.FormKey), entry.Type));
            }
            if (points.Count < 5) continue;
            var (minX, maxX) = (points.Min(p => p.At.X), points.Max(p => p.At.X));
            var (minY, maxY) = (points.Min(p => p.At.Y), points.Max(p => p.At.Y));
            var minZ = points.Min(p => p.At.Z);
            var near = points.FirstOrDefault(p => p.Type == "Container") is { Name: not null } container ? container : points[0];
            var beside = Triple(new P3Float(near.At.X + 64, near.At.Y, near.At.Z));
            var cellName = cell.EditorID ?? Session.FormKeyText(cell.FormKey);
            foreach (var reference in own)
            {
                var at = PlacementOf(reference)?.Position ?? new P3Float(0, 0, 0);
                var under = at.Z < minZ - Below;
                var outside = at.X < minX - Beyond || at.X > maxX + Beyond || at.Y < minY - Beyond || at.Y > maxY + Beyond;
                if (!under && !outside) continue;
                var file = files.FirstOrDefault(f => f.Identity.FormKey == reference.FormKey
                    || reference.EditorID is { } id && string.Equals(f.Identity.EditorId, id, StringComparison.OrdinalIgnoreCase)).Relative;
                var name = reference.EditorID ?? Session.FormKeyText(reference.FormKey);
                var where = under && outside ? "under the floor and outside the walls" : under ? "under the floor" : "outside the walls";
                notes.Add($"{file ?? name}: {name} is placed at {Triple(at)}, and {cellName}'s containers, furniture, doors and activators lie " +
                    $"between x {Whole(minX)} and {Whole(maxX)}, y {Whole(minY)} and {Whole(maxY)}, the lowest at z {Whole(minZ)} — in game it is " +
                    $"{where}, where the player never finds it. " +
                    (file is not null
                        ? $"One call places it beside {near.Name}, 64 units along x, and builds again: " +
                          ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), file, $"{{\"path\": \"Placement.Position\", \"value\": \"{beside}\"}}", build: true)
                        : $"Write its Position as one beside a reference of the cell — {beside} is beside {near.Name}") +
                    "; where it is meant to be hidden, nothing to change, and this warning stays");
            }
        }
        return notes;
    }

    /// <summary>
    /// The project's references placed in an exterior cell at a position of another cell — each with the cell its position lies in,
    /// and the calls that copy that cell in and move the reference there, or put it back in its cell.
    /// </summary>
    private static IEnumerable<string> OutsideGrid(ICellGetter cell, P2Int grid, List<IPlacedGetter> own,
        List<(string Relative, (FormKey? FormKey, string? EditorId) Identity)> files,
        Func<FormKey, List<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>> contexts, string pluginDir)
    {
        var cellName = cell.EditorID ?? Session.FormKeyText(cell.FormKey);
        var worldspace = contexts(cell.FormKey).Select(WorldspaceOf).FirstOrDefault(w => w is not null);
        foreach (var reference in own)
        {
            if (PlacementOf(reference) is not { } placement) continue;
            var lies = GridAt(placement.Position);
            if (lies.Equals(grid)) continue;
            var name = reference.EditorID ?? Session.FormKeyText(reference.FormKey);
            var file = files.FirstOrDefault(f => f.Identity.FormKey == reference.FormKey
                || reference.EditorID is { } id && string.Equals(f.Identity.EditorId, id, StringComparison.OrdinalIgnoreCase)).Relative;
            var other = worldspace is null ? null : CellAt(contexts(worldspace.Value), lies);
            var inside = Triple(new P3Float(
                Math.Clamp(placement.Position.X, grid.X * CellSize + 64, (grid.X + 1) * CellSize - 64),
                Math.Clamp(placement.Position.Y, grid.Y * CellSize + 64, (grid.Y + 1) * CellSize - 64), placement.Position.Z));
            var project = ToolCalls.ProjectOf(pluginDir);
            var there = other is null
                ? $"cell {lies.X}, {lies.Y}, which no plugin of the load order has"
                : $"{other.EditorID ?? Session.FormKeyText(other.FormKey)}, cell {lies.X}, {lies.Y}";
            var move = other is null || file is null ? "" :
                $" Where it is meant to stand there, the calls are addPatch {{\"project\": {ToolCalls.Quoted(project)}, \"records\": [\"{Session.FormKeyText(other.FormKey)}\"]}}, " +
                $"then moveRecord of {file} into that cell's Temporary/ folder, which addPatch's answer names.";
            yield return $"{file ?? name}: {name} is placed at {Triple(placement.Position)}, in {there} — not in {cellName}, cell {grid.X}, {grid.Y}, " +
                $"between x {grid.X * CellSize} and {(grid.X + 1) * CellSize}, y {grid.Y * CellSize} and {(grid.Y + 1) * CellSize}: the game loads it with {cellName} " +
                "while it stands elsewhere, on ground that is not loaded with it. " +
                (file is not null
                    ? $"One call places it in {cellName}, at its edge nearest to where it is, and builds again: " +
                      ToolCalls.Edit(project, file, $"{{\"path\": \"Placement.Position\", \"value\": \"{inside}\"}}", build: true) + "."
                    : $"Write its Position in {cellName}'s cell — {inside} is.") + move;
        }
    }

    /// <summary>The worldspace a cell's version lies in, through its parents.</summary>
    private static FormKey? WorldspaceOf(IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter> context)
    {
        for (var parent = context.Parent; parent is not null; parent = parent.Parent)
            if (parent.Record is IWorldspaceGetter world) return world.FormKey;
        return null;
    }

    /// <summary>The cell of a worldspace at a grid, from the worldspace's versions, winning first.</summary>
    private static ICellGetter? CellAt(IEnumerable<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> worldspaces, P2Int grid)
    {
        foreach (var context in worldspaces)
            if (context.Record is IWorldspaceGetter world)
                foreach (var block in world.SubCells)
                    foreach (var subBlock in block.Items)
                        foreach (var cell in subBlock.Items)
                            if (cell.Grid?.Point is { } point && point.Equals(grid)) return cell;
        return null;
    }

    /// <summary>A coordinate rounded to a whole unit, for a note.</summary>
    private static string Whole(float value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);

    private static FormKey? BaseOf(IPlacedGetter reference) => reference switch
    {
        IPlacedObjectGetter o => o.Base.FormKeyNullable,
        IPlacedNpcGetter n => n.Base.FormKeyNullable,
        _ => null
    };

    private static IPlacementGetter? PlacementOf(IPlacedGetter reference) => reference switch
    {
        IPlacedObjectGetter o => o.Placement,
        IPlacedNpcGetter n => n.Placement,
        _ => null
    };

    /// <summary>x, y, z as Spriggit writes a position; a negative zero, which rounding leaves, written 0.</summary>
    private static string Triple(P3Float p) => string.Join(", ", new[] { p.X, p.Y, p.Z }.Select(v =>
        (Math.Round((double)v, 4) + 0.0).ToString("0.####", CultureInfo.InvariantCulture)));
}
