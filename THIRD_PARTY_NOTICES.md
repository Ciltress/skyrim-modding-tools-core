# Third-party notices

## Phostwood's Skyrim Crash Log Analyzer

The crash rules in `src/backend/skyrim/crash/rules/` are a TypeScript rewrite of the diagnoses of Phostwood's
Skyrim Crash Log Analyzer — `js/diagnoses.js`, the rules written inline in `js/analyzeLog.js`, and the lists in
`js/crashIndicatorsMap.js`, `js/utils.js`, `js/antivirusSignatures.js` and `js/overlaySignatures.js` — as of its
commit of 2026-05-29.

- Source: https://github.com/Phostwood/crash-analyzer (web app: https://phostwood.github.io/crash-analyzer/skyrim.html)
- Author: Phostwood, with the contributors named there
- License: GNU General Public License v3.0, the license of this package too (GPL-3.0-only, docs/adr/0006)

Changed in the rewrite (docs/adr/0011): each rule is one entry with a test of its own; the text is plain and shorter;
the game's code addresses, which Phostwood gives as offsets into SkyrimSE.exe 1.6.1170, are kept as Address Library
IDs and found in each game version; rules for Nolvus only, for other crash loggers, those that go online, the
speculative insights and the DLL version table are left out.

## Address Library for SKSE Plugins

`src/backend/skyrim/crash/gameAddresses.ts` holds offsets read from meh321's Address Library for SKSE Plugins
(Nexus mod 32444, version 13) for the game versions it lists. No file of it is included.

## Crash Logger SSE

The crash-log parser reads the format of alandtse's Crash Logger SSE (Nexus mod 59818,
https://github.com/alandtse/CrashLoggerSSE). The logs in `src/__tests__/fixtures/crash-logs/` are its output, from
the author's own game, with the user and hardware names removed (scripts/scrub-crash-log.mjs); the one named
`synthetic-…` was written by hand in its 1.27 format.

## libloot

`sortPlugins` sorts with libloot, LOOT's library, through its Node.js wrapper libloot-nodejs, version 0.29.7, unmodified:
built from its source (`nodejs/`, `npm run build`) for 64-bit Windows and Linux and published as the assets of this
repository's release `libloot-0.29.7`, which the core downloads when it first sorts (docs/adr/0013). The masterlist and
prelude it loads are LOOT's, fetched from their repositories when a sort runs. No file of either is included here.

- Source: https://github.com/loot/libloot (tag 0.29.7); masterlist https://github.com/loot/skyrimse, prelude https://github.com/loot/prelude
- Authors: Oliver Hamlet (WrinklyNinja) and LOOT's contributors
- License: GNU General Public License v3.0 or later (libloot); the masterlist and prelude are CC0 1.0 Universal
