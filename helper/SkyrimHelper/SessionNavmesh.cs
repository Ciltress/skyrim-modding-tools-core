using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkyrimHelper;

public sealed partial class Session
{
    /// <summary>The game's one Navigation Info Map: every plugin's version of it holds its own navmeshes' entries, and the game merges them.</summary>
    private static readonly FormKey NavigationInfoMap = FormKey.Factory("012FB4:Skyrim.esm");

    // Constants the game's navmeshes carry in every door link and map entry (read off Skyrim.esm and the Creation Club's).
    private const int DoorLinkConstant = -460622861;
    private const int MapEntryConstant = -1511415748;

    /// <summary>How far past an exterior cell's edges the collision is read: an actor's reach, and the objects standing over the edge.</summary>
    private const float Margin = 256;

    private static readonly NavmeshTriangle.Flag[] EdgeFlags = [NavmeshTriangle.Flag.EdgeLink_0_1, NavmeshTriangle.Flag.EdgeLink_1_2, NavmeshTriangle.Flag.EdgeLink_2_0];

    /// <summary>What a cell's navmesh is made from, gathered once for the meshes the server finds and again to make it.</summary>
    private sealed record NavmeshScene(ICellGetter Cell, FormKey CellKey, string CellName, List<ICellGetter> Versions, P2Int? Grid,
        FormKey? Worldspace, List<IPlacedObjectGetter> References, List<IPlacedObjectGetter> Doors, List<(P2Int Grid, float[,] Heights)> Ground,
        Func<FormKey, IMajorRecordGetter?> Winning, ISkyrimModGetter? Project, float? WaterHeight);

    /// <summary>The meshes generateNavmesh reads for a cell: the server finds each, loose or in an archive.</summary>
    private JsonNode NavmeshModels(JsonObject p)
    {
        var built = p["projectPlugin"]?.GetValue<string>();
        using var project = built is not null && File.Exists(built) ? SkyrimMod.Create(release).FromPath(built).Construct() : null;
        var scene = SceneForNavmesh(Required(p, "cell").Trim(), project);
        var models = scene.References.Select(r => scene.Winning(r.Base.FormKey)).Where(b => b is not null and not IDoorGetter)
            .Select(b => CellScene.ModelOf(b!)).Where(m => m is not null).Distinct(StringComparer.OrdinalIgnoreCase);
        return new JsonObject { ["cell"] = scene.CellName, ["models"] = new JsonArray(models.Select(m => (JsonNode)m!).ToArray()) };
    }

    /// <summary>
    /// A cell's navmesh made anew (Navmesh.cs) from the collision of what stands in it — the load order's references with the
    /// project's built plugin over them, outdoors the ground too, and the neighbouring cells' as far as an actor reaches — and
    /// written into the project. The new surface is shared out among the cell's navmeshes by where each triangle lies, so
    /// every one keeps its FormID and nothing that points at it breaks; a cell without one gets a new one. Each keeps its old
    /// triangles' road and water flags where it lies on them. The links between them, and outdoors with the neighbouring
    /// cells' navmeshes both ways, are made by the edges they share; each navmesh changed gets its map entry (NAVI) anew.
    /// The meshes come from the server, `models`: each model path a loose `file` or an `archive` and the path `under` it.
    /// With `surface` — the navmeshes as edited in Blender, brought back by importCell — that surface is the cell's navmesh
    /// instead of one made from the collision (GivenSurface): each triangle stays with the navmesh of its Blender object, and
    /// the rest is as above — flags, links, neighbours, doors and map entries.
    /// </summary>
    private async Task<JsonNode> GenerateNavmesh(JsonObject p)
    {
        var pluginName = ModKey.FromFileName(Required(p, "plugin"));
        var projectDir = Required(p, "projectPluginDir");
        var built = p["projectPlugin"]?.GetValue<string>();
        using var project = built is not null && File.Exists(built) ? SkyrimMod.Create(release).FromPath(built).Construct() : null;
        var scene = SceneForNavmesh(Required(p, "cell").Trim(), project);
        var square = scene.Grid is { } g
            ? (g.X * (float)CellContents.CellSize, g.Y * (float)CellContents.CellSize, (g.X + 1) * (float)CellContents.CellSize, (g.Y + 1) * (float)CellContents.CellSize)
            : ((float, float, float, float)?)null;
        var given = p["surface"] is JsonArray edited ? GivenSurface(edited, square) : null;

        // The collision: each reference's mesh where it stands, outdoors on the ground.
        var models = ReadModels(p["models"] as JsonObject);
        var collisionOf = new Dictionary<string, Navmesh.Geometry?>(StringComparer.OrdinalIgnoreCase);
        var collision = new Navmesh.Geometry();
        var (solid, open) = (0, 0);
        var unread = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (given is null)
        {
            foreach (var (at, heights) in scene.Ground) collision.Add(Navmesh.Ground(heights, at), Matrix4x4.Identity);
            foreach (var reference in scene.References)
            {
                var baseRecord = scene.Winning(reference.Base.FormKey);
                // A door opens: actors walk through its frame.
                if (baseRecord is null or IDoorGetter || CellScene.ModelOf(baseRecord) is not { } model || reference.Placement is not { } at) continue;
                if (!collisionOf.TryGetValue(model, out var shape))
                    collisionOf[model] = shape = models.TryGetValue(model, out var bytes) ? Navmesh.Collision(bytes) : null;
                if (shape is null) { unread.Add(model); continue; }
                if (shape.TriangleCount == 0) { open++; continue; }
                collision.Add(shape, Navmesh.Placement(at.Position, at.Rotation, reference.Scale ?? 1f));
                solid++;
            }
            if (collision.TriangleCount == 0)
                throw new HelperException("notFound", $"Nothing in {scene.CellName} has collision to walk on" +
                    (unread.Count > 0 ? $" — {unread.Count} mesh(es) could not be read, {string.Join(", ", unread.Take(5))}." : ": place its floor first."));
        }
        else if (given.Mesh.TriangleCount == 0)
        {
            throw new HelperException("badInput", $"The navmesh brought back from Blender has no triangle to walk on in {scene.CellName}" +
                (given.Outside > 0 ? $": all {given.Outside} lie outside the cell." : ": every face was deleted, or stands upright.") +
                " Undo that in Blender, export_layout again, then importCell.");
        }

        var made = given?.Mesh ?? Navmesh.Generate(collision, new Navmesh.Settings(), square);
        // Water: the planes the cell places (each water activator's bounds, at their top) and the cell's own water level.
        // Deeper than an actor wades, it swims: no navmesh there; shallower, its triangles are flagged Water.
        var water = new Navmesh.Geometry();
        var waterPlanes = 0;
        foreach (var reference in scene.References)
            if (scene.Winning(reference.Base.FormKey) is IActivatorGetter { WaterType.IsNull: false, ObjectBounds: { } bounds } && reference.Placement is { } at && ++waterPlanes > 0)
                water.Add(Navmesh.Plane(bounds.First.X, bounds.First.Y, bounds.Second.X, bounds.Second.Y, bounds.Second.Z), Navmesh.Placement(at.Position, at.Rotation, reference.Scale ?? 1f));
        if (scene.WaterHeight is { } level)
        {
            var reach = given is null ? collision : made;
            var (lo, hi) = (reach.Vertices.Aggregate(Vector3.Min), reach.Vertices.Aggregate(Vector3.Max));
            water.Add(Navmesh.Plane(lo.X - 1000, lo.Y - 1000, hi.X + 1000, hi.Y + 1000, level), Matrix4x4.Identity);
        }
        float Depth(Vector3 point) => Enumerable.Range(0, water.TriangleCount).Select(w => Navmesh.HeightAt(water, w, point.X, point.Y)).Where(z => z is not null)
            .Select(z => z!.Value - point.Z).DefaultIfEmpty(float.MinValue).Max();
        const float wades = 80;
        var droppedInWater = made.TriangleCount;
        // A surface from Blender keeps what the modder drew, deep water too: only flagged.
        if (water.TriangleCount > 0 && given is null) made = Navmesh.Only(made, Enumerable.Range(0, made.TriangleCount).Where(t => Depth(made.Centre(t)) <= wades));
        droppedInWater -= made.TriangleCount;
        if (made.TriangleCount == 0) throw new HelperException("notFound", $"No floor in {scene.CellName} is wide and high enough for an actor to walk on.");

        // The pieces kept: those a load door stands at or that reach a neighbouring cell, and every one at least a sixth the
        // size of the largest. The rest are table tops, shelves and gaps behind furniture no actor reaches. A surface from
        // Blender is kept whole, in its order: its triangles' objects are known by their index.
        var pieces = given is null ? Navmesh.Pieces(made) : [Enumerable.Range(0, made.TriangleCount).ToList()];
        var atDoor = scene.Doors.Select(d => Navmesh.Nearest(made, Position(d))).ToHashSet();
        // Outdoors, the neighbouring cells' navmeshes by side: a piece at the cell's edge counts where it meets one — a roof
        // over the edge meets none — and the links are made with them.
        var sides = new List<(int Line, List<NavmeshPart> Theirs)>();
        if (square is { } sqr && scene.Grid is { } at0 && scene.Worldspace is { } world0)
        {
            var worlds = WorldsOf(world0);
            foreach (var (dx, dy, line) in new[] { (-1, 0, 0), (1, 0, 1), (0, -1, 2), (0, 1, 3) })
                if (CellContents.CellAt(worlds, new P2Int(at0.X + dx, at0.Y + dy)) is { } other)
                    sides.Add((line, CellVersions(other.FormKey, project).SelectMany(v => v.NavigationMeshes).Select(n => n.FormKey).Distinct()
                        .Select(key => CurrentNavmesh(key, project)).Where(n => n is not null).Select(n => FromData(n!, false)).ToList()));
        }
        var probe = new NavmeshPart(FormKey.Null, made, new NavmeshTriangle.Flag[made.TriangleCount], null, true);
        bool MeetsNeighbour(int t) => square is { } sq && sides.Any(side => Enumerable.Range(0, 3)
            .Any(e => EdgeOn(made, t, e, sq, side.Line) && Overlapping(probe, t, e, side.Theirs, sq, side.Line) is not null));
        var keep = given is not null ? pieces
            : pieces.Where(piece => piece.Count * 6 >= pieces[0].Count || piece.Any(atDoor.Contains) || piece.Any(MeetsNeighbour)).ToList();
        var mesh = Navmesh.Only(made, keep.SelectMany(t => t));

        // The cell's navmeshes as they stand — the project's version where it has one — each to get its share.
        var olds = scene.Versions.SelectMany(v => v.NavigationMeshes).Select(n => n.FormKey).Distinct()
            .Select(key => CurrentNavmesh(key, project)).Where(n => n is not null).Select(n => n!).ToList();
        var oldMeshes = olds.Select(GeometryOf).ToList();
        // From Blender, each object's triangles stay with its navmesh; one whose FormID is not the cell's goes by where it lies.
        var ownerOfObject = given?.FormIds.Select(id => olds.FindIndex(o => string.Equals(FormKeyText(o.FormKey), id, StringComparison.OrdinalIgnoreCase))).ToArray();
        var owner = new int[mesh.TriangleCount];
        var flags = new NavmeshTriangle.Flag[mesh.TriangleCount];
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            flags[t] = NavmeshTriangle.Flag.Found;
            var centre = mesh.Centre(t);
            if (water.TriangleCount > 0 && Depth(centre) > 0) flags[t] |= NavmeshTriangle.Flag.Water;
            if (olds.Count == 0) continue;
            var (best, bestTriangle, bestDistance) = (0, -1, float.MaxValue);
            for (var o = 0; o < olds.Count; o++)
            {
                var old = oldMeshes[o];
                for (var ot = 0; ot < old.TriangleCount; ot++)
                {
                    var distance = Navmesh.HeightAt(old, ot, centre.X, centre.Y) is { } z && MathF.Abs(z - centre.Z) < 64 ? 0 : Vector3.DistanceSquared(old.Centre(ot), centre);
                    if (distance < bestDistance) (best, bestTriangle, bestDistance) = (o, ot, distance);
                    if (distance == 0) break;
                }
                if (bestDistance == 0) break;
            }
            owner[t] = ownerOfObject is not null && ownerOfObject[given!.ObjectOf[t]] is >= 0 and var mine ? mine : best;
            // The road and the water the old triangle was on, kept.
            if (bestDistance == 0 && bestTriangle >= 0)
                flags[t] |= olds[best].Data!.Triangles[bestTriangle].Flags & (NavmeshTriangle.Flag.Preferred | NavmeshTriangle.Flag.Water | NavmeshTriangle.Flag.NoLargeCreatures);
        }
        var shares = olds.Count == 0 ? 1 : olds.Count;
        var keys = olds.Count > 0 ? olds.Select(o => o.FormKey).ToList() : [new FormKey(pluginName, NextFormId(projectDir, pluginName))];
        var parts = new List<NavmeshPart>();
        var localOf = new int[mesh.TriangleCount];
        for (var o = 0; o < shares; o++)
        {
            var members = Enumerable.Range(0, mesh.TriangleCount).Where(t => owner[t] == o).ToList();
            for (var i = 0; i < members.Count; i++) localOf[members[i]] = i;
            parts.Add(new NavmeshPart(keys[o], Navmesh.Only(mesh, members), members.Select(t => flags[t]).ToArray(), olds.Count > 0 ? olds[o] : null, true));
        }
        // A navmesh no new triangle lies on keeps what it had, unlinked from the rest: none of the cell's navmeshes is deleted.
        var regenerated = parts.Where(x => x.Mesh.TriangleCount > 0).Select(x => x.Key).ToHashSet();
        foreach (var part in parts.Where(x => x.Mesh.TriangleCount == 0 && x.Old is not null).ToList())
        {
            var kept = FromData(part.Old!, true) with { Kept = true };
            for (var i = 0; i < kept.Edges.Length; i++)
                if (kept.Linked[i] && regenerated.Contains(kept.Links[kept.Edges[i]].Mesh)) (kept.Edges[i], kept.Linked[i]) = (-1, false);
            kept.Compact();
            parts[parts.IndexOf(part)] = kept;
        }

        // Neighbours inside the mesh: the same navmesh's triangle, or a link to the cell's other navmesh it is in.
        var across = Navmesh.Neighbours(mesh);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var part = parts[owner[t]];
            if (part.Kept) continue;
            for (var e = 0; e < 3; e++)
            {
                var n = across[t * 3 + e];
                var at = localOf[t] * 3 + e;
                if (n < 0) part.Edges[at] = -1;
                else if (owner[n] == owner[t]) part.Edges[at] = (short)localOf[n];
                else if (!parts[owner[n]].Kept) (part.Edges[at], part.Linked[at]) = (part.Link(parts[owner[n]].Key, localOf[n]), true);
                else part.Edges[at] = -1;
            }
        }

        // Outdoors, the edges along the cell's sides linked with the neighbouring cells' navmeshes, both ways.
        var relinked = new List<NavmeshPart>();
        if (square is { } side)
        {
            foreach (var (line, theirs) in sides)
            {
                var mine = parts.Where(x => !x.Kept).ToList();
                // Ours to theirs.
                foreach (var part in mine)
                    foreach (var (t, e) in SideEdges(part, side, line, unlinkedOnly: true))
                        if (Overlapping(part, t, e, theirs, side, line) is { } hit) (part.Edges[t * 3 + e], part.Linked[t * 3 + e]) = (part.Link(hit.Part.Key, hit.Triangle), true);
                // Theirs to ours: every edge of theirs along the side, linked anew or unlinked.
                foreach (var their in theirs)
                {
                    var before = (string.Join(",", their.Edges), string.Join(",", their.Linked));
                    var oldLinks = their.Links.ToList();
                    foreach (var (t, e) in SideEdges(their, side, line, unlinkedOnly: false))
                    {
                        if (Overlapping(their, t, e, mine, side, line) is { } hit) (their.Edges[t * 3 + e], their.Linked[t * 3 + e]) = (their.Link(hit.Part.Key, hit.Triangle), true);
                        else (their.Edges[t * 3 + e], their.Linked[t * 3 + e]) = (-1, false);
                    }
                    their.Compact();
                    if ((string.Join(",", their.Edges), string.Join(",", their.Linked)) != before || !oldLinks.SequenceEqual(their.Links)) relinked.Add(their);
                }
            }
        }

        // Load doors: the triangle nearest each door.
        foreach (var door in scene.Doors)
        {
            var t = Navmesh.Nearest(mesh, Position(door));
            if (!parts[owner[t]].Kept) parts[owner[t]].Doors.Add((localOf[t], door));
        }

        // Written: each navmesh of the cell, each neighbour's whose links changed, and their map entries.
        var temp = new SkyrimMod(pluginName, release);
        var written = parts.Concat(relinked).ToList();
        foreach (var part in written)
        {
            NavigationMesh navmesh;
            if (Cache.ResolveAllContexts(part.Key, typeof(INavigationMeshGetter)).FirstOrDefault() is { } context) navmesh = (NavigationMesh)context.GetOrAddAsOverride(temp);
            else navmesh = NewNavmesh(temp, part.Key, scene, part.Cell);
            Fill(navmesh.Data ??= new NavigationMeshData(), part);
        }
        var winner = Cache.ResolveAllContexts(NavigationInfoMap, typeof(INavigationMeshInfoMapGetter)).First();
        var map = (NavigationMeshInfoMap)winner.GetOrAddAsOverride(temp);
        var entries = Cache.ResolveAllContexts(NavigationInfoMap, typeof(INavigationMeshInfoMapGetter)).Select(c => c.Record).OfType<INavigationMeshInfoMapGetter>()
            .SelectMany(m => m.MapInfos).ToList();
        var ownEntries = project?.EnumerateMajorRecords<INavigationMeshInfoMapGetter>().SelectMany(m => m.MapInfos).ToList() ?? [];
        var writtenKeys = written.Select(x => x.Key).ToHashSet();
        // The project's own entries stay, those written now made anew; the other plugins' are theirs: the game merges them.
        map.MapInfos.Clear();
        map.MapInfos.AddRange(ownEntries.Where(i => !writtenKeys.Contains(i.NavigationMesh.FormKey)).Select(i => i.DeepCopy()));
        foreach (var part in written)
        {
            var was = ownEntries.FirstOrDefault(i => i.NavigationMesh.FormKey == part.Key) ?? entries.FirstOrDefault(i => i.NavigationMesh.FormKey == part.Key);
            map.MapInfos.Add(Entry(part, was, scene));
        }

        var rendered = await Render(temp, pluginName);
        var files = new JsonArray();
        var cellsWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in written)
        {
            var name = $"{part.Key.ID:X6}_{part.Key.ModKey.FileName}.yaml";
            var (path, text) = rendered.FirstOrDefault(f => f.Path.EndsWith("/NavigationMeshes/" + name, StringComparison.OrdinalIgnoreCase));
            if (path is null) throw new HelperException("badRecord", $"The navmesh {FormKeyText(part.Key)} could not be written as project text.");
            var renderedFolder = path[..path.LastIndexOf("/NavigationMeshes/", StringComparison.OrdinalIgnoreCase)];
            var cellKey = part.Cell ?? scene.CellKey;
            var folder = CellFolderIn(projectDir, cellKey);
            if (folder is null)
            {
                // The cell's copy first, and outdoors its worldspace's and blocks' files, as addPatch writes them.
                folder = renderedFolder;
                foreach (var (shellPath, shellText) in rendered.Where(f => !f.Path.Contains("/NavigationMeshes/") && !f.Path.StartsWith("NavigationMeshInfoMaps/", StringComparison.Ordinal)
                    && (f.Path.StartsWith(renderedFolder + "/", StringComparison.Ordinal) || !f.Path.EndsWith("/RecordData.yaml", StringComparison.Ordinal) || IsAbove(f.Path, renderedFolder))))
                {
                    var target = Path.Combine(projectDir, shellPath);
                    if (File.Exists(target)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllTextAsync(target, Annotated(shellText));
                }
            }
            var file = $"{folder}/NavigationMeshes/{name}";
            Directory.CreateDirectory(Path.Combine(projectDir, folder, "NavigationMeshes"));
            await File.WriteAllTextAsync(Path.Combine(projectDir, file), Annotated(text));
            files.Add(file);
        }
        if (rendered.FirstOrDefault(f => f.Path.StartsWith("NavigationMeshInfoMaps/", StringComparison.Ordinal)) is { Path: not null } mapText)
        {
            Directory.CreateDirectory(Path.Combine(projectDir, Path.GetDirectoryName(mapText.Path)!));
            await File.WriteAllTextAsync(Path.Combine(projectDir, mapText.Path), Annotated(mapText.Text));
            files.Add(mapText.Path);
        }

        var main = parts.Where(x => !x.Kept).OrderByDescending(x => x.Mesh.TriangleCount).First();
        var answer = new JsonObject
        {
            ["cell"] = scene.CellName,
            ["navmesh"] = FormKeyText(main.Key),
            ["replaced"] = olds.Count > 0,
            ["triangles"] = mesh.TriangleCount,
            ["vertices"] = mesh.Vertices.Count,
            ["files"] = files,
            ["doors"] = new JsonArray(parts.SelectMany(x => x.Doors.Select(d => (JsonNode)new JsonObject { ["door"] = FormKeyText(d.Door.FormKey), ["navmesh"] = FormKeyText(x.Key), ["triangle"] = d.Triangle })).ToArray()),
            ["objects"] = given is not null ? null
                : new JsonObject { ["withCollision"] = solid, ["withoutCollision"] = open, ["unreadMeshes"] = new JsonArray(unread.Take(10).Select(m => (JsonNode)m).ToArray()) },
            ["left"] = given is not null ? null : new JsonObject { ["pieces"] = pieces.Count - keep.Count, ["triangles"] = made.TriangleCount - mesh.TriangleCount },
            ["fromBlender"] = given is null ? null : new JsonObject
            {
                ["turned"] = given.Turned, ["dropped"] = given.Dropped, ["outside"] = given.Outside,
                // Its navmesh's object deleted in Blender: the navmesh stays as it was, as below — none is deleted.
                ["notBack"] = new JsonArray(olds.Where(o => !given.FormIds.Any(id => string.Equals(id, FormKeyText(o.FormKey), StringComparison.OrdinalIgnoreCase)))
                    .Select(o => (JsonNode)FormKeyText(o.FormKey)).ToArray())
            },
            ["water"] = water.TriangleCount == 0 ? null : new JsonObject { ["planes"] = waterPlanes, ["level"] = scene.WaterHeight, ["dropped"] = droppedInWater, ["flagged"] = flags.Count(f => f.HasFlag(NavmeshTriangle.Flag.Water)) }
        };
        if (parts.Count > 1)
            answer["navmeshes"] = new JsonArray(parts.Select(x => (JsonNode)new JsonObject { ["navmesh"] = FormKeyText(x.Key), ["triangles"] = x.Mesh.TriangleCount, ["kept"] = x.Kept ? true : null }).ToArray());
        if (relinked.Count > 0)
            answer["neighbours"] = new JsonArray(relinked.Select(x => (JsonNode)new JsonObject { ["navmesh"] = FormKeyText(x.Key), ["cell"] = x.Cell is { } c ? (Index.ByFormKey.TryGetValue(c, out var e) ? e.EditorId : null) ?? FormKeyText(c) : null }).ToArray());
        if (olds.Count > 0)
        {
            var oldMesh = new Navmesh.Geometry();
            foreach (var old in oldMeshes) oldMesh.Add(old, Matrix4x4.Identity);
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
        return answer;
    }

    /// <summary>The surface brought back from Blender: one mesh, each triangle's object, and what was set right in it.</summary>
    private sealed record GivenNavmesh(Navmesh.Geometry Mesh, int[] ObjectOf, List<string?> FormIds, int Turned, int Dropped, int Outside);

    /// <summary>
    /// The navmeshes as Blender's export_layout writes them — each object's FormID, its vertices in game units and its triangles
    /// — as one surface the game can walk. Vertices within half a unit are one: a cell's navmeshes meet at the same points, and
    /// triangles that share an edge are neighbours. A triangle drawn clockwise seen from above is turned, as the game's run
    /// counter-clockwise; one with no area seen from above (a sliver, a face standing upright) or drawn twice is dropped; and
    /// outdoors one whose centre lies outside the cell is left to the neighbouring cell's navmesh.
    /// </summary>
    private static GivenNavmesh GivenSurface(JsonArray surface, (float MinX, float MinY, float MaxX, float MaxY)? square)
    {
        var welded = new Navmesh.Geometry();
        var at = new Dictionary<(int, int, int), int>();
        int Weld(Vector3 v)
        {
            var key = ((int)MathF.Round(v.X * 2), (int)MathF.Round(v.Y * 2), (int)MathF.Round(v.Z * 2));
            if (at.TryGetValue(key, out var index)) return index;
            at[key] = welded.Vertices.Count;
            welded.Vertices.Add(v);
            return welded.Vertices.Count - 1;
        }
        var objectOf = new List<int>();
        var formIds = new List<string?>();
        var drawn = new HashSet<(int, int, int)>();
        var (turned, dropped, outside) = (0, 0, 0);
        foreach (var node in surface)
        {
            if (node is not JsonObject navmesh) continue;
            formIds.Add(navmesh["formId"]?.GetValue<string>());
            var vertices = (navmesh["vertices"] as JsonArray ?? []).Select(v => v is JsonArray xyz && xyz.Count == 3
                ? new Vector3(xyz[0]!.GetValue<float>(), xyz[1]!.GetValue<float>(), xyz[2]!.GetValue<float>())
                : throw new HelperException("badInput", "A navmesh vertex from Blender is not x, y, z.")).ToList();
            foreach (var triangle in navmesh["triangles"] as JsonArray ?? [])
            {
                var corners = (triangle as JsonArray ?? []).Select(i => i!.GetValue<int>()).ToArray();
                if (corners.Length != 3 || corners.Any(i => i < 0 || i >= vertices.Count))
                    throw new HelperException("badInput", "A navmesh triangle from Blender is not three of its vertices.");
                var (a, b, c) = (Weld(vertices[corners[0]]), Weld(vertices[corners[1]]), Weld(vertices[corners[2]]));
                var (pa, pb, pc) = (welded.Vertices[a], welded.Vertices[b], welded.Vertices[c]);
                // Twice the area seen from above, positive counter-clockwise.
                var turn = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
                if (a == b || b == c || a == c || MathF.Abs(turn) < 1) { dropped++; continue; }
                if (turn < 0) { (b, c) = (c, b); turned++; }
                var centre = (pa + pb + pc) / 3;
                if (square is { } s && (centre.X < s.MinX || centre.X > s.MaxX || centre.Y < s.MinY || centre.Y > s.MaxY)) { outside++; continue; }
                var sorted = new[] { a, b, c }.Order().ToArray();
                if (!drawn.Add((sorted[0], sorted[1], sorted[2]))) { dropped++; continue; }
                welded.Triangles.AddRange([a, b, c]);
                objectOf.Add(formIds.Count - 1);
            }
        }
        // Only the vertices the triangles use, the triangles in their order.
        return new GivenNavmesh(Navmesh.Only(welded, Enumerable.Range(0, welded.TriangleCount)), objectOf.ToArray(), formIds, turned, dropped, outside);
    }

    /// <summary>A navmesh being written: its triangles with their flags, each edge's neighbour or link, its links and doors.</summary>
    private sealed record NavmeshPart(FormKey Key, Navmesh.Geometry Mesh, NavmeshTriangle.Flag[] Flags, INavigationMeshGetter? Old, bool Ours)
    {
        public short[] Edges { get; init; } = Enumerable.Repeat((short)-1, Mesh.Triangles.Count).ToArray();
        public bool[] Linked { get; init; } = new bool[Mesh.Triangles.Count];
        public List<(FormKey Mesh, short Triangle)> Links { get; init; } = [];
        public List<(int Triangle, IPlacedObjectGetter Door)> Doors { get; } = [];
        /// <summary>No new triangle lies on it: written as it was, unlinked from the cell's others.</summary>
        public bool Kept { get; init; }
        /// <summary>The cell it is in, from its parent; null for a new navmesh of the cell asked.</summary>
        public FormKey? Cell => Old?.Data?.Parent switch { CellNavmeshParent c => c.Parent.FormKey, WorldspaceNavmeshParent => OuterCell, _ => null };
        public FormKey? OuterCell { get; init; }

        public short Link(FormKey mesh, int triangle)
        {
            var at = Links.IndexOf((mesh, (short)triangle));
            if (at < 0) { Links.Add((mesh, (short)triangle)); at = Links.Count - 1; }
            return (short)at;
        }

        /// <summary>The links no edge uses any more dropped, the edges renumbered to the rest.</summary>
        public void Compact()
        {
            var used = new List<(FormKey, short)>();
            for (var i = 0; i < Edges.Length; i++)
            {
                if (!Linked[i]) continue;
                var link = Links[Edges[i]];
                var at = used.IndexOf(link);
                if (at < 0) { used.Add(link); at = used.Count - 1; }
                Edges[i] = (short)at;
            }
            Links.Clear();
            Links.AddRange(used);
        }
    }

    /// <summary>A navmesh as it stands, its triangles, flags, edges and links read in.</summary>
    private NavmeshPart FromData(INavigationMeshGetter navmesh, bool ours)
    {
        var data = navmesh.Data!;
        var part = new NavmeshPart(navmesh.FormKey, GeometryOf(navmesh), data.Triangles.Select(t => t.Flags & ~(EdgeFlags[0] | EdgeFlags[1] | EdgeFlags[2])).ToArray(), navmesh, ours)
        {
            Links = data.EdgeLinks.Select(l => (l.Mesh.FormKey, l.TriangleIndex)).ToList(),
            OuterCell = OuterCellOf(navmesh)
        };
        for (var t = 0; t < data.Triangles.Count; t++)
        {
            var triangle = data.Triangles[t];
            var edges = new[] { triangle.EdgeLink_0_1, triangle.EdgeLink_1_2, triangle.EdgeLink_2_0 };
            for (var e = 0; e < 3; e++)
            {
                part.Edges[t * 3 + e] = edges[e];
                part.Linked[t * 3 + e] = triangle.Flags.HasFlag(EdgeFlags[e]) && edges[e] >= 0 && edges[e] < data.EdgeLinks.Count;
            }
        }
        return part;
    }

    /// <summary>An exterior navmesh's cell: the cell at its grid in its worldspace.</summary>
    private FormKey? OuterCellOf(INavigationMeshGetter navmesh)
    {
        if (navmesh.Data?.Parent is not WorldspaceNavmeshParent world) return null;
        return CellContents.CellAt(WorldsOf(world.Parent.FormKey), new P2Int(world.Coordinates.X, world.Coordinates.Y))?.FormKey;
    }

    /// <summary>The project's built plugin the navmesh being made reads (SceneForNavmesh sets it): its own worldspaces count.</summary>
    private ISkyrimModGetter? navmeshProject;

    /// <summary>A worldspace's versions, the project's first: a worldspace of its own (newWorldspace) has no other.</summary>
    private List<IWorldspaceGetter> WorldsOf(FormKey world)
    {
        var worlds = new List<IWorldspaceGetter>();
        if (navmeshProject?.Worldspaces.FirstOrDefault(w => w.FormKey == world) is { } own) worlds.Add(own);
        worlds.AddRange(Contexts(world).Select(c => c.Record).OfType<IWorldspaceGetter>());
        return worlds;
    }

    private static Navmesh.Geometry GeometryOf(INavigationMeshGetter navmesh) =>
        new(navmesh.Data!.Vertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToList(),
            navmesh.Data.Triangles.SelectMany(t => new int[] { t.Vertices.X, t.Vertices.Y, t.Vertices.Z }).ToList());

    /// <summary>A navmesh as it stands now: the project's built version where it has one, else the load order's; null where deleted.</summary>
    private INavigationMeshGetter? CurrentNavmesh(FormKey key, ISkyrimModGetter? project)
    {
        var own = project?.EnumerateMajorRecords<INavigationMeshGetter>().FirstOrDefault(n => n.FormKey == key);
        var navmesh = own ?? Cache.ResolveAllContexts(key, typeof(INavigationMeshGetter)).FirstOrDefault()?.Record as INavigationMeshGetter;
        return navmesh?.Data is { Triangles.Count: > 0 } ? navmesh : null;
    }

    /// <summary>A cell's versions, the project's first.</summary>
    private List<ICellGetter> CellVersions(FormKey cell, ISkyrimModGetter? project)
    {
        var versions = Contexts(cell).Select(c => c.Record).OfType<ICellGetter>().ToList();
        if (project?.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(c => c.FormKey == cell) is { } own) versions.Insert(0, own);
        return versions;
    }

    /// <summary>The cell and what its navmesh is made from: its references and doors, outdoors the ground and the neighbours' too.</summary>
    private NavmeshScene SceneForNavmesh(string asked, ISkyrimModGetter? project)
    {
        navmeshProject = project;
        var ownCells = project?.EnumerateMajorRecords<ICellGetter>().ToList() ?? [];
        var own = ownCells.FirstOrDefault(c => string.Equals(c.EditorID, asked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(FormKeyText(c.FormKey), asked, StringComparison.OrdinalIgnoreCase));
        var cellKey = own?.FormKey ?? Resolve(asked);
        var versions = CellVersions(cellKey, project);
        if (versions.Count == 0) throw new HelperException("notFound", $"{asked} is no cell: findRecords with types [\"CELL\"] finds one.");
        var cell = versions[0];
        var cellName = cell.EditorID ?? FormKeyText(cellKey);
        var projectRecords = project?.EnumerateMajorRecords().GroupBy(r => r.FormKey).ToDictionary(r => r.Key, r => r.First()) ?? [];
        IMajorRecordGetter? Winning(FormKey key) => projectRecords.TryGetValue(key, out var mine) ? mine : Contexts(key).FirstOrDefault()?.Record;

        // Each reference's first version, the project's before the load order's; initially disabled ones are not there.
        var references = new Dictionary<FormKey, IPlacedObjectGetter>();
        void Take(IEnumerable<IPlacedGetter> placed)
        {
            foreach (var reference in placed.OfType<IPlacedObjectGetter>())
                if (!references.ContainsKey(reference.FormKey)) references[reference.FormKey] = reference;
        }
        foreach (var version in versions) Take(version.Persistent.Concat(version.Temporary));
        var doorsIn = new HashSet<FormKey>(references.Keys);

        var grid = CellContents.GridOf(cell);
        FormKey? worldKey = null;
        var ground = new List<(P2Int, float[,])>();
        if (grid is { } square)
        {
            foreach (var context in Contexts(cellKey))
                for (var parent = context.Parent; parent is not null && worldKey is null; parent = parent.Parent)
                    if (parent.Record is IWorldspaceGetter w) worldKey = w.FormKey;
            // A cell of a worldspace the project made (newWorldspace) is in the project alone.
            worldKey ??= project is null ? null : CellContents.WorldOf(project, cellKey)?.FormKey;
            if (worldKey is not { } wk) throw new HelperException("notFound", $"{cellName} is in no worldspace of the load order or the project.");
            var tops = WorldsOf(wk);
            var worlds = tops;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                {
                    var at = new P2Int(square.X + dx, square.Y + dy);
                    if (CellContents.CellAt(worlds, at) is not { } near) continue;
                    var nearVersions = (dx, dy) == (0, 0) ? versions : CellVersions(near.FormKey, project);
                    if (nearVersions.Select(v => v.Landscape).FirstOrDefault(l => l?.VertexHeightMap is not null) is { VertexHeightMap: { } heights })
                        ground.Add((at, CellScene.Heights(heights)));
                    if ((dx, dy) != (0, 0)) Take(nearVersions.SelectMany(v => v.Persistent.Concat(v.Temporary)));
                }
            // The worldspace's persistent references in these squares: in Riverwood, Alvor's forge and the inn's door are such.
            foreach (var top in tops.Select(t => t.TopCell).Where(t => t is not null))
                foreach (var reference in top!.Persistent.OfType<IPlacedObjectGetter>())
                {
                    if (reference.Placement is not { } placement) continue;
                    var at = CellContents.GridAt(placement.Position);
                    if (Math.Abs(at.X - square.X) > 1 || Math.Abs(at.Y - square.Y) > 1 || references.ContainsKey(reference.FormKey)) continue;
                    references[reference.FormKey] = reference;
                    if (at.Equals(square)) doorsIn.Add(reference.FormKey);
                }
        }
        var standing = references.Values.Where(r => (r.MajorRecordFlagsRaw & 0x800) == 0 && r.Placement is not null).ToList();
        var doors = standing.Where(r => r.TeleportDestination is not null && doorsIn.Contains(r.FormKey)).ToList();
        // The cell's own water: its height, or its worldspace's where it gives none (the game's "none" is the largest float).
        float? waterHeight = null;
        if (cell.Flags.HasFlag(Cell.Flag.HasWater))
        {
            waterHeight = versions.Select(v => v.WaterHeight).FirstOrDefault(h => h is { } v && Math.Abs(v) < 1e6f);
            if (waterHeight is null && worldKey is { } w2 && Contexts(w2).Select(c => c.Record).OfType<IWorldspaceGetter>().FirstOrDefault()?.LandDefaults is { } defaults)
                waterHeight = defaults.DefaultWaterHeight;
            waterHeight ??= grid is null ? 0 : null;
        }
        return new NavmeshScene(cell, cellKey, cellName, versions, grid, worldKey, standing, doors, ground, Winning, project, waterHeight);
    }

    /// <summary>
    /// A navmesh's edges along a side of the cell's square, as (triangle, edge) — 0 west, 1 east, 2 south, 3 north: x = min,
    /// x = max, y = min, y = max. The neighbour's navmesh across that side has its edges on the same line. `unlinkedOnly`:
    /// only edges with no neighbour and no link yet.
    /// </summary>
    private static IEnumerable<(int Triangle, int Edge)> SideEdges(NavmeshPart part, (float MinX, float MinY, float MaxX, float MaxY) square, int line, bool unlinkedOnly)
    {
        for (var t = 0; t < part.Mesh.TriangleCount; t++)
            for (var e = 0; e < 3; e++)
            {
                if (unlinkedOnly && (part.Linked[t * 3 + e] || part.Edges[t * 3 + e] >= 0)) continue;
                if (EdgeOn(part.Mesh, t, e, square, line)) yield return (t, e);
            }
    }

    /// <summary>Whether a triangle's edge lies along a side of the square, within half a unit.</summary>
    private static bool EdgeOn(Navmesh.Geometry mesh, int t, int e, (float MinX, float MinY, float MaxX, float MaxY) square, int line)
    {
        const float near = 0.5f;
        var value = SideValue(square, line);
        var (a, b) = (mesh.Vertices[mesh.Triangles[t * 3 + e]], mesh.Vertices[mesh.Triangles[t * 3 + (e + 1) % 3]]);
        return line is 0 or 1 ? MathF.Abs(a.X - value) < near && MathF.Abs(b.X - value) < near : MathF.Abs(a.Y - value) < near && MathF.Abs(b.Y - value) < near;
    }

    /// <summary>The coordinate a side of the square lies on.</summary>
    private static float SideValue((float MinX, float MinY, float MaxX, float MaxY) square, int line) => line switch
    {
        0 => square.MinX, 1 => square.MaxX, 2 => square.MinY, _ => square.MaxY
    };

    /// <summary>
    /// The triangle of the other navmeshes whose edge along the same line overlaps this edge the most, at a height within 64
    /// units: the one an actor walks into across the cell's edge.
    /// </summary>
    private static (NavmeshPart Part, int Triangle)? Overlapping(NavmeshPart part, int t, int e, List<NavmeshPart> others,
        (float MinX, float MinY, float MaxX, float MaxY) square, int line)
    {
        var (a, b) = (part.Mesh.Vertices[part.Mesh.Triangles[t * 3 + e]], part.Mesh.Vertices[part.Mesh.Triangles[t * 3 + (e + 1) % 3]]);
        var alongY = line is 0 or 1;
        float Along(Vector3 v) => alongY ? v.Y : v.X;
        var (lo, hi) = (MathF.Min(Along(a), Along(b)), MathF.Max(Along(a), Along(b)));
        (NavmeshPart, int)? best = null;
        var bestOverlap = 1f;
        foreach (var other in others)
            foreach (var (ot, oe) in SideEdges(other, square, line, unlinkedOnly: false))
            {
                var (c, d) = (other.Mesh.Vertices[other.Mesh.Triangles[ot * 3 + oe]], other.Mesh.Vertices[other.Mesh.Triangles[ot * 3 + (oe + 1) % 3]]);
                var overlap = MathF.Min(hi, MathF.Max(Along(c), Along(d))) - MathF.Max(lo, MathF.Min(Along(c), Along(d)));
                if (overlap <= bestOverlap) continue;
                var mid = (MathF.Max(lo, MathF.Min(Along(c), Along(d))) + MathF.Min(hi, MathF.Max(Along(c), Along(d)))) / 2;
                if (MathF.Abs(HeightAlong(a, b, mid, alongY) - HeightAlong(c, d, mid, alongY)) > 64) continue;
                (best, bestOverlap) = ((other, ot), overlap);
            }
        return best;
    }

    private static float HeightAlong(Vector3 a, Vector3 b, float at, bool alongY)
    {
        var (from, to) = alongY ? (a.Y, b.Y) : (a.X, b.X);
        if (MathF.Abs(to - from) < 1e-4f) return (a.Z + b.Z) / 2;
        var f = Math.Clamp((at - from) / (to - from), 0, 1);
        return a.Z + f * (b.Z - a.Z);
    }

    private static bool IsAbove(string file, string folder)
    {
        var dir = file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
        return folder.StartsWith(dir + "/", StringComparison.Ordinal) || dir.Length == 0;
    }

    /// <summary>A navmesh of the cell's own where it has none: under a copy of the cell, outdoors in its worldspace's blocks.</summary>
    /// <summary>
    /// A navmesh the load order has no version of, in a shell of its cell in the temporary plugin: a new one of the cell being
    /// made, or — in a worldspace of the project's own (newWorldspace) — a neighbouring cell's the project made before, in
    /// `inCell`. Each cell's shell is made once; a second navmesh of it joins the first.
    /// </summary>
    private NavigationMesh NewNavmesh(SkyrimMod temp, FormKey key, NavmeshScene scene, FormKey? inCell = null)
    {
        var navmesh = new NavigationMesh(key, release);
        var cellKey = inCell ?? scene.CellKey;
        var source = cellKey == scene.CellKey ? scene.Cell : CellVersions(cellKey, scene.Project).FirstOrDefault() ?? scene.Cell;
        if (scene.Worldspace is { } world && CellContents.GridOf(source) is { } grid)
        {
            Cell? cell = null;
            var fresh = false;
            if (Contexts(cellKey).FirstOrDefault(c => c.Record is ICellGetter) is { } context)
            {
                fresh = !temp.EnumerateMajorRecords<ICellGetter>().Any(c => c.FormKey == cellKey);
                cell = (Cell)context.GetOrAddAsOverride(temp);
            }
            else
            {
                // A cell of the project's own worldspace: it and its worldspace copied in, without the other cells.
                var own = scene.Project?.Worldspaces.FirstOrDefault(w => w.FormKey == world)
                    ?? throw new HelperException("notFound", $"{scene.CellName} is in no worldspace of the load order or the project.");
                var copy = temp.Worldspaces.FirstOrDefault(w => w.FormKey == world);
                if (copy is null)
                {
                    copy = own.DeepCopy();
                    copy.SubCells.Clear();
                    copy.TopCell = null;
                    temp.Worldspaces.Add(copy);
                }
                var sub = SubBlockOf(copy, grid.X, grid.Y);
                cell = sub.Items.FirstOrDefault(c => c.FormKey == cellKey);
                if (cell is null)
                {
                    sub.Items.Add(cell = source.DeepCopy());
                    fresh = true;
                }
            }
            if (fresh)
            {
                cell.Temporary.Clear();
                cell.Persistent.Clear();
                cell.NavigationMeshes.Clear();
                cell.Landscape = null;
            }
            // Mutagen holds an exterior navmesh's grid as the game stores it, y before x (Riverwood's 4, -12 reads "-12, 4").
            navmesh.Data = new NavigationMeshData { Parent = new WorldspaceNavmeshParent { Parent = new FormLink<IWorldspaceGetter>(world), Coordinates = new P2Int16((short)grid.Y, (short)grid.X) } };
            cell.NavigationMeshes.Add(navmesh);
            return navmesh;
        }
        var shell = new Cell(scene.CellKey, release) { EditorID = scene.Cell.EditorID, Flags = Cell.Flag.IsInteriorCell };
        temp.Cells.Records.Add(new CellBlock
        {
            BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock,
            SubBlocks = { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = { shell } } }
        });
        navmesh.Data = new NavigationMeshData { Parent = new CellNavmeshParent { Parent = new FormLink<ICellGetter>(scene.CellKey) } };
        shell.NavigationMeshes.Add(navmesh);
        return navmesh;
    }

    /// <summary>
    /// A navmesh's map entry from what it is now (read off the game's): the navmeshes it links to — those reached from a road
    /// triangle its preferred merges, the rest merged — the share of road triangles as a float's bits, the mean of its
    /// vertices, its load doors; its flags and parent kept from the entry it had.
    /// </summary>
    private static NavigationMapInfo Entry(NavmeshPart part, INavigationMapInfoGetter? was, NavmeshScene scene)
    {
        var linked = new HashSet<FormKey>();
        var preferred = new HashSet<FormKey>();
        for (var i = 0; i < part.Edges.Length; i++)
        {
            if (!part.Linked[i]) continue;
            var mesh = part.Links[part.Edges[i]].Mesh;
            if (mesh == part.Key) continue;
            if (part.Flags[i / 3].HasFlag(NavmeshTriangle.Flag.Preferred)) preferred.Add(mesh); else linked.Add(mesh);
        }
        linked.ExceptWith(preferred);
        var roads = part.Flags.Count(f => f.HasFlag(NavmeshTriangle.Flag.Preferred));
        var share = part.Mesh.TriangleCount == 0 ? 0f : (float)roads / part.Mesh.TriangleCount;
        var doors = part.Kept ? was?.LinkedDoors.Select(d => d.DeepCopy()).ToList() ?? [] : part.Doors.Select(d => new LinkedDoor { Unknown = DoorLinkConstant, Door = d.Door.ToLink() }).DistinctBy(d => d.Door.FormKey).ToList();
        ANavigationMapInfoParent parent = was?.Parent?.DeepCopy() as ANavigationMapInfoParent
            ?? (scene.Grid is { } g && scene.Worldspace is { } w
                ? new NavigationMapInfoWorldParent { ParentWorldspace = new FormLink<IWorldspaceGetter>(w), ParentWorldspaceCoord = new P2Int16((short)g.Y, (short)g.X) }
                : new NavigationMapInfoCellParent { ParentCell = new FormLink<ICellGetter>(scene.CellKey) });
        return new NavigationMapInfo
        {
            NavigationMesh = new FormLink<INavigationMeshGetter>(part.Key),
            Unknown = was?.Unknown ?? 0,
            Point = Vector(part.Mesh.Vertices.Aggregate(Vector3.Zero, (a, b) => a + b) / Math.Max(1, part.Mesh.Vertices.Count)),
            PreferredMergesFlag = (uint)BitConverter.SingleToInt32Bits(share),
            MergedTo = new(linked.OrderBy(k => k.ID).Select(k => new FormLink<INavigationMeshGetter>(k))),
            PreferredMerges = new(preferred.OrderBy(k => k.ID).Select(k => new FormLink<INavigationMeshGetter>(k))),
            LinkedDoors = new(doors),
            Unknown2 = MapEntryConstant,
            Parent = parent
        };
    }

    private static Vector3 Position(IPlacedObjectGetter reference) =>
        reference.Placement is { } at ? new Vector3(at.Position.X, at.Position.Y, at.Position.Z) : Vector3.Zero;

    private static P3Float Vector(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The navmesh's data as the game reads it: vertices, triangles with their neighbours or links, links, door triangles, the grid.</summary>
    private static void Fill(NavigationMeshData data, NavmeshPart part)
    {
        var mesh = part.Mesh;
        // A navmesh kept, or a neighbour's relinked: its triangles are the old one's, with their cover, doors and grid.
        var keepsTriangles = part.Kept || !part.Ours;
        var doorAt = keepsTriangles ? part.Old!.Data!.DoorTriangles.Select(d => (int)d.TriangleBeforeDoor).ToHashSet() : part.Doors.Select(d => d.Triangle).ToHashSet();
        data.Vertices.Clear();
        data.Vertices.AddRange(mesh.Vertices.Select(Vector));
        data.Triangles.Clear();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var flags = (part.Flags[t] & ~NavmeshTriangle.Flag.Door) | (doorAt.Contains(t) ? NavmeshTriangle.Flag.Door : 0);
            for (var e = 0; e < 3; e++) if (part.Linked[t * 3 + e]) flags |= EdgeFlags[e];
            var old = keepsTriangles ? part.Old!.Data!.Triangles[t] : null;
            data.Triangles.Add(new NavmeshTriangle
            {
                Vertices = new P3Int16((short)mesh.Triangles[t * 3], (short)mesh.Triangles[t * 3 + 1], (short)mesh.Triangles[t * 3 + 2]),
                EdgeLink_0_1 = part.Edges[t * 3], EdgeLink_1_2 = part.Edges[t * 3 + 1], EdgeLink_2_0 = part.Edges[t * 3 + 2],
                Flags = flags, CoverFlags = old?.CoverFlags ?? 0, IsCover = old?.IsCover ?? false
            });
        }
        data.EdgeLinks.Clear();
        data.EdgeLinks.AddRange(part.Links.Select(l => new EdgeLink { Mesh = new FormLink<INavigationMeshGetter>(l.Mesh), TriangleIndex = l.Triangle }));
        if (keepsTriangles)
        {
            var old = part.Old!.Data!;
            data.DoorTriangles.Clear();
            data.DoorTriangles.AddRange(old.DoorTriangles.Select(d => d.DeepCopy()));
            (data.NavmeshGridDivisor, data.MaxDistanceX, data.MaxDistanceY, data.Min, data.Max, data.NavmeshGrid) =
                (old.NavmeshGridDivisor, old.MaxDistanceX, old.MaxDistanceY, old.Min, old.Max, old.NavmeshGrid.ToArray());
        }
        else
        {
            data.DoorTriangles.Clear();
            foreach (var (triangle, door) in part.Doors)
                data.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = (short)triangle, Unknown = DoorLinkConstant, Door = door.ToLink() });
            var grid = Navmesh.Grid(mesh);
            data.NavmeshGridDivisor = grid.Divisor;
            data.MaxDistanceX = grid.StepX;
            data.MaxDistanceY = grid.StepY;
            data.Min = Vector(grid.Min);
            data.Max = Vector(grid.Max);
            data.NavmeshGrid = grid.Cells;
        }
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
    internal static string? CellFolderIn(string projectDir, FormKey cell)
    {
        var suffix = $"{cell.ID:X6}_{cell.ModKey.FileName}";
        foreach (var top in new[] { "Cells", "Worldspaces" })
        {
            var root = Path.Combine(projectDir, top);
            if (!Directory.Exists(root)) continue;
            if (Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .FirstOrDefault(d => Path.GetFileName(d).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(d, "RecordData.yaml"))) is { } hit)
                return Path.GetRelativePath(projectDir, hit).Replace('\\', '/');
        }
        return null;
    }

    /// <summary>The FormID after the highest the project's files use, as the build numbers new records (000800 on).</summary>
    private static uint NextFormId(string projectDir, ModKey plugin)
    {
        // Indented too: a cell's landscape, a worldspace's top cell and its references sit inside another record's file.
        var own = new Regex($@"^[ \t]*FormKey:[ \t]*([0-9A-Fa-f]{{6}}):{Regex.Escape(plugin.FileName)}[ \t]*(?:#.*)?\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var highest = 0x7FFu;
        if (Directory.Exists(projectDir))
            foreach (var file in Directory.EnumerateFiles(projectDir, "*.yaml", SearchOption.AllDirectories))
                foreach (Match m in own.Matches(File.ReadAllText(file))) highest = Math.Max(highest, Convert.ToUInt32(m.Groups[1].Value, 16));
        return highest + 1;
    }
}
