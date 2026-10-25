import { FAMILY_SKILLS } from './skills.js';

/**
 * The MCP `instructions` this server announces at `initialize`.
 *
 * They are the only thing a client sees before it calls anything, which makes
 * them the place for two facts a tool list cannot carry:
 *
 * - **What this instance is bound to.** When several of these servers are
 *   registered — one per environment, account or tenant — an agent asked to
 *   "check something in staging" has to pick the right one, and tool names
 *   alone do not say.
 * - **Where the help is.** A cold session knows nothing about this server; the
 *   one sentence pointing at `readServerGuide` is what turns a wrong first
 *   guess into a lookup.
 * - **How a mod is delivered.** What the local-model eval rounds showed an agent
 *   needs before its first call (instructions.test.ts names the round behind
 *   each rule). Until 2026-10-25 these lived in the OpenClaw workspace files the
 *   MCP server's setup wrote; the OpenClaw plugin adds only what is OpenClaw's.
 *
 * Kept short on purpose: clients put this into the system prompt on every turn.
 */
export function serverInstructions(target: string): string {
    return [
        'This server authors and analyses Skyrim Special Edition mods: it looks records up across the ' +
        'load order, builds mods kept as text projects into plugins deployed to the mod manager (Mod Organizer or Amethyst), ' +
        'and installs downloaded mods — with a Nexus Mods API key, it also finds them on Nexus Mods. ' +
        `It is bound to ${target} and cannot be switched to another at runtime.`,
        '',
        'Don\'t overthink: decide, then deliver the task. A mod is delivered when the buildProject after your last change ' +
        'answers `success`: after each edit or refusal, build again, and answer with what that build said.',
        '',
        'Before modding, read the skill skyrim-modding-basics, then the skill of the records you will write: ' +
        `${FAMILY_SKILLS.map(family => `${family.skill} (${family.covers})`).join(', ')} (readSkill).`,
        '',
        'How a mod is made:',
        '- A mod is a project. newProject creates it and answers the full path where each record file goes: write the ' +
        'YAML files exactly there, with your file tools.',
        '- editRecord changes a record file\'s fields by their paths, with no old text to match: ' +
        '`{"project": "FlameBlade", "record": "Weapons/FlameBlade.yaml", "set": [{"path": "BasicStats.Damage", "value": 12}]}`.',
        '- A new record — a new sword, a new note — is a new file you write, following a vanilla record read with ' +
        'readRecord: copy its values instead of guessing. Find records with findRecords.',
        '- addPatch is only for changing an existing record: its copy changes that record everywhere in the game. ' +
        'Change the copy with editRecord; never edit another mod\'s plugin.',
        '- buildProject checks and builds. A refusal names the line to write: fix it, build again. `built_with_warnings`: ' +
        'the plugin is built — fix what each warning says and build again, that is part of the task. A warning stays ' +
        'only where the user asked for just what it names.',
        '- deployProject, installMod and disableMod change the user\'s mod manager: only when asked.',
        '- Answer short: what you made, each file you wrote by its full path, what is left to do.',
        '',
        'If you know the job but not the tool, call readServerGuide with `{"guide":"router"}`; with no ' +
        'arguments it lists every guide and its sections. healthcheck reports what this server is bound ' +
        'to, the active tool profile, and anything withheld. An answer marked status:"truncated" was too large ' +
        'for this profile: narrow the request as its nextStep says instead of repeating it.'
    ].join('\n');
}
