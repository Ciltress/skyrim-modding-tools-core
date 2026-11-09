using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkyrimHelper;

public sealed partial class Session
{
    /// <summary>
    /// The kinds of reference a room is made of, copied from the room a new cell is made like: its walls and floor, its
    /// lights, what stands in it to sit or lean on. Not its NPCs, containers, items lying there, activators or doors — they
    /// belong to the game's own room, its quests and owners.
    /// </summary>
    private static readonly HashSet<string> RoomKinds = new(StringComparer.Ordinal)
        { "Static", "MoveableStatic", "Light", "Furniture", "IdleMarker", "AddonNode", "SoundMarker" };

    /// <summary>
    /// A new interior cell of the project's own (newCell), made like a room of the load order (`like`): its lighting, sound
    /// and flags, and its walls, floor, lights and furniture copied as references of the mod's own, so the room stands, can be
    /// lit and walked, and generateNavmesh can make its navmesh. With `door`, a load-door pair: the room's own load door kept
    /// where it stands, and its twin — the door the game pairs with it — placed `at` a spot in the cell `in`, each one's
    /// TeleportDestination the other, the player arriving where the game's pair has him arrive. A COC marker stands where the
    /// player arrives in the room. Written into the project as addPatch writes records, the other cell's copy too; the project
    /// builds it. A cell written by hand is named by a folder the build cannot read without its FormID (eval round 139's door
    /// runs, none of which built a cell), and an empty one has no floor for a navmesh.
    /// </summary>
    private async Task<JsonNode> NewCell(JsonObject p)
    {
        var pluginName = ModKey.FromFileName(Required(p, "plugin"));
        var projectDir = Required(p, "projectPluginDir");
        var editorId = Required(p, "editorId").Trim();
        if (!Regex.IsMatch(editorId, "^[A-Za-z][A-Za-z0-9_]{0,55}$"))
            throw new HelperException("badRequest", $"'{editorId}' is no editor ID: letters, digits and _, starting with a letter, at most 56 (its doors add 'Entrance').");
        var door = p["door"] as JsonObject;
        var names = door is null ? new[] { editorId } : [editorId, editorId + "Door", editorId + "Entrance"];
        foreach (var name in names)
        {
            if (Index.ByEditorId.TryGetValue(name, out var taken))
                throw new HelperException("badRequest", $"The load order has a record named {name} already ({FormKeyText(taken)}): name the cell otherwise.");
            if (Directory.Exists(projectDir) && Directory.EnumerateFiles(projectDir, "*.yaml", SearchOption.AllDirectories)
                .Any(f => Regex.IsMatch(File.ReadAllText(f), $@"^[ \t]*EditorID:[ \t]*{Regex.Escape(name)}[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)))
                throw new HelperException("badRequest", $"The project has a record named {name} already: name the cell otherwise.");
        }

        var likeName = Required(p, "like").Trim();
        var likeContexts = Contexts(Resolve(likeName, byName: true)).Where(c => c.ModKey != pluginName).ToList();
        if (likeContexts.FirstOrDefault()?.Record is not ICellGetter like || !like.Flags.HasFlag(Cell.Flag.IsInteriorCell))
            throw new HelperException("badRequest", $"{likeName} is no interior cell of the load order: name a room like the one wanted by its editor ID — " +
                "findRecords with types [\"CELL\"] finds one, a cellar in a cellar's name, e.g. MorthalAlvasHouseCellar");

        // Every reference the room's versions place, the highest-priority version of each, as readRecord's `placed` gathers them.
        var seen = new HashSet<FormKey>();
        var placed = new List<IPlacedObjectGetter>();
        foreach (var context in likeContexts)
        {
            if (context.Record is not ICellGetter version) continue;
            foreach (var reference in version.Persistent.Concat(version.Temporary))
                if (seen.Add(reference.FormKey) && reference is IPlacedObjectGetter placedObject) placed.Add(placedObject);
        }
        RecordIndex.Entry? BaseOf(IPlacedObjectGetter reference) =>
            reference.Base.FormKeyNullable is { } key && Index.ByFormKey.TryGetValue(key, out var entry) ? entry : null;
        // The engine's markers — room bounds, portals, the COC and X markers — are Skyrim.esm's first FormIDs: they belong to the
        // game's room, and a room bound copied without its portals hides what it bounds.
        bool EngineMarker(FormKey key) => key.ModKey == SkyrimMaster && key.ID < 0x800;
        var room = placed.Where(r => BaseOf(r) is { } b && RoomKinds.Contains(b.Type) && !EngineMarker(b.FormKey) && r.Placement is not null).ToList();
        var loadDoor = placed.FirstOrDefault(r => r.TeleportDestination is not null && r.Placement is not null && BaseOf(r)?.Type == "Door");
        if (door is not null && loadDoor is null)
            throw new HelperException("badRequest", $"{likeName} has no load door to keep as the room's own: name a room the player walks into through a door.");

        var next = NextFormId(projectDir, pluginName);
        FormKey Next() => new(pluginName, next++);
        var cell = new Cell(Next(), release)
        {
            EditorID = editorId,
            Name = p["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } shown ? shown : editorId,
            Flags = like.Flags,
            Lighting = like.Lighting?.DeepCopy(),
            WaterHeight = like.WaterHeight
        };
        cell.LightingTemplate.SetTo(like.LightingTemplate.FormKey);
        cell.AcousticSpace.SetTo(like.AcousticSpace.FormKeyNullable);
        cell.Music.SetTo(like.Music.FormKeyNullable);
        cell.ImageSpace.SetTo(like.ImageSpace.FormKeyNullable);
        cell.Water.SetTo(like.Water.FormKeyNullable);

        var temp = new SkyrimMod(pluginName, release);
        // An interior cell's block and sub-block are its FormID's last two decimal digits, as the game's are (Breezehome,
        // 0165A8: 91560, block 0, sub-block 6).
        var number = (int)cell.FormKey.ID;
        temp.Cells.Records.Add(new CellBlock
        {
            BlockNumber = number % 10, GroupType = GroupTypeEnum.InteriorCellBlock,
            SubBlocks = { new CellSubBlock { BlockNumber = number / 10 % 10, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = { cell } } }
        });

        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reference in room)
        {
            cell.Temporary.Add(Stripped(reference.Duplicate(Next())));
            var kind = BaseOf(reference)!.Type;
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }

        JsonObject? doors = null;
        if (door is not null && loadDoor is not null)
        {
            var inName = door["in"]?.GetValue<string>()?.Trim() is { Length: > 0 } given ? given
                : throw new HelperException("badRequest", "door.in is the cell the door to the new room stands in, by its editor ID, e.g. WhiterunBreezehome.");
            var inContexts = Contexts(Resolve(inName, byName: true)).Where(c => c.ModKey != pluginName).ToList();
            if (inContexts.FirstOrDefault()?.Record is not ICellGetter inCell)
                throw new HelperException("badRequest", $"{inName} is no cell of the load order: name the cell the door stands in by its editor ID.");
            var at = Triple(door["at"], "door.at", "the spot in " + inName + " the door stands at, x, y and z in game units — readRecord of the cell lists " +
                "what stands there, with the floor's height")!.Value;
            var turned = Triple(door["rotation"], "door.rotation", "its rotation in radians about x, y and z, as readRecord's placed lists them") ?? new P3Float(0, 0, 0);

            // The game's own pair: the room's load door and the door it leads to, whose model the new door outside takes.
            var twinKey = loadDoor.TeleportDestination!.Door.FormKey;
            var twin = Contexts(twinKey).Select(c => c.Record).OfType<IPlacedObjectGetter>().FirstOrDefault();
            var baseName = door["base"]?.GetValue<string>()?.Trim();
            var baseKey = baseName is { Length: > 0 } ? Resolve(baseName) : twin?.Base.FormKeyNullable ?? loadDoor.Base.FormKey;
            if (!Index.ByFormKey.TryGetValue(baseKey, out var baseEntry) || baseEntry.Type != "Door")
                throw new HelperException("badRequest", $"{baseName} is no door of the load order: name a door by its editor ID — findRecords with types [\"DOOR\"] — or leave base out.");

            var inside = Stripped(loadDoor.Duplicate(Next()));
            inside.EditorID = editorId + "Door";
            var outside = new PlacedObject(Next(), release)
            {
                EditorID = editorId + "Entrance",
                Base = new FormLinkNullable<IPlaceableObjectGetter>(baseKey),
                Placement = new Placement { Position = at, Rotation = turned }
            };
            // Where the player arrives: in the room where the game's pair has him arrive in front of its load door; outside,
            // in front of the new door as the game's twin has him arrive in front of it — that offset turned with the door.
            var intoRoom = twin?.TeleportDestination is { } fromTwin
                ? (fromTwin.Position, fromTwin.Rotation)
                : Ahead(loadDoor.Placement!.Position, loadDoor.Placement.Rotation);
            var outOfRoom = twin?.Placement is { } twinAt
                ? Moved(loadDoor.TeleportDestination.Position, loadDoor.TeleportDestination.Rotation, twinAt.Position, twinAt.Rotation, at, turned)
                : Ahead(at, turned);
            inside.TeleportDestination = new TeleportDestination
            {
                Door = new FormLink<IPlacedObjectGetter>(outside.FormKey), Position = outOfRoom.Position, Rotation = outOfRoom.Rotation
            };
            outside.TeleportDestination = new TeleportDestination
            {
                Door = new FormLink<IPlacedObjectGetter>(inside.FormKey), Position = intoRoom.Position, Rotation = intoRoom.Rotation
            };
            cell.Temporary.Add(inside);
            // The COC marker where the player arrives, so `coc <cell>` goes there as the door does.
            cell.Temporary.Add(new PlacedObject(Next(), release)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(CocMarkerHeading),
                Placement = new Placement { Position = intoRoom.Position, Rotation = intoRoom.Rotation }
            });
            // The room is the house's: its location and owner, so it is no stranger's room to the game's crime and AI.
            cell.Location.SetTo(inCell.Location.FormKeyNullable);
            if (inCell.Owner.FormKeyNullable is { } owner) cell.Owner.SetTo(owner);
            var copy = (Cell)inContexts[0].GetOrAddAsOverride(temp);
            copy.Temporary.Add(outside);
            doors = new JsonObject
            {
                ["in"] = inCell.EditorID ?? FormKeyText(inCell.FormKey),
                ["entrance"] = new JsonObject { ["editorId"] = outside.EditorID, ["formId"] = FormKeyText(outside.FormKey), ["base"] = baseEntry.EditorId, ["at"] = TripleText(at) },
                ["roomDoor"] = new JsonObject { ["editorId"] = inside.EditorID, ["formId"] = FormKeyText(inside.FormKey), ["at"] = TripleText(inside.Placement!.Position) },
                ["arrival"] = TripleText(intoRoom.Position)
            };
        }

        var rendered = await Render(temp, pluginName);
        var files = new JsonArray();
        foreach (var (path, text) in rendered)
        {
            var target = Path.Combine(projectDir, path);
            // The other cell's copy, brought in before by addPatch or a door made earlier: its file stays as the project has it.
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, Annotated(text));
            files.Add(path);
        }
        var cellFile = files.Select(f => f!.GetValue<string>()).FirstOrDefault(f => f.EndsWith($"/{editorId} - {cell.FormKey.ID:X6}_{pluginName.FileName}/RecordData.yaml", StringComparison.OrdinalIgnoreCase));
        return new JsonObject
        {
            ["cell"] = editorId,
            ["formId"] = FormKeyText(cell.FormKey),
            ["like"] = like.EditorID ?? likeName,
            ["references"] = room.Count,
            ["byKind"] = new JsonObject(kinds.OrderByDescending(k => k.Value).Select(k => KeyValuePair.Create(k.Key, (JsonNode?)k.Value))),
            ["doors"] = doors,
            ["files"] = files.Count,
            ["cellFile"] = cellFile
        };
    }

    /// <summary>A copy of the game's reference, kept to what stands there: no owner, lock, enable parent, links or door of the game's room.</summary>
    private static PlacedObject Stripped(PlacedObject reference)
    {
        reference.EditorID = null;
        reference.EnableParent = null;
        reference.Owner.Clear();
        reference.FactionRank = null;
        reference.Lock = null;
        reference.TeleportDestination = null;
        reference.LinkedReferences.Clear();
        reference.LinkedRooms.Clear();
        reference.MultiBoundReference.Clear();
        reference.LocationReference.Clear();
        reference.PersistentLocation.Clear();
        reference.EncounterZone.Clear();
        reference.ActivateParents = null;
        reference.Patrol = null;
        reference.NavigationDoorLink = null;
        return reference;
    }

    /// <summary>Three numbers given as a list — a position or a rotation — or null where none is given and none is needed.</summary>
    private static P3Float? Triple(JsonNode? node, string field, string what)
    {
        if (node is null) return field == "door.at" ? throw new HelperException("badRequest", $"{field} is {what}.") : null;
        if (node is not JsonArray { Count: 3 } values || values.Any(v => v is not JsonValue))
            throw new HelperException("badRequest", $"{field} is three numbers, [x, y, z]: {what}.");
        var n = values.Select(v => v!.GetValue<float>()).ToArray();
        return new P3Float(n[0], n[1], n[2]);
    }

    private static string TripleText(P3Float p) => string.Join(", ", new[] { p.X, p.Y, p.Z }.Select(v => Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A point turned with a reference about z, as the game turns one: heading 0 faces +y (north), a quarter turn +x (east).</summary>
    private static P3Float TurnZ(P3Float v, float angle) =>
        new(v.X * MathF.Cos(angle) + v.Y * MathF.Sin(angle), -v.X * MathF.Sin(angle) + v.Y * MathF.Cos(angle), v.Z);

    /// <summary>
    /// The arrival in front of a door as another door has it in front of the same model: the offset from the game's door to its
    /// arrival, turned from that door's heading to this one's.
    /// </summary>
    private static (P3Float Position, P3Float Rotation) Moved(P3Float arrival, P3Float arrivalTurn, P3Float gameDoor, P3Float gameTurn,
        P3Float door, P3Float doorTurn)
    {
        var offset = TurnZ(new P3Float(arrival.X - gameDoor.X, arrival.Y - gameDoor.Y, arrival.Z - gameDoor.Z), -gameTurn.Z);
        var turned = TurnZ(offset, doorTurn.Z);
        return (new P3Float(door.X + turned.X, door.Y + turned.Y, door.Z + turned.Z),
            new P3Float(0, 0, arrivalTurn.Z - gameTurn.Z + doorTurn.Z));
    }

    /// <summary>Where a player arrives at a door without the game's pair to go by: 96 units in front of it, on its level.</summary>
    private static (P3Float Position, P3Float Rotation) Ahead(P3Float door, P3Float turn)
    {
        var ahead = TurnZ(new P3Float(0, 96, 0), turn.Z);
        return (new P3Float(door.X + ahead.X, door.Y + ahead.Y, door.Z), new P3Float(0, 0, turn.Z));
    }
}
