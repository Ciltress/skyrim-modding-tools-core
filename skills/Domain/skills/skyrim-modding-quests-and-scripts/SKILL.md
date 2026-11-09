---
name: skyrim-modding-quests-and-scripts
description: Quests, Papyrus scripts and dialogue in Skyrim SE - a quest that starts with the game, scripts on quests, items, magic effects and NPCs, their properties and the events the game sends them, and dialogue - a topic the player can ask an NPC and what the NPC answers - message boxes, scenes of NPCs talking, and quests the story manager starts. A follower the player recruits needs no dialogue of the mod's own: skyrim-modding-npcs. Use when a mod should do something rather than only add a record - give the player items, spells or perks when a game starts, react to an item being equipped or an actor dying, give an NPC a new line - or when a script compiles or a line builds but does nothing in game.
---

# Quests, scripts and dialogue

Read [`REFERENCE.md`](REFERENCE.md) for the quest's fields and flags, the events each kind of script is sent, the
property kinds, dialogue's records and conditions, and the typical bugs. Fundamentals — projects, overrides, the build — are in `skyrim-modding-basics`.

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

## Give an NPC a new line the player can ask for

Dialogue belongs to a quest: the game offers a topic only while its quest runs, to the NPCs its responses' conditions
name. Four records, Hulda of Whiterun's Bannered Mare as the NPC — each file in the plugin folder:

`Quests/HuldaRumorsQuest.yaml` — the quest, started with the game:

```yaml
EditorID: HuldaRumorsQuest
Name:
  TargetLanguage: English
  Value: Hulda's Rumors
Flags:
- StartGameEnabled
Priority: 50
```

`DialogBranches/HuldaRumorsBranch.yaml` — the branch the player starts the conversation in:

```yaml
EditorID: HuldaRumorsBranch
Quest: HuldaRumorsQuest
Category: Player
Flags:
- TopLevel
StartingTopic: HuldaRumorsTopic
```

`DialogTopics/HuldaRumorsTopic/RecordData.yaml` — the topic: what the player says, its name. A topic is a folder:

```yaml
EditorID: HuldaRumorsTopic
Name:
  TargetLanguage: English
  Value: Heard any rumors lately?
Branch: HuldaRumorsBranch
Quest: HuldaRumorsQuest
```

`DialogTopics/HuldaRumorsTopic/Responses/HuldaRumorsInfo.yaml` — what the NPC answers, and to whom it is given:

```yaml
EditorID: HuldaRumorsInfo
Responses:
- EmotionValue: 50
  ResponseNumber: 1
  Flags:
  - UseEmotionAnimation
  Text:
    TargetLanguage: English
    Value: They say a dragon was seen over Helgen. Strange times.
Conditions:
- MutagenObjectType: ConditionFloat
  Data:
    MutagenObjectType: GetIsIDConditionData
    Object: Hulda
  ComparisonValue: 1
```

- **Who says it** is the response's conditions: `GetIsIDConditionData` with the NPC's editor ID (`findRecords` with
  `types: ["NPC_"]` finds it) gives the line to that NPC alone. A response with no condition is said by every NPC, and
  the topic offered by every NPC — the build warns.
- **A longer answer** is more items under `Responses:`, `ResponseNumber` 2, 3…: one is said after the other.
- **More answers to choose from**, each for another NPC or another state, are more files in `Responses/`, each with its
  conditions; the game says the first whose conditions hold.
- **A greeting** — what an NPC says when the player walks up — is a response in a topic of no branch with `Category:
  Misc` and `Subtype: Hello`: read the game's `readRecord {"record": "DialogueGenericHello"}` and write your topic as
  it is, your response in it conditioned on the NPC.
- **Read the game's dialogue** with `readRecord` of a topic: its answer lists every response — what is said, and
  the conditions, as `GetIsID(Hulda) == 1`. readRecord of a response's formId shows its whole file.
- `StartGameEnabled` starts the quest, and its dialogue, with the game and in a save that had no mod. The build
  writes the SEQ file such a quest needs into the build (`Seq/<plugin>.seq`): the game offers a start-game-enabled
  quest's dialogue only when that file names it, and deploy takes it into the mod.
- The response's text is shown as a subtitle. It has no voice: the game shows a line without a voice file only for a
  moment, so players of unvoiced dialogue install *Fuz Ro D-oh* (SKSE), which holds each line as long as it takes to
  read — tell the user.

## Messages, scenes and the story manager

- **A message box or a notification** is a `Message` in `Messages/`: its text is `Description`'s `Value`; `Flags` with
  `MessageBox` makes it a box the player closes, without it a notification in the corner; `MenuButtons`, each with its
  `Text`, are a box's buttons ("OK"). A script shows it: a `Message Property WelcomeMessage Auto`, filled with the
  record in the script's `VirtualMachineAdapter`, and `WelcomeMessage.Show()` — in a quest's `OnInit` for "when a new
  game starts" (the section above).
- **A scene** — NPCs talking to each other — is a `Scene` in `Scenes/` that belongs to a quest (`Quest`): its `Actors`
  are the quest's aliases by `ID`, filled with the NPCs (an alias's `UniqueActor`, or a `GetIsID` condition, as
  `DialogueSolitudeAddvarHouseScene2`'s are); its `Actions`, in their order, each name the actor (`ActorID`) and, for a
  line, the dialogue topic whose response is said (the topic and responses written as in the section above). `Flags` with
  `BeginOnQuestStart` plays it when the quest starts; mirror a vanilla scene, `readRecord` `AddvarHouse2`.
- **The story manager** starts a quest when something happens in game: a `StoryManagerQuestNode` in
  `StoryManagerQuestNodes/` whose `Parent` is the game's node for that event, with the quest under `Quests`; the quest's
  `Event` is the event's code — `CLOC` for the player changing location (`DA09ChangeLocation`), `ADIA` for dialogue —
  and its aliases can be filled from the event (`FindMatchingRefFromEvent`). `findRecords` with `types: ["SMEN"]` lists
  the event nodes; mirror a vanilla quest node, `readRecord` `DA09ChangeLocationSharesEvent`. The node and the quest are
  all it takes: a script is only for what the quest does.
- **Started each time**, the quest has no `RunOnce` flag and stops itself: the story manager starts a quest that is not
  running. Its script is sent the event — for `CLOC` `Event OnStoryChangeLocation(ObjectReference akActor, Location
  akOldLocation, Location akNewLocation)`, as `Quest.psc` declares it — and ends with `Stop()`. A location alias filled
  from the event answers its location with `GetLocation()`; a location's name is no function of the game's Papyrus
  (`GetName` is SKSE's).

## Workflow

The loop of `skyrim-modding-basics` — look up, write, build — with these for scripts:

- **Look up** the records the script gives or changes with `findRecords`: each becomes a property of the script and
  a fill in the attaching record.
- **Mirror** a game quest with `readRecord` where one is like yours (`DLC1VQFactions` starts with the game and fills
  its script's properties); a game script's source is the `.psc` of its name among the game's sources.
- **Build**: a Papyrus error names its file, line and column — fix it there and build again. A warning on a script is
  a mod that builds and does nothing in game: fix it too.
- **Done** when the build answers success with no warning on the script, and each property is filled.
