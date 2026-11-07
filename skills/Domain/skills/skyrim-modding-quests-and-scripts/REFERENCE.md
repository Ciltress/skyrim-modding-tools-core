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

## Dialogue: branch, topic, response

Three records of a quest, each linking up to it:

| Record | Folder | What it is | Its lines |
|---|---|---|---|
| DialogBranch (DLBR) | `DialogBranches/<Branch>.yaml` | a conversation the player starts | `Quest`, `Category: Player`, `Flags: [TopLevel]`, `StartingTopic` |
| DialogTopic (DIAL) | `DialogTopics/<Topic>/RecordData.yaml` | a line the player says — or, of no branch, a greeting, an idle, a combat line | `Name` (the player's line), `Branch`, `Quest`; `Category`, `Subtype` for no player's line |
| DialogResponses (INFO) | `DialogTopics/<Topic>/Responses/<Info>.yaml` | what is said back, and when | `Responses` (the lines), `Conditions`, `Flags`, `LinkTo` |

- A topic the player says is `Category: Topic` (written as no line at all) of `Subtype: Custom`; the build writes the
  code beside it, `SubtypeName: CUST`, as the game's topics carry it.
- A greeting is a topic of no branch with `Category: Misc`, `Subtype: Hello`; the quest's own, its responses each
  conditioned on the NPC who says it.
- A follow-up — the player's choice after an answer — is a second topic in the same branch, named in the answer's
  `LinkTo:` list. A branch that is not `TopLevel` is reached only so.
- A response's `Flags`: `Goodbye` ends the conversation after it, `SayOnce` says it once a game, `Random` picks among
  the responses so flagged.
- The responses of a topic are tried in their order; the first whose conditions all hold is said.

## Conditions on a response

Each is a `ConditionFloat` holding the function in `Data`, compared with `ComparisonValue` (`CompareOperator`
`EqualTo` unless written). A condition runs on the speaker; `RunOnType: Target` in its `Data` runs it on the one
spoken to — the player in a conversation. `Flags: [OR]` ors it with the next.

| To say it only | Data |
|---|---|
| by one NPC | `MutagenObjectType: GetIsIDConditionData`, `Object: <NPC>` |
| by the members of a faction | `GetInFactionConditionData`, `Faction: <faction>` |
| by one voice — every guard | `GetIsVoiceTypeConditionData`, `VoiceTypeOrList: <voice type or list>` |
| once a quest reached a stage | `GetStageDoneConditionData`, `Quest: <quest>`, `Stage: 20` |
| while a quest is at a stage or later | `GetStageConditionData`, `Quest: <quest>`, `CompareOperator: GreaterThanOrEqualTo`, value the stage |
| to a player of a sex | `GetIsSexConditionData`, `MaleFemaleGender: Female`, `RunOnType: Target` |

readRecord of a game topic lists its responses with their conditions — `GetInFaction(CWImperialFaction) == 1` — the
way to find the condition a line like yours uses.

## Typical bugs

Each builds and compiles, and the build warns of all but the last:

- **The script never runs:** no record attaches it; or it extends nothing, or not its record's type; or its code is
  in an event its type is never sent (`OnQuestInit`, `OnGameStart`).
- **A line does nothing:** the record it names is a variable nothing sets, or a property nothing fills — None in
  game.
- **A script the game does not have:** a record names a script with no source in the project and none in the game.
- **It runs again:** a quest without `RunOnce` started again by another script, or OnInit of a quest not
  `StartGameEnabled`, which runs at load and again when the quest starts.
- **A line nobody says, or everybody:** a topic in no branch, a branch not `TopLevel`, a quest neither
  `StartGameEnabled` nor started by anything — nobody; a response with no condition — every NPC. The build warns of
  each.
- **A line shown for a blink:** a response without a voice file; the user installs *Fuz Ro D-oh* for unvoiced
  dialogue.
