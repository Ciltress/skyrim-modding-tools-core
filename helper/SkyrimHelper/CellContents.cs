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
        RecordIndex index, string? cellFile,
        Func<FormKey, List<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>>? allContexts = null)
    {
        if (contexts.Count == 0 || contexts[0].Record is not ICellGetter winner) return null;
        var (rows, listed) = RowsOf(contexts, index, allContexts);
        var placed = rows;
        var byKind = rows.GroupBy(r => r.Base?.Type ?? "unknown").OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

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
        var position = near.Placement is { } at ? Triple(Beside(at.Position)) : "0, 0, 0";
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

    /// <summary>A reference a cell places, with its placement and the record it places, as the index knows it.</summary>
    private readonly record struct Row(IPlacedGetter Reference, bool Persistent, IPlacementGetter? Placement, RecordIndex.Entry? Base);

    /// <summary>
    /// What a cell places, from every version of it — outdoors with the worldspace's persistent references standing in its
    /// square — and those its answer lists, in its order: what a new object is placed beside.
    /// </summary>
    private static (List<Row> Rows, List<Row> Listed) RowsOf(IReadOnlyList<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> contexts,
        RecordIndex index, Func<FormKey, List<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>>? allContexts)
    {
        var seen = new HashSet<FormKey>();
        var placed = new List<(IPlacedGetter Reference, bool Persistent)>();
        foreach (var context in contexts)
        {
            if (context.Record is not ICellGetter cell) continue;
            foreach (var reference in cell.Persistent) if (seen.Add(reference.FormKey)) placed.Add((reference, true));
            foreach (var reference in cell.Temporary) if (seen.Add(reference.FormKey)) placed.Add((reference, false));
        }
        // Outdoors, the worldspace's persistent references standing in this cell's square too: the game keeps them in the
        // worldspace's top cell, and shows them here — Alvor's grindstone in Riverwood is one, and readRecord of Riverwood listed
        // it nowhere (the outdoor task's second mini-round).
        if (contexts.Count > 0 && contexts[0].Record is ICellGetter winner && GridOf(winner) is { } square && allContexts is not null
            && contexts.Select(WorldspaceOf).FirstOrDefault(w => w is not null) is { } world)
        {
            foreach (var version in allContexts(world))
            {
                if (version.Record is not IWorldspaceGetter { TopCell: { } top }) continue;
                foreach (var reference in top.Persistent)
                    if (PlacementOf(reference) is { } spot && GridAt(spot.Position).Equals(square) && seen.Add(reference.FormKey))
                        placed.Add((reference, true));
            }
        }
        var rows = placed.Select(p => new Row(p.Reference, p.Persistent, PlacementOf(p.Reference),
            BaseOf(p.Reference) is { } key && index.ByFormKey.TryGetValue(key, out var entry) ? entry : null)).ToList();
        var listed = rows.Where(r => r.Base is not null && Array.IndexOf(Shown, r.Base.Type) >= 0 && r.Placement is not null)
            .OrderBy(r => Array.IndexOf(Shown, r.Base!.Type)).Take(Listed).ToList();
        return (rows, listed);
    }

    /// <summary>The spot of a new reference beside a placed one: 64 units along x.</summary>
    private static P3Float Beside(P3Float at) => new(at.X + 64, at.Y, at.Z);

    /// <summary>
    /// The example spot readRecord of a cell gives a new reference — beside the first object it lists — and that object's
    /// name; null where it lists none.
    /// </summary>
    private static (P3Float At, string By)? ExampleSpot(List<Row> listed) =>
        listed is [{ Placement: { } first, Base: { } by }, ..] ? (Beside(first.Position), NameOf(by)) : null;

    /// <summary>A placed record's name for messages: its editor ID, else its FormID.</summary>
    private static string NameOf(RecordIndex.Entry entry) => entry.EditorId ?? Session.FormKeyText(entry.FormKey);

    /// <summary>How near across the ground two references stand that are taken for one inside the other, in game units.</summary>
    private const float Inside = 16;

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
                notes.AddRange(OutsideGrid(cell, grid, own, files, contexts, index, pluginDir));
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
        Func<FormKey, List<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>> contexts, RecordIndex index, string pluginDir)
    {
        var cellName = cell.EditorID ?? Session.FormKeyText(cell.FormKey);
        var versions = contexts(cell.FormKey);
        var worldspace = versions.Select(WorldspaceOf).FirstOrDefault(w => w is not null);
        // The ground, from the version of the cell whose landscape wins: the project's own references are held to it.
        var land = versions.Select(v => (v.Record as ICellGetter)?.Landscape?.VertexHeightMap).FirstOrDefault(m => m is not null);
        var heights = land is null ? null : CellScene.Heights(land);
        // What the load order places there, and the example spot readRecord of the cell gave: before the project placed anything.
        var ownPlugin = own[0].FormKey.ModKey;
        var (rows, listed) = RowsOf(versions.Where(v => v.ModKey != ownPlugin).ToList(), index, contexts);
        var spot = ExampleSpot(listed);
        foreach (var reference in own)
        {
            if (PlacementOf(reference) is not { } placement) continue;
            var lies = GridAt(placement.Position);
            var name = reference.EditorID ?? Session.FormKeyText(reference.FormKey);
            var file = files.FirstOrDefault(f => f.Identity.FormKey == reference.FormKey
                || reference.EditorID is { } id && string.Equals(f.Identity.EditorId, id, StringComparison.OrdinalIgnoreCase)).Relative;
            if (lies.Equals(grid))
            {
                var at = placement.Position;
                if (rows.FirstOrDefault(r => r.Base is not null && r.Placement is { } other && r.Reference.FormKey != reference.FormKey
                        && Math.Abs(other.Position.X - at.X) < Inside && Math.Abs(other.Position.Y - at.Y) < Inside) is { Base: { } holder, Placement: { } held })
                    yield return InsideAnother(name, file, at, holder, held.Position, cellName, pluginDir);
                else if (spot is { } example && Math.Abs(at.X - example.At.X) < 1 && Math.Abs(at.Y - example.At.Y) < 1)
                    yield return ExampleKept(name, file, at, example.By, cellName);
                if (heights is not null && CellScene.GroundAt(heights, grid, at.X, at.Y) is var ground
                    && OffTheGround(name, file, at, ground, FloorsNear(rows, reference.FormKey, at, ground), pluginDir) is { } off)
                    yield return off;
                continue;
            }
            var other = worldspace is null ? null : CellAt(contexts(worldspace.Value), lies);
            var inside = Triple(new P3Float(
                Math.Clamp(placement.Position.X, grid.X * CellSize + 64, (grid.X + 1) * CellSize - 64),
                Math.Clamp(placement.Position.Y, grid.Y * CellSize + 64, (grid.Y + 1) * CellSize - 64), placement.Position.Z));
            var project = ToolCalls.ProjectOf(pluginDir);
            var there = other is null
                ? $"cell {lies.X}, {lies.Y}, which no plugin of the load order has"
                : $"{other.EditorID ?? Session.FormKeyText(other.FormKey)}, cell {lies.X}, {lies.Y}";
            var move = other is null || file is null ? "" :
                $"the calls are addPatch {{\"project\": {ToolCalls.Quoted(project)}, \"records\": [\"{Session.FormKeyText(other.FormKey)}\"]}}, " +
                $"then moveRecord of {file} into that cell's Temporary/ folder, which addPatch's answer names, and a build";
            // What the game places beside it there: placed a step from Riverwood's grindstone, which stands in Riverwood04, and
            // filed under Riverwood, eval round 135's outdoor+detailed #2 made the call that set the chest at Riverwood's edge,
            // away from the grindstone, and its check found it in the wrong cell.
            var at2 = placement.Position;
            var beside = other is null || move.Length == 0 ? null
                : RowsOf(contexts(other.FormKey).Where(v => v.ModKey != ownPlugin).ToList(), index, contexts).Rows
                    .Where(r => r.Base is { } entry && entry.EditorId?.Contains("Marker", StringComparison.OrdinalIgnoreCase) != true && r.Placement is not null)
                    .Select(r => (Row: r, Distance: Math.Sqrt(Math.Pow(r.Placement!.Position.X - at2.X, 2) + Math.Pow(r.Placement.Position.Y - at2.Y, 2))))
                    .Where(r => r.Distance <= FloorReach).OrderBy(r => r.Distance).Select(r => NameOf(r.Row.Base!)).FirstOrDefault();
            var edge = file is not null
                ? $"one call places it in {cellName}, at its edge nearest to where it is, and builds again: " +
                  ToolCalls.Edit(project, file, $"{{\"path\": \"Placement.Position\", \"value\": \"{inside}\"}}", build: true)
                : $"write its Position in {cellName}'s cell — {inside} is";
            yield return $"{file ?? name}: {name} is placed at {Triple(placement.Position)}, in {there} — not in {cellName}, cell {grid.X}, {grid.Y}, " +
                $"between x {grid.X * CellSize} and {(grid.X + 1) * CellSize}, y {grid.Y * CellSize} and {(grid.Y + 1) * CellSize}: the game loads it with {cellName} " +
                "while it stands elsewhere, on ground that is not loaded with it. " +
                (beside is not null
                    ? $"It stands beside {beside}, which the game places in {other!.EditorID ?? Session.FormKeyText(other.FormKey)}: where it is meant " +
                      $"to stand there, {move}; only where it was meant in {cellName}, {edge}."
                    : $"{char.ToUpperInvariant(edge[0])}{edge[1..]}." + (move.Length > 0 ? $" Where it is meant to stand there, {move}." : ""));
        }
    }

    /// <summary>How far above the ground an object may stand outdoors before it is warned of: on a table, a cart, a low wall.</summary>
    private const float AboveGround = 200;
    /// <summary>How far below it: an object's origin is its base, and the ground's heights are 128 units apart.</summary>
    private const float BelowGround = 64;

    /// <summary>How far across the ground an object the game places stands from a reference to say what floor it stands on.</summary>
    private const float FloorReach = 256;

    /// <summary>The kinds of record placed standing on a floor, their origin at their base: what a floor's height is read from.</summary>
    private static readonly string[] StandingKinds = ["Container", "Furniture", "Door", "Npc", "Flora", "Tree"];

    /// <summary>
    /// The heights of the floors the game's own objects stand on near a reference outdoors — a smithy's, a walkway's, a cliff's
    /// top — that the landscape does not hold: of each container, piece of furniture, door, NPC, plant and tree within reach of
    /// it, at or above the ground there. Markers float where they are placed and are left out. Eval round 134: Riverwood's
    /// grindstone stands 144 units above the landscape, on the smithy's floor, and a chest built at its height beside it was
    /// warned of floating, set down onto the landscape by the warning's call, and stood under the smithy.
    /// </summary>
    private static List<(float Z, string By)> FloorsNear(List<Row> rows, FormKey self, P3Float at, float ground) =>
        rows.Where(r => r.Reference.FormKey != self && r.Base is { } entry && Array.IndexOf(StandingKinds, entry.Type) >= 0
                && entry.EditorId?.Contains("Marker", StringComparison.OrdinalIgnoreCase) != true
                && r.Placement is { } p && p.Position.Z >= ground - BelowGround
                && Math.Abs(p.Position.X - at.X) <= FloorReach && Math.Abs(p.Position.Y - at.Y) <= FloorReach
                && (p.Position.X - at.X) * (p.Position.X - at.X) + (p.Position.Y - at.Y) * (p.Position.Y - at.Y) <= FloorReach * FloorReach)
            .Select(r => (r.Placement!.Position.Z, NameOf(r.Base!)))
            .ToList();

    /// <summary>
    /// A reference placed outdoors far above the ground at its x and y, or below it — floating, or buried — with the call that sets
    /// it on the ground. Eval round 132's hand-made outdoor mods built a chest 311 units above Riverwood's ground with no word.
    /// One standing at about the height of an object the game places beside it stands on that object's floor, and is not warned.
    /// </summary>
    private static string? OffTheGround(string name, string? file, P3Float at, float ground, List<(float Z, string By)> floors, string pluginDir)
    {
        var above = at.Z - ground;
        if (above <= AboveGround && above >= -BelowGround) return null;
        if (floors.Any(f => at.Z - f.Z <= AboveGround && at.Z - f.Z >= -BelowGround)) return null;
        // Floating: set down onto the highest floor under it, the ground's or one the game's objects beside it stand on.
        (float Z, string By)? floor = null;
        if (above > 0)
            foreach (var f in floors)
                if (f.Z > ground && f.Z < at.Z && (floor is null || f.Z > floor.Value.Z)) floor = f;
        var onIt = Triple(new P3Float(at.X, at.Y, (float)Math.Round(floor?.Z ?? ground)));
        var how = above > 0
            ? $"{Whole(above)} units above the ground there, at z {Whole(ground)}" +
              (floor is { } on ? $", and {Whole(at.Z - on.Z)} above where {on.By} beside it stands, at z {Whole(on.Z)}" : "") + ": in game it floats"
            : $"{Whole(-above)} units below the ground there, at z {Whole(ground)}: in game it is buried";
        return $"{file ?? name}: {name} is placed at {Triple(at)}, {how}. " +
            (file is not null
                ? (floor is { } by ? $"One call sets it at {by.By}'s height and builds again: " : "One call sets it on the ground and builds again: ") +
                  ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), file, $"{{\"path\": \"Placement.Position\", \"value\": \"{onIt}\"}}", build: true)
                : $"Write its Position on the ground — {onIt}") +
            (above > 0 ? "; where it stands on something there — a table, a roof, a bridge — nothing to change, and this warning stays" : "");
    }

    /// <summary>
    /// A reference placed outdoors on the example spot readRecord of its cell gave — beside the first object listed, whatever
    /// it is — where the user may have named a place. The outdoor task's third mini-round: asked for a chest beside Riverwood's
    /// grindstone, a run read the cell, not the grindstone, and built its chest on the example spot, beside another object.
    /// </summary>
    private static string ExampleKept(string name, string? file, P3Float at, string by, string cellName) =>
        $"{file ?? name}: {name} stands at {Triple(at)}, the example spot readRecord of {cellName} gave — beside {by}, the first thing " +
        "it lists, 64 units along x — not a place the user named. Where the user named one — beside a grindstone, by a door — read " +
        $"that thing with `in`, readRecord {{\"record\": \"<its editor ID>\", \"in\": {ToolCalls.Quoted(cellName)}}}: its placedIn gives " +
        "its position; set this Position 64 units from it, then build again. Where the user named no place, nothing to change, and " +
        "this warning stays";

    /// <summary>
    /// A reference placed outdoors where another of the cell stands — the two inside each other in game — with the call that
    /// sets it beside that one. The outdoor task's third mini-round: a run read Riverwood, took the position of the first thing
    /// listed, Alvor's merchant chest, hidden under the ground, and built its own chest in it, not beside the grindstone asked for.
    /// </summary>
    private static string InsideAnother(string name, string? file, P3Float at, RecordIndex.Entry other, P3Float there, string cellName, string pluginDir)
    {
        var beside = Triple(new P3Float(there.X + 64, there.Y, at.Z));
        var what = other.Name is { Length: > 0 } shown ? $"{NameOf(other)} \"{shown}\", {RecordFiles.Article(other.Type)}" : NameOf(other);
        return $"{file ?? name}: {name} is placed at {Triple(at)}, where {what} stands, at {Triple(there)}: in game the two are " +
            "inside each other. Place it beside what the user named — read that thing with `in` for its position, readRecord " +
            $"{{\"record\": \"<its editor ID>\", \"in\": {ToolCalls.Quoted(cellName)}}} — 64 units from it" +
            (file is not null
                ? $"; or beside {NameOf(other)}, one call, which builds again: " +
                  ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), file, $"{{\"path\": \"Placement.Position\", \"value\": \"{beside}\"}}", build: true)
                : $"; or beside {NameOf(other)}, at {beside}");
    }

    /// <summary>The worldspace a cell's version lies in, through its parents.</summary>
    private static FormKey? WorldspaceOf(IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter> context)
    {
        for (var parent = context.Parent; parent is not null; parent = parent.Parent)
            if (parent.Record is IWorldspaceGetter world) return world.FormKey;
        return null;
    }

    /// <summary>The cell of a worldspace at a grid, from the worldspace's versions, winning first.</summary>
    internal static ICellGetter? CellAt(IEnumerable<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> worldspaces, P2Int grid)
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
