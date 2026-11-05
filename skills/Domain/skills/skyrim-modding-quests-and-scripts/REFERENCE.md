# Quests and scripts — reference

## The quest record (QUST)

A quest is what runs a mod's own logic: it needs no journal entry, no dialogue and no stages to start and run its
script. The fields a script's quest needs:

| Field | What it does |
|---|---|
| `EditorID`, `Name` | Its names; `Name` shows only in the journal, where a quest with no objective shows nothing. |
| `Flags` | `StartGameEnabled` — starts with a new game, and once in a save that did not have the mod. `RunOnce` — can never start again once it stopped. `AllowRepeatedStages`, `WarnOnAliasFillFailure` as the game's quests have them. |
| `VirtualMachineAdapter` | Its scripts, each `- Name:` with its `Properties`; its aliases' scripts; `FileName` and `Fragments` for stage fragments (below). |
| `Stages` | Stages by `Index`, each with log entries; a stage can run a fragment of the quest's fragment script. |
| `Aliases` | The references and locations it holds — `ForcedReference: 000014:Skyrim.esm` (PlayerRef) for the player — each of which can have scripts of its own. |

A quest that is not `StartGameEnabled` starts from a script (`MyQuest.Start()`), from a story manager event, or from
dialogue. Its OnInit runs twice — when the game first loads the quest, and again when it starts: give a start's work
to a stage (`SetStage`) or to the code that starts it, not to OnInit.

## Attaching a script, and its properties

```yaml
VirtualMachineAdapter:
  Scripts:
  - Name: MyQuestScript
    Properties:
    - MutagenObjectType: ScriptObjectProperty   # a record: Object names it by editor ID or FormID
      Name: Gold001
      Object: Gold001
    - MutagenObjectType: ScriptIntProperty      # a whole number: Data holds it
      Name: Amount
      Data: 500
```

The other kinds: `ScriptFloatProperty`, `ScriptBoolProperty` and `ScriptStringProperty` with `Data`, and the list
kinds (`ScriptObjectListProperty` …) for an array property. Each property filled is one the script declares, of the
same name: `MiscObject Property Gold001 Auto`, `Int Property Amount Auto`. A property can carry a value of its own in
the script (`Int Property Amount = 500 Auto`), which needs no fill; a record property with none and no fill is None.

An alias's scripts sit under the quest's `VirtualMachineAdapter` → `Aliases`, each with the alias's number; an
alias script extends `ReferenceAlias` (or `LocationAlias`). Stage fragments sit in one script the quest names as
`FileName`, each fragment a function `Fragment_<n>` that `Fragments` ties to a stage by `StageIndex`.

## Property types

The script type of each record type a script names, as its property's type:

| Record | Type | Record | Type |
|---|---|---|---|
| MISC (gold, a misc item) | `MiscObject` | WEAP | `Weapon` |
| ARMO | `Armor` | AMMO | `Ammo` |
| ALCH (potion, food) | `Potion` | INGR | `Ingredient` |
| BOOK | `Book` | KEYM | `Key` |
| SPEL | `Spell` | PERK | `Perk` |
| MGEF | `MagicEffect` | GLOB | `GlobalVariable` |
| NPC_ (the base) | `ActorBase` | an actor in the world, the player (`PlayerRef`) | `Actor` |
| QUST | `Quest` | FACT | `Faction` |
| LVLI | `LeveledItem` | MESG | `Message` |
| FLST | `FormList` | KYWD | `Keyword` |

`Form` takes any record, and does for AddItem; the named type lets the compiler check the calls made on it.

## What each script extends, and the events it is sent

The game sends a script only the events its type declares, and every script OnInit, OnBeginState and OnEndState.

| Attached to | Extends | Events it is sent, among others |
|---|---|---|
| a quest | `Quest` | OnInit, OnUpdate (after `RegisterForSingleUpdate`), OnUpdateGameTime, OnStory… (a story manager event) |
| a quest alias | `ReferenceAlias` | the events of its reference: OnActivate, OnContainerChanged, OnDeath, OnItemAdded, OnPlayerLoadGame (the player's alias) |
| a magic effect | `ActiveMagicEffect` | OnEffectStart, OnEffectFinish, OnHit, OnUpdate — on the actor the effect is on |
| an item, a container, a door, an activator | `ObjectReference` | OnActivate, OnEquipped, OnUnequipped, OnContainerChanged, OnLoad, OnRead (a book), OnOpen, OnClose |
| an NPC | `Actor` | the ObjectReference events, and OnDeath, OnDying, OnCombatStateChanged, OnLocationChange, OnObjectEquipped |

The game's sources of these types (`Quest.psc`, `ObjectReference.psc`, `Actor.psc`, `ActiveMagicEffect.psc`, …) are
among the Creation Kit's script sources, in `Data\Source\Scripts`: an event a script declares is sent only if one of
them declares it. A mod event is sent once the script registers for it by name, `RegisterForModEvent("MyMod_Start",
"OnMyModStart")`.

## Functions

The game's own, each on what its script calls it on:

| To | Write |
|---|---|
| give the player an item | `Game.GetPlayer().AddItem(Gold001, 500)` |
| take one | `Game.GetPlayer().RemoveItem(Gold001, 100)` |
| teach a spell | `Game.GetPlayer().AddSpell(MySpell)` |
| give a perk | `Game.GetPlayer().AddPerk(MyPerk)` |
| show a message in the corner | `Debug.Notification("The text")` |
| stop the quest the script is on | `Stop()` |
| set a stage | `SetStage(10)` |
| run code again in 5 s | `RegisterForSingleUpdate(5.0)`, then `Event OnUpdate()` |
| a record no property names, by its FormID | `Game.GetFormFromFile(0x0000000F, "Skyrim.esm")` |

## Typical bugs

Each builds and compiles, and the build warns of all but the last:

- **The script never runs:** no record attaches it; or it extends nothing, or not its record's type; or its code is
  in an event its type is never sent (`OnQuestInit`, `OnGameStart`).
- **A line does nothing:** the record it names is a variable nothing sets, or a property nothing fills — None in
  game.
- **A script the game does not have:** a record names a script with no source in the project and none in the game.
- **It runs again:** a quest without `RunOnce` started again by another script, or OnInit of a quest not
  `StartGameEnabled`, which runs at load and again when the quest starts.
