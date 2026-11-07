---
name: skyrim-modding-settings
description: Game settings, globals and keywords in Skyrim SE - the engine's numbers by name (jump height, fall damage, prices, the armor cap, trainings per level), global variables (time scale, the values scripts and conditions read), and the keywords the game checks on items (which merchant buys it, its material, silver). Use when a mod should change how the game plays rather than add an item - jump higher, time pass slower, more trainings - or should make an item count as another kind.
---

# Game settings, globals and keywords

Fundamentals — projects, overrides, the build — are in `skyrim-modding-basics`. Each of these is changed the way
any game record is: copy it into the project with addPatch, change the copy with editRecord, build.

## Game settings (GMST): the engine's numbers

A game setting is a value the engine reads by its name. Its first letter is its kind — `f` a decimal number, `i` a
whole one, `b` true or false, `s` a text — and its file's first line says the same, `MutagenObjectType:
GameSettingFloat`; the build corrects a kind that does not fit the name. A setting's file is three lines:

```yaml
MutagenObjectType: GameSettingFloat
EditorID: fJumpHeightMin
Data: 76
```

To make the player jump twice as high: `addPatch {"project": "HigherJump", "records": ["fJumpHeightMin"]}`, then
`editRecord` of the file addPatch names, `{"path": "Data", "value": 152}`, then build.

| Setting | The game's value | What it is |
|---|---|---|
| `fJumpHeightMin` | 76 | how high the player jumps |
| `fJumpFallHeightMin` | 600 | how far one falls before it hurts, in units |
| `fSprintStaminaDrainMult` | 7 | how fast sprinting uses stamina |
| `fBarterMin`, `fBarterMax` | 2, 3.3 | the merchants' price factor at the best Speech and at the worst |
| `fMaxArmorRating` | 80 | the most damage armor can stop, in percent |
| `fSkillUseCurve` | 1.95 | how much more use each skill level needs |
| `iTrainingNumAllowedPerLevel` | 5 | trainings a level — a setting of the engine alone |

- **Find a setting** with `findRecords` and `types: ["GMST"]`, by a word of its name: `{"text": "Jump", "types":
  ["GMST"]}`.
- **A setting no plugin has:** the engine knows more settings than the game's plugins carry —
  `iTrainingNumAllowedPerLevel` is in none, and `findRecords` finds nothing. Write it as a new file
  `GameSettings/iTrainingNumAllowedPerLevel.yaml` — its kind by its first letter, `MutagenObjectType:
  GameSettingInt`, its `EditorID`, its `Data` — and the game reads it. The build checks the name against the game
  itself, and warns one the game knows nowhere: a misspelled name does nothing.
- **A save** reads the settings from the plugins at each load: a changed setting is there in a running game too.

## Globals (GLOB): values scripts and conditions read

A global is a named value the game's scripts and conditions read and set: `TimeScale` (20 — how many game minutes a
real minute is), `GameDaysPassed`, a quest's counters. Its file's first line is its kind — `GlobalShort`,
`GlobalInt`, `GlobalFloat` — kept as the game's is. Change one with addPatch and editRecord of `Data`, as a setting.

- **A save keeps each global's value.** A changed global reaches a new game; a game already running keeps its own.
  Tell the user so, and the console line that sets it there: `set timescale to 10`.
- **A new global** is read by nothing but the mod's own scripts and conditions that name it.

## Keywords (KYWD): what the game checks on a record

A keyword is a tag a record carries in its `Keywords:` list; perks, conditions, merchants and scripts check for it.
To make an item count as another kind, copy it in with addPatch and write the game's keyword into its copy's
`Keywords:` list, one line among the others: `- WeapMaterialSilver`. editRecord writes a list whole — `{"path":
"Keywords", "value": [...]}` with every keyword the copy has and the new one.

| Keyword | What checks it |
|---|---|
| `WeapMaterialSilver` | the player's `SilverPerk`: 20 more damage to undead, ghosts and werewolves |
| `WeapMaterialSteel`, `ArmorMaterialSteel`… | the smithing perk of that material, which doubles its improvement |
| `VendorItemWeapon`, `VendorItemArmor`, `VendorItemPotion`… | which merchants buy it |
| `ArmorHeavy`, `ArmorLight`, `ArmorHelmet`… | the armor perks — Juggernaut, Agile Defender, the matching set's |

- **Find a keyword** with `findRecords` and `types: ["KYWD"]`; readRecord of a game item like yours shows those it
  carries.
- **A new keyword** is checked by nothing until a perk, condition or script of the mod names it.
