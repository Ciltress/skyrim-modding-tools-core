import fs from 'fs';
import path from 'path';
import { CORE_ROOT } from './product.js';

/**
 * Muddy, the modding agent the products set up: a mudcrab with a soul, an identity, his working rules and his
 * avatar, kept here in agents/muddy so the MCP server's setup and the OpenClaw plugin's wizard install the same one.
 */
export const MUDDY_DIR = path.join(CORE_ROOT, 'agents', 'muddy');

/** What an agent list shows of him. */
export const MUDDY = {
    id: 'muddy',
    name: 'Muddy',
    emoji: '🦀',
    avatar: 'avatars/mudcrab.png',
    description: 'Muddy, the mudcrab who mods Skyrim Special Edition: looks records up, makes mods and patches, ' +
        'installs mods and reads crash logs.'
} as const;

/** His files in an agent's workspace, relative to it: what a client that reads a folder gets. */
export const MUDDY_WORKSPACE_FILES = ['AGENTS.md', 'SOUL.md', 'IDENTITY.md', MUDDY.avatar] as const;

/** A file's text without its `# TITLE.md - Muddy` line, which names the file, not a part of him. */
function body(file: string): string {
    return fs.readFileSync(path.join(MUDDY_DIR, file), 'utf8').replace(/^# .*\r?\n+/, '').trim();
}

/**
 * His soul and his working rules as one text, for a client that takes an agent's instructions in one field: a
 * Claude Code subagent, a VS Code agent, a project's instructions.
 */
export function muddyPrompt(): string {
    return `${body('SOUL.md')}\n\n## How you work\n\n${body('AGENTS.md')}\n`;
}

/**
 * His files copied into a workspace folder, made where it is missing. A file already there is the user's and is
 * kept, unless `overwrite` says to replace it.
 */
export function writeMuddyWorkspace(folder: string, options: { overwrite?: boolean } = {}): { written: string[]; kept: string[] } {
    const written: string[] = [];
    const kept: string[] = [];
    for (const file of MUDDY_WORKSPACE_FILES) {
        const target = path.join(folder, file);
        if (fs.existsSync(target) && !options.overwrite) {
            kept.push(file);
            continue;
        }
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.copyFileSync(path.join(MUDDY_DIR, file), target);
        written.push(file);
    }
    return { written, kept };
}
