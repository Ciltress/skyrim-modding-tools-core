# LOOT sorts through libloot, built for Windows and Linux

Sorting a load order the way modders trust means LOOT's: its sorter, its masterlist of rules and messages for thousands
of plugins, the user's own rules from LOOT. Writing a sorter of our own from the masterlist would give a second order
users compare with LOOT's and find different. Every Mod Organizer install ships LOOT's command-line sorter, lootcli,
with libloot 0.23; it runs only on Windows, only under Mod Organizer, and outside Mod Organizer's virtual file system it
sees no mod's plugins (a dry run on the test instance warned that SKSE's scripts were missing: they are a mod's).
Amethyst, on Linux, has no sorter at all.

libloot itself — LOOT's library, rewritten in Rust in 0.28 — has a Node.js wrapper in its repository, libloot-nodejs,
built with NAPI-RS for 64-bit Windows and Linux, but not published to npm. The core builds it from libloot's source at a
release tag (0.29.7) and publishes the two binaries as assets of a release of its own repository, `libloot-<version>`.
The first sort downloads the one for the system into the core's cache folder (Local AppData on Windows, the XDG cache
on Linux) and loads it only when its SHA-256 is the one pinned in `src/backend/skyrim/loot/libloot.ts`. The user picked
this route on 2026-11-08 over lootcli now and libloot later.

The mod manager's layers are given to libloot as additional data paths, the highest priority first — Mod Organizer's
overwrite, then each enabled mod — which libloot searches before the game's Data, as the game sees them. The load order
goes to libloot as a plugins.txt the core writes in asterisk form into a folder of its own: libloot never reads or
writes the manager's files. A sort is a preview; `apply` writes the order into plugins.txt and loadorder.txt, each
file's lines and active marks kept.

## Consequences

- libloot is GPL-3.0-or-later, the core GPL-3.0-only: the binaries are libloot's, unmodified, with its source a link
  away (THIRD_PARTY_NOTICES.md).
- A new libloot is a new build of both binaries, a new release and new pins; the masterlist branch follows libloot's
  minor version (`v0.29`).
- The wrapper is marked experimental by its authors; the core uses its game, database and plugin calls only.
- Sorting needs the network once for the library and once a day for the masterlist; offline, the last masterlist is used.
