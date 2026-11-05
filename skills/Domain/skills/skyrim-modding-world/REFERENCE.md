# Cells and placed objects — reference

## Cells

A cell is a space of the world: an **interior** — a house, a cave, a shop — or an **exterior**, a square of a
worldspace's grid. Its record holds the space's own settings; what stands in it are the references placed there.

| Field | What it does |
|---|---|
| `EditorID`, `Name` | Its names: the name shows on the prompt of a door leading there. |
| `Flags` | `IsInteriorCell` for an interior; `PublicArea`, `ShowSky`, `CantTravelFromHere`. |
| `Lighting`, `LightingTemplate`, `ImageSpace` | Its light and look. |
| `Location` | The location it belongs to — Breezehome's is WhiterunBreezehomeLocation. |
| `EncounterZone` | Its level and its reset: `NoResetZone` keeps everything in it as the player left it. |
| `Owner`, `FactionRank` | Who owns it: taking from an owned cell is stealing. |

In a project an interior cell is a folder, `Cells/<block>/<sub-block>/<EditorID> - <FormID>_<plugin>/`, with the
cell's own fields in its `RecordData.yaml` and a `GroupRecordData.yaml` for each block above it, as addPatch copies them.
Its references are files in its `Temporary` and `Persistent` folders.

## Placed references

| Kind (`MutagenObjectType`) | Places | Base |
|---|---|---|
| `PlacedObject` | an object: a container, a door, furniture, an activator, a light, a static, an item lying there | the record, of any of these types |
| `PlacedNpc` | an NPC (or a creature) | an NPC record |

Other kinds — hazards, projectiles, traps — are the game's own and rarely placed by a mod.

A reference's fields, beside its kind, `EditorID` and `Base`:

| Field | What it does |
|---|---|
| `Placement` | `Position: x, y, z` in game units — x and y across the floor, z up, 64 units about an arm's length — and `Rotation: x, y, z` in radians, z turning it on the floor (1.5708 a quarter turn). |
| `Scale` | Its size, 1 by default. |
| `Owner` | An NPC or faction owning it: taking from it is stealing. |
| `Lock` | A lock's `Level`, its `Key`. |
| `EnableParent` | The reference it appears and disappears with. |
| `LinkedReferences` | The references it is linked to, by keyword. |
| `TeleportDestination` | A door's: the door it leads to. |

**Temporary or Persistent.** A Temporary reference is loaded with its cell; a Persistent one is always in memory, so a
quest's alias, a script or a reference in another cell can name it. An object placed for the player — a chest, a
bed — is Temporary.

## How the game reads a placed reference

- The game shows a reference in its cell with its `Base`'s model, at its `Placement`; a reference whose Base has no
  model the game has draws nothing (the build warns of the model).
- A container's items are its record's: every reference of that container holds them. A reference of the game's
  own container, a second chest of its record, holds that record's items; a container of the mod's own holds its own.
- A container with the `Respawns` flag gets its items back when its cell resets; one without keeps what the player
  leaves in it. A cell whose EncounterZone is `NoResetZone` never resets.
- Each plugin's version of a cell holds the references that plugin places there; the cell's own fields come from the
  winning version. A mod's copy of the cell with a field changed changes it for the whole cell.

## Typical bugs

- **The object is not there:** written into the cell's own file as a field (`Items`, `References`) — refused by the
  build since the reference is a file of its own; or a reference in no `Temporary` or `Persistent` folder.
- **It stands in a wall or floats:** a position from memory; take one from the `placed` list readRecord gives, moved a
  little, at the height the room's other objects stand at.
- **The chest is empty, or holds the game's items:** the reference names the game's container, whose items are its
  own, not a container of the mod's own with the items asked for.
- **The items come back, or vanish:** a container with `Respawns` in a cell that resets.
