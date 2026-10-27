# The core is its own package, which the MCP server and the OpenClaw plugin serve

The tools were written as an MCP server, and the MCP server stays the complete product. Since 2026-10-17 the
user's client is OpenClaw, and as an MCP server the project cannot control what OpenClaw does with it:

- every tool is named `skyrim__<tool>`, and a model that calls the bare name is told "Tool editRecord not
  found", with no right name given (7 calls in eval round 62);
- the server's `instructions` are dropped, so a workspace AGENTS.md has to carry them;
- skills reach the model only through `skills.load.extraDirs`;
- Tool Search hides MCP tools behind a search a small model did not find them through, so it is off globally,
  for every agent.

An OpenClaw plugin can do each of these: its tools keep the names they are registered with, a tool can stay
outside Tool Search (`catalogMode: "direct-only"`), a hook can add the instructions to the Skyrim agents' system
prompt, and its manifest ships the skills.

Everything the tools do is therefore this package, `skyrim-modding-tools-core`, in its own repository, with the
history of the files it took from `skyrim-modding-tools-mcp` (split at aa9a5ca with `git filter-repo`). The
backend, the one list of tools, the plugin helper, the skills, the router and the tools guide, profiles, the
response budget and the capability gate are here. A product — the MCP server, the OpenClaw plugin — holds its
transport and its client's glue, and depends on the core.

What differs between products and the tools still need, the product says: a `Product` (`src/lib/product.ts`)
carries its name, from which its resource URIs' scheme comes, the version Nexus is told, the folder its
projects, `.env` and downloaded tools are in, and the guides it serves in their order. A tool fails with a
`ToolError`, whose codes are JSON-RPC's: the MCP server makes it the McpError it always answered, and the plugin
shows its message as it stands.

The rule is core-first: a change to a tool, an answer, a guide or a skill is made here, never only in a
product. What only one client can do — an OpenClaw hook — stays in that product, and the MCP server gets the
nearest equivalent where one exists: its instructions, a nextStep line.

Also considered: the plugin importing the MCP server's package (it would have depended on an MCP server and its
SDK to reach the tools), the core as a workspace package inside the MCP repository, and a plugin shipping the
MCP server unchanged through its manifest's `mcpServers` (the tools would still be MCP tools, prefixed and
hidden by Tool Search).

## Consequences

- A change to the core that a product's answers show needs the product rebuilt, and its tool reference
  regenerated: the reference renders the tools as that product serves them, so each product commits its own.
- The products depend on the core by `file:../skyrim-modding-tools-core` while it is developed on one machine,
  then on a tag of its GitHub repository. Installed from GitHub, its `prepare` script builds it, and npm runs
  that only for a package allowed to run scripts.
- The helper is built in the core's folder (`helper/bin`), whichever product starts it; setup builds it there.
  In an installed core the build then removes `helper/SkyrimHelper`, the C# project with its `bin` and `obj`
  (2026-10-27). OpenClaw copies the whole package, 96 MB of it, into its plugin captures for every
  generation. VS Code's C# extension found the `.csproj` in such a copy and built it there, and its MSBuild
  nodes held the copy open, so OpenClaw failed with EPERM on every load. A product that builds the helper
  again needs the core reinstalled.
- A product's CI checks the core out beside it, where the `file:` link points.
- The guide `tools` (`docs/MCP-Tools.md`) still describes the MCP server's behaviour. It is the core's, and
  becomes product-neutral when the plugin serves it.
- The eval of local models moves to the plugin's repository, from round 64. Rounds 1–63 stay in the MCP
  repository's `docs/research/local-model-evals.md`.
