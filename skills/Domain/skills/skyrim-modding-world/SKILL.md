---
name: skyrim-modding-world
description: Cells and what is placed in them in Skyrim SE - interiors like a player home, the references the game places there (containers, furniture, doors, NPCs, items), and a new object placed in one. Use when a mod should put something into the world - a chest with items in Breezehome, a container, an NPC standing in a room - or when an object a mod placed is missing in game.
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
- **An exterior cell** — a town's streets, the wilderness — sits in a worldspace, by its grid: the tools do not place
  references there yet. Place them in the Creation Kit and bring the work back with pullProject.
