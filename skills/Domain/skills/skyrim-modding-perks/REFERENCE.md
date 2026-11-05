# Perks — reference

## The perk record (PERK)

| Field | What it does |
|---|---|
| `Name`, `Description` | What the skill tree shows. |
| `Conditions` | What the player needs to take it: a skill's level, another perk (`HasPerkConditionData`). |
| `NumRanks`, `NextPerk` | Its ranks: a perk of several ranks is a perk for each, each naming the next as `NextPerk`. |
| `Playable` | `True` for one the player can have; `Hidden` keeps it out of the menu. |
| `Effects` | What it does, each an entry point, an ability or a quest's stage (below). |

## Effects

| Kind (`MutagenObjectType`) | Does |
|---|---|
| `PerkEntryPointModifyValue` | changes a number at an `EntryPoint`, by `Modification` (`Multiply`, `Add`, `Set`) and `Value` |
| `PerkAbilityEffect` | gives the owner an ability, `Ability:` a spell of type Ability, while it has the perk |

Each effect's `Conditions` are tabs, each `- RunOnTabIndex: <n>` with its `Conditions` below it (tab 0 written without
the line); `PerkConditionTabCount` is how many tabs the entry point has.

## Entry points most used, and their tabs

| EntryPoint | The number | Tab 0 | Tab 1 | Tab 2 |
|---|---|---|---|---|
| `ModAttackDamage` | the damage of the owner's attacks | the owner | the weapon | the target |
| `ModIncomingDamage` | the damage the owner takes | the owner | the attacker | the attacker's weapon |
| `ModSpellMagnitude` | the magnitude of the owner's spells | the owner | the spell | the target |
| `ModSpellCost` | the cost of the owner's spells | the owner | the spell | |
| `ModSpellDuration` | the duration of the owner's spells | the owner | the spell | the target |
| `ModArmorWeight` | the weight of armor the owner wears | the owner | the armor | |
| `ModBuyPrices`, `ModSellPrices` | the prices the owner buys and sells at (`Multiply 0.9` buys 10% cheaper) | the owner | the merchant | |
| `ModSneakAttackMult` | the multiple of a sneak attack's damage | the owner | the weapon | the target |
| `ModPowerAttackDamage` | the damage of a power attack | the owner | the weapon | the target |

The tab counts are the game's perks' own — Armsman, Haggling, Conditioning, Backstab, Augmented Flames. For another
entry point, readRecord a game perk of it (findRecords `types: ["PERK"]`) and mirror its tabs.

## Conditions

Each is `- MutagenObjectType: ConditionFloat` with its `Data` — the function, named `<Function>ConditionData` — and a
`ComparisonValue`, compared by `CompareOperator` (`EqualTo` unless written; `GreaterThanOrEqualTo`, `LessThan` …).

| Asks | Data | On |
|---|---|---|
| Sneaking | `IsSneakingConditionData` | tab 0, the owner |
| A weapon kind | `HasKeywordConditionData`, `Keyword: WeapTypeBow` (`WeapTypeSword`, `WeapTypeDagger`, `WeapTypeGreatsword` …) | the weapon's tab |
| A skill's level | `GetBaseActorValueConditionData`, `ActorValue: Archery` (`OneHanded`, `Destruction`, `Sneak` …) | the perk's own Conditions, with `GreaterThanOrEqualTo` |
| A perk the owner has | `HasPerkConditionData`, `Perk: Overdraw00` | the perk's own Conditions |
| A spell's school | `HasKeywordConditionData`, `Keyword: MagicSchoolDestruction` | the spell's tab |

## Typical bugs

- **It does less, not more:** `Multiply` with `Value: 0.2` is a fifth of the damage; 20% more is `1.2`. `Add` adds to
  the number itself: `Add 20` to damage is twenty points.
- **It works everywhere, or never:** a condition on the wrong tab — a weapon's keyword asked of the owner on tab 0 is
  never true; a perk with no weapon condition changes every weapon.
- **The player never gets it:** in no skill tree and given by no script; or `Playable` left out.
- **Every perk of the game's changed:** a copy of a game perk (addPatch) where a new perk was meant.
