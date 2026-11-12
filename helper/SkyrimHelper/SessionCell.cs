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
        // The names the game shows — the room's on its door, its location's on the map — in words, never an editor ID's spelling:
        // eval round 151's location #2 named its room's location "AshenCampLocation", and the map would have shown that.
        var shownCell = p["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } shownGiven ? ShownName(shownGiven, "name") : Words(editorId);
        var names = new List<string> { editorId };
        if (door is not null) names.AddRange([editorId + "Door", editorId + "Entrance"]);
        if (p["location"] is JsonObject) names.Add(editorId + "Location");
        bool ProjectNames(string name) => Directory.Exists(projectDir) && Directory.EnumerateFiles(projectDir, "*.yaml", SearchOption.AllDirectories)
            .Any(f => Regex.IsMatch(File.ReadAllText(f), $@"^[ \t]*EditorID:[ \t]*{Regex.Escape(name)}[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase));
        foreach (var name in names)
        {
            if (Index.ByEditorId.TryGetValue(name, out var taken))
                throw new HelperException("badRequest", $"The load order has a record named {name} already ({FormKeyText(taken)}): name the cell otherwise.");
            if (ProjectNames(name))
                // The room's own location's name, taken by a Location the project has: told "name the cell otherwise", eval round
                // 150's location+detailed #1 had written that location itself, and only had to name it.
                throw new HelperException("badRequest", name == editorId + "Location"
                    ? $"The project has a record named {name} already, the name newCell gives a location of the room's own: to put the room in " +
                      $"that one, name it, \"location\": \"{name}\" — or give the cell another name."
                    : $"The project has a record named {name} already: name the cell otherwise.");
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
            Name = shownCell,
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

        // The location the room is in, which the game knows it by: one named, or one of its own written beside it — over the door's
        // cell's. Asked for a room the game knows as a location of its own, none of the 8 plain location runs of eval rounds 144 and
        // 149 wrote a Location, and 6 of the 10 detailed ones wrote one and set no cell's Location to it.
        JsonObject? where = null;
        string? byEditorId = null; // a Location of the project's not built yet, written into the cell's file by its editor ID
        FormKey? OfKind(string wanted, string kind, string field)
        {
            FormKey key;
            try { key = Resolve(wanted); }
            catch (HelperException) { return null; }
            return Index.ByFormKey.TryGetValue(key, out var entry) && entry.Type == kind ? key
                : throw new HelperException("badRequest", $"{field}: {wanted} is {(Index.ByFormKey.TryGetValue(key, out var other) ? RecordFiles.Article(other.Type) : "no record")}, " +
                    $"not {RecordFiles.Article(kind)} — findRecords with types [\"{(kind == "Location" ? "LCTN" : "KYWD")}\"] finds one.");
        }
        // A Location of the room's own, its parent the place it lies in, and the room in it.
        JsonObject Made(string shown, FormKey? parent, IReadOnlyList<FormKey> kinds)
        {
            var made = new Location(Next(), release) { EditorID = editorId + "Location", Name = shown };
            if (parent is { } lies) made.ParentLocation.SetTo(lies);
            if (kinds.Count > 0)
            {
                made.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>>();
                foreach (var kind in kinds) made.Keywords.Add(new FormLink<IKeywordGetter>(kind));
            }
            temp.Locations.Add(made);
            cell.Location.SetTo(made.FormKey);
            return new JsonObject
            {
                ["editorId"] = made.EditorID, ["formId"] = FormKeyText(made.FormKey), ["made"] = true, ["name"] = shown,
                ["parent"] = parent is { } p2 ? Index.ByFormKey[p2].EditorId ?? FormKeyText(p2) : null
            };
        }
        switch (p["location"])
        {
            case JsonValue named when named.TryGetValue<string>(out var locationName) && locationName.Trim() is { Length: > 0 } wanted:
                if (OfKind(wanted, "Location", "location") is { } gameLocation)
                {
                    var gameName = Index.ByFormKey[gameLocation].EditorId ?? wanted;
                    // A hold, the whole of its land, named where the room was to be a place of its own in it: 2 of eval round 150's 3
                    // plain location runs sent WhiterunHoldLocation, and told that a place of the room's own is a Location whose
                    // parent the hold is, round 151's location #3 answered its room a place in Whiterun Hold, in the hold itself.
                    // So the room is a place of its own in the hold, its location newCell's, unless that name is taken.
                    var hold = Contexts(gameLocation).Select(c => c.Record).OfType<ILocationGetter>().FirstOrDefault()?.Keywords?
                        .Any(k => Index.ByFormKey.TryGetValue(k.FormKey, out var keyword) && keyword.EditorId == "LocTypeHold") == true;
                    if (hold && !Index.ByEditorId.ContainsKey(editorId + "Location") && !ProjectNames(editorId + "Location"))
                    {
                        where = Made(shownCell, gameLocation, []);
                        where["hold"] = gameName;
                        break;
                    }
                    cell.Location.SetTo(gameLocation);
                    where = new JsonObject { ["editorId"] = gameName, ["formId"] = FormKeyText(gameLocation), ["made"] = false, ["hold"] = hold ? gameName : null };
                    break;
                }
                var locations = Path.Combine(projectDir, "Locations");
                var own = Directory.Exists(locations) ? Directory.EnumerateFiles(locations, "*.yaml", SearchOption.AllDirectories).Select(File.ReadAllText)
                    .FirstOrDefault(t => Regex.IsMatch(t, $@"^EditorID:[ \t]*{Regex.Escape(wanted)}[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)) : null;
                if (own is null)
                    throw new HelperException("badRequest", $"location: {wanted} is no Location of the load order or the project — name one by its editor ID " +
                        "(findRecords with types [\"LCTN\"]), or make the room's own: {\"name\": \"Ashen Camp\", \"parent\": \"WhiterunHoldLocation\"}.");
                if (Regex.Match(own, @"^FormKey:[ \t]*(\S+)", RegexOptions.Multiline) is { Success: true } line && FormKey.TryFactory(line.Groups[1].Value, out var ownKey))
                    cell.Location.SetTo(ownKey);
                else byEditorId = wanted;
                where = new JsonObject { ["editorId"] = wanted, ["made"] = false };
                break;
            case JsonObject wanted:
                // Its name the room's where none is given.
                var shownName = wanted["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } given ? ShownName(given, "location.name") : shownCell;
                // The game names a location as the place it is — "Bleak Falls Barrow", "Whiterun Hold" — never "… Location": eval round
                // 152's location #2 named its camp's "Ashen Camp Location", which the map would have shown so.
                string? sentAs = null;
                if (Regex.Match(shownName, @"^(?<place>.+?)\s+Location$", RegexOptions.IgnoreCase) is { Success: true } asPlace)
                    (sentAs, shownName) = (shownName, asPlace.Groups["place"].Value);
                FormKey? parent = null;
                if (wanted["parent"]?.GetValue<string>()?.Trim() is { Length: > 0 } parentName)
                    parent = OfKind(parentName, "Location", "location.parent") ?? throw new HelperException("badRequest",
                        $"location.parent: {parentName} is no Location of the load order — a hold's is like WhiterunHoldLocation; findRecords with types [\"LCTN\"] finds one.");
                var keywords = (wanted["keywords"] as JsonArray)?.Select(k => k?.GetValue<string>()?.Trim()).OfType<string>().Where(k => k.Length > 0)
                    .Select(keyword => OfKind(keyword, "Keyword", "location.keywords") ?? throw new HelperException("badRequest",
                        $"location.keywords: {keyword} is no Keyword of the load order — a place's kind is like LocTypeDwelling; findRecords with types [\"KYWD\"] finds one."))
                    .ToList() ?? [];
                where = Made(shownName, parent, keywords);
                if (sentAs is not null) where["sentAs"] = sentAs;
                break;
        }
        if (where is null && cell.Location.FormKeyNullable is { } kept)
            where = new JsonObject { ["editorId"] = Index.ByFormKey.TryGetValue(kept, out var keptEntry) ? keptEntry.EditorId : null, ["formId"] = FormKeyText(kept), ["made"] = false };

        var rendered = await Render(temp, pluginName);
        var files = new JsonArray();
        foreach (var (path, text) in rendered)
        {
            var target = Path.Combine(projectDir, path);
            // The other cell's copy, brought in before by addPatch or a door made earlier: its file stays as the project has it.
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var written = Annotated(text);
            if (byEditorId is not null && path.EndsWith($"/{editorId} - {cell.FormKey.ID:X6}_{pluginName.FileName}/RecordData.yaml", StringComparison.OrdinalIgnoreCase))
                written = Regex.Replace(written, @"^(EditorID:[^\n]*\n)", $"$1Location: {byEditorId}\n", RegexOptions.Multiline);
            await File.WriteAllTextAsync(target, written);
            files.Add(path);
        }
        var cellFile = files.Select(f => f!.GetValue<string>()).FirstOrDefault(f => f.EndsWith($"/{editorId} - {cell.FormKey.ID:X6}_{pluginName.FileName}/RecordData.yaml", StringComparison.OrdinalIgnoreCase));
        if (where?["made"]?.GetValue<bool>() == true)
            where["file"] = files.Select(f => f!.GetValue<string>()).FirstOrDefault(f => f.StartsWith("Locations/", StringComparison.OrdinalIgnoreCase));
        return new JsonObject
        {
            ["cell"] = editorId,
            ["formId"] = FormKeyText(cell.FormKey),
            ["like"] = like.EditorID ?? likeName,
            ["references"] = room.Count,
            ["byKind"] = new JsonObject(kinds.OrderByDescending(k => k.Value).Select(k => KeyValuePair.Create(k.Key, (JsonNode?)k.Value))),
            ["doors"] = doors,
            ["location"] = where,
            ["files"] = files.Count,
            ["cellFile"] = cellFile
        };
    }

    /// <summary>An editor ID in words, as the game shows a name: AshenCamp "Ashen Camp", Breezehome_Cellar "Breezehome Cellar".</summary>
    internal static string Words(string editorId) =>
        Regex.Replace(Regex.Replace(editorId, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|_+", " "), @"\s+", " ").Trim();

    /// <summary>
    /// A name the game shows, as given — refused where it is written as an editor ID, one word of capitals inside it or joined by
    /// _: eval round 151's location #2 named its room's location AshenCampLocation, which the map would have shown so.
    /// </summary>
    private static string ShownName(string given, string field)
    {
        if (Regex.IsMatch(given, @"\s") || !Regex.IsMatch(given, @"[a-z][A-Z]|_")) return given;
        var meant = Words(Regex.Replace(given, "Location$", ""));
        throw new HelperException("badRequest", $"{field}: {given} is written as an editor ID, and {field} is the name the game shows — on the " +
            $"door, on the map — in words, as the user said it: \"{meant}\". newCell gives the editor IDs itself: the room's is `cell`, its " +
            "location's the room's with Location after it.");
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
