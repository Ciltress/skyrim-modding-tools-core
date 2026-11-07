"""Starts Blender's MCP server (Blender Lab's blmcp, as installed) with each Blender it runs in the background given no stdin.

Its execute_blender_code_for_cli starts `blender --background <file> --python-expr …` with subprocess.run and no `stdin`, so
Blender inherits the server's own stdin: the MCP client's JSON-RPC pipe. On Windows a Blender started so never finished — every
call timed out after 120 s in the eval's first Blender runs (2026-11-06), the same call answering in 4.6 s with stdin at
DEVNULL. A client starts the server through this file, with the server's own Python (blenderMcpLaunch in cellScene.ts
says how), until blmcp passes stdin itself; the installed package is left as it is.

    python blender-mcp-no-stdin.py [blender-mcp's arguments]
"""

import subprocess
import sys

_run = subprocess.run


def _run_without_stdin(*args, **kwargs):
    kwargs.setdefault("stdin", subprocess.DEVNULL)
    return _run(*args, **kwargs)


subprocess.run = _run_without_stdin

import blmcp  # noqa: E402  (after the patch, so its modules see it)

sys.exit(blmcp.main())
