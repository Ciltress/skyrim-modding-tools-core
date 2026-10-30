# Archives are read for attribution

A crash log names files — a DLL in the call stack, a mesh the game was loading — and the user needs the mod behind
each, to update or disable it. Loose files have one: the mod manager's layers say which mod's copy the game loads
(`resolveDataFiles`). But most mods ship their meshes and textures in archives (BSAs), and nothing read those, so a
crash in a mesh from an archive named no mod at all.

The helper reads the file lists of the archives the game loads, through Mutagen's archive reader (ADR 0003), when a
file the log names is loose in no mod: `bsaFiles` answers, for each file asked for, the last archive in the list that
holds it. The order of the list is the server's, as the game loads them: the archives of no active plugin first —
the game's own, which its INI lists — then each active plugin's (`X.bsa`, `X - Textures.bsa` for X.esp) in load
order. A loose file beats every archive.

## Consequences

- Archives are read only for files no mod has loose, and only their lists: a test instance's dozen take well under
  a second; a list of thousands takes longer, within the helper's two minutes.
- An archive the INI lists that belongs to a mod, not the game, is taken as the game's: it loads first either way,
  and few mods add to the INI's list.
- The conflicts part of the analysis wave reads archives the same way.
