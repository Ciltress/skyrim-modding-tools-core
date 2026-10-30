# skyrim-modding-tools-core

The core of the Skyrim Special Edition modding tools: it looks records up across a mod manager's load order —
Mod Organizer 2 on Windows, or Amethyst on Linux — authors mods and patches as text projects, builds them into
plugins through a Mutagen helper and deploys them to the instance, and installs downloaded mods, with Nexus
Mods search for an API key's owner. The tools, their guides and the modding skills are all here.

A modder does not install this package by itself. Two products serve it:

- **[skyrim-modding-tools-mcp](https://github.com/Ciltress/skyrim-modding-tools-mcp)**, the MCP server, for any
  MCP client — the complete product;
- **skyrim-modding-tools-openclaw-plugin**, the same tools as native OpenClaw tools.

Why it is a package of its own, and what a product hands it: [ADR-0009](docs/adr/0009-the-core-is-its-own-package.md).

## Requirements

- Node.js 24.18 or later.
- The .NET 10 SDK, to build the plugin helper.

## Development

```bash
npm install            # also builds dist/
npm run build:helper   # the C# plugin helper, into helper/bin
npm test
```

Guidance for agents working on it, the layout and the conventions: [AGENTS.md](AGENTS.md). The vocabulary:
[CONTEXT.md](CONTEXT.md).

## License

GPL-3.0-only ([ADR-0006](docs/adr/0006-gpl-3.md)).

The crash rules `readCrashLog` applies are Phostwood's [Skyrim Crash Log Analyzer](https://github.com/Phostwood/crash-analyzer)'s
(GPL-3.0), rewritten in TypeScript ([ADR-0011](docs/adr/0011-crash-rules-are-phostwoods-ported.md)). Thanks to
Phostwood for years of collecting them. Details in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
