# AGENTS.md

Guidance for AI agents working **on this repository**. If you are an agent _using_ the tools, you want the
guide `tools` ([`docs/MCP-Tools.md`](docs/MCP-Tools.md)) instead, which every product serves through
`readServerGuide`.

## What this is

The core of the Skyrim Special Edition modding tools: everything the tools do, and nothing of how a client
reaches them. It looks records up across a mod manager's load order — Mod Organizer 2 on Windows, or Amethyst
on Linux — and builds mods kept as text projects into plugins deployed to that instance. What differs by
operating system and by mod manager sits behind two seams
([ADR-0007](docs/adr/0007-operating-system-and-mod-manager-are-separate-seams.md)).

Two products serve it ([ADR-0009](docs/adr/0009-the-core-is-its-own-package.md)):

- **skyrim-modding-tools-mcp**, the MCP server (stdio and Streamable HTTP), the complete product;
- **skyrim-modding-tools-openclaw-plugin**, the same tools as native OpenClaw tools.

A product hands the core a `Product` ([`src/lib/product.ts`](src/lib/product.ts)): its name, from which its
resource URIs' scheme comes, its version, which Nexus is told, its folder — where `projects/`, `.env` and the
Papyrus compiler setup downloads are — and the guides it serves, in their order.

**Core-first.** A change to a tool, an answer, a guide or a skill is made here, never only in a product. A
product holds its transport and its client's glue; what only one client can do stays there, and the MCP server
gets the nearest equivalent where one exists.

Two programs make it up:

- **The tools** (TypeScript, `src/`) — the mod managers' readers, projects, deploy, Nexus, and the tools
  themselves.
- **The plugin helper** (C#, `helper/`) — a .NET process built on Mutagen that does every plugin read
  and write ([ADR-0003](docs/adr/0003-plugins-through-a-mutagen-helper.md)). The core talks to it in
  JSON lines over stdio. Build it with `npm run build:helper` (needs the .NET 10 SDK).

The vocabulary is in [`CONTEXT.md`](CONTEXT.md); use its words — *mod*, *plugin*, *record*,
*FormID*, *instance*, *project*, *patch* — in code, docs and messages alike.

## Read these first

| Document | When you need it |
| --- | --- |
| **[`docs/Tool-Router.md`](docs/Tool-Router.md)** | Intent to tool, hand-written. The cheapest useful thing to read. |
| **[`docs/MCP-Tools.md`](docs/MCP-Tools.md)** | The rules a generator cannot produce: the architecture, profiles, the response budget, the gate, the error model, troubleshooting, how to extend. **Start here** for how the tools behave. |
| **[`CONTEXT.md`](CONTEXT.md)**, **[`docs/adr`](docs/adr)** | The vocabulary, and the design decisions everything else follows from. |

The tool reference is not here: it renders the tools as one product serves them, so each product generates
and commits its own (`renderToolReference(collectToolFamilies(product))`).

They are kept accurate deliberately — treat a contradiction between a doc and the code as a bug in one
of them, and fix it rather than working around it.

**The guides and skills are served to clients at runtime**, so they are part of the product, not side notes:
through the `readServerGuide` and `readSkill` tools, and as MCP resources. The core's guides are
`CORE_GUIDES` in [`src/lib/guides.ts`](src/lib/guides.ts); a product lists them among its own.
`guides.test.ts` reads the real files, so moving or renaming a doc without updating the registry fails the
tests.

The skill collections under [`skills/`](skills/) are discovered from disk by
[`src/lib/skills.ts`](src/lib/skills.ts):

- **`skills/Domain`** holds the modding skills — ours, edit freely. They are knowledge an agent using
  the tools reads before modding; keep them accurate to the game, and to the project text format.

## The graph: find your way with it, keep it current

`graphify-out/graph.json` is this repo's knowledge graph: its code, documents and images, and how they link. It is
built by [graphify](https://github.com/Graphify-Labs/graphify), the Python package `graphifyy` (`uv tool install
graphifyy`; the npm package of that name is another project).

- **Ask the graph before you search.** Run `graphify query "<question>"` before a grep or a source read,
  `graphify path "<A>" "<B>"` for how two things connect, and `graphify explain "<concept>"` for one of them; then
  read only the lines it pointed you to.
- **Update it after every change,** in a commit of its own right after the change: `graphify update .` for code (by
  syntax, no model needed). A changed document or image needs the semantic pass, `/graphify --update`, which reads it
  again; `graphify update .` refuses where a document's saved nodes would go missing.
- Only `graph.json` and `.graphify_labels.json` are in git; the rest of `graphify-out/` stays local.

## Layout

```
src/
  index.ts                 what a product builds from: the one entry point; a module not exported there is
                           the core's own business
  backend/Backend.ts       the lifecycle every backend has, and the two rules it keeps
  backend/index.ts         AppBackend, createBackend(env, {product}), readConfiguration(env, {root}),
                           REQUIRED_ENV — the one place naming the backend
  backend/skyrim/SkyrimBackend.ts   the domain operations and their types
  backend/skyrim/SkyrimInstanceBackend.ts  the backend: credential check, records, projects, deploy,
                                    pull — knowing only the ModManager interface
  backend/skyrim/engine.ts          the shared helper process and its load order, kept in step with
                                    the mod manager
  backend/skyrim/helperClient.ts    the helper's JSON-lines client
  backend/skyrim/helperBuild.ts     what `npm run build:helper` does; an installed core keeps no C# project
  backend/skyrim/modManager.ts      the ModManager seam (ADR-0007) and the types every manager shares
  backend/skyrim/mo2Manager.ts      Mod Organizer 2 as the mod manager, over mo2.ts and mo2Profile.ts
  backend/skyrim/mo2.ts             reading an MO2 instance: ini, profile, mod order, load order, files;
                                    under Proton, the Wine prefix its ini's paths are read through
  backend/skyrim/mo2Profile.ts      the profile edits deploy makes, and refreshing a running MO2
  backend/skyrim/mo2Instances.ts    a product's setup finding the instance: MO2's global instances for Skyrim
                                    (checkMo2Instance, in backend/index.ts, reads a typed folder as the tools do)
  backend/skyrim/amethyst.ts        Amethyst as the mod manager: finding its config, its profile, its
                                    deploy command
  backend/skyrim/projects.ts        a project on disk: paths, FormID assignment, text sync, deploy state
  backend/skyrim/modList.ts         listMods: the profile's mods as the manager shows them, read from files
  backend/skyrim/sksePlugins.ts     whether an SKSE plugin fits the game: the rules, SKSE's log, the verdicts
  backend/skyrim/gameVersion.ts     checkGameVersion's gathering: winning DLLs, SKSE, Address Library, the log
  backend/skyrim/nexus.ts           Nexus Mods: the catalogue over GraphQL, the account and downloads over
                                    v1 with NEXUS_API_KEY, BBCode as text; a download's .meta record
  backend/skyrim/installer.ts       what installMod decides before writing: a plain file's Data folder, a
                                    FOMOD installer's questions and the files its choices install
  backend/skyrim/externalTools.ts   the Papyrus compilers (the Creation Kit's, russo-2025's) and git
  backend/skyrim/platform.ts        everything that differs by operating system (ADR-0007) — the only
                                    place that asks; Windows and Linux side by side
  backend/skyrim/pathSettings.ts    the settings that name a path, read once: `~` and variables expanded
                                    as a POSIX shell would, made absolute from the product's folder
  backend/skyrim/paths.ts           finding files whatever their letter case, on every system
  handlers/BaseHandler.ts  the ToolSpec contract: dispatch, timing, envelope, error mapping
  handlers/errors.ts       ToolError and its codes, JSON-RPC's
  handlers/index.ts        createHandlers(backend, product): the one list served, documented and tested
  handlers/args.ts         shared argument checks, and request errors -> InvalidParams
  handlers/RecordsHandlers.ts       findRecords, readRecord
  handlers/ProjectsHandlers.ts      newProject, addPatch, editRecord, buildProject, deployProject, pullProject
  handlers/NexusHandlers.ts         searchNexus, readNexusMod, downloadNexusFile
  handlers/ModsHandlers.ts          listMods, checkGameVersion, installMod, disableMod
  handlers/DocsHandlers.ts, SkillsHandlers.ts   readServerGuide and readSkill
  types/tools.ts           ToolDefinition / ToolProperty / ToolAnnotations — the inputSchema contract
  lib/product.ts           Product, CORE_ROOT, CORE_INFO, resourceScheme()
  lib/muddy.ts             Muddy, the modding agent the products set up: MUDDY, muddyPrompt(), writeMuddyWorkspace()
  lib/                     logger, guides, skills, profiles, responseBudget, capabilityGate, instructions,
                           toolDocs, env
  testing/fakes.ts         the shared doubles and CORE_PRODUCT, also a product's through
                           `skyrim-modding-tools-core/testing`
  __tests__/               vitest suites, all offline
helper/SkyrimHelper/       the C# plugin helper: Session.cs (the methods), RecordIndex.cs, SpriggitFormat.cs,
                           PeVersion.cs (an exe's version, read the same way on Windows and Linux), SksePlugins.cs (an SKSE plugin's
                           exports and version data, read without loading it),
                           Archives.cs (downloaded files, through SharpCompress), Fomod.cs (ModuleConfig.xml as data),
                           RecordFiles.cs (a project's record files checked before a build: folders, fields,
                           copied FormKeys), RecordLinks.cs (links written as editor IDs made FormIDs)
skills/Domain              the modding skills — ours, edit freely
agents/muddy               Muddy's soul, identity, working rules and avatar: what every product installs; small,
                           as a small local model reads them every turn
scripts/build-helper.mjs   npm run build:helper, over helperBuild.ts
scripts/check-helper.mjs   starts the built helper and checks it answers; CI runs it on both systems
```

## Conventions that matter

- **Never write to stdout.** The MCP server speaks MCP over stdio and runs this code in its process; anything
  on stdout that is not a protocol frame corrupts the stream and breaks every tool at once. Use the logger,
  which writes to stderr. The MCP server's `npm run smoke` is what catches it.
- **A tool returns its payload; `BaseHandler` does everything else.** Each tool is one `ToolSpec` — a
  `ToolDefinition` plus a `run(args)` that returns plain data — and
  [`BaseHandler.handle()`](src/handlers/BaseHandler.ts) owns dispatch, timing, the envelope and
  the error mapping. Do not build `{content:[{type:'text',…}]}` in a handler; a product passes the envelope
  through, so doing it twice nests JSON inside JSON.
  - `status:'success'` is added for you. Return your own `status` to override it, and return an
    array to be serialised as-is.
  - Throw to fail. A `ToolError` passes through with its code intact; anything else becomes an
    `InternalError` prefixed with the spec's `onFailure`, keeping the backend's detail.
  - The envelope is compact, never pretty-printed. Indentation costs response budget and buys a
    model nothing.
- **Adding a tool is a one-file change.** Add a `ToolSpec` to `toolSpecs()`; `getTools()`, the routers,
  the tool lists and the contract tests are all derived from it, so a listed tool is always callable
  and always tested. A tool that belongs in a small profile is the exception — name it in
  [`src/lib/profiles.ts`](src/lib/profiles.ts) too. Then regenerate each product's tool reference.
- **Every tool declares its `annotations`: a `title` and all four hints, as booleans.** On a read-only
  tool that means `destructiveHint: false, idempotentHint: true`. The specification defines those two
  as meaningful only when a tool writes, but OpenAI's directory rejects a tool that omits either, and
  a client falling back to the defaults — not read-only, destructive, open-world — treats a new search
  tool like a delete. The conventions each flag follows are in
  [§2.2 of `docs/MCP-Tools.md`](docs/MCP-Tools.md); `handlerContract.test.ts` fails on a tool missing
  any of them.
- **Make every annotation true.** `idempotentHint: true` is a claim a test can check: repeat the call
  and compare the state. `skyrimBackend.test.ts` does it for `deployProject` — a second deploy of the
  same build leaves Mod Organizer's profile untouched — and for `newProject` and `assignFormIds`.
  `addPatch` leaving a record already in the project alone is the helper's behaviour, so it was
  checked against the test instance, not in the suite.
- **Errors must be `ToolError`, and say the next step.** A raw `Error` reaching a client becomes an
  opaque "Internal error". `InvalidParams` for a bad argument, with a message that names what to send
  instead — a model reads the message and acts on it.
- **Declare argument types honestly.** An argument the backend wants as an object or a list must be
  declared `type: 'object'` or `'array'`, not `'string'`. There is no `optional` marker — omit the
  name from `required`.
- **Answers have a ceiling.** [`src/lib/responseBudget.ts`](src/lib/responseBudget.ts) decides it, and each
  product applies it in the one funnel every answer passes through, so a tool added tomorrow is covered
  without being told. Over-budget answers are replaced by valid JSON stating the original size and the next
  step — never by a cut-off fragment, which would not parse and which a model answers by retrying the same
  call. A tool that overruns _routinely_ should grow a real argument (a row limit, a section) instead, and
  carry a `narrowingHint` naming it.
- **The backend decides what it can serve; ask it, do not hard-code it.** Tools declare
  `needsFeature`; `capabilities()` says what this backend has. **Every failure path leaves the gate
  empty and the list complete**: hiding a tool that would have worked is worse than offering one that
  errors, because the first is invisible. The one exception: `downloadNexusFile` is offered only when
  Nexus confirms the key in `NEXUS_API_KEY` is a premium account's — a check Nexus did not answer
  withholds it; searching and reading Nexus need no key ([§3.3 of `docs/MCP-Tools.md`](docs/MCP-Tools.md)).
- **A profile decides what is listed _and_ what is routed.** A tool outside the profile is not
  callable, so `readonly` genuinely cannot write. Renaming a tool a profile names fails at **startup**
  (`applyProfile` throws), and `profiles.test.ts` catches it first.
- **Retry only what was refused before it ran, and only once.** `isSessionExpired()` answers true
  only for errors that prove nothing was applied. A credential the backend refused is latched and
  never retried — for a password, retries lock the account.
- **One list of handlers.** `createHandlers()` in `src/handlers/index.ts` is what every product serves,
  what the references document and what the contract tests check. Never keep a second list.
- **A status code is not a diagnosis.** When a message names a cause, the code has to have evidence
  for that cause. An unreachable backend, a refused credential and a missing permission can arrive as
  the same HTTP status and need opposite fixes; `health()` exists so a startup failure and
  `healthcheck` can say which.

## Working on this repo

```bash
npm install            # also builds dist/ (the prepare script)
npm run build:helper   # dotnet publish helper/SkyrimHelper -> helper/bin (needs the .NET 10 SDK and dist/);
                       # in an installed core it then removes helper/SkyrimHelper (helperBuild.ts)
npm run build          # tsc -> dist/, with the types a product compiles against
npm test               # vitest, fully offline
npx tsc --noEmit       # type check only
npm run check:helper   # start the built helper once (after build and build:helper)
```

Before you call something done: `npx tsc --noEmit` clean and `npm test` green here; then, in each product,
its build and its tests, `npm run docs:tools` leaving its reference unchanged (or regenerated, when a tool's
definition changed on purpose), and the MCP server's `npm run smoke`. A change to the helper also needs
`npm run build:helper`, `npm run check:helper`, and a call through it against a real instance. CI runs it on
Windows and Linux; code that differs by system belongs in `platform.ts`, and its Linux side is tested on
Windows too, against laid-out folders.

`handlerContract.test.ts` holds every tool to the same contract using arguments generated from its
`inputSchema`, so a new tool is covered as soon as it is listed. If it fails for a legitimate design
decision, encode the exception there with a comment — do not weaken the assertion.

**A green suite does not mean a write path works.** A fake accepts any order of calls, so it cannot
know the backend's own rules — that a lock must be released before activating, that a freshly created
object is not yet searchable. **Anything that locks, writes, activates or deletes has to be run
against a real backend once**, with a throwaway object created and deleted in the same session. Then
encode what you learned as an assertion, so the fake enforces it from then on. Here the real backend
is a **test instance** — a portable Mod Organizer on a clean game, never someone's playthrough; with
Amethyst, a throwaway profile — and the throwaway object is a project, deployed and then removed with
its mod.

## Measuring with a local model

The eval runs fixed modding tasks with a local model through OpenClaw and scores them. From round 64 it is the
OpenClaw plugin's (`npm run eval` there; rounds 1–63 ran the MCP server's `scripts/eval-ollama.mjs`); every round
is in `docs/research/local-model-evals.md` in the skyrim-modding-tools folder beside the repositories. The transcripts are where a model's wasted calls point at the tools: a
relative path joined to the wrong folder, a misspelled type answered with "nothing found". Fix those here, for
every client.

## Care with a real backend

The tools write into a mod manager's instance: mod folders, and the selected profile's mod order
and load order — and with Amethyst they run Amethyst's deploy, which rewrites the game's `Data`. When working against a real one:

- Prefer read-only tools; treat anything not demonstrably read-only as a write.
- Work on a test instance. A Wabbajack-installed instance deletes every mod it did not install on its
  next update, and its authors usually void support for modified installs.
- Change other people's mods only through patches, never in their plugins.
- Ask before deploying into an instance you do not own, or a profile someone plays.
