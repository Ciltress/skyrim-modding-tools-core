---
name: skyrim-modding-items-and-crafting
description: Weapons, armor, ammo, potions, food, poisons, ingredients, books, misc items, soul gems, keys, and crafting, tempering, smelting and cooking recipes in Skyrim SE. Use when creating or changing an item or recipe, or diagnosing one - invisible armor, a recipe missing at the workbench, an item that perks or merchants ignore.
---

# Items and crafting

Read [`REFERENCE.md`](REFERENCE.md) before touching an item record: it covers the item record types,
the keywords gameplay hangs on, armor and its addons, recipes, how an item reaches the player, and
the typical bugs. Fundamentals — FormIDs, overrides, patches — are in the `skyrim-modding-basics`
skill.

## Workflow

The loop in `skyrim-modding-basics` — look up, get the text, edit, build, deploy — with these for items:

- **Look up** with `types` from `WEAP`, `ARMO`, `ARMA`, `AMMO`, `ALCH`, `INGR`, `BOOK`, `MISC`, `COBJ`.
  An item's recipes are records of their own that point at it: `findRecords` with the item's editor ID
  as `text` and `types: ["COBJ"]` finds the crafting and the tempering recipe.
- **A new weapon or armor** starts from `readRecord` on a vanilla one of the same kind, and for armor
  on the addon its `Armature` lists too. A new record that points at another new one — the armor at
  its addon, a recipe at its item, an enchanted weapon at its enchantment — names it by its editor ID;
  one build gives both their FormIDs and writes the link. New meshes and textures go into `assets/`.
- **A key** is a `Key` record (`KEYM`, in `Keys/`), from a vanilla one like `AngasMillAeriHouseKey`. What it opens is
  no item: a chest is a Container (`CONT`, in `Containers/`) — never a recipe (`COBJ`) or a misc item — and its lock
  is the reference's that places it in a cell, `Lock:` with `Level:` and `Key:` below it. Both are in the
  `skyrim-modding-world` skill: read it.
- **Done** when the item is built with every keyword its kind needs (REFERENCE, *Keywords carry the
  gameplay*) and a route brings it to the player (REFERENCE, *How an item reaches the player*).
- **A keyword the game checks** — a weapon that counts as silver, an item a merchant buys — is one line in its
  `Keywords:` list, by editor ID: `- WeapMaterialSilver`, never a FormID from memory. Which keyword does what:
  `skyrim-modding-settings`.
- **Diagnosing**: `readRecord` with `allVersions: true` on the item and on its recipes, then match the
  symptom in REFERENCE, *Typical bugs*.

## A new weapon, as it builds

A war axe and its forge recipe: two files in the project's plugin folder, mirroring `IronWarAxe` and
`RecipeWeaponIronWarAxe`. Each link is copied from the record mirrored: the editor ID `readRecord`
shows beside each of its FormIDs — keywords, sounds, impact set, materials. The recipe's
`CreatedObject` is the new axe's own editor ID. The build writes all their FormIDs. Neither file has a
`FormKey` line: the build gives each new record its own.

```yaml
# <project>/plugin/Weapons/AshenHatchet.yaml
EditorID: AshenHatchet
Name:
  TargetLanguage: English
  Value: Ashen Hatchet
Model:
  File: Weapons\Iron\WarAxe.nif
EquipmentType: RightHand
Keywords:
- WeapMaterialIron
- WeapTypeWarAxe
- VendorItemWeapon
ImpactDataSet: WPNzAxeImpactSet
FirstPersonModel: 1stPersonIronWarAxe
EquipSound: WPNAxe1HandDrawSD
UnequipSound: WPNAxe1HandSheatheSD
BasicStats:
  Value: 45
  Weight: 13
  Damage: 9
Data:
  AnimationType: OneHandAxe
  Speed: 0.9
  Reach: 1
  Skill: OneHanded
  Stagger: 0.85
Critical:
  Damage: 4
  PercentMult: 1

# <project>/plugin/ConstructibleObjects/RecipeAshenHatchet.yaml
EditorID: RecipeAshenHatchet
Items:
- Item:
    Item: IngotIron
    Count: 2
- Item:
    Item: LeatherStrips
    Count: 1
CreatedObject: AshenHatchet
WorkbenchKeyword: CraftingSmithingForge
CreatedObjectCount: 1
```

Another kind of weapon mirrors the iron one of its kind the same way. Its lines that differ by kind —
Skyrim.esm's records, in every load order, so written as they are, with no search:

| Kind | Mirror | `AnimationType` | `WeapType…` keyword | `EquipmentType` | `ImpactDataSet` |
|---|---|---|---|---|---|
| Sword | `IronSword` | `OneHandSword` | `WeapTypeSword` | `RightHand` | `WPNzBlade1HandImpactSet` |
| Dagger | `IronDagger` | `OneHandDagger` | `WeapTypeDagger` | `RightHand` | `WPNzBlade1HandImpactSet` |
| War axe | `IronWarAxe` | `OneHandAxe` | `WeapTypeWarAxe` | `RightHand` | `WPNzAxeImpactSet` |
| Mace | `IronMace` | `OneHandMace` | `WeapTypeMace` | `RightHand` | `WPNzBluntImpactSet` |
| Greatsword | `IronGreatsword` | `TwoHandSword` | `WeapTypeGreatsword` | `BothHands` | `WPNzBlade2HandImpactSet` |
| Battleaxe | `IronBattleaxe` | `TwoHandAxe` | `WeapTypeBattleaxe` | `BothHands` | `WPNzAxeLargeImpactSet` |
| Warhammer | `IronWarhammer` | `TwoHandAxe` | `WeapTypeWarhammer` | `BothHands` | `WPNzBlunt2HandImpactSet` |
| Bow | `LongBow` | `Bow` | `WeapTypeBow` | `BothHands` | `WPNzArrowImpactSet` |

Its `Model`, `FirstPersonModel`, `EquipSound` and `UnequipSound` are the mirrored weapon's, as
`readRecord` shows them; `Data`'s `Skill` is `OneHanded`, `TwoHanded` or `Archery`.

## An enchanted weapon

A weapon names its enchantment as `ObjectEffect`, and holds its charge as `EnchantmentAmount` — 500
on the vanilla `EnchIronSwordFire01`. The enchantment is a vanilla one, named as it is, or a new one
in `ObjectEffects/` that names the vanilla effect of its element — both written as the table has
them, with no search:

| On hit | Vanilla enchantment | Its `BaseEffect` | Its `Data` |
|---|---|---|---|
| Fire damage | `EnchWeaponFireDamage01` | `EnchFireDamageFFContact` | Magnitude 5 |
| Frost damage | `EnchWeaponFrostDamage01` | `EnchFrostDamageFFContact` | Magnitude 5 |
| Shock damage | `EnchWeaponShockDamage01` | `EnchShockDamageFFContact` | Magnitude 5, Duration 1 |
| Absorb health | `EnchWeaponAbsorbHealth02` | `EnchAbsorbHealthFFContact` | Magnitude 5, Duration 1 |
| Absorb magicka | `EnchWeaponAbsorbMagicka02` | `EnchAbsorbMagickaFFContact` | Magnitude 10, Duration 1 |
| Absorb stamina | `EnchWeaponAbsorbStamina02` | `EnchAbsorbStaminaFFContact` | Magnitude 10, Duration 1 |
| Magicka damage | `EnchWeaponMagickaDamage01` | `EnchMagickaDamageFFContact` | Magnitude 10 |
| Stamina damage | `EnchWeaponStaminaDamage01` | `EnchStaminaDamageFFContact` | Magnitude 5 |
| Paralysis | `EnchWeaponParalysis04` | `EnchParalysisFFContact` | Duration 2 |
| Soul trap | `EnchWeaponSoulTrap01` | `EnchSoulTrapFFContact` | Duration 3 |
| Fear | `EnchWeaponFear01` | `EnchInfluenceConfDownFFContactLow` | Magnitude 5, Duration 30 |
| Turn undead | `EnchWeaponTurnUndead01` | `EnchTurnUndeadFFContact` | Magnitude 3, Duration 30 |
| Banish | `EnchWeaponBanish04` | `EnchBanishFFContact` | Magnitude 12 |

A new frost enchantment, and the weapon's two lines that carry it:

```yaml
# <project>/plugin/ObjectEffects/EnchRimeEdge.yaml
EditorID: EnchRimeEdge
Name:
  TargetLanguage: English
  Value: Rime Edge
CastType: FireAndForget
TargetType: Touch
EnchantType: Enchantment
Effects:
- BaseEffect: EnchFrostDamageFFContact
  Data:
    Magnitude: 10

# in the weapon's file
ObjectEffect: EnchRimeEdge
EnchantmentAmount: 500
```

A weapon's enchantment is cast `FireAndForget` on `Touch`, as its effects are; the build refuses one
cast otherwise. A vanilla enchanted weapon such as `EnchIronSwordFire01` has a `Template` — the plain
weapon — and carries that weapon's `Data`, keywords, `Model` and `EquipmentType` itself, as every
official weapon with a `Template` does; the build refuses one without them.

## Armor and clothing

A new piece mirrors the vanilla one of its kind: its slot under `BodyTemplate`, its keywords, its
addon as `Armature`, its `Model`. `ArmorType` is `HeavyArmor`, `Clothing`, or left out for light armor
— `LightArmor` is its default, so `readRecord` shows no line for it. The type keyword agrees with it:
`ArmorHeavy`, `ArmorLight` or `ArmorClothing`. Skyrim.esm's records, written as they are, with no
search:

| Piece | Heavy: mirror | Its `Armature` | Light: mirror | Its `Armature` | `FirstPersonFlags` | Slot keyword |
|---|---|---|---|---|---|---|
| Cuirass | `ArmorIronCuirass` | `IronCuirassAA` | `ArmorLeatherCuirass` | `FullLeatherCuirassAA` | `Body` | `ArmorCuirass` |
| Boots | `ArmorIronBoots` | `IronBootsAA` | `ArmorLeatherBoots` | `FullLeatherBootsAA` | `Feet` | `ArmorBoots` |
| Gauntlets | `ArmorIronGauntlets` | `IronGlovesAA` | `ArmorLeatherGauntlets` | `FullLeatherGlovesAA` | `Hands` | `ArmorGauntlets` |
| Helmet | `ArmorIronHelmet` | `IronHelmetAA` and its race variants | `ArmorLeatherHelmet` | `FullLeatherHelmetAA` and its race variants | `Hair`, `Circlet` | `ArmorHelmet` |
| Shield | `ArmorIronShield` | `IronShieldAA` | `ArmorHideShield` | `HideShieldAA` | `Shield` | `ArmorShield` |

The material keyword is the mirror's — `ArmorMaterialIron`, `ArmorMaterialLeather`, `ArmorMaterialHide`
— and every piece has `VendorItemArmor`. A shield has `EquipmentType: Shield` besides. Clothing mirrors
`ClothesFarmClothes01` (a tunic: `Body`, `ArmorClothing`, `ClothingBody`, `VendorItemClothing`, addon
`FarmerTorso01AA`) or `ClothesFarmBoots01` (`Feet`, `ClothingFeet`, addon `FarmerBoots01AA`). An armor
with a `TemplateArmor` carries the template's `Armature`, keywords and `BodyTemplate` itself.

An armor's enchantment is cast `ConstantEffect` on `Self` — both defaults, so its file needs neither
line — and names its effect as the vanilla one does; the armor names it as `ObjectEffect`:

| While worn | Vanilla enchantment | Its `BaseEffect` | Magnitude |
|---|---|---|---|
| Fortify health | `EnchArmorFortifyHealth01` | `EnchFortifyHealthConstantSelf` | 20 |
| Fortify magicka | `EnchArmorFortifyMagicka01` | `EnchFortifyMagickaConstantSelf` | 20 |
| Fortify stamina | `EnchArmorFortifyStamina01` | `EnchFortifyStaminaConstantSelf` | 20 |
| Fortify health regen | `EnchArmorFortifyHealRate01` | `EnchFortifyHealRateConstantSelf` | 10 |
| Fortify one-handed | `EnchArmorFortifyOneHanded01` | `EnchFortifyOneHandedConstantSelf` | 15 |
| Fortify two-handed | `EnchArmorFortifyTwoHanded01` | `EnchFortifyTwoHandedConstantSelf` | 15 |
| Fortify archery | `EnchArmorFortifyMarksman01` | `EnchFortifyArcheryConstantSelf` | 15 |
| Fortify block | `EnchArmorFortifyBlock01` | `EnchFortifyBlockConstantSelf` | 15 |
| Fortify heavy armor | `EnchArmorFortifyHeavyArmor01` | `EnchFortifyHeavyArmorConstantSelf` | 12 |
| Fortify light armor | `EnchArmorFortifyLightArmor01` | `EnchFortifyLightArmorConstantSelf` | 12 |
| Fortify sneak | `EnchArmorFortifySneak01` | `EnchFortifySneakConstantSelf` | 15 |
| Fortify destruction | `EnchArmorFortifyDestruction01` | `EnchFortifyDestructionConstantSelf` | 12 |
| Fortify restoration | `EnchArmorFortifyRestoration01` | `EnchFortifyRestorationConstantSelf` | 12 |
| Fortify carry weight | `EnchArmorFortifyCarry01` | `EnchFortifyCarryConstantSelf` | 25 |
| Resist fire | `EnchArmorResistFire01` | `EnchResistFireConstantSelf` | 15 |
| Resist frost | `EnchArmorResistFrost01` | `EnchResistFrostConstantSelf` | 15 |
| Resist shock | `EnchArmorResistShock01` | `EnchResistShocktConstantSelf` | 15 |
| Resist magic | `EnchArmorResistMagic01` | `EnchResistMagicConstantSelf` | 10 |

`EnchResistShocktConstantSelf` is spelled as Skyrim.esm spells it.

## Potions

A potion is an `Ingestible` in `Ingestibles/`, mirroring the vanilla one of its kind: its effect and
`Data`, `Keywords: [VendorItemPotion]`, its `Model`, `PickUpSound: ITMPotionUpSD`, `PutDownSound:
ITMPotionDownSD`, `Weight: 0.5`. Skyrim.esm's records, written as they are:

| Potion of | Mirror | `BaseEffect` | `Data` |
|---|---|---|---|
| Restore health | `RestoreHealth01` | `AlchRestoreHealth` | Magnitude 25 |
| Restore magicka | `RestoreMagicka01` | `AlchRestoreMagicka` | Magnitude 25 |
| Restore stamina | `RestoreStamina01` | `AlchRestoreStamina` | Magnitude 25 |
| Fortify health | `FortifyHealth01` | `AlchFortifyHealth` | Magnitude 20, Duration 60 |
| Fortify one-handed | `FortifySkillOneHanded01` | `AlchFortifyOneHanded` | Magnitude 20, Duration 60 |
| Resist fire | `ResistFire25` | `AlchResistFire` | Magnitude 20, Duration 60 |
| Cure disease | `CureDisease` | `AlchCureDisease` | Magnitude 100 |
| Invisibility | `Invisibility01` | `AlchInvisibillity` | Duration 20 |

## Food, poisons, ingredients and the small items

Each is a new file in its type's folder, mirroring a vanilla record of its kind — `readRecord` it, copy its `Model`,
sounds and keywords, then write the name, numbers and effects asked for. Effects are written as a potion's (above): a
`BaseEffect` and its `Data`.

| A new … | Folder | Mirror | What makes it one |
|---|---|---|---|
| food | `Ingestibles/` | `FoodSweetroll`, `FoodApple` | `Flags` with `FoodItem`; `ConsumeSound` an eating one (`ITMFoodEat`), not a potion's; keyword `VendorItemFood`; effects as `FoodRestoreHealth`-like restores |
| poison | `Ingestibles/` | `DamageHealth01` ("Weak Poison") | `Flags` with `Poison`; `ConsumeSound: ITMPoisonUse`; keyword `VendorItemPoison`; harmful effects — a damage effect flagged `Detrimental`, named by its editor ID: health `AlchDamageHealth` (Weak Poison's own), frost `EnchFrostDamageFFContact`, fire `EnchFireDamageFFContact`, shock `EnchShockDamageFFContact` — the effects the game's frost, fire and shock weapons strike with, resisted by `ResistFrost`, `ResistFire`, `ResistShock`; the damage is the effect's `Magnitude` |
| ingredient | `Ingredients/` | `FrostSalts`, `GlowingMushroom` | Up to four `Effects`, each an ingredient's (`Alch…` effects); `IngredientValue`; keyword `VendorItemIngredient` |
| misc item | `MiscItems/` | a vanilla clutter or gem item | `Value`, `Weight`, a `Model` the game has; a `VendorItem…` keyword (`VendorItemClutter`, `VendorItemGems`, `VendorItemJewelry`) so merchants buy it |
| soul gem | `SoulGems/` | `SoulGemGrand` (empty), `SoulGemGrandFilled` | `MaximumCapacity` (`Petty` … `Grand`, `Black` holds black souls), an empty one without a `ContainedSoul`; keyword `VendorItemSoulGem` |
| key | `Keys/` | a vanilla house key (`AngasMillAeriHouseKey`) | `Model`, `PickUpSound: ITMKeyUpSD`; keyword `VendorItemKey`. It opens what names it in its `Lock`'s `Key` — a placed door or container (`skyrim-modding-world`) |

A light the player carries — a torch, a lantern — is a `Light` in `Lights/` flagged `CanBeCarried`, mirroring
`Torch01`; one placed only to light a room is a `Light` too, placed as a reference (`skyrim-modding-world`).

## Books and notes

A note or a letter is a `Book` too, in `Books/`, mirroring a vanilla one of its kind — Skyrim.esm's,
`readRecord` by these names:

| A new … | Mirror | `Model` `File` | `InventoryArt` | Also |
|---|---|---|---|---|
| note, letter | `LetterRiftenBlackBriarMeadery04` ("Note From Maven") | `Clutter\Books\Note03.nif` | `HighPolyNote03` | `PickUpSound: ITMNoteUp` |
| book | `Book3ValuableLustyArgonianMaidVol01` | `Clutter\Books\BasicBook07.nif` | `HighPolyBasicBook07` | `Value`, `Weight` |

Both carry `Keywords` with `VendorItemBook`, and `Teaches` with `MutagenObjectType: BookTeachesNothing`.
The text is `BookText`'s `Value`, a block under `Value: |-` whose lines are the text's own, an empty one too:

- A text taken from another book is copied character for character, as `readRecord` shows it: its
  markup — `<font face="$HandwrittenFont">` makes a note handwritten — its words, its empty lines.
- Every line of the text sits four spaces deep, as deep as its first; the record's next field,
  `PickUpSound:` or `Keywords:`, starts at the margin again.

## Recipes: stations, materials, perks

A recipe's `WorkbenchKeyword` is its station; its `Items` the materials; a recipe for a higher
material is shown only with its smithing perk, a condition the mirrored recipe carries. All
Skyrim.esm's records:

| Station | `WorkbenchKeyword` |
|---|---|
| Forge: a new item | `CraftingSmithingForge` |
| Grindstone: tempering a weapon | `CraftingSmithingSharpeningWheel` |
| Workbench: tempering armor | `CraftingSmithingArmorTable` |
| Smelter | `CraftingSmelter` |
| Tanning rack | `CraftingTanningRack` |
| Cooking pot | `CraftingCookpot` |

| Material | Editor ID |
|---|---|
| Iron, steel ingot | `IngotIron`, `IngotSteel` |
| Corundum, orichalcum ingot | `IngotCorundum`, `IngotOrichalcum` |
| Refined moonstone, refined malachite | `IngotIMoonstone`, `IngotMalachite` |
| Quicksilver, ebony, dwarven metal ingot | `IngotQuicksilver`, `IngotEbony`, `IngotDwarven` |
| Leather, leather strips | `Leather01`, `LeatherStrips` |
| Dragon bone, dragon scales | `DragonBone`, `DragonScales` |

The smithing perks are `SteelSmithing`, `ElvenSmithing`, `AdvancedArmors`, `OrcishSmithing`,
`DwarvenSmithing`, `GlassSmithing`, `EbonySmithing`, `DaedricSmithing` and `DragonArmor`;
`ArcaneBlacksmith` lets enchanted items be tempered.

A perk a recipe needs is a condition, as `RecipeWeaponSteelSword` carries it — no `RequiredPerk:`
field. With no `CompareOperator` line it is `EqualTo`: shown when the player has the perk.

```yaml
Conditions:
- MutagenObjectType: ConditionFloat
  Data:
    MutagenObjectType: HasPerkConditionData
    Perk: SteelSmithing
  ComparisonValue: 1
```

A recipe for an item the game has already — a forge recipe for its Forsworn Axe — names that item
as its `CreatedObject` by its editor ID (`ForswornAxe`, found by its in-game name with
`findRecords`; an editor ID is often not the name), and is the mod's only file: no weapon file is
written for it, or the forge makes the copy, not the game's item.

A tempering recipe mirrors `TemperWeaponIronSword`: one ingot of the item's material at the
grindstone (a weapon) or the workbench (armor), the item as `CreatedObject`, and its conditions as
they are.
