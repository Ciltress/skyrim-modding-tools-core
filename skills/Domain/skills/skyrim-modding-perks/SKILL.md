---
name: skyrim-modding-perks
description: Perks in Skyrim SE - what a perk changes (its entry points - damage, spell cost, prices), its conditions (a weapon kind, sneaking, a skill level), its ranks and how the player gets it. Use when a mod should change how the player fights, casts, sneaks or trades - bows dealing more damage, cheaper spells - or when a perk does nothing in game.
---

# Perks

Read [`REFERENCE.md`](REFERENCE.md) for the entry points and the tabs of their conditions, the conditions a perk
uses most, the perk's other fields and the typical bugs. Fundamentals are in `skyrim-modding-basics`; a script that
gives a perk in `skyrim-modding-quests-and-scripts`.

## How a perk works

A perk (`PERK`) changes a number the game works out — the damage of an attack, a spell's cost, a price — at an
**entry point**, with a `Modification`: `Multiply` by its `Value` (1.2 is 20% more, 0.8 20% less), `Add` it, or `Set`
the number to it. Each effect has **conditions** in tabs: the first, tab 0, on the perk's owner; the others on what
the entry point works with — for an attack's damage, tab 1 the weapon and tab 2 the target.

- `Conditions` at the perk's margin are what the player needs to take it — a skill's level.
- `Playable: True` lets the player have it; `NumRanks: 1` for a perk of one rank.
- **The player gets a perk** from a skill's tree — the Creation Kit's work: the tree is the skill's
  ActorValueInformation record, Archery's `AVMarksman` — or from a script, `Game.GetPlayer().AddPerk(...)` in a quest
  started with the game.

## Bows deal 30% more damage while sneaking, from Archery 30

`Perks/SteadyAim.yaml` in the plugin folder:

```yaml
EditorID: SteadyAim
Name:
  TargetLanguage: English
  Value: Steady Aim
Description:
  TargetLanguage: English
  Value: Bows do 30% more damage while you sneak.
Conditions:
- MutagenObjectType: ConditionFloat
  CompareOperator: GreaterThanOrEqualTo
  Data:
    MutagenObjectType: GetBaseActorValueConditionData
    ActorValue: Archery
  ComparisonValue: 30
NumRanks: 1
Playable: True
Effects:
- MutagenObjectType: PerkEntryPointModifyValue
  Conditions:
  - Conditions:
    - MutagenObjectType: ConditionFloat
      Data:
        MutagenObjectType: IsSneakingConditionData
      ComparisonValue: 1
  - RunOnTabIndex: 1
    Conditions:
    - MutagenObjectType: ConditionFloat
      Data:
        MutagenObjectType: HasKeywordConditionData
        Keyword: WeapTypeBow
      ComparisonValue: 1
  EntryPoint: ModAttackDamage
  PerkConditionTabCount: 3
  Modification: Multiply
  Value: 1.3
```

- The first tab has no `RunOnTabIndex` line: 0 is the default. A condition's `CompareOperator` is `EqualTo` unless
  written, and `ComparisonValue: 1` with it means true.
- A record in a condition is named by its editor ID or its FormID: `Keyword: WeapTypeBow`, `ActorValue: Archery`.
- `Overdraw00`, Archery's first perk, is the game's perk most like it: readRecord it to see one.

## Workflow

- **Look up** a game perk like yours with findRecords (`types: ["PERK"]`) and readRecord it: its entry point, its tabs
  and its conditions are the lines to mirror.
- **Write** the perk as a new file in `Perks/`, leaving the game's perks alone: a copy of a game perk changes it for
  everyone who has it.
- **Give** it to the player where the user asked for it to be had: a script's AddPerk (the quests-and-scripts skill),
  or say that its place in a skill tree is made in the Creation Kit.
- **Done** when the build answers success, the perk is Playable, and its Value says what the user asked: 1.2 for 20% more.
