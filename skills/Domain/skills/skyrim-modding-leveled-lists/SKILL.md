---
name: skyrim-modding-leveled-lists
description: Leveled item, NPC and spell lists in Skyrim SE - how the game picks from them, and why several mods editing one list conflict. Use when adding items, enemies or spells to the world, when loot, merchant stock or spawns look wrong, or when merging lists from several mods. A form list (FLST), records checked as a group, is no leveled list: skyrim-modding-settings.
---

# Leveled lists

Read [`REFERENCE.md`](REFERENCE.md) before touching a leveled list: it covers the three list types,
how a pick works, where lists are used, the conflict problem and its fixes, and the typical bugs.
Fundamentals — overrides, patches — are in the `skyrim-modding-basics` skill.

## Workflow

The loop in `skyrim-modding-basics` — look up, get the text, edit, build, deploy — with these for lists:

- **Look up** with `types` from `LVLI`, `LVLN`, `LVSP`. To find the list that hands out a kind of
  thing, search by its editor-ID words (`text: "LItemWeapon"`, `"LItemBandit"`) and read candidates.
  `LItem…` lists hold items, `LChar…` lists hold NPCs: an item never goes into an `LChar…` list.
- **What NPCs of a kind carry** comes from the leveled item list their template's `Items` name: add an
  item to that list, not to the NPCs — *Where a new record goes* below names the bandits'.
- **What NPCs of a kind cast** comes from the leveled spell list among their template's `ActorEffect`: bandit
  mages cast from `LSpellBandit01FireFrostShock` (and its `03`, `05` for stronger ones). Add the spell to that list,
  `addPatch` and an entry with its `Level`, as for items, and change no NPC.
- **How strong a place's enemies are** is no list's: a leveled NPC's level follows the player's within the cell's
  encounter zone, its `MinLevel` and `MaxLevel` — `skyrim-modding-world`, *Locations, encounter zones, regions*.
- **Adding to an existing list**: `addPatch` the list, then append your entries to the copy, every
  entry already in it kept. A new item or NPC goes into a list once a build has given it its FormID.
- **More of a kind the game has** — wolves that can be ice wolves, bandits that can be Orcs — is the game's NPC put into
  the list, not a new NPC: `findRecords` with types `["NPC_"]` and its name finds it (the Ice Wolf is `EncWolfIce`), and
  the list its kind comes from is named so (`LCharWolf`). A new NPC is written only where the user asks for one.
- **Other mods changing the same list**: `readRecord` with `allVersions: true` shows every version.
  Their additions belong in your copy too, or your override drops them (REFERENCE, *The conflict
  problem*).
- **Done** when built with the copy holding the winning version's entries, every other mod's
  additions you mean to keep, and yours.
- **Diagnosing**: `readRecord` with `allVersions: true` on the list and on any list nested in it, then
  match the symptom in REFERENCE, *Typical bugs*.

## Where a new record goes

The vanilla lists that hand out each kind of thing — as loot in chests and on enemies, by the
player's level, and in blacksmiths' stock. All are Skyrim.esm's records, spelled as it spells them
(`Litem…` in a few): `addPatch` them by these names, with no search.

| A new … | Loot | Blacksmiths' stock |
|---|---|---|
| sword | `LItemWeaponSword` | `LItemWeaponSwordBlacksmith` |
| dagger | `LItemWeaponDagger` | `LItemWeaponDaggerBlacksmith` |
| war axe | `LItemWeaponWarAxe` | `LItemWeaponWarAxeBlacksmith` |
| mace | `LItemWeaponMace` | `LItemWeaponMaceBlacksmith` |
| greatsword | `LItemWeaponGreatSword` | `LItemWeaponGreatSwordBlacksmith` |
| battleaxe | `LItemWeaponBattleAxe` | `LItemWeaponBattleAxeBlacksmith` |
| warhammer | `LItemWeaponWarhammer` | `LItemWeaponWarhammerBlacksmith` |
| bow | `LItemWeaponBow` | `LItemWeaponBowBlacksmith` |
| heavy cuirass, light cuirass | `LItemArmorCuirassHeavy`, `LItemArmorCuirassLight` | `LItemArmorCuirassHeavyBlacksmith`, `LItemArmorCuirassLightBlacksmith` |
| heavy boots, light boots | `LItemArmorBootsHeavy`, `LItemArmorBootsLight` | `LItemArmorBootsHeavyBlacksmith`, `LitemArmorBootsLightBlacksmith` |
| heavy gauntlets, light gauntlets | `LItemArmorGauntletsHeavy`, `LItemArmorGauntletsLight` | `LItemArmorGauntletsHeavyBlacksmith`, `LItemArmorGauntletsLightBlacksmith` |
| heavy helmet, light helmet | `LItemArmorHelmetHeavy`, `LItemArmorHelmetLight` | `LItemArmorHelmetHeavyBlacksmith`, `LItemArmorHelmetLightBlacksmith` |
| heavy shield, light shield | `LItemArmorShieldHeavy`, `LItemArmorShieldLight` | `LitemArmorShieldHeavyBlacksmith`, `LItemArmorShieldLightBlacksmith` |
| potion of healing, of magicka, of stamina | `LItemPotionRestoreHealth`, `LItemPotionRestoreMagicka`, `LItemPotionRestoreStamina` | |
| weapon bandits carry: one-handed, two-handed, bow | `LItemBanditWeapon1H`, `LItemBanditWeapon2H`, `LItemBanditWeaponBow` | |

A new bandit — an NPC, not an item — goes into the leveled NPC lists the bandits come from:
`LCharBanditMelee1H`, `LCharBanditMissile`, `LCharBanditWizard`. What bandits wear is in `LItemBandit…`
lists too (`LItemBanditCuirass`, `LItemBanditBoots`); another kind's are found with `text: "LItem<Kind>"`.

A spell tome goes into its school's list of its level — `LItemSpellTomes00Destruction` for a novice
Destruction tome, then `25`, `50`, `75`, `100` for apprentice to master, and likewise for
`Alteration`, `Conjuration`, `Illusion` and `Restoration`.

An entry names the new record by its editor ID and the lowest player level it can appear at, beside
every entry already in the list's copy:

```yaml
Entries:
- Data: { Level: 1, Reference: IronSword, Count: 1 }   # the entries already there, kept
- Data: { Level: 6, Reference: AshenBlade, Count: 1 }  # the new sword, from level 6
```
