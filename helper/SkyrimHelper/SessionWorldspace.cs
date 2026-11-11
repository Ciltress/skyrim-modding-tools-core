using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkyrimHelper;

public sealed partial class Session
{
    private static readonly FormKey DefaultWater = FormKey.Factory("000018:Skyrim.esm");
    private static readonly FormKey CocMarkerHeading = FormKey.Factory("000032:Skyrim.esm");
    private const int MaxWorldCells = 32;

    /// <summary>
    /// A worldspace of the project's own (newWorldspace): `cells` × `cells` exterior cells around grid 0, 0, each with its
    /// ground — `height` everywhere, or `heights`, one height a vertex for the whole world, (cells × 32 + 1) rows south to
    /// north of as many west to east, the edges two cells share the same in both — under the game's sky (`climate`, by
    /// default SkyrimClimate) and, with `water`, its water at that height. Modelled on the game's small worldspaces
    /// (Skuldafn): small, no fast travel. Cell 0, 0 is named `<editorId>Start` and holds a COC marker on its ground at its
    /// centre, so `coc <editorId>Start` goes there and a door can lead there. Written into the project as addPatch writes
    /// records; the project builds it.
    /// </summary>
    private async Task<JsonNode> NewWorldspace(JsonObject p)
    {
        var pluginName = ModKey.FromFileName(Required(p, "plugin"));
        var projectDir = Required(p, "projectPluginDir");
        var editorId = Required(p, "editorId").Trim();
        if (!Regex.IsMatch(editorId, "^[A-Za-z][A-Za-z0-9_]{0,57}$"))
            throw new HelperException("badRequest", $"'{editorId}' is no editor ID: letters, digits and _, starting with a letter, at most 58 (the start cell adds 'Start').");
        var start = editorId + "Start";
        foreach (var name in new[] { editorId, start })
        {
            if (Index.ByEditorId.TryGetValue(name, out var taken))
                throw new HelperException("badRequest", $"The load order has a record named {name} already ({FormKeyText(taken)}): name the worldspace otherwise.");
            if (Directory.Exists(projectDir) && Directory.EnumerateFiles(projectDir, "*.yaml", SearchOption.AllDirectories)
                .Any(f => Regex.IsMatch(File.ReadAllText(f), $@"^[ \t]*EditorID:[ \t]*{Regex.Escape(name)}[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)))
                throw new HelperException("badRequest", $"The project has a record named {name} already: name the worldspace otherwise.");
        }
        var size = p["cells"]?.GetValue<int>() ?? 4;
        if (size is < 1 or > MaxWorldCells)
            throw new HelperException("badRequest", $"cells is the worldspace's width in cells, 1 to {MaxWorldCells} (each 4096 units, about 58 m): {size} is not.");
        var climateName = p["climate"]?.GetValue<string>()?.Trim() is { Length: > 0 } c ? c : "SkyrimClimate";
        // A weather named as the climate: asked for snow over Riverwood with SkyrimOvercastSnow, eval round 144's 5 detailed region
        // runs made a worldspace of their own with it, were told only it is no climate, and built a new land with no snow over Riverwood.
        if (Index.ByEditorId.TryGetValue(climateName, out var named) && Index.ByFormKey.TryGetValue(named, out var namedEntry) && namedEntry.Type == "Weather")
            throw new HelperException("badRequest", $"{climateName} is a weather, not a climate: a climate lists a land's weathers. Weather over cells " +
                "of the game's — snow around Riverwood — is a Region of the mod's own over them, no new worldspace: a file in Regions/ with " +
                $"`Worldspace: Tamriel`, its RegionAreas around the cells, and its weather data naming {climateName} with a chance, as the " +
                "skyrim-modding-world skill's \"Locations, encounter zones, regions\" shows (readSkill {\"skill\": \"skyrim-modding-world\"}). " +
                "newWorldspace makes a new land apart from Tamriel; there, name a climate, e.g. SkyrimClimate, or leave climate out.");
        if (!Index.ByEditorId.TryGetValue(climateName, out var climate) || !Index.ByFormKey.TryGetValue(climate, out var climateEntry) || climateEntry.Type != "Climate")
            throw new HelperException("badRequest", $"{climateName} is no climate of the load order: name one by its editor ID, e.g. SkyrimClimate, or leave climate out.");
        float? water = p["water"] is JsonValue w ? w.GetValue<float>() : null;
        var textureName = p["texture"]?.GetValue<string>()?.Trim() is { Length: > 0 } t ? t : "LFieldGrass01";
        if (!Index.ByEditorId.TryGetValue(textureName, out var texture) || !Index.ByFormKey.TryGetValue(texture, out var textureEntry) || textureEntry.Type != "LandscapeTexture")
            throw new HelperException("badRequest", $"{textureName} is no landscape texture of the load order: name one by its editor ID, e.g. LFieldGrass01 or LSnow01, or leave texture out.");

        // The whole world's heights, vertex by vertex: 32 steps of 128 units a cell, the last row and column the edge.
        var span = size * 32 + 1;
        var heights = new float[span, span];
        if (p["heights"] is JsonArray rows)
        {
            if (rows.Count != span || rows.Any(r => r is not JsonArray row || row.Count != span))
                throw new HelperException("badRequest", $"heights holds {span} rows of {span} heights for {size} × {size} cells.");
            for (var y = 0; y < span; y++) for (var x = 0; x < span; x++) heights[y, x] = rows[y]![x]!.GetValue<float>();
        }
        else
        {
            var flat = p["height"]?.GetValue<float>() ?? 0;
            for (var y = 0; y < span; y++) for (var x = 0; x < span; x++) heights[y, x] = flat;
        }
        var (lowest, highest) = (float.MaxValue, float.MinValue);
        foreach (var h in heights) (lowest, highest) = (Math.Min(lowest, h), Math.Max(highest, h));

        var next = NextFormId(projectDir, pluginName);
        FormKey Next() => new(pluginName, next++);
        var first = -size / 2;
        var world = new Worldspace(Next(), release)
        {
            EditorID = editorId,
            Name = p["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } shown ? shown : editorId,
            Climate = new FormLinkNullable<IClimateGetter>(climate),
            Water = new FormLinkNullable<IWaterGetter>(DefaultWater),
            LodWater = new FormLinkNullable<IWaterGetter>(DefaultWater),
            LodWaterHeight = water ?? lowest - 2048,
            // Below the lowest ground, without water: the game draws its default water there, out of sight.
            LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = -2048, DefaultWaterHeight = water ?? lowest - 2048 },
            WorldMapOffsetScale = 1,
            DistantLodMultiplier = 1,
            Flags = Worldspace.Flag.SmallWorld | Worldspace.Flag.CannotFastTravel | (water is null ? Worldspace.Flag.NoLodWater : 0),
            ObjectBoundsMin = new P2Float(first, first),
            ObjectBoundsMax = new P2Float(first + size - 1, first + size - 1),
            // The worldspace's persistent cell, as the game's are (Giant's Tooth's): flagged persistent (0x400), its grid 0, 0.
            TopCell = new Cell(Next(), release)
            {
                Flags = Cell.Flag.HasWater,
                MajorRecordFlagsRaw = 0x400,
                Grid = new CellGrid { Point = new P2Int(0, 0) },
                WaterHeight = float.MaxValue
            }
        };
        var temp = new SkyrimMod(pluginName, release);
        temp.Worldspaces.Add(world);

        var clamped = 0;
        Cell? startCell = null;
        for (var gy = first; gy < first + size; gy++)
        {
            for (var gx = first; gx < first + size; gx++)
            {
                // This cell's 33 × 33 heights out of the world's; outside it, the neighbours' for the normals at its edges.
                var (ox, oy) = ((gx - first) * 32, (gy - first) * 32);
                var own = new float[Terrain.Size, Terrain.Size];
                for (var y = 0; y < Terrain.Size; y++) for (var x = 0; x < Terrain.Size; x++) own[y, x] = heights[oy + y, ox + x];
                float? Around(int x, int y) => ox + x is >= 0 && oy + y >= 0 && ox + x < span && oy + y < span ? heights[oy + y, ox + x] : null;
                var encoded = Terrain.Encode(own);
                clamped += encoded.Clamped;
                var normals = Terrain.Normals(own, Around);
                var heightMap = new Array2d<sbyte>(new sbyte[Terrain.Size, Terrain.Size]);
                var normalMap = new Array2d<P3UInt8>(new P3UInt8[Terrain.Size, Terrain.Size]);
                for (var y = 0; y < Terrain.Size; y++)
                    for (var x = 0; x < Terrain.Size; x++)
                    {
                        heightMap[x, y] = (sbyte)encoded.Rows[y][x];
                        var n = normals[y][x].Split(", ").Select(v => byte.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        normalMap[x, y] = new P3UInt8(n[0], n[1], n[2]);
                    }
                var colours = new Array2d<P3UInt8>(new P3UInt8[Terrain.Size, Terrain.Size]);
                for (var y = 0; y < Terrain.Size; y++) for (var x = 0; x < Terrain.Size; x++) colours[x, y] = new P3UInt8(255, 255, 255);
                // As the Creation Kit writes a landscape (the game's, and Giant's Tooth's of a light plugin): flags 31, white vertex
                // colours, and a base texture in each quadrant. A light plugin that is no master loses it in game (MadeNotes.LightWorldspace).
                var land = new Landscape(Next(), release)
                {
                    Flags = (Landscape.Flag)31,
                    VertexHeightMap = new LandscapeVertexHeightMap { Offset = encoded.Offset, HeightMap = heightMap },
                    VertexNormals = normalMap,
                    VertexColors = colours
                };
                foreach (var quadrant in new[] { Quadrant.BottomLeft, Quadrant.BottomRight, Quadrant.TopLeft, Quadrant.TopRight })
                    land.Layers.Add(new BaseLayer { Header = new LayerHeader { Texture = new FormLink<ILandscapeTextureGetter>(texture), Quadrant = quadrant, LayerNumber = ushort.MaxValue } });
                // Each cell's water is the worldspace's (no height of its own), as the game's cells have it.
                var cell = new Cell(Next(), release)
                {
                    Grid = new CellGrid { Point = new P2Int(gx, gy) },
                    Flags = Cell.Flag.HasWater,
                    // The game's "none": the worldspace's water.
                    WaterHeight = float.MaxValue,
                    Landscape = land
                };
                if (gx == 0 && gy == 0)
                {
                    cell.EditorID = start;
                    startCell = cell;
                    // The COC marker the game's coc looks for, on the ground at the cell's centre, a little above it.
                    cell.Temporary.Add(new PlacedObject(Next(), release)
                    {
                        Base = new FormLinkNullable<IPlaceableObjectGetter>(CocMarkerHeading),
                        Placement = new Placement { Position = new P3Float(2048, 2048, heights[oy + 16, ox + 16] + 16), Rotation = new P3Float(0, 0, 0) }
                    });
                }
                SubBlockOf(world, gx, gy).Items.Add(cell);
            }
        }

        var rendered = await Render(temp, pluginName);
        var files = new JsonArray();
        foreach (var (path, text) in rendered)
        {
            var target = Path.Combine(projectDir, path);
            if (File.Exists(target)) throw new HelperException("badRequest", $"{path} is in the project already: the worldspace was made before.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, Annotated(text));
            files.Add(path);
        }
        return new JsonObject
        {
            ["worldspace"] = FormKeyText(world.FormKey),
            ["editorId"] = editorId,
            ["cells"] = size * size,
            ["grid"] = new JsonObject { ["from"] = $"{first}, {first}", ["to"] = $"{first + size - 1}, {first + size - 1}" },
            ["start"] = new JsonObject { ["cell"] = start, ["formId"] = FormKeyText(startCell!.FormKey), ["marker"] = FormKeyText(startCell.Temporary[0].FormKey) },
            ["ground"] = new JsonObject { ["lowest"] = lowest, ["highest"] = highest, ["clamped"] = clamped },
            ["water"] = water,
            ["climate"] = climateName,
            ["texture"] = textureName,
            ["files"] = files.Count,
            ["worldspaceFile"] = files.Select(f => f!.GetValue<string>()).FirstOrDefault(f => f.EndsWith("/RecordData.yaml") && f.Count(ch => ch == '/') == 2)
        };
    }

    /// <summary>The block and sub-block an exterior cell is stored in: blocks of 32 × 32 cells, sub-blocks of 8 × 8, y before x.</summary>
    private static WorldspaceSubBlock SubBlockOf(Worldspace world, int gx, int gy)
    {
        var (bx, by) = ((short)Math.Floor(gx / 32.0), (short)Math.Floor(gy / 32.0));
        var (sx, sy) = ((short)Math.Floor(gx / 8.0), (short)Math.Floor(gy / 8.0));
        var block = world.SubCells.FirstOrDefault(b => b.BlockNumberX == bx && b.BlockNumberY == by);
        if (block is null)
            world.SubCells.Add(block = new WorldspaceBlock { BlockNumberX = bx, BlockNumberY = by, GroupType = GroupTypeEnum.ExteriorCellBlock });
        var sub = block.Items.FirstOrDefault(b => b.BlockNumberX == sx && b.BlockNumberY == sy);
        if (sub is null) block.Items.Add(sub = new WorldspaceSubBlock { BlockNumberX = sx, BlockNumberY = sy, GroupType = GroupTypeEnum.ExteriorCellSubBlock });
        return sub;
    }
}
