---
name: skyrim-modding-conflicts
description: Mod conflicts in Skyrim SE - two plugins changing one record, or two mods giving the game one file, and which one the game takes. Found with findConflicts, settled by a merged patch (addPatch with merge) or, on the user's word, by the load order or the mod order (movePlugin, setModPriority). Use when the user asks whether mods conflict, why one mod's change is missing in game, why a texture or mesh is not the one installed, or asks for a conflict patch.
---

# Mods in conflict

Two plugins that change one record: the game takes the version of the plugin that loads **last**, whole, and every
change the other made is lost. Two mods that give the game one file: the mod **later in the mod order** wins for
loose files; files in archives (BSAs) follow the load order of their plugins, and a loose file beats every archive.
`findConflicts` finds both; nothing it does changes the setup.

## Workflow

0. **Find.** `findConflicts` — for the whole setup with no arguments, or of the mod or plugin the user named
   (`mod`, `plugin`), or of one record (`record`). Done when you have its answer.
1. **Weigh.** Done when you can say, for each conflict, which mod or plugin wins and what the other loses:
   - `records`: each record, its `winner`, and under `lost` each plugin's changes the winner does not carry —
     `Keywords + WeapMaterialSilver` (an item its list gained), `BasicStats.Damage: 12 (the winner has 9)` (a value).
     A record marked `clash` has a field both plugins changed, each its own way: which value is meant is the user's
     choice.
   - `files`: each pair of mods, the `winner` and the `loser`, how many files and a few of them, and `how` it wins.
   - A plugin that names the other as a master saw it and chose — a patch: findConflicts lists no such pair.
   - Many conflicts are meant: a texture mod replaces another's on purpose, a patch overrides the mods it patches.
     The lost changes of a mod the user cares about are the ones to tell.
2. **Tell the user** what each conflict costs in game — the DLC's keyword a location lost, the sword's new damage
   another mod set back — and the two ways out. Ask before any change. Done when the user chose:
   - **A patch keeps both** — for records. It is a project of its own, loading last.
   - **The order picks one** — `movePlugin` for records, `setModPriority` for loose files.
3. **Patch, for records:** `addPatch {"project": "ConflictPatch", "records": [the records], "merge": true}` copies
   each record from its winner and puts back every lost change; `added[].merged` names what was put back, and
   `clashes` each field two plugins changed, where the later one's is kept — change it with editRecord where the user
   chose the other. Then `buildProject`. Done when the build answers `success`; `deployProject` on the user's word.
4. **Order, on the user's yes:** `movePlugin {"plugin": "A.esp", "after": "B.esp"}` makes A's records win over B's;
   `setModPriority {"mod": "A", "after": "B"}` makes A's loose files win over B's. A move the game would not load
   — a plugin before its master, a plain plugin among masters — is refused with why. Done when the tool answered.

## Rules of thumb

- **Leveled lists, containers, keywords** merge well: a patch carries every mod's additions. A record whose fields
  two mods set differently has no merge — one value wins; say which.
- **Never edit another mod's plugin.** The patch is the fix: it survives the other mod's update.
- **The game's own plugins** — Skyrim.esm, the DLCs, the Creation Club's — conflict with each other too (Dawnguard and
  Dragonborn on locations, perks, idles): the Unofficial Skyrim Special Edition Patch settles those. Suggest it
  where the user has none, rather than a patch of the game's own records.
- **After a move or a patch**, findConflicts again: a settled conflict is gone from it — for a patch, once it is
  deployed and in the load order.
