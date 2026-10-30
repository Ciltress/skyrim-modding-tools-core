# Crash rules are Phostwood's, ported

A crash log says where the game crashed, not why. Knowing why takes rules: this address is a corrupt texture, that
DLL on the first line is an outdated driver, those two mods together crash the menu. Phostwood's Skyrim Crash Log
Analyzer has gathered about 115 such rules since 2023, from logs users posted. It is GPL-3.0, like this package
(ADR 0006). The user chose to port its rules over writing a small set of our own (the analysis wave's design,
2026-10-30).

Each rule is rewritten in TypeScript as one entry of `src/backend/skyrim/crash/rules/` with a test of its own, and
answers a `Finding`: a title, Phostwood's confidence (🎯 certain, ❗ likely, ❓ possible), what it means, the fix, and
the lines of the log it rests on. A rule reads the log through Phostwood's own views of it — the first line, the top
quarter, third and half, the bottom half — cut to his lengths, so a rule fires where his would.

`readCrashLog` ranks the findings by confidence, then by where their evidence is: the first line, the probable call
stack, the relevant objects, the stack scan (the user's order). It answers at most five, and at most five suspect
mods ranked the same way, with the system DLLs Phostwood calls unlikely culprits last.

Changed in the port:

- **Game versions.** Phostwood names game code by its offset into SkyrimSE.exe 1.6.1170, which every update moves.
  Each is kept as the Address Library ID of its function and the distance into it (`gameAddresses.ts`), with the
  offset that gives in 1.6.1170, 1.7.99 and 1.7.104 — and matched by either, since Crash Logger writes both. A rule
  that tests the game version includes 1.7.x where it plainly applies.
- **Crash Logger 1.26 and 1.27.** Their MISSING MASTERS section is the certain diagnosis it names; the model the
  game was loading when it crashed (RECENT MODEL REQUESTS, MODEL PATHS…) is the mesh rule's strongest evidence.
- **Text.** Plain and short, for a model acting with tools: mods by name and Nexus number, no links.
- **Left out:** rules for Nolvus only, for .NET Script Framework and Trainwreck logs, anything that goes online, the
  speculative insights, general advice that fires whatever the log says, and the DLL version table — checkGameVersion
  judges SKSE plugins against the game from the DLLs themselves.

## Consequences

- One tool, `readCrashLog`, in every profile: it only reads (ADR 0001 counts each tool).
- Phostwood's rules change; ours do not follow by themselves. Porting a new one is an entry and a test.
- Some rules fire on most logs at `possible` (an outdated Crash Logger, common node names); the ranking keeps them
  under the ones that point somewhere.
- Credit: README and THIRD_PARTY_NOTICES.md.
