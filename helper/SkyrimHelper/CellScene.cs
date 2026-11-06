using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkyrimHelper;

/// <summary>
/// A cell as a scene, for exportCell to lay out in Blender: every reference placed in it — the winning version of each, the
/// project's own built plugin over them — with its base record, the model the game shows for it, its position, rotation and
/// scale as the plugin holds them, and an exterior cell's ground. The models' files are the server's to find and copy; the
/// rotation the Blender script turns into Blender's (the game applies z, then y, then x, each angle clockwise).
/// </summary>
internal static class CellScene
{
    public static JsonObject Of(IReadOnlyList<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> contexts,
        Func<FormKey, IMajorRecordGetter?> winning, RecordIndex index, ISkyrimModGetter? project,
        Func<FormKey, List<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>>? allContexts = null)
    {
        if (contexts.Count == 0 || contexts[0].Record is not ICellGetter winner)
            throw new HelperException("badRequest", "exportCell takes a cell: an interior's editor ID like WhiterunBreezehome, or an exterior cell's like Riverwood.");

        // Each plugin's version of the cell holds the references that plugin places; the project's built plugin loads last.
        var versions = contexts.Select(c => (Cell: c.Record as ICellGetter, Own: false)).Where(v => v.Cell is not null).ToList();
        var projectCell = project?.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(c => c.FormKey == winner.FormKey);
        if (projectCell is not null) versions.Insert(0, (projectCell, true));

        var seen = new HashSet<FormKey>();
        var references = new JsonArray();
        foreach (var (cell, own) in versions)
        {
            foreach (var (reference, persistent) in cell!.Persistent.Select(r => (r, true)).Concat(cell.Temporary.Select(r => (r, false))))
            {
                if (!seen.Add(reference.FormKey)) continue;
                if (Row(reference, persistent, own, winning, index) is { } row) references.Add(row);
            }
        }
        // Outdoors, the worldspace's persistent references standing in the cell's square too — the project's own top cell's
        // first — as readRecord lists them (CellContents): Alvor's forge and grindstone in Riverwood are such.
        if (CellContents.GridOf(winner) is { } square && allContexts is not null)
        {
            FormKey? worldKey = null;
            for (var parent = contexts[0].Parent; parent is not null; parent = parent.Parent)
                if (parent.Record is IWorldspaceGetter w) { worldKey = w.FormKey; break; }
            if (worldKey is { } wk)
            {
                var tops = new List<(IWorldspaceGetter World, bool Own)>();
                if (project?.Worldspaces.FirstOrDefault(w => w.FormKey == wk) is { } ownWorld) tops.Add((ownWorld, true));
                tops.AddRange(allContexts(wk).Select(c => c.Record).OfType<IWorldspaceGetter>().Select(w => (w, false)));
                foreach (var (world, own) in tops)
                {
                    if (world.TopCell is not { } top) continue;
                    foreach (var reference in top.Persistent)
                    {
                        var at = reference switch { IPlacedObjectGetter o => o.Placement, IPlacedNpcGetter n => n.Placement, _ => null };
                        if (at is null || !CellContents.GridAt(at.Position).Equals(square) || !seen.Add(reference.FormKey)) continue;
                        if (Row(reference, true, own, winning, index) is { } row) references.Add(row);
                    }
                }
            }
        }

        var answer = new JsonObject
        {
            ["cell"] = new JsonObject
            {
                ["formId"] = Session.FormKeyText(winner.FormKey),
                ["editorId"] = winner.EditorID,
                ["name"] = winner.Name?.String,
                ["interior"] = CellContents.GridOf(winner) is null
            },
            ["references"] = references
        };
        if (CellContents.GridOf(winner) is { } grid)
        {
            var cellNode = answer["cell"]!.AsObject();
            cellNode["grid"] = new JsonArray(grid.X, grid.Y);
            for (var parent = contexts[0].Parent; parent is not null; parent = parent.Parent)
                if (parent.Record is IWorldspaceGetter world)
                {
                    cellNode["worldspace"] = new JsonObject { ["formId"] = Session.FormKeyText(world.FormKey), ["editorId"] = world.EditorID };
                    break;
                }
            // The ground, from the version of the cell whose landscape wins.
            var land = versions.Select(v => v.Cell!.Landscape).FirstOrDefault(l => l?.VertexHeightMap is not null);
            if (land?.VertexHeightMap is { } heights) answer["ground"] = Ground(heights, grid);
        }
        return answer;
    }

    private static JsonObject? Row(IPlacedGetter reference, bool persistent, bool own, Func<FormKey, IMajorRecordGetter?> winning, RecordIndex index)
    {
        var (baseKey, placement, scale, kind) = reference switch
        {
            IPlacedObjectGetter o => (o.Base.FormKeyNullable, o.Placement, o.Scale ?? 1f, "PlacedObject"),
            IPlacedNpcGetter n => (n.Base.FormKeyNullable, n.Placement, n.Scale ?? 1f, "PlacedNpc"),
            _ => (null, null, 1f, reference.GetType().Name.Replace("Binary", "").Replace("Overlay", ""))
        };
        if (placement is null) return null;
        var baseRecord = baseKey is { } key ? winning(key) : null;
        var entry = baseKey is { } k && index.ByFormKey.TryGetValue(k, out var e) ? e : null;
        var row = new JsonObject
        {
            ["formId"] = Session.FormKeyText(reference.FormKey),
            ["editorId"] = reference.EditorID,
            ["kind"] = kind,
            ["base"] = baseKey is null ? null : new JsonObject
            {
                ["formId"] = Session.FormKeyText(baseKey.Value),
                ["editorId"] = entry?.EditorId ?? baseRecord?.EditorID,
                ["type"] = entry?.Type,
                ["name"] = entry?.Name
            },
            ["model"] = baseRecord is null ? null : ModelOf(baseRecord),
            ["position"] = Triple(placement.Position),
            ["rotation"] = Triple(placement.Rotation),
            ["scale"] = Math.Round(scale, 4),
            ["persistent"] = persistent,
            ["own"] = own,
            ["disabled"] = (reference.MajorRecordFlagsRaw & 0x800) != 0
        };
        if (baseRecord is ILightGetter light)
            row["light"] = new JsonObject
            {
                ["radius"] = light.Radius,
                ["color"] = new JsonArray(light.Color.R / 255.0, light.Color.G / 255.0, light.Color.B / 255.0)
            };
        return row;
    }

    /// <summary>The model the game shows for a record placed in the world, under Data — `meshes\clutter\barrel01.nif` — or null.</summary>
    internal static string? ModelOf(IMajorRecordGetter record)
    {
        var given = record switch
        {
            IArmorGetter armor => armor.WorldModel?.Male?.Model?.File.GivenPath ?? armor.WorldModel?.Female?.Model?.File.GivenPath,
            IModeledGetter modeled => modeled.Model?.File.GivenPath,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(given)) return null;
        var path = given.Replace('/', '\\').TrimStart('\\');
        return path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? path : $"meshes\\{path}";
    }

    /// <summary>
    /// An exterior cell's ground as 33 × 33 heights, [y, x]: west to east in each row, south to north row by row, 128 units apart —
    /// as the game reads its landscape: each height the one before it in its row plus its step, a row's first the row before's
    /// first plus its step, all times 8.
    /// </summary>
    internal static float[,] Heights(ILandscapeVertexHeightMapGetter map)
    {
        var heights = new float[33, 33];
        var rowStart = map.Offset;
        for (var y = 0; y < 33; y++)
        {
            rowStart += unchecked((sbyte)map.HeightMap[0, y]);
            var height = rowStart;
            heights[y, 0] = height * 8;
            for (var x = 1; x < 33; x++)
            {
                height += unchecked((sbyte)map.HeightMap[x, y]);
                heights[y, x] = height * 8;
            }
        }
        return heights;
    }

    /// <summary>The ground's height at a position of an exterior cell, between its four nearest heights.</summary>
    internal static float GroundAt(float[,] heights, P2Int grid, float x, float y)
    {
        var fx = Math.Clamp((x - grid.X * CellContents.CellSize) / (CellContents.CellSize / 32f), 0, 32);
        var fy = Math.Clamp((y - grid.Y * CellContents.CellSize) / (CellContents.CellSize / 32f), 0, 32);
        var (i, j) = ((int)Math.Min(31, Math.Floor(fx)), (int)Math.Min(31, Math.Floor(fy)));
        var (tx, ty) = (fx - i, fy - j);
        return (heights[j, i] * (1 - tx) + heights[j, i + 1] * tx) * (1 - ty) + (heights[j + 1, i] * (1 - tx) + heights[j + 1, i + 1] * tx) * ty;
    }

    private static JsonObject Ground(ILandscapeVertexHeightMapGetter map, P2Int grid)
    {
        var heights = Heights(map);
        var rows = new JsonArray();
        for (var y = 0; y < 33; y++)
        {
            var row = new JsonArray();
            for (var x = 0; x < 33; x++) row.Add(Math.Round(heights[y, x], 1));
            rows.Add(row);
        }
        return new JsonObject
        {
            ["origin"] = new JsonArray(grid.X * CellContents.CellSize, grid.Y * CellContents.CellSize),
            ["spacing"] = CellContents.CellSize / 32,
            ["heights"] = rows
        };
    }

    private static JsonArray Triple(P3Float p) => new(Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4));

    /// <summary>
    /// Files out of the game's archives, for exportCell: each `{archive, from, to}` — `from` under Data as the archive holds it,
    /// `to` a full path. Answers how many were written, and each one the archive does not hold.
    /// </summary>
    public static JsonNode Extract(JsonObject p, GameRelease release)
    {
        var files = p["files"]?.AsArray() ?? throw new HelperException("badRequest", "'files' is required: [{archive, from, to}].");
        var written = 0;
        var missing = new JsonArray();
        foreach (var group in files.OfType<JsonObject>().GroupBy(f => f["archive"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
        {
            var reader = Archive.CreateReader(release, group.Key);
            var wanted = group.GroupBy(f => Key(f["from"]!.GetValue<string>())).ToDictionary(g => g.Key, g => g.Select(f => f["to"]!.GetValue<string>()).ToList());
            foreach (var file in reader.Files)
            {
                if (!wanted.Remove(Key(file.Path), out var targets)) continue;
                var bytes = file.GetBytes();
                foreach (var target in targets)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                    written++;
                }
                if (wanted.Count == 0) break;
            }
            foreach (var left in wanted.Keys) missing.Add(new JsonObject { ["archive"] = group.Key, ["from"] = left });
        }
        return new JsonObject { ["written"] = written, ["missing"] = missing };
    }

    private static string Key(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
}
