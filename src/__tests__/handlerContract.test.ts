import { vi, type MockInstance } from 'vitest';
import { ToolError } from '../handlers/errors';
import { createHandlers } from '../handlers/index';
import type { ToolDefinition, ToolProperty } from '../types/tools';
import { CORE_PRODUCT, InMemorySkyrimBackend, SAMPLE, recordingBackend } from '../testing/fakes';

/**
 * The contract every handler owes its caller, checked for every tool in the
 * catalogue rather than handler by handler.
 *
 * Handlers are thin wrappers over a backend, so the bugs that actually occur are
 * not in their logic but at their edges: an argument passed to the wrong method,
 * a response wrapped twice, an error that escapes as a raw TypeError, or a
 * failure reported as `status:"success"`. Those are uniform properties, so they
 * are asserted uniformly — driven by each tool's own inputSchema, which means a
 * newly added tool is covered the moment it is listed, with no fixture to write.
 *
 * Behaviour belongs in the suites next door (skyrimBackend.test.ts). If a tool
 * fails one of these for a legitimate design decision, encode the exception
 * below with a comment — do not weaken the assertion.
 */

type Stubs = ReturnType<typeof recordingBackend>;
const handlersFrom = (stubs: Stubs) => createHandlers(stubs.backend, CORE_PRODUCT);

/**
 * The arguments whose values the handler checks against a list the schema leaves out — readCrashLog's section, as a
 * client turns a null into "" against an enum — with one of those values.
 */
const LISTED_VALUES: Record<string, string> = { section: 'callStack' };

/** A plausible value for one declared argument, derived from its schema. */
function sampleValue(name: string, property: ToolProperty): any {
    if (property.enum?.length) return property.enum[0];
    if (LISTED_VALUES[name] && property.type === 'string') return LISTED_VALUES[name];
    switch (property.type) {
        case 'string': return SAMPLE;
        case 'number': return 1;
        case 'boolean': return true;
        case 'array': return property.items ? [sampleValue(name, property.items)] : [];
        case 'object':
            return property.properties
                ? Object.fromEntries(
                    Object.entries(property.properties).map(([k, v]) => [k, sampleValue(k, v)]))
                : {};
        default: return SAMPLE;
    }
}

function sampleArgs(tool: ToolDefinition): Record<string, any> {
    return Object.fromEntries(
        Object.entries(tool.inputSchema.properties).map(([name, property]) => [name, sampleValue(name, property)])
    );
}

/** Every (handler, tool) pair, for table-driven cases. */
const catalogue = handlersFrom(recordingBackend())
    .flatMap(handler => handler.getTools().map(tool => ({ handler: handler.constructor.name, tool })));

const cases = () => catalogue.map(c => [c.handler, c.tool.name, c.tool] as const);

/**
 * Tools that legitimately answer without touching the backend. The guide and
 * skill readers serve files off disk — there is no backend call to make.
 */
const NO_BACKEND_CALL_EXPECTED = new Set<string>(['readServerGuide', 'readSkill']);

/**
 * Tools whose failure shape is a payload rather than a thrown error, and what
 * that payload must say. Empty in the template; a probe whose answer *is*
 * "unreachable" belongs here, with the assertion that it says so.
 */
const FAILURE_IN_PAYLOAD: Record<string, (payload: any) => void> = {};

let consoleError: MockInstance;
beforeAll(() => { consoleError = vi.spyOn(console, 'error').mockImplementation(() => { }); });
afterAll(() => { consoleError.mockRestore(); });

it('has a catalogue to check', () => {
    expect(catalogue.length).toBeGreaterThan(0);
});

/**
 * The annotations every tool owes a client, checked uniformly for the same
 * reason as the envelope: they are a per-tool declaration, so the failure mode
 * is not a wrong one but a missing one. A new tool with no annotations falls
 * back to the specification's defaults — not read-only, destructive,
 * open-world — which makes it look more dangerous than a delete and pushes a
 * client to prompt for it forever.
 */
describe('every tool declares its annotations', () => {
    it.each(cases())('%s.%s says what it does to the backend', (_handler, _name, tool) => {
        const annotations = tool.annotations;
        expect(annotations).toBeDefined();

        expect(typeof annotations!.title).toBe('string');
        expect(annotations!.title!.length).toBeGreaterThan(2);
        // A title that only restates the name buys a UI nothing.
        expect(annotations!.title).not.toBe(tool.name);

        // All four, on every tool. The specification calls destructiveHint and
        // idempotentHint meaningful only when the tool writes, but OpenAI's
        // directory rejects a tool that omits either, and leaving them out
        // tells a client nothing it can use.
        expect(typeof annotations!.readOnlyHint).toBe('boolean');
        expect(typeof annotations!.destructiveHint).toBe('boolean');
        expect(typeof annotations!.idempotentHint).toBe('boolean');
        expect(typeof annotations!.openWorldHint).toBe('boolean');

        if (annotations!.readOnlyHint) {
            // A tool that changes nothing cannot destroy anything, and running
            // it twice leaves the backend exactly as once did. Anything else is
            // a typo.
            expect(annotations!.destructiveHint).toBe(false);
            expect(annotations!.idempotentHint).toBe(true);
        }
    });

    it('marks the tools that write, and only those', () => {
        // A sample from each end, so the suite fails loudly if a family is
        // annotated by pattern-matching on names rather than on what it does.
        const byName = new Map(catalogue.map(c => [c.tool.name, c.tool]));
        const writes = (name: string) => byName.get(name)?.annotations?.readOnlyHint === false;
        const destroys = (name: string) => byName.get(name)?.annotations?.destructiveHint === true;

        for (const name of ['newProject', 'addPatch', 'buildProject', 'deployProject', 'pullProject'])
            expect(`${name} writes: ${writes(name)}`).toBe(`${name} writes: true`);
        for (const name of ['findRecords', 'readRecord', 'readServerGuide'])
            expect(`${name} writes: ${writes(name)}`).toBe(`${name} writes: false`);
        // Replacing a mod in Mod Organizer, and rewriting a project's text, overwrite what was there;
        // adding records to a patch never does.
        for (const name of ['deployProject', 'pullProject'])
            expect(`${name} destroys: ${destroys(name)}`).toBe(`${name} destroys: true`);
        expect(`addPatch destroys: ${destroys('addPatch')}`).toBe('addPatch destroys: false');
    });
});

describe('every tool honours the response contract', () => {
    it.each(cases())('%s.%s returns one single-level MCP envelope', async (handlerName, _name, tool) => {
        const handler = handlersFrom(recordingBackend()).find(h => h.constructor.name === handlerName)!;

        let result: any;
        try {
            result = await handler.handle(tool.name, sampleArgs(tool));
        } catch (error) {
            // Rejecting is allowed; rejecting with something other than an
            // ToolError is not — that reaches the client as "Internal error".
            expect(error).toBeInstanceOf(ToolError);
            return;
        }

        expect(Array.isArray(result?.content)).toBe(true);
        expect(result.content.length).toBeGreaterThan(0);
        expect(result.content[0].type).toBe('text');
        expect(typeof result.content[0].text).toBe('string');

        // Exactly one level of JSON: parsing once must yield an object, not
        // another JSON string. That is the double-wrapping regression.
        const payload = JSON.parse(result.content[0].text);
        expect(typeof payload).toBe('object');
        expect(payload).not.toBeNull();
    });
});

describe('every tool reports a backend failure as a ToolError', () => {
    it.each(cases())('%s.%s surfaces the failure', async (handlerName, _name, tool) => {
        const handler = handlersFrom(recordingBackend({ fail: true })).find(h => h.constructor.name === handlerName)!;

        let result: any;
        try {
            result = await handler.handle(tool.name, sampleArgs(tool));
        } catch (error) {
            expect(error).toBeInstanceOf(ToolError);
            return;
        }

        const payload = JSON.parse(result.content[0].text);

        // A tool may report the failure in its payload instead of throwing, but
        // only if that is its documented contract — and then the payload has to
        // actually say so.
        const documented = FAILURE_IN_PAYLOAD[tool.name];
        if (documented) {
            documented(payload);
            return;
        }

        // Otherwise: returning is fine, claiming success is not.
        expect(`${handlerName}.${tool.name}: ${JSON.stringify(payload).slice(0, 120)}`)
            .not.toMatch(/"status"\s*:\s*"success"/);
    });
});

describe('every tool delegates to the backend', () => {
    it.each(cases())('%s.%s calls the backend', async (handlerName, toolName, tool) => {
        if (NO_BACKEND_CALL_EXPECTED.has(toolName)) return;

        const stubs = recordingBackend();
        const handler = handlersFrom(stubs).find(h => h.constructor.name === handlerName)!;

        // The generated arguments name the seeded note, so every tool has a
        // success path — a tool that rejects them anyway proves nothing about
        // delegation, which is why that is a failure here rather than a skip.
        await handler.handle(tool.name, sampleArgs(tool));

        expect(`${handlerName}.${toolName} called: ${stubs.calls.map(c => c.method).join(',') || '<nothing>'}`)
            .not.toMatch(/<nothing>/);
    });
});

describe('every tool survives being called with no arguments', () => {
    it.each(cases())('%s.%s fails cleanly without arguments', async (handlerName, _name, tool) => {
        const handler = handlersFrom(recordingBackend()).find(h => h.constructor.name === handlerName)!;
        try {
            const result = await handler.handle(tool.name, {});
            expect(Array.isArray(result?.content)).toBe(true);
        } catch (error) {
            // A missing mandatory argument must be a ToolError, never a
            // TypeError from reading a property of undefined.
            expect(error).toBeInstanceOf(ToolError);
        }
    });

    it.each(cases())('%s.%s fails cleanly with undefined arguments', async (handlerName, _name, tool) => {
        const handler = handlersFrom(recordingBackend()).find(h => h.constructor.name === handlerName)!;
        try {
            const result = await handler.handle(tool.name, undefined);
            expect(Array.isArray(result?.content)).toBe(true);
        } catch (error) {
            expect(error).toBeInstanceOf(ToolError);
        }
    });
});

describe('argument mapping', () => {
    /**
     * Spot checks that pin the arguments handed to the backend. These are the
     * mappings a refactor silently breaks, because the wrapper keeps compiling
     * and the envelope keeps looking right.
     */
    const mappings: { tool: string; args: any; method: string; expected: any[] }[] = [
        {
            tool: 'findRecords',
            args: { text: ' iron ', types: ['WEAP'], plugin: 'Skyrim.esm', limit: 5 },
            method: 'findRecords',
            expected: [{ text: 'iron', records: undefined, types: ['WEAP'], plugin: 'Skyrim.esm', limit: 5 }]
        },
        {
            // Defaults are the handler's job, so the backend never sees a missing limit.
            tool: 'findRecords', args: {}, method: 'findRecords',
            expected: [{ text: undefined, records: undefined, types: undefined, plugin: undefined, limit: 25 }]
        },
        {
            tool: 'readRecord', args: { record: ` ${SAMPLE} `, allVersions: true }, method: 'readRecord',
            expected: [{ record: SAMPLE, plugin: undefined, allVersions: true }]
        },
        {
            tool: 'addPatch', args: { project: SAMPLE, records: [' IronSword '] }, method: 'addPatch',
            expected: [{ project: SAMPLE, records: ['IronSword'], from: undefined, plugin: undefined }]
        },
        // A link line copied with the comment readRecord writes after it: refused as no FormID (eval round 19).
        {
            tool: 'readRecord', args: { record: '012EB7:Skyrim.esm  # IronSword' }, method: 'readRecord',
            expected: [{ record: '012EB7:Skyrim.esm', plugin: undefined, allVersions: undefined }]
        },
        {
            tool: 'findRecords', args: { records: ['045C2D:Skyrim.esm  # EnchWeaponFireDamage04'] }, method: 'findRecords',
            expected: [{ text: undefined, records: ['045C2D:Skyrim.esm'], types: undefined, plugin: undefined, limit: 25 }]
        },
        {
            tool: 'addPatch', args: { project: SAMPLE, records: ['012EB7:Skyrim.esm # IronSword'] }, method: 'addPatch',
            expected: [{ project: SAMPLE, records: ['012EB7:Skyrim.esm'], from: undefined, plugin: undefined }]
        },
        { tool: 'deployProject', args: { project: SAMPLE }, method: 'deployProject', expected: [SAMPLE] }
    ];

    it.each(mappings.map(m => [`${m.tool} -> ${m.method}`, m] as const))('%s', async (_label, mapping) => {
        const stubs = recordingBackend();
        const handler = handlersFrom(stubs).find(h => h.getTools().some(t => t.name === mapping.tool))!;

        await handler.handle(mapping.tool, mapping.args);

        const call = stubs.calls.find(c => c.method === mapping.method);
        expect(`${mapping.method} called: ${!!call}`).toBe(`${mapping.method} called: true`);
        expect(call!.args.slice(0, mapping.expected.length)).toEqual(mapping.expected);
    });
});

/**
 * Small models — and some clients — send an argument in the wrong JSON type: a list as the text of a
 * list, a number in quotes. Refusing costs a turn the model may not recover from, and the intent is
 * not in doubt, so every tool takes such a value as what its schema declares. Checked against each
 * tool's own inputSchema: the call with the text form must come out exactly as the call with the
 * real one — the same backend calls, the same answer.
 */
describe('every tool takes an argument sent as text for its declared type', () => {
    async function outcome(handlerName: string, toolName: string, args: any) {
        const stubs = recordingBackend();
        const handler = handlersFrom(stubs).find(h => h.constructor.name === handlerName)!;
        try {
            const result = await handler.handle(toolName, args);
            return { calls: stubs.calls, text: result.content[0].text };
        } catch (error) {
            return { calls: stubs.calls, error: (error as Error).message };
        }
    }

    /** The tools with at least one argument of this kind, with those arguments sent as text. */
    function asText(kind: (property: ToolProperty) => boolean, toText: (value: any) => string) {
        return cases()
            .filter(([, , tool]) => Object.values(tool.inputSchema.properties).some(kind))
            .map(([handlerName, toolName, tool]) => {
                const real = sampleArgs(tool);
                const sent = Object.fromEntries(Object.entries(real).map(([name, value]) =>
                    [name, kind(tool.inputSchema.properties[name]) ? toText(value) : value]));
                return [handlerName, toolName, real, sent] as const;
            });
    }

    const ofType = (type: ToolProperty['type']) => (property: ToolProperty) => property.type === type;
    const listOfText = (property: ToolProperty) => property.type === 'array' && property.items?.type === 'string';

    it.each(asText(ofType('array'), value => JSON.stringify(value)))(
        '%s.%s takes a list sent as JSON text', async (handlerName, toolName, real, sent) => {
            expect(await outcome(handlerName, toolName, sent)).toEqual(await outcome(handlerName, toolName, real));
        });

    it.each(asText(ofType('number'), value => ` ${value} `))(
        '%s.%s takes a number sent in quotes', async (handlerName, toolName, real, sent) => {
            expect(await outcome(handlerName, toolName, sent)).toEqual(await outcome(handlerName, toolName, real));
        });

    it.each(asText(ofType('boolean'), value => (String(value) === 'true' ? 'True' : 'False')))(
        '%s.%s takes true or false sent in quotes', async (handlerName, toolName, real, sent) => {
            expect(await outcome(handlerName, toolName, sent)).toEqual(await outcome(handlerName, toolName, real));
        });

    it.each(asText(listOfText, value => value[0]))(
        '%s.%s takes one item sent where a list of them is declared', async (handlerName, toolName, real, sent) => {
            expect(await outcome(handlerName, toolName, sent)).toEqual(await outcome(handlerName, toolName, real));
        });

    // Text that does not read as the declared type is not guessed at: the tool refuses it, naming
    // the argument, and the model gets to send it again.
    it.each([
        ['findRecords', { limit: 'five' }, 'limit'],
        ['findRecords', { records: '["IronSword"' }, 'records'],
        ['readRecord', { record: SAMPLE, allVersions: 'yes' }, 'allVersions']
    ] as const)('%s refuses %j, naming the argument', async (toolName, args, name) => {
        const handler = handlersFrom(recordingBackend()).find(h => h.getTools().some(t => t.name === toolName))!;
        await expect(handler.handle(toolName, args)).rejects.toThrow(new RegExp(`'${name}'`));
    });
});

/**
 * addPatch's note says what each copy is. Asked for a fire enchantment, a model copied EnchIronSwordFire01 — the Iron
 * Sword of Embers, a Weapon — took it for the enchantment and answered the sword built (eval rounds 97, 108 and 111).
 */
describe("addPatch's note says what each copy is", () => {
    async function answer(added: { formId: string; editorId: string | null; type: string; text?: string; files?: { path: string; text?: string }[] }[],
        already: { formId: string; file: string; text?: string }[] = []) {
        const backend = new InMemorySkyrimBackend();
        vi.spyOn(backend, 'addPatch').mockResolvedValue({
            project: { project: 'FlameBlade', plugin: 'FlameBlade.esp', light: true, path: 'p', pluginFolder: 'p/plugin', created: false },
            added: added.map(a => ({ formId: a.formId, editorId: a.editorId, type: a.type, from: 'Skyrim.esm',
                files: a.files ?? [{ path: `p/plugin/Weapons/${a.editorId}.yaml`, text: a.text }] })),
            alreadyInProject: already.map(a => ({ formId: a.formId, files: [{ path: `p/plugin/${a.file}`, text: a.text }] }))
        } as any);
        const handler = createHandlers(backend, CORE_PRODUCT).find(h => h.getTools().some(t => t.name === 'addPatch'))!;
        const result = await handler.handle('addPatch', { project: 'FlameBlade', records: [...added.map(a => a.editorId ?? a.formId), ...already.map(a => a.formId)] });
        return JSON.parse(result.content[0].text) as { note: string; nextStep: string };
    }
    const note = async (added: Parameters<typeof answer>[0]) => (await answer(added)).note;

    // The first of a cell's files is the GroupRecordData.yaml of its sub-block, and was named as the record to edit (eval round 125).
    it('names a cell\'s own file as its copy, and where a reference placed in it goes', async () => {
        const cell = 'Cells/0/6/WhiterunBreezehome - 0165A8_Skyrim.esm';
        const said = await note([{ formId: '0165A8:Skyrim.esm', editorId: 'WhiterunBreezehome', type: 'Cell', files: [
            { path: 'p/plugin/Cells/0/6/GroupRecordData.yaml', text: 'BlockNumber: 6\n' },
            { path: `p/plugin/${cell}/RecordData.yaml`, text: 'FormKey: 0165A8:Skyrim.esm\nEditorID: WhiterunBreezehome\n' },
            { path: 'p/plugin/Cells/0/GroupRecordData.yaml', text: 'GroupType: InteriorCellBlock\n' }] }]);
        expect(said).toContain(`"record": "${cell}/RecordData.yaml"`);
        expect(said).not.toContain('"record": "Cells/0/6/GroupRecordData.yaml"');
        // At its full path: by the path in the plugin folder alone, round 128's references landed outside the project.
        expect(said).toContain(`each reference is a file of its own in p/plugin/${cell}/Temporary/, written at that full path with your file tools ` +
            '— readRecord {"record": "WhiterunBreezehome"}');
    });

    it('sends a cell copied alone on to the reference\'s file, not to editRecord', async () => {
        const { nextStep } = await answer([{ formId: '0165A8:Skyrim.esm', editorId: 'WhiterunBreezehome', type: 'Cell', files: [
            { path: 'p/plugin/Cells/0/6/WhiterunBreezehome - 0165A8_Skyrim.esm/RecordData.yaml', text: 'FormKey: 0165A8:Skyrim.esm\n' }] }]);
        expect(nextStep).toMatch(/^Write what is placed in the cell as a file of its own in its Temporary folder/);
        expect(nextStep).not.toContain('editRecord');
    });

    it('names an enchanted weapon as a Weapon with its enchantment, and the enchantment as the record to copy', async () => {
        const said = await note([{ formId: '049BB8:Skyrim.esm', editorId: 'EnchIronSwordFire01', type: 'Weapon',
            text: 'FormKey: 049BB8:Skyrim.esm\nEditorID: EnchIronSwordFire01\nObjectEffect: 049BB7:Skyrim.esm  # EnchWeaponFireDamage01\n' }]);
        expect(said).toMatch(/^EnchIronSwordFire01 \(049BB8:Skyrim\.esm, a Weapon enchanted with EnchWeaponFireDamage01 \(049BB7:Skyrim\.esm\)\) is copied/);
        expect(said).toContain('addPatch {"project": "FlameBlade", "records": ["EnchWeaponFireDamage01"]}');
    });

    it('names a plain copy by its type alone, and an Armor with "an"', async () => {
        const said = await note([{ formId: '012EB7:Skyrim.esm', editorId: 'IronSword', type: 'Weapon', text: 'EditorID: IronSword\n' }]);
        expect(said).toMatch(/^IronSword \(012EB7:Skyrim\.esm, a Weapon\) is copied/);
        expect(said).not.toContain('An enchantment is a record of its own');
        expect(await note([{ formId: '012E49:Skyrim.esm', editorId: 'ArmorIronCuirass', type: 'Armor' }])).toMatch(/, an Armor\)/);
    });

    // Answered "is copied as an override" with the IronSword of an answer that copied nothing, a model sent addPatch for the
    // same enchantment four more times and ran out of turns (eval round 113's sword+detailed #3).
    it('says a record copied before is in the project already, left as it is, with its own file and names', async () => {
        const file = 'ObjectEffects/EnchWeaponFireDamage01 - 049BB7_Skyrim.esm.yaml';
        const { note: said, nextStep } = await answer([], [{ formId: '049BB7:Skyrim.esm', file,
            text: 'FormKey: 049BB7:Skyrim.esm\nEditorID: EnchWeaponFireDamage01\nEnchantmentAmount: 500\n' }]);
        expect(said).toMatch(new RegExp(`^EnchWeaponFireDamage01 \\(049BB7:Skyrim\\.esm\\), ${file.replace(/[.()]/g, '\\$&')} is in the project ` +
            'already, copied before, and not copied again: that file is the copy, as it was left, with every change made to it'));
        expect(said).not.toMatch(/is copied as|IronSword/);
        expect(said).toContain(`editRecord {"project": "FlameBlade", "record": "${file}", "set": [{"path": "EditorID", "value": ` +
            '"NewEnchWeaponFireDamage01"}], "remove": ["FormKey"], "build": true}');
        expect(nextStep).toContain('{"record": "EnchWeaponFireDamage01", "set": [{"path": "<the field>"');
    });

    it('names a record copied before by its file where its text is left out, beside one copied now', async () => {
        const said = await answer([{ formId: '012EB7:Skyrim.esm', editorId: 'IronSword', type: 'Weapon', text: 'EditorID: IronSword\n' }],
            [{ formId: '013989:Skyrim.esm', file: 'Weapons/SteelSword - 013989_Skyrim.esm.yaml' }]);
        expect(said.note).toMatch(/^IronSword \(012EB7:Skyrim\.esm, a Weapon\) is copied as an override: .* in place of NewIronSword\. /);
        expect(said.note).toMatch(/SteelSword \(013989:Skyrim\.esm\), Weapons\/SteelSword - 013989_Skyrim\.esm\.yaml is in the project already/);
        expect(said.note).not.toContain('still an override');
    });

    it('says a copy made a new record since is no override, with the move that frees its name for a copy', async () => {
        const file = 'Weapons/IronSword - 012EB7_Skyrim.esm.yaml';
        const { note: said } = await answer([], [{ formId: '012EB7:Skyrim.esm', file, text: 'FormKey: 000800:FlameBlade.esp\nEditorID: FlameBlade\n' }]);
        expect(said).toBe(`${file} was IronSword's copy (012EB7:Skyrim.esm) and is a new record of its own now, FlameBlade — it keeps ` +
            'the copy\'s file name, so IronSword was not copied again. To change IronSword itself as well, give that file a name of its ' +
            `own first, moveRecord {"project": "FlameBlade", "from": "${file}", "to": "Weapons/FlameBlade.yaml"}, then send addPatch again.`);
    });
});
