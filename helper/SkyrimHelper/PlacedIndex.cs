using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkyrimHelper;

/// <summary>
/// Where the load order places each record — the references of every cell, indoors and out, by their base — for readRecord to
/// say where a thing stands: "the grindstone at Alvor's smithy" is CraftingBlacksmithSharpeningWheel, placed in Riverwood01.
/// Asked for a chest beside it, both plain runs of the outdoor task's first mini-round took the cell named Riverwood and the
/// position beside its cooking pot that readRecord offered: no tool said where the grindstone stood. Built on first use, from
/// each plugin's cells, the last version of a reference winning; dropped with the load order.
/// </summary>
internal sealed class PlacedIndex
{
    /// <summary>One placed reference: its cell, the cell's grid outdoors, and its position.</summary>
    internal sealed record Spot(FormKey Reference, FormKey Cell, string? CellEditorId, string? CellName, string? Worldspace, P2Int? Grid, P3Float Position);

    private readonly Dictionary<FormKey, List<Spot>> byBase = new();

    /// <summary>Each load door's spot, by the door reference: the cell a door teleporting to it leads to.</summary>
    private readonly Dictionary<FormKey, Spot> loadDoors = new();

    /// <summary>Where the load order places a record: every reference of it, by its base.</summary>
    public IReadOnlyList<Spot> SpotsOf(FormKey baseRecord) => byBase.GetValueOrDefault(baseRecord) ?? [];

    /// <summary>Where a load door stands — its cell — or null for a reference that is none.</summary>
    public Spot? LoadDoor(FormKey reference) => loadDoors.GetValueOrDefault(reference);

    /// <summary>A spot's cell for a message: its editor ID and name, and outdoors its worldspace.</summary>
    public static string CellText(Spot spot) =>
        $"{spot.CellEditorId ?? Session.FormKeyText(spot.Cell)}" + (spot.CellName is { Length: > 0 } name ? $" \"{name}\"" : "") +
        (spot.Grid is { } g ? $" (outdoors, {spot.Worldspace} {g.X}, {g.Y})" : "");

    /// <summary>The most places readRecord lists; the rest are counted.</summary>
    internal const int Listed = 40;

    public static PlacedIndex Build(IReadOnlyList<ISkyrimModGetter> mods)
    {
        var clock = Stopwatch.StartNew();
        var index = new PlacedIndex();
        var spots = new Dictionary<FormKey, (FormKey Base, Spot Spot)>();
        // The load doors: a few thousand of the references, kept by their own FormKey.
        var doors = new HashSet<FormKey>();
        // An exterior cell by its worldspace and grid, for a persistent reference of the worldspace's top cell, which lies in
        // the cell its position is in.
        // Each plugin is read on its own thread — a plugin has its own reader, and reading 1,408 one after another took 6.5 s —
        // and what each gives is merged in load order after, so the index is the one a single thread makes. A thread reads its
        // own plugin only: the grid's cells are kept as values, never as another plugin's records.
        var gridOf = new List<((FormKey, P2Int) At, GridCell Cell)>[mods.Count];
        Parallel.For(0, mods.Count, i =>
        {
            var found = new List<((FormKey, P2Int), GridCell)>();
            foreach (var world in mods[i].Worldspaces)
                foreach (var block in world.SubCells)
                    foreach (var sub in block.Items)
                        foreach (var cell in sub.Items)
                            if (cell.Grid?.Point is { } point) found.Add(((world.FormKey, point), new GridCell(cell.FormKey, cell.EditorID, cell.Name?.String)));
            gridOf[i] = found;
        });
        var gridCells = new Dictionary<(FormKey, P2Int), GridCell>();
        foreach (var found in gridOf)
            foreach (var (at, cell) in found) gridCells.TryAdd(at, cell);

        var putsOf = new List<Placed>[mods.Count];
        Parallel.For(0, mods.Count, i =>
        {
            var mod = mods[i];
            var puts = new List<Placed>();
            foreach (var cell in Interiors(mod))
                Add(puts, cell, null, null);
            foreach (var world in mod.Worldspaces)
            {
                if (world.TopCell is { } top)
                    foreach (var reference in top.Persistent.Concat(top.Temporary))
                    {
                        if (Placement(reference) is not { } at) continue;
                        var grid = CellContents.GridAt(at.Position);
                        var cell = gridCells.GetValueOrDefault((world.FormKey, grid));
                        Put(puts, reference, at.Position, cell?.FormKey ?? top.FormKey, cell?.EditorId, cell?.Name, world.EditorID, grid);
                    }
                foreach (var block in world.SubCells)
                    foreach (var sub in block.Items)
                        foreach (var cell in sub.Items)
                            Add(puts, cell, world.EditorID, cell.Grid?.Point);
            }
            putsOf[i] = puts;
        });
        foreach (var puts in putsOf)
        {
            foreach (var placed in puts)
            {
                // The last plugin's version of a reference wins, as in game.
                spots[placed.Spot.Reference] = (placed.Base, placed.Spot);
                if (placed.Door) doors.Add(placed.Spot.Reference);
            }
        }
        foreach (var (_, (baseKey, spot)) in spots)
            (index.byBase.TryGetValue(baseKey, out var list) ? list : index.byBase[baseKey] = []).Add(spot);
        foreach (var door in doors) if (spots.TryGetValue(door, out var at)) index.loadDoors[door] = at.Spot;
        Console.Error.WriteLine($"[helper] placed index: {spots.Count} references of {index.byBase.Count} records in {clock.ElapsedMilliseconds} ms");
        return index;

        static void Add(List<Placed> puts, ICellGetter cell, string? world, P2Int? grid)
        {
            foreach (var reference in cell.Persistent.Concat(cell.Temporary))
                if (Placement(reference) is { } at)
                    Put(puts, reference, at.Position, cell.FormKey, cell.EditorID, cell.Name?.String, world, grid);
        }

        static void Put(List<Placed> puts, IPlacedGetter reference, P3Float position, FormKey cell, string? editorId, string? name, string? world, P2Int? grid)
        {
            var baseKey = reference switch
            {
                IPlacedObjectGetter o => o.Base.FormKeyNullable,
                IPlacedNpcGetter n => n.Base.FormKeyNullable,
                _ => null
            };
            if (baseKey is not { } key) return;
            puts.Add(new Placed(key, new Spot(reference.FormKey, cell, editorId, name, world, grid, position),
                reference is IPlacedObjectGetter { TeleportDestination: not null }));
        }
    }

    /// <summary>An exterior cell as a reference of the worldspace's top cell needs it: kept as values, read by any thread.</summary>
    private sealed record GridCell(FormKey FormKey, string? EditorId, string? Name);

    /// <summary>One reference a plugin places, before the plugins are merged in load order.</summary>
    private readonly record struct Placed(FormKey Base, Spot Spot, bool Door);

    private static IEnumerable<ICellGetter> Interiors(ISkyrimModGetter mod) =>
        mod.Cells.Records.SelectMany(block => block.SubBlocks).SelectMany(sub => sub.Cells);

    private static IPlacementGetter? Placement(IPlacedGetter reference) => reference switch
    {
        IPlacedObjectGetter o => o.Placement,
        IPlacedNpcGetter n => n.Placement,
        _ => null
    };

    /// <summary>
    /// Where a record is placed, for readRecord: the count, and up to <see cref="Listed"/> places by their cell's editor ID —
    /// "Riverwood01 (outdoors, Tamriel 4, -11): 20455, -42893, -111" — or null where the load order places it nowhere.
    /// </summary>
    public JsonObject? Of(FormKey record, string? place = null)
    {
        if (!byBase.TryGetValue(record, out var all) || all.Count == 0) return null;
        var wanted = place?.Trim();
        bool There(Spot s) => (s.CellEditorId?.Contains(wanted!, StringComparison.OrdinalIgnoreCase) ?? false)
            || (s.CellName?.Contains(wanted!, StringComparison.OrdinalIgnoreCase) ?? false);
        var spots = string.IsNullOrEmpty(wanted) ? all : all.Where(There).ToList();
        if (spots.Count == 0)
        {
            // The place found, and the record not there: told "try a shorter part of the name" for a chair the Bannered Mare has
            // none of, a run asked three times more by shorter names (eval round 139's package #3).
            var cells = byBase.Values.SelectMany(v => v).Where(There).Select(s => s.CellEditorId ?? Session.FormKeyText(s.Cell))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            return new JsonObject
            {
                ["count"] = all.Count,
                ["in"] = new JsonArray(),
                ["note"] = cells.Count > 0
                    ? $"Placed in {all.Count} places, none in {string.Join(", ", cells)} — the cell{(cells.Count > 1 ? "s" : "")} whose editor ID or " +
                      $"name holds \"{wanted}\": this record does not stand there. readRecord of the cell lists what does, under `placed`"
                    : $"Placed in {all.Count} places, none in a cell whose editor ID or name holds \"{wanted}\": a town's outdoor cells " +
                      "are named after it (Riverwood, Riverwood01…), a house's or a dungeon's after it — try a shorter part of the name"
            };
        }
        var listed = spots.OrderBy(s => s.CellEditorId ?? "￿", StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Reference.ToString()).Take(Listed);
        return new JsonObject
        {
            ["count"] = spots.Count,
            ["in"] = new JsonArray(listed.Select(s => (JsonNode)(
                CellText(s) +
                $": {Whole(s.Position.X)}, {Whole(s.Position.Y)}, {Whole(s.Position.Z)} — reference {Session.FormKeyText(s.Reference)}")).ToArray()),
            ["note"] = "Where the load order places this record: each reference's cell and position. To place something beside one, " +
                "readRecord that cell — its `newReference` names the folder a new reference goes in — and write the new one a little along " +
                "x or y from the position here, at its z" +
                (spots.Count > Listed
                    ? $"; {spots.Count - Listed} more places are left out, the first {Listed} by their cell's editor ID shown — for the places " +
                      $"of one town or dungeon, readRecord {{\"record\": \"<this record>\", \"in\": \"<its name>\"}}, as \"in\": \"Riverwood\""
                    : "")
        };
    }

    private static string Whole(float value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);
}
