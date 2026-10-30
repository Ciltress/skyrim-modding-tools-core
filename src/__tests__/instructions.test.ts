import type { AppBackend } from '../backend/index';
import { createHandlers } from '../handlers/index';
import { serverInstructions } from '../lib/instructions';
import { discoverSkills, FAMILY_SKILLS } from '../lib/skills';
import { CORE_PRODUCT } from '../testing/fakes';

/**
 * What every client's agent reads before its first call: the instance, where the help is, and how a mod is
 * delivered. Each rule is pinned with the eval round that asked for it (docs/research/local-model-evals.md,
 * beside the repositories); until 2026-10-25 they lived in the OpenClaw workspace files the MCP server's setup
 * wrote.
 */

const text = serverInstructions('the Mod Organizer instance at G:\\Modding\\SkyrimSE-Test');
const tools = createHandlers(undefined as unknown as AppBackend, CORE_PRODUCT).flatMap(handler => handler.getTools());

describe('the instructions', () => {
    it('name the instance they are bound to', () => {
        expect(text).toContain('It is bound to the Mod Organizer instance at G:\\Modding\\SkyrimSE-Test and cannot be switched');
    });

    it('stay short: every client puts them into the system prompt on every turn', () => {
        expect(text.length).toBeLessThan(3_000);
    });

    it('name only tools there are, and the answer field nextStep', () => {
        const served = new Set([...tools.map(tool => tool.name), 'healthcheck', 'nextStep']);
        const named = [...text.matchAll(/\b[a-z]+[A-Z]\w*\b/g)].map(match => match[0]);
        expect(named.length).toBeGreaterThan(10);
        for (const name of named) expect(served, name).toContain(name);
    });

    it('show each call with the arguments its tool takes', () => {
        // Each example is `{…}` in backticks; its tool is the last one named before it on its line.
        const examples = text.split('\n').flatMap(line => [...line.matchAll(/`(\{[^`]*\})`/g)].map(match => {
            const before = [...line.slice(0, match.index).matchAll(/\b[a-z]+[A-Z]\w*\b/g)];
            return [before.at(-1)?.[0], match[1]] as const;
        }));
        expect(examples.map(([tool]) => tool)).toEqual(expect.arrayContaining(['editRecord', 'readServerGuide']));
        for (const [tool, json] of examples) {
            const schema = tools.find(t => t.name === tool)?.inputSchema as { properties: Record<string, unknown>; required?: string[] };
            expect(schema, tool).toBeDefined();
            const args = Object.keys(JSON.parse(json));
            for (const arg of args) expect(Object.keys(schema.properties), `${tool} ${arg}`).toContain(arg);
            for (const arg of schema.required ?? []) expect(args, `${tool} ${arg}`).toContain(arg);
        }
    });

    it('name only skills there are: every family of records, and the one for mods a game update broke', () => {
        const skills = new Set(discoverSkills().filter(skill => skill.collection === 'domain').map(skill => skill.name));
        for (const [name] of text.matchAll(/skyrim-modding-[a-z-]+/g)) expect(skills, name).toContain(name);
        for (const family of FAMILY_SKILLS) expect(text).toContain(family.skill);
        expect(text).toContain('Mods that break after a game update: skyrim-modding-game-versions.');
        // The analysis wave, 2026-10-30: crash logs get a skill of their own, named where a crash first comes up.
        expect(text).toContain('A game that crashed: skyrim-modding-crash-analysis.');
    });

    it('say when a mod is delivered, beside the line that asks for it, and nowhere else', () => {
        // Rounds 23–33: 10 of the 30 failed runs answered as done after a refusal or an edit, with no build after it.
        // The user, 2026-10-22: a model that likes to overthink should be told not to, and to deliver the task.
        expect(text).toContain('Don\'t overthink: decide, then deliver the task. A mod is delivered when the buildProject after ' +
            'your last change answers `success`: after each edit or refusal, build again, and answer with what that build said.');
        expect(text).not.toContain('A mod is done when');
    });

    it('show editRecord with its arguments, and send a copy\'s changes to it', () => {
        // Round 62: editRecord named nowhere in the agent's prompt, the model guessed its name; "Edit the copy" read as the
        // file tool edit. The example lacked the required project until it moved here and the test above checked it.
        expect(text).toContain('- editRecord changes a record file\'s fields by their paths, with no old text to match: ' +
            '`{"project": "FlameBlade", "record": "Weapons/FlameBlade.yaml", "set": [{"path": "BasicStats.Damage", "value": 12}]}`.');
        expect(text).toContain('in the game. Change the copy with editRecord; never edit another mod\'s plugin.');
        expect(text).not.toContain('Edit the copy');
    });

    it('send a new record to a vanilla one to follow, and keep the mod manager\'s tools for when the user asks', () => {
        expect(text).toContain('is a new file you write, following a vanilla record read with readRecord: copy its values instead of guessing.');
        expect(text).toContain('- deployProject, installMod, disableMod, enableMod and editIni change the user\'s setup: only when asked.');
    });
});
