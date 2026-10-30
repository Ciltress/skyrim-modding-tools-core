---
name: skyrim-modding-crash-analysis
description: Crash logs - why the game crashed to desktop, read from Crash Logger SSE's log with readCrashLog, and the fix it points at, made on the user's word. Use when the user says the game crashed, closed, or froze to the desktop, or asks what a crash log means.
---

# Why the game crashed

Crash Logger SSE writes a log each time the game crashes. `readCrashLog` reads the newest one of this
setup and answers what crashed, up to five **diagnoses** (known crashes, each with its fix) and up to
five **suspects** (the mods whose files the log points at). The answer is evidence; the user decides
every change.

## Workflow

0. **Read.** `readCrashLog` with no arguments. For a crash the user names by time, give `log`:
   `"2026-10-30"` or `"2026-10-30 14:02"`. Done when you have its answer — or, when it finds no log,
   when you have told the user why (its message says) and, if Crash Logger SSE is not installed,
   offered to install it: Crash Logger SSE (Nexus 59818) and Address Library (Nexus 32444), each with
   `downloadNexusFile` and `installMod`, on their yes.
1. **Weigh.** Done when you can say, in one sentence each, what crashed and which mod is the lead:
   - `log.thisSetup` false: another setup's game wrote the log. Say so before anything else.
   - The first diagnosis is the best: `certain` is the cause, `likely` the probable one, `possible`
     a lead only. Its `evidence` says where in the log it was found — the first line is where the game
     crashed, the call stack how it got there, the relevant objects the records it was handling.
   - The first suspect is the mod the strongest evidence points at. A suspect with a `certain` or
     `likely` diagnosis naming its mod or file is the lead; with only `possible` ones, it is a guess.
   - `records`: each record's defining plugin and the one that changed it last, with their mods.
2. **Check the lead**, with the tool for its kind. Done when the check has answered:
   - An SKSE plugin (`… (SKSE plugin)`): `checkGameVersion` — a DLL for another game version crashes.
   - A mod: `listMods` with `text` and `details: true` — its version, and whether it is enabled.
   - Unsure which part of the log a diagnosis means: `readCrashLog` with `section`, e.g. `"callStack"`.
3. **Tell the user** the cause, the mod, the fix, and what the fix changes. Ask before any change.
   Done when the user has said yes or no to the fix.
4. **Fix, on the user's yes, for that mod**: `installMod` with a newer or fitting file and
   `replace: true`; `disableMod` to take a mod out (`enableMod` brings it back); `editIni` for an INI
   setting a diagnosis names. Before a mod is disabled or replaced mid-playthrough, tell the user to keep
   a copy of their latest save. Done when the tool answered `success`, and you have told the user to
   start the game again — the next crash log, if there is one, says whether the fix held.

## Reading a log by section

`readCrashLog` with `section` answers that part of the log, line by line: `callStack` (the frames,
`[P]` probable, `[S]` from the stack scan), `relevantObjects`, `modelRequests` (the model the game was
loading, Crash Logger 1.26 on), `plugins` (the load order), `sksePlugins`, `gameState` (open menus,
the player's cell; 1.27), `systemSpecs` (memory in use).
