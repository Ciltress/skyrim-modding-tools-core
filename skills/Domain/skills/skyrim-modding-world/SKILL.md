---
name: skyrim-modding-world
description: Cells and what is placed in them in Skyrim SE - interiors like a player home and the exterior cells of a worldspace like a town's streets, the references the game places there (containers, furniture, doors, NPCs, items), a new object placed in one - a static, chair, lever, plant or light of the mod's own, a locked chest - a new interior cell with the doors to it, a cell's navmesh, locations, encounter zones and regions, the ground outdoors, and a cell arranged in Blender and brought back. Use when a mod should put something into the world - a chest with items in Breezehome, a bedroll beside Riverwood's cooking pot, an NPC standing in a room - when objects of a cell are to be moved, copied or removed, or when an object a mod placed is missing in game.
---

# Cells and placed objects

Read [`REFERENCE.md`](REFERENCE.md) for the cell's fields, the kinds of placed reference and their fields, positions
and rotations, and the typical bugs. Fundamentals — overrides, the build — are in `skyrim-modding-basics`; a
container's items and an NPC's record in `skyrim-modding-items-and-crafting` and `skyrim-modding-npcs`.

## What is placed in a cell

A record — a container, a door, an NPC — is only a kind of thing. What stands in the world is a **reference** placed in
a cell: it names its record as its `Base`, and has a position and a rotation of its own. Each plugin's version of a
cell holds the references that plugin places; a mod places one with a copy of the cell and a file of its own for each
reference.

- **readRecord of a cell** shows its fields and, under `placed`, what the load order places there: the containers,
  furniture, doors, activators, NPCs and items with their positions, the rest counted by kind, and `newReference` — the
  file a new reference goes in and its lines.
- **The cell's copy**, from addPatch, keeps the cell's own fields: its file, `RecordData.yaml` in the cell's folder,
  holds no references, and a field of its own for one — `Items`, `References` — is refused.
- **A new reference** is a file of its own in the cell's `Temporary` folder, written with the lines readRecord gives.

## Place a chest of the mod's own in Breezehome

1. `findRecords` with `types: ["CELL"]` and the cell's name — Breezehome is `WhiterunBreezehome` — and readRecord it:
   its `placed` lists the chests, cupboards and shelves already there, with the floor's height.
2. `addPatch` the cell: `{"project": "BreezehomeChest", "records": ["WhiterunBreezehome"]}`.
3. The chest, a container of the mod's own — `Containers/HuntersChest.yaml` in the plugin folder:

   ```yaml
   EditorID: HuntersChest
   Name:
     TargetLanguage: English
     Value: Hunter's Chest
   Model:
     File: Clutter\Upperclass\UpperChest01.nif
   Items:
   - Item:
       Item: Gold001
       Count: 500
   - Item:
       Item: SteelSword
       Count: 1
   ```

4. Its reference in the cell — `Cells/0/6/WhiterunBreezehome - 0165A8_Skyrim.esm/Temporary/HuntersChestRef.yaml`, the
   folder readRecord's `newReference` names:

   ```yaml
   MutagenObjectType: PlacedObject
   EditorID: HuntersChestRef
   Base: HuntersChest
   Placement:
     Position: -431.5, 525.6, 64
     Rotation: 0, 0, 0
   ```

5. `buildProject`. The build reads the reference's `Base` by the chest's editor ID, in the same build that numbers the
   chest; it refuses a field no placed reference has, and a `Base` naming no record.

- **Position**: beside an object `placed` lists, a little along x or y — 64 units is about an arm's length — at the
  floor's height of the objects there, so the chest stands in the room, not in a wall.
- **Respawning**: a container without the `Respawns` flag keeps what the player puts in it; a home's chest never
  respawns. Breezehome's cell never resets (its EncounterZone is NoResetZone).
- **An existing container** the game places — the chest already in Breezehome — is changed with addPatch of its
  record: every reference of that record changes with it, in every cell.

## Workflow

- **Look up** the cell with findRecords (`types: ["CELL"]`) and read it; the record to place with findRecords, its
  type's — `CONT` for a container, `NPC_` for an NPC, `FURN` for furniture.
- **Write** the cell's copy (addPatch), the record of the mod's own if it has one, and a reference's file for each
  object placed.
- **Done** when the build answers success, the build's line naming each reference placed, with no warning.

## Place something outdoors

An exterior cell — a town's streets, the wilderness — is a square of 4096 units of a worldspace, by its grid. A
reference is placed there as indoors, in the cell's folder inside its worldspace's.

1. Find where the thing it goes beside stands. Beside something the game places — a well, a grindstone, a shrine —
   `findRecords` its name (`{"text": "Well", "types": ["ACTI", "STAT", "FURN"]}`) and readRecord it with `in`, the place's
   name: `{"record": "<its editor ID>", "in": "Riverwood"}`. Its `placedIn` lists each place it stands — the cell, its grid
   outdoors, the position. Otherwise `findRecords` with `types: ["CELL"]` and the place's name — a town's cells are named
   after it: `Riverwood`, `Riverwood01`, `Riverwood02`… — and readRecord the one whose `placed` lists it.
   readRecord of the cell gives its `newReference`: the cell's folder and its square, the x and y a reference placed in it
   stands between.
2. `addPatch` the cell: `{"project": "RiverwoodCamp", "records": ["Riverwood"]}` copies the worldspace's file, its
   blocks' and the cell's.
3. The reference in the cell's `Temporary` folder —
   `Worldspaces/Tamriel - 00003C_Skyrim.esm/0, -1/0, -2/Riverwood - 009732_Skyrim.esm/Temporary/RiverwoodCampBedroll.yaml`:

   ```yaml
   MutagenObjectType: PlacedObject
   EditorID: RiverwoodCampBedroll
   Base: Bedroll01L
   Placement:
     Position: 19356.4, -47335.5, -141.5
     Rotation: 0, 0, 2.6832
   ```

4. `buildProject`. A reference whose position lies in another cell's square is warned, with the cell it lies in.

- **Position**: inside the cell's square, beside the object `placedIn` or `placed` gives, at that object's z — the
  ground rises and falls, so a z from across the cell sinks it into a hill or leaves it floating. A thing near a cell's
  edge has its neighbour beside it: the new reference goes in the folder of the cell its own position lies in.
- **Navmesh and LOD**: a large object placed on a road blocks the NPCs' paths there until `generateNavmesh` makes the
  cell's anew; LOD is xLODGen's, so nothing placed is seen from afar. A chest, a bedroll, a barrel need neither.

## A new object of the mod's own, placed

A statue, a chair, a lever, a plant, a lamp of the mod's own is two files: the **base record** in its type's folder,
mirroring a vanilla one of its kind (`readRecord` it: copy its `Model` and what the table names, write the name and what
was asked), and a **reference** placing it in a cell, as the chest above.

| A new … | Folder | Mirror | What makes it work |
|---|---|---|---|
| static (a statue, a rock) | `Statics/` | a vanilla static of its look | `Model` a mesh the game has; nothing else to do |
| chair, bench, bed | `Furniture/` | `WoodenChair01`, a vanilla bed | `Model` and its `Markers` (where and how an actor sits or lies) copied whole: without them nobody can use it |
| lever, button, shrine | `Activators/` | `SimpleLever`, `DweLever01` | `Model`; what it does is a script of its own in `VirtualMachineAdapter` with `Event OnActivate(ObjectReference akActionRef)` (`skyrim-modding-quests-and-scripts`) |
| plant to harvest | `Florae/` | `BirdsNest` | `Ingredient` — what picking gives, the mod's own ingredient or a game one; `HarvestSound`; `Production` per season; `ActivateTextOverride` "Harvest" |
| light | `Lights/` | `Torch01`, a candle's light | `Radius`, `Color` (`'#00RRGGBB'`), `Flags`; placed as a reference, it lights what is around it |

A **locked** door or container is its reference with `Lock`: `Level` (`Novice` … `Master`, `RequiresKey`) and `Key`, the
key that opens it (`skyrim-modding-items-and-crafting`).

## A new interior cell and the door to it

A new room — a cellar, a cave, a back room — is an interior cell of the mod's own, and one call makes it, made like a room
of the game: its lighting and sound, its walls, floor, lights and furniture copied as the mod's own references, and the
player's way in, a **pair of load doors** each teleporting to the other — the room's own door, and its twin placed where
the player comes from.

1. Find a game room like it: `findRecords` with `types: ["CELL"]` and "cellar", "cave", "basement" —
   `MorthalAlvasHouseCellar` is a small cellar.
2. Find where the door stands: `readRecord` of the cell the player comes from lists what is placed there under
   `placed.references` — its doors among them, each load door with the cell it `leadsTo` — and the new door stands on
   the floor of one of them, along the same wall, a step from it. One read is enough: no door record to choose, newCell
   places the door the game pairs with the room's own.
3. `newCell {"project": "BreezehomeCellar", "cell": "BreezehomeCellar", "name": "Breezehome Cellar", "like":
   "MorthalAlvasHouseCellar", "door": {"in": "WhiterunBreezehome", "at": [x, y, z]}}` — its answer names the doors and
   where the player arrives, and `coc BreezehomeCellar` goes there too. The room takes the location of the door's cell;
   a room the game knows as a place of its own — a camp, a cave — takes `"location": {"name": …, "parent": …}`.
4. `generateNavmesh` the new cell, then the door's cell — its door is new to its navmesh (next section) — and build.

What stands in the room then changes as in any cell: its references are files in its folder's `Temporary/`, and its own
fields — its `Location`, its `EncounterZone` — change with `editRecord` of its file. No cell is written by hand, not even
an empty one: its folder is named by the FormID newCell gives it, and a room written as a file has no floor to stand on.

## A cell's navmesh

NPCs and followers walk on the cell's **navmesh**, not on its floor: a new room without one keeps followers out, and a
room whose furniture moved — or ground raised outdoors — keeps paths through the new table or into the new hill. `generateNavmesh` `{"project": "BreezehomeTidy", "cell":
"WhiterunBreezehome"}` makes it from the collision of what stands in the cell — the game's objects and the project's,
outdoors the ground — and writes it into the project, over the cell's navmeshes or new; outdoors it links the edges
with the cells beside it and writes their navmeshes too. It builds the project first where it changed. Run it
after the objects are placed, then `buildProject` — the build warns, with this call, where a big object, a load door,
changed ground or a new room needs it; a chest or a chair does not. Done when the build answers `success` and you told the user:

- **`triangles`**, and the **`doors`** linked: a room with no load door gets no follower in until one links it.
- **`before`** — over the game's navmesh, how much of the old one the new covers (`oldCovered`) and how much of the new
  lies on the old (`newOnOld`): a generated navmesh also reaches narrow gaps a level designer leaves out.
- **`objects.unreadMeshes`** — meshes it could not read: actors walk through those objects.

To see it, exportCell the cell after the build: its navmeshes come into Blender as green wireframes, `Navmesh <FormID>`.
To change it by hand — a path nobody should take, a ledge they should — edit that wireframe in Edit Mode (move vertices,
delete or add faces), run the exportScript, and importCell makes it the cell's navmesh, linked to its doors and
neighbours; then buildProject. Never write a navmesh file by hand: its links and grid are computed.

## A new worldspace

A land of the mod's own, apart from Skyrim's, is one call: `newWorldspace` `{"project": "HiddenValley", "editorId":
"HiddenValleyWorld", "name": "Hidden Valley", "cells": 8}` — flat at `height`, or shaped by a `heightmap` image (PNG or
RAW, brighter higher, top north) between `lowest` and `highest`, its look a landscape `texture`, with `water` at a height
or none. Never write a worldspace, its cells or their landscape by hand: the heights and normals are computed. Then buildProject.

- Its plugin is a full one: newWorldspace takes a light project's `Small` flag off. Leave it off, or the game shows the
  worldspace without its ground.
- In game, `coc <editorId>Start` goes to its centre.
- To let players in, a load door pair (A new interior cell and the door to it, above): one door where they come from,
  its twin in `<editorId>Start` near the COC marker, each one's `TeleportDestination` the other.
- Its cells take objects, `editTerrain` and Blender like the game's; `generateNavmesh` each cell once things stand in it
  (by `<editorId>Start` or a cell's FormID).
- Far land shows only after LOD is generated (xLODGen); the map stays empty.

## Locations, encounter zones, regions

| To give … | Record | Fields | Set on |
|---|---|---|---|
| a place the game knows (map, quests, "Cleared") | `Location` in `Locations/` | `Name`, `ParentLocation` (the hold's, `WhiterunHoldLocation`), `Keywords` of its kind (`LocTypeDwelling`, `LocTypeDungeon`) | a cell's `Location` — a new room's in the newCell call itself: `"location": {"name": "Ashen Camp", "parent": "WhiterunHoldLocation", "keywords": ["LocTypeDwelling"]}` writes the room's own, `"location": "WhiterunBreezehomeLocation"` names one |
| the level of a cell's enemies and loot | `EncounterZone` in `EncounterZones/` | `MinLevel`, `MaxLevel` (0: none), `Location`, `Flags` (`NeverResets`) | a cell's `EncounterZone` — a game cell's through its copy (addPatch); or the zone a game cell has, copied with addPatch, which changes every cell that has it |
| weather or sound over an area outdoors | `Region` in `Regions/` | `Worldspace`, `RegionAreas` (the area's points), its weather data naming `Weather` records with a chance | the exterior cells the area covers — each cell's copy names it in its `Regions`, as the game's cells name theirs |

Weather over a place of the game's — snow around Riverwood — is a region of the mod's own in Tamriel, never a new
worldspace (newWorldspace makes a land apart). Its area's points are world units, the corners around the cells: the cell
at grid x, y runs from x·4096 to (x+1)·4096, and readRecord of an exterior cell names its grid — `Riverwood` is at 4, -12.
A `Priority` above the game's regions there (theirs are about 50) and the `Override` flag make its weather the one shown,
as the game's `WeatherPineForest` has them:

```yaml
EditorID: SnowyRiverwoodRegion
Worldspace: Tamriel
RegionAreas:
- EdgeFallOff: 1024
  RegionPointListData:
  - 12288, -53248
  - 24576, -53248
  - 24576, -40960
  - 12288, -40960
Weather:
  Flags:
  - Override
  Priority: 60
  Weathers:
  - Weather: SkyrimOvercastSnow
    Chance: 100
```

## Change the ground outdoors

A plot flattened for a house, a hill raised, a ditch dug: `editTerrain` with the centre `at` ("x, y" in world units,
as readRecord's positions), a `radius` and an `action` — `flatten` (to the ground's height at the centre, or `height`),
`raise` or `lower` by an `amount`, `smooth`. It changes every cell the circle touches, blending into the ground around
over `falloff`, and writes each cell's copy with its landscape into the project; then `buildProject`. Done when the build
answers `success` and you told the user what the answer names:

- **`gameReferences`** — the game's trees, rocks and buildings the ground moved under: they float or sink now. Keep the
  edit where none stand, or move them (a reference's copy, as above).
- **`navmesh`** — NPCs walk the cell's old navmesh: `generateNavmesh` each cell after the build (A cell's navmesh, below).
- Distant terrain (LOD) keeps the old shape until xLODGen runs.

In Blender (the next section) an exterior cell's `Ground` mesh can be sculpted instead: export_layout writes its heights,
and importCell sets the cell's ground to them, its neighbours' edges moved with it.

## Arrange a cell in Blender

To move, turn, copy or remove what stands in a cell by eye, the cell goes to Blender and back — Blender with the
PyNifly add-on, which reads the game's meshes.

1. `exportCell` `{"project": "BreezehomeTidy", "cell": "WhiterunBreezehome"}` writes the cell's objects, the meshes and
   textures they show into the project's `blender/` folder, and answers `blendFile` — the cell placed and saved in
   Blender, each object where the game places it, named after its reference (`PlayerHouseChest [10EB84]`), an exterior
   cell on its ground — and `exportScript`, which writes what changed.
2. Arrange the objects: move, turn or scale one; copy one for one more of its kind; delete one to take it out.
   - **By hand:** open `blendFile` in Blender; Shift+D copies an object.
   - **Through Blender's MCP server, no window:** `execute_blender_code_for_cli` with `blend_file` the `blendFile` and
     code that changes the objects and ends with the exportScript and a save:

     ```python
     import bpy
     chest = bpy.data.objects["PlayerHouseChest [10EB84]"]
     chest.location.x += 100                      # game units: 64 is about an arm's length
     copy = chest.copy(); chest.users_collection[0].objects.link(copy); copy.location.y += 80
     exec(open(r"<exportScript>", encoding="utf-8").read())
     bpy.ops.wm.save_mainfile()
     result = {"moved": chest.name, "copy": copy.name}
     ```

     Find a name first with the same tool and code that lists them —
     `result = {"names": [o.name for o in bpy.data.objects if o.get("skyrim_base_editor_id") == "PlayerHouseChest"]}`.
3. `importCell` `{"project": "BreezehomeTidy"}` writes each change into the project — a moved object as the project's copy
   of its reference with its new placement, a copy as a new reference, a deleted one of another plugin disabled — then
   `buildProject`.

Without a `blendFile` — no Blender found, set `SKYRIM_BLENDER` — the answer's `importScript` places the cell in a
running Blender: its Scripting tab's Run Script, or its text sent through `execute_blender_code`; then the
`exportScript` the same way.
