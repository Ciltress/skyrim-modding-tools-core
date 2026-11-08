using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

public sealed partial class Session
{
    /// <summary>The game's one Navigation Info Map: every plugin's version of it holds its own navmeshes' entries, and the game merges them.</summary>
    private static readonly FormKey NavigationInfoMap = FormKey.Factory("012FB4:Skyrim.esm");

    // Constants the game's navmeshes carry in every door link and map entry (read off Skyrim.esm and the Creation Club's).
    private const int DoorLinkConstant = -460622861;
    private const int MapEntryConstant = -1511415748;

    /// <summary>
    /// An interior cell's navmesh made anew (Navmesh.cs) from the collision of what stands in it — the load order's references
    /// with the project's built plugin over them — and written into the project: over the cell's own navmesh where it has one,
    /// as a new navmesh where it has none. The meshes come from the server, `models`: each model path a loose `file` or an
    /// `archive` and the path `under` it there. A map entry (NAVI) is written where the navmesh is new or its doors changed.
    /// </summary>
    private async Task<JsonNode> GenerateNavmesh(JsonObject p)
    {
        var pluginName = ModKey.FromFileName(Required(p, "plugin"));
        var projectDir = Required(p, "projectPluginDir");
        var built = p["projectPlugin"]?.GetValue<string>();
        using var project = built is not null && File.Exists(built) ? SkyrimMod.Create(release).FromPath(built).Construct() : null;
        var asked = Required(p, "cell").Trim();

        // The cell: one the project made, or the load order's; its versions, the project's first.
        var ownCells = project?.EnumerateMajorRecords<ICellGetter>().ToList() ?? [];
        var own = ownCells.FirstOrDefault(c => string.Equals(c.EditorID, asked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(FormKeyText(c.FormKey), asked, StringComparison.OrdinalIgnoreCase));
        var cellKey = own?.FormKey ?? Resolve(asked);
        own ??= ownCells.FirstOrDefault(c => c.FormKey == cellKey);
        var contexts = Contexts(cellKey).Where(c => c.Record is ICellGetter).ToList();
        var versions = (own is null ? [] : new List<ICellGetter> { own }).Concat(contexts.Select(c => (ICellGetter)c.Record)).ToList();
        if (versions.Count == 0) throw new HelperException("notFound", $"{asked} is no cell: findRecords with types [\"CELL\"] finds one.");
        var cell = versions[0];
        var cellName = cell.EditorID ?? FormKeyText(cellKey);
        if (CellContents.GridOf(cell) is not null)
            throw new HelperException("badRequest", $"{cellName} is an exterior cell: generateNavmesh makes an interior cell's navmesh. An exterior cell's — its " +
                "ground and the edges it shares with its neighbours' — is not made yet: it stays the game's.");

        // Its references: each one's first version, the project's before the load order's.
        var projectRecords = project?.EnumerateMajorRecords().ToDictionary(r => r.FormKey) ?? [];
        IMajorRecordGetter? Winning(FormKey key) => projectRecords.TryGetValue(key, out var mine) ? mine : Contexts(key).FirstOrDefault()?.Record;
        var references = versions.SelectMany(v => v.Persistent.Concat(v.Temporary)).OfType<IPlacedObjectGetter>().DistinctBy(r => r.FormKey).ToList();

        var models = ReadModels(p["models"] as JsonObject);
        var collisionOf = new Dictionary<string, Navmesh.Geometry?>(StringComparer.OrdinalIgnoreCase);
        var collision = new Navmesh.Geometry();
        var doors = new List<IPlacedObjectGetter>();
        var (solid, open) = (0, 0);
        var unread = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            // Initially disabled: not there when the game starts.
            if ((reference.MajorRecordFlagsRaw & 0x800) != 0 || reference.Placement is not { } at) continue;
            if (reference.TeleportDestination is not null) doors.Add(reference);
            var baseRecord = Winning(reference.Base.FormKey);
            // A door opens: actors walk through its frame.
            if (baseRecord is null or IDoorGetter || CellScene.ModelOf(baseRecord) is not { } model) continue;
            if (!collisionOf.TryGetValue(model, out var shape))
                collisionOf[model] = shape = models.TryGetValue(model, out var bytes) ? Navmesh.Collision(bytes) : null;
            if (shape is null) { unread.Add(model); continue; }
            if (shape.TriangleCount == 0) { open++; continue; }
            collision.Add(shape, Navmesh.Placement(at.Position, at.Rotation, reference.Scale ?? 1f));
            solid++;
        }
        if (collision.TriangleCount == 0)
            throw new HelperException("notFound", $"Nothing in {cellName} has collision to walk on" +
                (unread.Count > 0 ? $" — {unread.Count} mesh(es) could not be read, {string.Join(", ", unread.Take(5))}." : ": place its floor first."));

        var settings = new Navmesh.Settings();
        var made = Navmesh.Generate(collision, settings);
        if (made.TriangleCount == 0) throw new HelperException("notFound", $"No floor in {cellName} is wide and high enough for an actor to walk on.");

        // The pieces kept: those a load door stands at, and every one at least a sixth the size of the largest. The rest are
        // table tops, shelves and gaps behind furniture no actor reaches.
        var pieces = Navmesh.Pieces(made);
        var atDoor = doors.Select(d => Navmesh.Nearest(made, Position(d))).ToHashSet();
        var keep = pieces.Where(piece => piece.Count * 6 >= pieces[0].Count || piece.Any(atDoor.Contains)).ToList();
        var mesh = Navmesh.Only(made, keep.SelectMany(t => t));
        if (mesh.Vertices.Count > short.MaxValue || mesh.TriangleCount > short.MaxValue)
            throw new HelperException("badRequest", $"{cellName}'s navmesh would have {mesh.TriangleCount} triangles, more than one navmesh holds.");
        var doorTriangles = doors.Select(d => (Door: d, Triangle: Navmesh.Nearest(mesh, Position(d)))).ToList();

        // The navmesh it replaces: the cell's largest, as the load order has it.
        var existing = versions.SelectMany(v => v.NavigationMeshes).Select(n => n.FormKey).Distinct()
            .Select(key => Cache.ResolveAllContexts(key, typeof(INavigationMeshGetter)).FirstOrDefault())
            .Where(c => c?.Record is INavigationMeshGetter { Data: not null })
            .OrderByDescending(c => ((INavigationMeshGetter)c!.Record).Data!.Triangles.Count).ToList();
        var replaced = existing.FirstOrDefault();

        var temp = new SkyrimMod(pluginName, release);
        NavigationMesh navmesh;
        if (replaced is not null) navmesh = (NavigationMesh)replaced.GetOrAddAsOverride(temp);
        else
        {
            var shell = new Cell(cellKey, release) { EditorID = cell.EditorID, Flags = Cell.Flag.IsInteriorCell };
            temp.Cells.Records.Add(new CellBlock
            {
                BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock,
                SubBlocks = { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = { shell } } }
            });
            navmesh = new NavigationMesh(new FormKey(pluginName, NextFormId(projectDir, pluginName)), release)
            {
                Data = new NavigationMeshData { Parent = new CellNavmeshParent { Parent = new FormLink<ICellGetter>(cellKey) } }
            };
            shell.NavigationMeshes.Add(navmesh);
        }
        Fill(navmesh.Data!, mesh, doorTriangles);

        // The map entry, where the game has none for it or its doors changed.
        var entry = Cache.ResolveAllContexts(NavigationInfoMap, typeof(INavigationMeshInfoMapGetter)).Select(c => c.Record).OfType<INavigationMeshInfoMapGetter>()
            .SelectMany(m => m.MapInfos).FirstOrDefault(i => i.NavigationMesh.FormKey == navmesh.FormKey);
        var doorKeys = doors.Select(d => d.FormKey).ToHashSet();
        var mapped = entry is null || !entry.LinkedDoors.Select(d => d.Door.FormKey).ToHashSet().SetEquals(doorKeys);
        if (mapped)
        {
            var winner = Cache.ResolveAllContexts(NavigationInfoMap, typeof(INavigationMeshInfoMapGetter)).First();
            var map = (NavigationMeshInfoMap)winner.GetOrAddAsOverride(temp);
            // The project's own entries stay, this navmesh's made anew; the other plugins' are theirs: the game merges them.
            var kept = project?.EnumerateMajorRecords<INavigationMeshInfoMapGetter>().SelectMany(m => m.MapInfos)
                .Where(i => i.NavigationMesh.FormKey != navmesh.FormKey).Select(i => i.DeepCopy()).ToList() ?? [];
            map.MapInfos.Clear();
            map.MapInfos.AddRange(kept);
            map.MapInfos.Add(new NavigationMapInfo
            {
                NavigationMesh = navmesh.ToLink(),
                Point = Vector(mesh.Vertices.Aggregate(Vector3.Zero, (a, b) => a + b) / mesh.Vertices.Count),
                LinkedDoors = new(doors.Select(d => new LinkedDoor { Unknown = DoorLinkConstant, Door = d.ToLink() })),
                Unknown2 = MapEntryConstant,
                Parent = new NavigationMapInfoCellParent { ParentCell = new FormLink<ICellGetter>(cellKey) }
            });
        }

        var rendered = await Render(temp, pluginName);
        var navName = $"{navmesh.FormKey.ID:X6}_{navmesh.FormKey.ModKey.FileName}.yaml";
        var navText = rendered.FirstOrDefault(f => f.Path.EndsWith("/NavigationMeshes/" + navName, StringComparison.OrdinalIgnoreCase));
        if (navText.Path is null) throw new HelperException("badRecord", $"The navmesh of {cellName} could not be written as project text.");
        // Into the project's copy of the cell; a game cell's copy made first, where the project has none.
        var cellFolder = CellFolderIn(projectDir, cellKey);
        var written = new JsonArray();
        if (cellFolder is null)
        {
            if (replaced is null) throw new HelperException("notFound", $"The project has no folder for {cellName}: build it once, then generateNavmesh again.");
            cellFolder = navText.Path[..navText.Path.LastIndexOf("/NavigationMeshes/", StringComparison.OrdinalIgnoreCase)];
            foreach (var (path, text) in rendered.Where(f => f.Path.StartsWith("Cells/", StringComparison.Ordinal) && !f.Path.Contains("/NavigationMeshes/")))
            {
                var target = Path.Combine(projectDir, path);
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, Annotated(text));
            }
        }
        var navFile = $"{cellFolder}/NavigationMeshes/{navName}";
        Directory.CreateDirectory(Path.Combine(projectDir, cellFolder, "NavigationMeshes"));
        await File.WriteAllTextAsync(Path.Combine(projectDir, navFile), Annotated(navText.Text));
        written.Add(navFile);
        if (mapped && rendered.FirstOrDefault(f => f.Path.StartsWith("NavigationMeshInfoMaps/", StringComparison.Ordinal)) is { Path: not null } mapText)
        {
            Directory.CreateDirectory(Path.Combine(projectDir, Path.GetDirectoryName(mapText.Path)!));
            await File.WriteAllTextAsync(Path.Combine(projectDir, mapText.Path), Annotated(mapText.Text));
            written.Add(mapText.Path);
        }

        var answer = new JsonObject
        {
            ["cell"] = cellName,
            ["navmesh"] = FormKeyText(navmesh.FormKey),
            ["replaced"] = replaced is not null,
            ["triangles"] = mesh.TriangleCount,
            ["vertices"] = mesh.Vertices.Count,
            ["files"] = written,
            ["doors"] = new JsonArray(doorTriangles.Select(d => (JsonNode)new JsonObject { ["door"] = FormKeyText(d.Door.FormKey), ["triangle"] = d.Triangle }).ToArray()),
            ["objects"] = new JsonObject { ["withCollision"] = solid, ["withoutCollision"] = open, ["unreadMeshes"] = new JsonArray(unread.Take(10).Select(m => (JsonNode)m).ToArray()) },
            ["left"] = new JsonObject { ["pieces"] = pieces.Count - keep.Count, ["triangles"] = made.TriangleCount - mesh.TriangleCount }
        };
        if (replaced?.Record is INavigationMeshGetter { Data: { } old })
        {
            var oldMesh = new Navmesh.Geometry(old.Vertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToList(),
                old.Triangles.SelectMany(t => new int[] { t.Vertices.X, t.Vertices.Y, t.Vertices.Z }).ToList());
            var (newOnOld, area) = Navmesh.On(mesh, oldMesh);
            var (oldOnNew, oldArea) = Navmesh.On(oldMesh, mesh);
            answer["before"] = new JsonObject
            {
                ["triangles"] = oldMesh.TriangleCount,
                // Square units to square metres: a unit is 1.4286 cm.
                ["areaM2"] = Math.Round(oldArea * 0.000204, 1), ["nowAreaM2"] = Math.Round(area * 0.000204, 1),
                ["oldCovered"] = Math.Round(oldOnNew, 2), ["newOnOld"] = Math.Round(newOnOld, 2)
            };
        }
        var others = existing.Skip(1).Select(c => (JsonNode)FormKeyText(c!.Record.FormKey)).ToArray();
        if (others.Length > 0) answer["otherNavmeshes"] = new JsonArray(others);
        return answer;
    }

    private static Vector3 Position(IPlacedObjectGetter reference) =>
        reference.Placement is { } at ? new Vector3(at.Position.X, at.Position.Y, at.Position.Z) : Vector3.Zero;

    private static Noggog.P3Float Vector(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The navmesh's data as the game reads it: vertices, triangles with their neighbours, door triangles, the grid.</summary>
    private static void Fill(NavigationMeshData data, Navmesh.Geometry mesh, List<(IPlacedObjectGetter Door, int Triangle)> doors)
    {
        var across = Navmesh.Neighbours(mesh);
        var doorAt = doors.Select(d => d.Triangle).ToHashSet();
        data.Vertices.Clear();
        data.Vertices.AddRange(mesh.Vertices.Select(Vector));
        data.Triangles.Clear();
        for (var t = 0; t < mesh.TriangleCount; t++)
            data.Triangles.Add(new NavmeshTriangle
            {
                Vertices = new Noggog.P3Int16((short)mesh.Triangles[t * 3], (short)mesh.Triangles[t * 3 + 1], (short)mesh.Triangles[t * 3 + 2]),
                EdgeLink_0_1 = across[t * 3], EdgeLink_1_2 = across[t * 3 + 1], EdgeLink_2_0 = across[t * 3 + 2],
                Flags = NavmeshTriangle.Flag.Found | (doorAt.Contains(t) ? NavmeshTriangle.Flag.Door : 0)
            });
        data.EdgeLinks.Clear();
        data.DoorTriangles.Clear();
        foreach (var (door, triangle) in doors)
            data.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = (short)triangle, Unknown = DoorLinkConstant, Door = door.ToLink() });
        var grid = Navmesh.Grid(mesh);
        data.NavmeshGridDivisor = grid.Divisor;
        data.MaxDistanceX = grid.StepX;
        data.MaxDistanceY = grid.StepY;
        data.Min = Vector(grid.Min);
        data.Max = Vector(grid.Max);
        data.NavmeshGrid = grid.Cells;
    }

    /// <summary>The meshes the server found, by model path: a loose file read, an archive's file read out of it.</summary>
    private Dictionary<string, byte[]> ReadModels(JsonObject? models)
    {
        var read = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (models is null) return read;
        var fromArchives = new List<(string Model, string Archive, string Under)>();
        foreach (var (model, source) in models)
        {
            if (source?["file"]?.GetValue<string>() is { } file && File.Exists(file)) read[model] = File.ReadAllBytes(file);
            else if (source?["archive"]?.GetValue<string>() is { } archive && source["under"]?.GetValue<string>() is { } under) fromArchives.Add((model, archive, under));
        }
        foreach (var group in fromArchives.GroupBy(f => f.Archive, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(group.Key)) continue;
            var wanted = group.GroupBy(f => ArchiveKey(f.Under)).ToDictionary(g => g.Key, g => g.Select(f => f.Model).ToList());
            foreach (var entry in Archive.CreateReader(release.ToGameRelease(), group.Key).Files)
            {
                if (!wanted.Remove(ArchiveKey(entry.Path), out var asked)) continue;
                var bytes = entry.GetBytes();
                foreach (var model in asked) read[model] = bytes;
                if (wanted.Count == 0) break;
            }
        }
        return read;
    }

    private static string ArchiveKey(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    /// <summary>The folder of a cell in a project, relative and with forward slashes: Spriggit names it "{EditorID} - {FormID}_{plugin}".</summary>
    private static string? CellFolderIn(string projectDir, FormKey cell)
    {
        var suffix = $"{cell.ID:X6}_{cell.ModKey.FileName}";
        var cells = Path.Combine(projectDir, "Cells");
        if (!Directory.Exists(cells)) return null;
        return Directory.EnumerateDirectories(cells, "*", SearchOption.AllDirectories)
            .FirstOrDefault(d => Path.GetFileName(d).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(d, "RecordData.yaml")))
            is { } hit ? Path.GetRelativePath(projectDir, hit).Replace('\\', '/') : null;
    }

    /// <summary>The FormID after the highest the project's files use, as the build numbers new records (000800 on).</summary>
    private static uint NextFormId(string projectDir, ModKey plugin)
    {
        var own = new Regex($@"^FormKey:[ \t]*([0-9A-Fa-f]{{6}}):{Regex.Escape(plugin.FileName)}[ \t]*(?:#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var highest = 0x7FFu;
        foreach (var file in Directory.EnumerateFiles(projectDir, "*.yaml", SearchOption.AllDirectories))
            foreach (Match m in own.Matches(File.ReadAllText(file))) highest = Math.Max(highest, Convert.ToUInt32(m.Groups[1].Value, 16));
        return highest + 1;
    }
}
