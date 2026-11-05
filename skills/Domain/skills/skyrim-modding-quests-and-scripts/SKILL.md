---
name: skyrim-modding-quests-and-scripts
description: Quests and Papyrus scripts in Skyrim SE - a quest that starts with the game, scripts on quests, items, magic effects and NPCs, their properties and the events the game sends them. Use when a mod should do something rather than only add a record - give the player items, spells or perks when a game starts, react to an item being equipped or an actor dying - or when a script compiles but does nothing in game.
---

# Quests and scripts

Read [`REFERENCE.md`](REFERENCE.md) for the quest's fields and flags, the events each kind of script is sent, the
property kinds and the typical bugs. Fundamentals — projects, overrides, the build — are in `skyrim-modding-basics`.

## How a script runs

A script runs only through the record it is attached to, and only in the events the game sends that record:

- **Attached:** the record's file names it under `VirtualMachineAdapter` → `Scripts` → `- Name:`. A script in the
  project's `scripts` folder that no record names never runs.
- **What it extends:** its first line, `ScriptName <Name> extends <Type>`. A quest's script extends `Quest`, a magic
  effect's `ActiveMagicEffect`, an item's, a container's or an activator's `ObjectReference`, an NPC's `Actor`, a quest
  alias's `ReferenceAlias`. The game runs a script on nothing else.
- **Its events:** `Event OnInit()` runs when the record's script starts — for a quest, when the quest starts. Papyrus
  compiles an event of any name, and the game sends only those `Quest.psc`, `ObjectReference.psc` and the other types
  declare: `OnQuestInit`, `OnGameStart` and `OnQuestStart` are no events, and never run.
- **Its properties:** a script reaches a game record only through a property it declares, `<Type> Property <Name>
  Auto`, which the record that attaches it fills with the record it stands for. A plain variable, `Form GoldCoins`,
  is filled by nothing: None in game, and each line using it does nothing.

The build compiles each `.psc` of the `scripts` folder into the plugin's `Scripts` folder, and warns where one of
these is missing: read each warning and fix it, as it says.

## Give the player something when a new game starts

A quest of the mod's own that starts with the game and runs once, its script giving the items in OnInit and then
stopping the quest. `Quests/StarterGoldQuest.yaml` in the plugin folder:

```yaml
EditorID: StarterGoldQuest
Name:
  TargetLanguage: English
  Value: Starter Gold
Flags:
- StartGameEnabled
- RunOnce
VirtualMachineAdapter:
  Scripts:
  - Name: StarterGoldScript
    Properties:
    - MutagenObjectType: ScriptObjectProperty
      Name: Gold001
      Object: Gold001
    - MutagenObjectType: ScriptObjectProperty
      Name: SteelSword
      Object: SteelSword
```

`scripts/StarterGoldScript.psc` in the project folder (beside `plugin`, not in it):

```papyrus
ScriptName StarterGoldScript extends Quest

MiscObject Property Gold001 Auto
Weapon Property SteelSword Auto

Event OnInit()
    Game.GetPlayer().AddItem(Gold001, 500)
    Game.GetPlayer().AddItem(SteelSword, 1)
    Stop()
EndEvent
```

- The mod is these two files. The gold and the sword are the game's own records — `Gold001` is the gold every
  merchant pays, `SteelSword` the game's Steel Sword — which the quest names by editor ID: write no file for them. A
  record file of the project is a new record, another gold or sword beside the game's.
- Each property's type is its record's script type: gold (`Gold001`, a MISC) is a `MiscObject`, a weapon a `Weapon`,
  armor `Armor`, a potion `Potion`, a spell `Spell`, a perk `Perk` (REFERENCE, *Property types*).
- `Object:` names the record by its editor ID or its FormID; the build writes the FormID.
- `StartGameEnabled` starts the quest with a new game, and once in a save that did not have the mod; `RunOnce` keeps
  it from starting again; `Stop()` ends it once its work is done.
- Give it with the game's own functions: `AddItem`, `AddSpell`, `AddPerk` and `Debug.Notification` are in
  REFERENCE, *Functions*.

## Workflow

The loop of `skyrim-modding-basics` — look up, write, build — with these for scripts:

- **Look up** the records the script gives or changes with `findRecords`: each becomes a property of the script and
  a fill in the attaching record.
- **Mirror** a game quest with `readRecord` where one is like yours (`DLC1VQFactions` starts with the game and fills
  its script's properties); a game script's source is the `.psc` of its name among the game's sources.
- **Build**: a Papyrus error names its file, line and column — fix it there and build again. A warning on a script is
  a mod that builds and does nothing in game: fix it too.
- **Done** when the build answers success with no warning on the script, and each property is filled.
