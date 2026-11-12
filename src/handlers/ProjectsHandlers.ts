import path from 'node:path';
import { BaseHandler } from './BaseHandler.js';
import type { ToolSpec } from './BaseHandler.js';
import type { BuildResult, ProjectFile } from '../backend/index.js';
import { asRequestError, optionalBoolean, optionalChoice, optionalFieldValues, optionalNumber, optionalString, optionalStringList, recordRef, requireString } from './args.js';
import { ToolError, ToolErrorCode } from './errors.js';
import { FAMILY_SKILLS } from '../lib/skills.js';

/**
 * Projects: mods authored as text (docs/adr/0004). Records are changed by editing the project's YAML
 * files with ordinary file tools, or field by field with editRecord, which needs no text to match
 * (docs/adr/0008). These tools create the starting points, and move a project between text, plugin and
 * the mod manager:
 *
 *   newProject / addPatch  ->  edit plugin/*.yaml, editRecord  ->  buildProject  ->  deployProject
 *                                        ^                                      |
 *                                        +------ pullProject <- Creation Kit ---+
 */

const PROJECT = {
    type: 'string' as const,
    description: 'The project\'s name: its folder under the projects folder, and its mod name in the mod manager, e.g. "IronSwordBuff".'
};

/** "a" or "an" before a type's name: an Armor, a Weapon. */
const article = (type: string) => (/^[AEIOU]/i.test(type) ? 'an' : 'a');

/** Three numbers, as a list or as a text "x, y, z" — small models send an argument as text now and then. */
function tripleOf(value: unknown, field: string, example: string): [number, number, number] | undefined {
    if (value === undefined || value === null || value === '') return undefined;
    const parts = typeof value === 'string' ? value.replace(/[[\]]/g, '').split(/[\s,]+/).filter(Boolean).map(Number) : value;
    if (!Array.isArray(parts) || parts.length !== 3 || parts.some(n => typeof n !== 'number' || !Number.isFinite(n))) {
        throw new ToolError(ToolErrorCode.InvalidParams, `'${field}' is three numbers, [x, y, z] — e.g. ${example}.`);
    }
    return parts as [number, number, number];
}

/** newCell's `door`: the cell it stands in and where, as an object or as its JSON text. */
function doorOf(value: unknown): { in: string; at: [number, number, number]; rotation?: [number, number, number]; base?: string } | undefined {
    if (value === undefined || value === null || value === '') return undefined;
    let door: any = value;
    if (typeof door === 'string') {
        try { door = JSON.parse(door); } catch { door = undefined; }
    }
    if (typeof door !== 'object' || door === null || Array.isArray(door)) {
        throw new ToolError(ToolErrorCode.InvalidParams, '\'door\' is the door into the room: {"in": "WhiterunBreezehome", "at": [x, y, z]}.');
    }
    const at = tripleOf(door.at, 'door.at', '[256, -128, 64]');
    if (!at) throw new ToolError(ToolErrorCode.InvalidParams, '\'door.at\' is where the door stands in its cell, [x, y, z]: readRecord of the cell lists what is placed there, with the floor\'s height.');
    const rotation = tripleOf(door.rotation, 'door.rotation', '[0, 0, 1.5708]');
    return {
        in: requireString(door, 'in', 'The cell the door stands in, by editor ID, e.g. "WhiterunBreezehome".'),
        at,
        ...(rotation ? { rotation } : {}),
        ...(optionalString(door, 'base') ? { base: optionalString(door, 'base')! } : {})
    };
}

/**
 * newCell's `location`: a Location by editor ID or FormID, or a new one of the room's own — as an object or as its JSON text.
 * Asked for a room the game knows as a location of its own, none of the 8 plain location runs of eval rounds 144 and 149 wrote
 * a Location: newCell had nothing to say it with.
 */
function locationOf(value: unknown): string | { name: string; parent?: string; keywords?: string[] } | undefined {
    if (value === undefined || value === null || value === '') return undefined;
    let location: any = value;
    if (typeof location === 'string' && location.trim().startsWith('{')) {
        try { location = JSON.parse(location); } catch { location = undefined; }
    }
    if (typeof location === 'string') return location.trim();
    if (typeof location !== 'object' || location === null || Array.isArray(location)) {
        throw new ToolError(ToolErrorCode.InvalidParams, '\'location\' is a Location\'s editor ID, like "WhiterunBreezehomeLocation", or a ' +
            'location of the room\'s own: {"name": "Ashen Camp", "parent": "WhiterunHoldLocation", "keywords": ["LocTypeDwelling"]}.');
    }
    const keywords = typeof location.keywords === 'string' ? location.keywords.split(/[\s,]+/).filter(Boolean)
        : Array.isArray(location.keywords) ? location.keywords.filter((k: unknown): k is string => typeof k === 'string' && k.trim() !== '').map((k: string) => k.trim())
        : undefined;
    return {
        name: requireString(location, 'name', 'The new location\'s in-game name, e.g. "Ashen Camp".'),
        ...(optionalString(location, 'parent') ? { parent: optionalString(location, 'parent')! } : {}),
        ...(keywords?.length ? { keywords } : {})
    };
}

/**
 * The enchantment a copied record's text carries — its `ObjectEffect:` line, `049BB7:Skyrim.esm  # EnchWeaponFireDamage01` —
 * as its editor ID (from the comment) with its FormID, and the reference addPatch takes for it; null without one, or
 * without the text.
 */
function enchantmentIn(text: string | undefined): { named: string; id: string } | null {
    const line = text ? /^ObjectEffect:[ \t]*([^\s#]+)(?:[ \t]+#[ \t]*(\S+))?/m.exec(text) : null;
    if (!line) return null;
    const [, link, editorId] = line;
    return editorId ? { named: `${editorId} (${link})`, id: editorId } : { named: link, id: link };
}

/**
 * A text sent for several copies, split into each one's fields: the lines below a line that names a copy — its editor ID or
 * FormID, alone or as a heading (`## LItemBanditWeapon1H`, `LItemBanditWeapon1H:`, `*** Update File: …`), or its file — up to
 * the next such line, as the copy's file → its text; a patch's `*** Begin Patch` and `*** End Patch` lines left out. Undefined
 * where words stand before the first such line, or none names a copy: a text that is no copy's alone is written into none.
 */
export function inputSections(text: string, copies: { names: string[]; file?: string }[]): Map<string, string> | undefined {
    const fence = /^\s*\*\*\*\s*(?:Begin|End)\s+Patch\s*$/i;
    const named = (line: string): string | undefined => {
        const bare = line.replace(/^\s*(?:\*\*\*\s*(?:Update|Add)\s+File:\s*|[#>*=-]+\s*)/i, '').replace(/\s*(?:#.*)?$/, '')
            .replace(/[:*]+$/, '').trim().replace(/\\/g, '/').toLowerCase();
        if (!bare) return undefined;
        return copies.find(copy => copy.file && (copy.names.some(name => name.toLowerCase() === bare)
            || bare.endsWith(copy.file.toLowerCase())))?.file;
    };
    const sections = new Map<string, string[]>();
    let current: string[] | undefined;
    for (const line of text.split(/\r?\n/)) {
        if (fence.test(line)) continue;
        const file = named(line);
        if (file) {
            current = sections.get(file) ?? [];
            sections.set(file, current);
        } else if (current) {
            current.push(line);
        } else if (line.trim()) {
            return undefined;
        }
    }
    const texts = new Map([...sections].map(([file, lines]) => [file, lines.join('\n').trim()] as const).filter(([, body]) => body.length > 0));
    return texts.size > 0 ? texts : undefined;
}

export class ProjectsHandlers extends BaseHandler {
    protected toolSpecs(): ToolSpec[] {
        return [
            {
                definition: {
                    name: 'newProject',
                    annotations: {
                        title: 'New project',
                        readOnlyHint: false,
                        destructiveHint: false,
                        // A second call returns the existing project and changes nothing.
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Create an empty project for a new mod. Add records by writing YAML files into its plugin ' +
                        'folder — one file per record, in its type\'s group folder there; the answer\'s ' +
                        '`writeRecordsTo` gives the full path, so never write into the project folder itself, and its ' +
                        '`readFirst` the skill to read for each family of records. Write ' +
                        'each record the way readRecord shows a vanilla one of its type, and leave out FormKey: the ' +
                        'build assigns one. To change records other mods already have, use addPatch instead.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            plugin: { type: 'string', description: 'The plugin file name. Defaults to "<project>.esp".' },
                            light: {
                                type: 'boolean',
                                description: 'A light (ESL-flagged) plugin: no load-order slot; the build numbers its new records 000800–000FFF, 2,048 in all. Defaults to true.'
                            }
                        },
                        required: ['project']
                    }
                },
                onFailure: 'Failed to create the project',
                run: async args => ({
                    ...await asRequestError(this.backend.newProject({
                        project: requireString(args, 'project'),
                        plugin: optionalString(args, 'plugin'),
                        light: optionalBoolean(args, 'light')
                    })),
                    // At the moment records are about to be written: the instructions alone left a model without
                    // the family's skill in three of nine authoring runs (eval round 12).
                    readFirst: `Before writing a record, read its family's skill with readSkill: ` +
                        `${FAMILY_SKILLS.map(family => `"${family.skill}" for ${family.covers}`).join('; ')}. ` +
                        'Each holds the vanilla values its records are written with.'
                })
            },
            {
                definition: {
                    name: 'addPatch',
                    annotations: {
                        title: 'Add records to a patch',
                        readOnlyHint: false,
                        // Only adds files; a record already in the project is left as it is.
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Start or extend a patch — no newProject first: this creates the project when it does not ' +
                        'exist. Copies existing records into the project as overrides, to change them without touching ' +
                        'the plugins they come from: each from its current winning version — so other mods\' changes ' +
                        'are kept — or from `from`. Answers each copied file\'s full path and text (`added[].files`): change ' +
                        'its fields with editRecord, which needs no old text to match, or with your file tools, then run ' +
                        'buildProject. Past about ten records the text is left out ' +
                        '(`textLeftOut`) — read those files first. A record already in the project is reported under ' +
                        '`alreadyInProject` and left alone. An override changes the record itself, everywhere: for a new ' +
                        'record like a vanilla one, write a new file with newProject instead.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            records: {
                                type: 'array',
                                items: { type: 'string', description: 'A FormID like 012EB7:Skyrim.esm, or an editor ID like IronSword.' },
                                description: 'The records to override. Find them with findRecords.'
                            },
                            from: {
                                type: 'string',
                                description: 'Copy this plugin\'s version instead of the winning one, e.g. "Update.esm".'
                            },
                            merge: {
                                type: 'boolean',
                                description: 'Make a conflict patch: each copy from its winning version, with the changes other ' +
                                    'plugins made that it lost put back — their values, and the items they put into a list or took ' +
                                    'out (findConflicts names them). `added[].merged` says what was put back, and each field two ' +
                                    'plugins changed each its own way, where the later one\'s is kept. With `from`, only where it names ' +
                                    'the version that wins.'
                            },
                            plugin: {
                                type: 'string',
                                description: 'The new project\'s own plugin file name, used only when this call creates the ' +
                                    'project. Defaults to "<project>.esp". Not the plugin to copy from — that is `from`.'
                            }
                        },
                        required: ['project', 'records']
                    }
                },
                onFailure: 'Failed to add records to the patch',
                run: args => {
                    const records = optionalStringList(args, 'records', '["IronSword"]')?.map(recordRef);
                    if (!records) {
                        throw new ToolError(ToolErrorCode.InvalidParams,
                            `'records' is required: the FormIDs or editor IDs to override, e.g. ["IronSword"].`);
                    }
                    return asRequestError(this.backend.addPatch({
                        project: requireString(args, 'project'),
                        records,
                        from: optionalString(args, 'from'),
                        ...(args?.merge === true ? { merge: true } : {}),
                        plugin: optionalString(args, 'plugin')
                    })).then(async result => {
                        // Asked for a new sword "like the Iron Sword", a model patched the Iron Sword and renamed it:
                        // every Iron Sword in the game became its Flame Blade (eval probe 16). Said after every copied
                        // file's text, it left the model making its "new" sword or note this way in four of six misses
                        // (eval round 22): first now, naming the records, as a build's warnings went first (round 21).
                        // The copy was written empty while OpenClaw gave a model no tool to delete a file (round 26): deleteRecord now.
                        // Each copy said as what it is, an enchanted weapon or armor with its enchantment: asked for a fire
                        // enchantment, a model copied EnchIronSwordFire01 — the Iron Sword of Embers, a Weapon — renamed it for
                        // the enchantment and set a magnitude on it, then answered the sword built (eval rounds 97, 108 and 111's
                        // sword+detailed).
                        // A record's own file among those its copy wrote: a cell's is its folder's RecordData.yaml, beside the
                        // GroupRecordData.yaml of each block it sits in, the first of which was named as the record to edit (eval
                        // round 125's world runs).
                        const ownFile = (files: ProjectFile[]) => files.find(f => !/(^|[\\/])GroupRecordData\.yaml$/i.test(f.path)) ?? files[0];
                        const enchanted = result.added.map(record => enchantmentIn(ownFile(record.files)?.text));
                        const folder = result.project.pluginFolder;
                        const inPlugin = (file: string | undefined) =>
                            file?.startsWith(folder) ? file.slice(folder.length + 1).replace(/\\/g, '/') : undefined;
                        // A record copied before is left as it is, and said so with its own file and names: answered "is copied
                        // as an override" with the IronSword of an answer that copied nothing, a model sent addPatch for the
                        // same enchantment four more times and ran out of turns (eval round 113's sword+detailed #3).
                        const kept = result.alreadyInProject.map(record => {
                            const relative = inPlugin(ownFile(record.files)?.path);
                            const text = ownFile(record.files)?.text;
                            const named = /^(.+?) - [0-9A-Fa-f]{6}_/.exec(relative?.split('/').pop() ?? '')?.[1];
                            const editorId = /^EditorID:[ \t]*['"]?([^\s'"#]+)/m.exec(text ?? '')?.[1] ?? named;
                            // The copy's file is found by its name: made a new record since, it holds no FormKey of the record's.
                            const remade = text !== undefined && !new RegExp(`^FormKey:[ \\t]*${record.formId.replace(/[.]/g, '\\.')}`, 'im').test(text);
                            return { formId: record.formId, named, editorId, relative, remade, type: relative?.split('/')[0].replace(/s$/, '') };
                        });
                        // A copy the merge was written into is said in the merge's note, not as one left as it was.
                        const mergedInto = new Set(result.alreadyInProject.filter(record => record.merged).map(record => record.formId));
                        const overrides = kept.filter(k => !k.remade && !mergedInto.has(k.formId));
                        const copies = result.added.map((record, i) => `${record.editorId ?? record.formId} (${record.editorId ? `${record.formId}, ` : ''}` +
                            `${article(record.type)} ${record.type}${enchanted[i] ? ` enchanted with ${enchanted[i]!.named}` : ''})`);
                        const carried = enchanted.find(e => e !== null);
                        const one = copies.length === 1;
                        // The copy's fields changed with the edit tool, its old text from memory: 32 of the 82 such edits in runs
                        // with addPatch of eval rounds 49–57 were refused, 3 of its 42 editRecord calls — and round 57's
                        // sword+detailed #5 ran out of turns on fifteen refused edits of addPatch's Iron Sword.
                        const first = result.added[0];
                        const shown = overrides[0] ?? kept[0];
                        const example = first?.editorId ?? first?.formId ?? shown?.editorId ?? shown?.formId ?? 'IronSword';
                        // A field of the copy's own kind; a name would rename the record everywhere, as the note says.
                        const field = ({
                            Weapon: '{"path": "BasicStats.Damage", "value": 12}', Armor: '{"path": "ArmorRating", "value": 30}',
                            Spell: '{"path": "BaseCost", "value": 60}', Npc: '{"path": "AIData.Aggression", "value": "Unaggressive"}'
                        } as Record<string, string>)[first?.type ?? shown?.type ?? 'Weapon'] ?? '{"path": "<the field>", "value": <its new value>}';
                        const record = (first ? inPlugin(ownFile(first.files)?.path) : undefined) ?? shown?.relative ?? example;
                        // The copy made new by one call, its changes kept: told to write a new file and delete the copy, a model
                        // deleted the copy that held the price of 150 it had set, and wrote the new sword at 25 (eval round 89's
                        // sword+detailed #1); 7 of 33 renamed copies of rounds 87–105 went that way.
                        const fresh = `New${example.replace(/[^A-Za-z0-9_]/g, '')}`;
                        const renew = `editRecord {"project": ${JSON.stringify(result.project.project)}, "record": ${JSON.stringify(record)}, ` +
                            `"set": [{"path": "EditorID", "value": "${fresh}"}], "remove": ["FormKey"], "build": true}`;
                        const copiedNote = copies.length === 0 ? '' :
                            `${copies.join(', ')} ${one ? 'is' : 'are'} copied as ${one ? 'an override' : 'overrides'}: ` +
                                `${one ? 'it changes that record' : 'each changes its record'} itself, everywhere the game uses ` +
                                `${one ? 'it' : 'them'} — a new name renames ${one ? 'it' : 'them'} everywhere. If a new record ` +
                                `like it was meant, one call makes ${one ? 'the copy' : 'the first copy'} a new record, keeping what it holds and ` +
                                `what was changed in it, and builds: ${renew}, with an EditorID of its own in place of ${fresh}.` +
                                (carried ? ` An enchantment is a record of its own: ${carried.named} is the ObjectEffect ` +
                                    `${one ? 'this copy carries' : 'a copy carries'} — to change it or make one like it, copy that record, ` +
                                    `addPatch {"project": ${JSON.stringify(result.project.project)}, "records": [${JSON.stringify(carried.id)}]}, ` +
                                    'or write a new ObjectEffect from it.' : '');
                        const single = overrides.length === 1;
                        const keptNote = overrides.length === 0 ? '' :
                            `${overrides.map(k => `${k.editorId ? `${k.editorId} (${k.formId})` : k.formId}${k.relative ? `, ${k.relative}` : ''}`).join('; ')} ` +
                                `${single ? 'is' : 'are'} in the project already, copied before, and not copied again: ${single ? 'that file is the copy' : 'those files are the copies'}, ` +
                                `as ${single ? 'it was' : 'they were'} left, with every change made to ${single ? 'it' : 'them'} — change ${single ? 'it' : 'them'} there.` +
                                (copies.length > 0 ? '' : ` ${single ? 'It is' : 'Each is'} still an override, changing ${single ? 'that record' : 'its record'} ` +
                                    `everywhere the game uses it. If a new record like it was meant, one call makes ${single ? 'the copy' : 'the first copy'} ` +
                                    `a new record, keeping what it holds, and builds: ${renew}, with an EditorID of its own in place of ${fresh}.`);
                        // A copy made a new record keeps the copy's file name, by which the record counts as copied: said so, with the
                        // move that frees the name for a copy of the record itself.
                        const remadeNote = kept.filter(k => k.remade).map(k => {
                            const source = k.named ?? k.formId;
                            return `${k.relative} was ${source}'s copy (${k.formId}) and is a new record of its own now, ${k.editorId} — it keeps ` +
                                `the copy's file name, so ${source} was not copied again. To change ${source} itself as well, give that file a ` +
                                `name of its own first, moveRecord {"project": ${JSON.stringify(result.project.project)}, "from": ` +
                                `${JSON.stringify(k.relative)}, "to": ${JSON.stringify(`${k.relative?.split('/')[0]}/${k.editorId}.yaml`)}}, then send addPatch again.`;
                        }).join(' ');
                        // What is placed in a cell is no field of its copy: five of eval round 125's world runs wrote a chest into the
                        // cell's own file under fields of their own (Items, PlacedRecords, References), and no run placed one.
                        // Its full path: written by the path in the plugin folder alone, three of eval round 128's world runs' references
                        // landed outside the project, cut down to plugin/Temporary/ by the plugin's write guard.
                        const cells = result.added.filter(record => record.type === 'Cell');
                        const cellNote = cells.map(cell => {
                            const file = ownFile(cell.files)?.path;
                            const folder = file?.replace(/[\\/]RecordData\.yaml$/i, '');
                            const sep = file?.includes('\\') ? '\\' : '/';
                            const name = cell.editorId ?? cell.formId;
                            return `What is placed in ${name} is no field of the cell's file: each reference is a file of its own in ` +
                                `${folder ? `${folder}${sep}Temporary${sep}` : 'the cell\'s folder\'s Temporary folder'}, written at that full path ` +
                                `with your file tools — readRecord {"record": ${JSON.stringify(name)}} lists what the game places there, and the ` +
                                'lines a new one is written with.';
                        }).join(' ');
                        // A merge said first, by plugin: eval round 135's conflict #2 and #5 answered with the patch built and named
                        // neither plugin the user asked to be told of — the answer's only word on the merge was `merged` beside the note.
                        const mergedNote = [...result.added, ...result.alreadyInProject.filter(record => record.merged)
                            .map(record => ({ ...record, from: record.from ?? 'the winning plugin', editorId: record.editorId ?? null }))].filter(record => record.merged).map(record => {
                            const merged = record.merged!;
                            const name = record.editorId ?? record.formId;
                            const plugins = merged.plugins.join(' and ');
                            return (mergedInto.has(record.formId)
                                ? `${name} was in the project already: what ${record.from}, the version that wins, lost of ${plugins}'s changes is ` +
                                  `put back into its copy, beside the changes made to it: ${merged.changes.join('; ')}`
                                : `${name} is copied from ${record.from}, the version that wins, with what it lost of ${plugins}'s changes put back: ` +
                                  merged.changes.join('; ')) +
                                (merged.clashes?.length ? `; changed by two plugins each its own way: ${merged.clashes.join('; ')}` : '') +
                                (merged.notWritten ? ` — but ${merged.notWritten}; write them with editRecord` : '') +
                                `. Tell the user whose changes ${record.from} wins over — ${merged.plugins.join(', ')} — and that the patch keeps them.`;
                        }).join(' ');
                        // Arguments addPatch has not, said so; a text among them — the fields to change in the copy, `Entries:` and the
                        // items to add — written into the one copy as editRecord writes it. Eval round 130's leveled runs sent the Silver
                        // Sword's entry as `input` four times, dropped without a word: each answer said "copied", and none passed.
                        // `merge` among them: eval round 135's conflict runs were told it was "no argument of addPatch's: nothing
                        // of it was used" by the call that merged their patch.
                        const known = new Set(['project', 'records', 'from', 'plugin', 'merge']);
                        const extra = Object.keys(args).filter(key => !known.has(key));
                        const textKey = extra.find(key => typeof args[key] === 'string' && /\S/.test(args[key] as string));
                        const copyFile = (first ? inPlugin(ownFile(first.files)?.path) : undefined) ?? shown?.relative;
                        let inputNote = '';
                        let inputWritten = false;
                        // A text for each copy, under a line naming it: eval round 135's leveled #5 copied the bandits' three weapon
                        // lists with the Silver Sword's entry for each so, written nowhere, then deleted the copies the build warned
                        // changed nothing, twice, and answered with no plugin.
                        const sections = textKey && result.added.length > 1
                            ? inputSections(args[textKey] as string, result.added.map(added => ({
                                names: [added.editorId, added.formId].filter((name): name is string => !!name),
                                file: inPlugin(ownFile(added.files)?.path)
                            })))
                            : undefined;
                        if (textKey && records.length === 1 && copyFile) {
                            const written = await this.backend.editInput({ project: result.project.project, record: copyFile, input: args[textKey] as string })
                                .catch((error: unknown) => error instanceof Error ? error : new Error(String(error)));
                            if (written instanceof Error) {
                                inputNote = `\`${textKey}\` is no argument of addPatch's, and what it held could not be written into ${copyFile}: ` +
                                    `${written.message} Change the copy with editRecord.`;
                            } else if (written.unread || written.changed.length === 0) {
                                inputNote = `\`${textKey}\` is no argument of addPatch's, and nothing of it is written: ` +
                                    (written.unread ? 'it reads as no fields of the copy' : `the copy holds it already (${written.kept.join('; ')})`) +
                                    `. Change ${copyFile} with editRecord.`;
                            } else {
                                inputWritten = true;
                                inputNote = `\`${textKey}\` is no argument of addPatch's — what it held is written into ${copyFile}, as editRecord ` +
                                    `writes it: ${written.changed.join('; ')}` +
                                    (written.kept.length > 0 ? `; left out, as the copy holds them already: ${written.kept.join('; ')}` : '') + '.';
                            }
                        } else if (textKey && sections) {
                            const said: string[] = [];
                            const failed: string[] = [];
                            for (const [file, input] of sections) {
                                const written = await this.backend.editInput({ project: result.project.project, record: file, input })
                                    .catch((error: unknown) => error instanceof Error ? error : new Error(String(error)));
                                if (written instanceof Error) failed.push(`${file}: ${written.message}`);
                                else if (written.unread) failed.push(`${file}: it reads as no fields of the copy`);
                                else if (written.changed.length === 0) said.push(`${file}: nothing, the copy holds it already (${written.kept.join('; ')})`);
                                else said.push(`${file}: ${written.changed.join('; ')}`);
                            }
                            inputWritten = said.length > 0 && failed.length === 0;
                            inputNote = `\`${textKey}\` is no argument of addPatch's — what it held for each copy is written into it, as editRecord ` +
                                `writes it: ${said.join('. ')}` + (failed.length > 0 ? `. Not written: ${failed.join('. ')} — change those copies with editRecord` : '') + '.';
                        } else if (textKey) {
                            inputNote = `\`${textKey}\` is no argument of addPatch's, and nothing of it is written: ` +
                                `${records.length === 1 ? 'no copy was made to write it into' : `this call copied ${records.length} records`} — ` +
                                'change each copy with editRecord.';
                        }
                        const ignored = extra.filter(key => key !== textKey);
                        const ignoredNote = ignored.length === 0 ? '' :
                            `${ignored.map(key => `\`${key}\``).join(', ')} ${ignored.length === 1 ? 'is no argument' : 'are no arguments'} of addPatch's: nothing of ${ignored.length === 1 ? 'it' : 'them'} was used.`;
                        return {
                            note: [mergedNote, inputNote, ignoredNote, copiedNote, keptNote, remadeNote, cellNote].filter(Boolean).join(' '),
                            nextStep: inputWritten
                                ? `${sections ? 'The copies hold' : 'The copy holds'} what \`${textKey}\` sent: run buildProject next — or change ${sections ? 'one' : 'it'} further with editRecord, {"record": ` +
                                  `${JSON.stringify(copyFile)}, "set": [${field}], "build": true}.`
                                : cells.length === result.added.length && cells.length > 0
                                ? 'Write what is placed in the cell as a file of its own in its Temporary folder, at the full path the note names, ' +
                                  'then run buildProject.'
                                : [...result.added, ...result.alreadyInProject].length > 0 && [...result.added, ...result.alreadyInProject].every(record => record.merged && !record.merged.notWritten)
                                ? 'The patch holds what each plugin changed: run buildProject next, then tell the user whose changes it keeps, as the note says.'
                                : `Change a copy's fields with editRecord, which needs no old text to match — {"record": ` +
                                  `"${example}", "set": [${field}]} — then run buildProject.`,
                            ...result
                        };
                    });
                }
            },
            {
                definition: {
                    name: 'editRecord',
                    annotations: {
                        title: 'Edit record fields',
                        readOnlyHint: false,
                        // Writes over the values it is given; the rest of the file stays as it is.
                        destructiveHint: true,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    // 95 of the text edits were refused in eval rounds 40–47: old text from memory, indented otherwise
                    // than the file, or made stale by a build's own changes (docs/adr/0008). `set` is a list of paths and
                    // values, no object keyed by path: Ollama's tool-call parser dropped calls with a dotted key (args.ts).
                    description:
                        'Change fields of one record file of a project by their paths — no old text to match. `set` lists ' +
                        'each field\'s path and its value: [{"path": "BasicStats.Damage", "value": 12}, {"path": "Name", ' +
                        '"value": "Flame Blade"}, {"path": "Effects[0].Data.Magnitude", "value": 40}] — a text field (Name, ' +
                        'Description) takes its words, a link a FormID or an editor ID, a list a list, written whole; an ' +
                        'object sets the fields it names inside that part and keeps its others, as {"path": "BasicStats", ' +
                        '"value": {"Damage": 12}} does. `remove` takes paths to delete, any the file has: ["Critical", "Effects[1]"]. Changes ' +
                        'only those lines, refuses a field the record\'s type does not have, and answers the file\'s new text. ' +
                        'Then run buildProject — or send `"build": true`, and this call builds the project after the change and ' +
                        'answers that build, as buildProject does.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            record: {
                                type: 'string',
                                description: 'The record file: its path in the plugin folder, like "Weapons/FlameBlade.yaml", or the record\'s EditorID.'
                            },
                            set: {
                                type: 'array',
                                items: {
                                    type: 'object',
                                    description: 'One field: its path and its new value.',
                                    properties: {
                                        path: {
                                            type: 'string',
                                            description: 'The field: fields joined with dots, a list\'s item by its number from 0 — "BasicStats.Damage", "Name", "Effects[0].Data.Magnitude".'
                                        },
                                        value: { description: 'Its new value: a number, words, a FormID or editor ID for a link, a list, or an object of fields.' }
                                    },
                                    required: ['path', 'value']
                                },
                                description: 'The fields to change, each its path and its new value.'
                            },
                            remove: {
                                type: 'array',
                                items: { type: 'string', description: 'A field\'s path, like "Critical" or "Effects[1]".' },
                                description: 'Paths of fields, or of a list\'s items, to delete.'
                            },
                            build: {
                                type: 'boolean',
                                description: 'true: build the project right after the change, and answer that build as buildProject does.'
                            }
                        },
                        required: ['project', 'record']
                    }
                },
                onFailure: 'Failed to edit the record',
                // A build refused, its field then set here, the model answered "Build Status: Success" with no build since
                // (eval round 61's npc+detailed #5): the plugin was the one before. Told by the rename warning to make its
                // call and build again, the model made the call and answered with no build since, twice in eval round 75:
                // with `build`, the change and the build are one call, and the warnings write theirs so.
                run: async args => {
                    const project = requireString(args, 'project');
                    const build = optionalBoolean(args, 'build');
                    const edited = await asRequestError(this.backend.editRecord({
                        project,
                        record: requireString(args, 'record', 'The record file, like "Weapons/FlameBlade.yaml", or its EditorID.'),
                        set: optionalFieldValues(args, 'set', '[{"path": "BasicStats.Damage", "value": 12}]'),
                        remove: optionalStringList(args, 'remove', '["Critical"]')
                    }));
                    // A path placed elsewhere than sent is said where the model reads first, not only among the changes.
                    const placed = edited.changed.filter(change => change.includes('(sent as '));
                    // So is a record found by the name its copy's file has, not its EditorID: "found Weapons/IronSword - 012EB7_Skyrim.esm.yaml …".
                    const copy = edited.changed.find(change => change.startsWith('found '));
                    const copyNote = copy ? ` F${copy.slice(1)}.` : '';
                    const placedNote = (placed.length > 0 ? ` Set where the record has it, not where it was sent: ${placed.join('; ')}.` : '') + copyNote;
                    // A remove of a field the file has not: "Changed and built" beside "no ObjectEffect to remove" (refusal probes of
                    // 2026-10-31, E09).
                    const made = edited.changed.filter(change => change !== copy);
                    if (made.length > 0 && made.every(change => /^no .+ to remove$/.test(change))) {
                        const nothing = `Nothing changed: ${made.join('; ')} — the file is as it was.${copyNote}`;
                        if (!build) return { nextStep: nothing, ...edited };
                        const { text: _unchanged, ...unchanged } = edited;
                        const built = await this.build(project);
                        return { ...built, nextStep: `${nothing} ${built.nextStep ?? ''}`.trim(), edited: unchanged };
                    }
                    if (!build) return { nextStep: `Run buildProject next — the plugin has this change only once a build answers success.${placedNote}`, ...edited };
                    // Without the file's text: the build may change the file again, and its answer says how.
                    const { text: _text, ...change } = edited;
                    // The change is made, its build refused: told only "Changed …", the model sent the same edit again, 9 times in 3
                    // runs of eval rounds 89–101, the refusal being for another file. The plugin's build check reads "Changed … Not built:".
                    const built = await this.build(project).catch((error: unknown) => {
                        if (error instanceof Error) {
                            // Refused for other files alone, said first: the long list of what this edit changed read as the
                            // refusal, and round 128's armor+detailed #3 sent the same edit of its helmet 5 times while the
                            // build refused its magic effect.
                            const edited = change.file.replace(/\\/g, '/').split('/plugin/').pop() ?? '';
                            const refused = [...new Set([...error.message.matchAll(/(?:^|[\s;:])([A-Z][\w ]*\/[^:;]+?\.yaml):/g)].map(m => m[1]))];
                            const others = refused.filter(file => file !== edited);
                            const lead = others.length > 0 && !refused.includes(edited)
                                ? `The build refused ${others.join(' and ')}, not this edit's file: fix ${others.length === 1 ? 'that' : 'those'} next, ` +
                                  'and do not send this edit again. '
                                : '';
                            error.message = `${lead}Changed ${change.file} — this edit is made, and sending it again changes nothing: ` +
                                `${change.changed.join('; ')}.${placedNote} The build after it: ${error.message}`;
                        }
                        throw error;
                    });
                    if (built.status) return { ...built, ...(placedNote && built.nextStep ? { nextStep: `${built.nextStep}${placedNote}` } : {}), edited: change };
                    const { nextStep, ...rest } = built;
                    return { nextStep: `${nextStep?.replace(/^Built\./, 'Changed and built: the plugin has this change.') ?? ''}${placedNote}`, ...rest, edited: change };
                }
            },
            // A file written where the build does not read it stayed there: OpenClaw gives a model no tool that moves or deletes
            // a file, so the model wrote it again where it belonged and left the first (eval round 66: 6 runs; the eval failed one on a
            // copied escape in the copy left behind, another ended on a warning it could not act on). The build's answers write
            // these calls out, as the rename warning writes editRecord's.
            {
                definition: {
                    name: 'moveRecord',
                    annotations: {
                        title: 'Move record file',
                        readOnlyHint: false,
                        // Overwrites nothing — a file already at `to` is refused — and the file's text goes with it.
                        destructiveHint: false,
                        // Moved once, the file is no longer at `from`: a second call is refused and changes nothing.
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Move one record file into a project\'s plugin folder: out of a folder the build does not read, to ' +
                        'its .yaml name, or in from outside every project. The build\'s answers write this call out for each ' +
                        'misplaced file. Then run buildProject.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            from: {
                                type: 'string',
                                description: 'The file: its path in the plugin folder, like "Notes/MavenWarning.yaml", the record\'s EditorID, ' +
                                    'or, for a file outside the plugin folder, its path from the projects folder or its full path.'
                            },
                            to: { type: 'string', description: 'Its path in the plugin folder, like "Books/MavenWarning.yaml".' }
                        },
                        required: ['project', 'from', 'to']
                    }
                },
                onFailure: 'Failed to move the record file',
                run: args => asRequestError(this.backend.moveRecord({
                    project: requireString(args, 'project'),
                    from: requireString(args, 'from', 'The file: its path in the plugin folder, its EditorID, or its full path.'),
                    to: requireString(args, 'to', 'Its path in the plugin folder, like "Books/MavenWarning.yaml".')
                }).then(result => ({
                    nextStep: 'Run buildProject next — the plugin has this change only once a build answers success.',
                    ...result
                })))
            },
            {
                definition: {
                    name: 'deleteRecord',
                    annotations: {
                        title: 'Delete record file',
                        readOnlyHint: false,
                        destructiveHint: true,
                        // A second call finds no file and changes nothing.
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Delete one record file of the projects folder: a copy left behind where the build does not read it, ' +
                        'or a record the mod no longer needs. The build\'s answers write this call out for each copy left ' +
                        'behind. Then run buildProject.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            record: {
                                type: 'string',
                                description: 'The file: its path in the plugin folder, like "Notes/MavenWarning.yaml", the record\'s EditorID, ' +
                                    'or, for a file outside the plugin folder, its path from the projects folder or its full path.'
                            },
                            build: {
                                type: 'boolean',
                                description: 'true: build the project right after the delete, and answer that build as buildProject does.'
                            }
                        },
                        required: ['project', 'record']
                    }
                },
                onFailure: 'Failed to delete the record file',
                // Its Silver Sword of its own deleted, the model answered with no build since (eval round 129's leveled #1): with
                // `build`, the delete and the build are one call, as editRecord's are, and the notes write theirs so.
                run: async args => {
                    const project = requireString(args, 'project');
                    const result = await asRequestError(this.backend.deleteRecord({
                        project,
                        record: requireString(args, 'record', 'The file: its path in the plugin folder, its EditorID, or its full path.')
                    }));
                    if (!optionalBoolean(args, 'build')) {
                        return { nextStep: 'Run buildProject next — the plugin has this change only once a build answers success.', ...result };
                    }
                    const built = await this.build(project).catch((error: unknown) => {
                        if (error instanceof Error) {
                            error.message = `Deleted ${result.deleted} — sending this again changes nothing. The build after it: ${error.message}`;
                        }
                        throw error;
                    });
                    if (built.status) return { ...built, deleted: result.deleted };
                    const { nextStep, ...rest } = built;
                    return {
                        nextStep: nextStep?.replace(/^Built\./, 'Deleted and built: the plugin is without that record.') ?? '',
                        ...rest, deleted: result.deleted
                    };
                }
            },
            {
                definition: {
                    name: 'editTerrain',
                    annotations: {
                        title: 'Change the ground outdoors',
                        readOnlyHint: false,
                        destructiveHint: false,
                        // A raise twice raises twice.
                        idempotentHint: false,
                        openWorldHint: false
                    },
                    description:
                        'Change the ground of a worldspace in a circle: `flatten` it to a height (a plot for a house, a road), `raise` ' +
                        'or `lower` it by an `amount`, or `smooth` it — across every exterior cell it touches, blended into the ' +
                        'ground around over `falloff`. The project gets a copy of each cell with its landscape, heights and normals ' +
                        'written anew, the edge two cells share kept the same; a second edit changes the copy further. Answers each ' +
                        'cell changed and the game\'s trees, rocks and buildings there whose ground moved: they float or sink now. ' +
                        'Positions are world units, as readRecord gives them; a cell is 4096 wide. Then buildProject.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            at: { type: 'string', description: 'The centre, "x, y" in world units — e.g. "17400, -47600"; a "x, y, z" position works too.' },
                            radius: { type: 'number', description: 'Changed fully within this many units of the centre, e.g. 400.' },
                            falloff: { type: 'number', description: 'Blended into the ground over this far beyond the radius; half the radius unless given.' },
                            action: { type: 'string', enum: ['flatten', 'raise', 'lower', 'smooth'], description: 'What to do with the ground in the circle.' },
                            height: { type: 'number', description: 'flatten: the height to flatten to; the ground\'s at the centre unless given.' },
                            amount: { type: 'number', description: 'raise and lower: by how many units, e.g. 200.' },
                            worldspace: { type: 'string', description: 'The worldspace by editor ID; Tamriel, Skyrim\'s, unless given.' }
                        },
                        required: ['project', 'at', 'radius', 'action']
                    }
                },
                onFailure: 'Failed to change the ground',
                run: async args => {
                    const at = /^\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)/.exec(String(args?.at ?? ''));
                    if (!at) throw new ToolError(ToolErrorCode.InvalidParams, '\'at\' is the centre as "x, y" in world units — e.g. "17400, -47600".');
                    const radius = optionalNumber(args, 'radius');
                    if (radius === undefined || radius <= 0) throw new ToolError(ToolErrorCode.InvalidParams, '\'radius\' is a number of units above 0, e.g. 400.');
                    const action = optionalChoice(args, 'action', ['flatten', 'raise', 'lower', 'smooth'] as const);
                    if (!action) throw new ToolError(ToolErrorCode.InvalidParams, '\'action\' is flatten, raise, lower or smooth.');
                    const amount = optionalNumber(args, 'amount');
                    if ((action === 'raise' || action === 'lower') && amount === undefined)
                        throw new ToolError(ToolErrorCode.InvalidParams, `'amount' is how many units to ${action} the ground by, e.g. 200.`);
                    const result = await asRequestError(this.backend.editTerrain({
                        project: requireString(args, 'project'), x: Number(at[1]), y: Number(at[2]), radius, action,
                        falloff: optionalNumber(args, 'falloff'), height: optionalNumber(args, 'height'), amount,
                        worldspace: optionalString(args, 'worldspace')
                    }));
                    const moved = result.gameReferences.length
                        ? ` The ground moved under ${result.gameReferences.length} of the game's references — ${result.gameReferences.slice(0, 3)
                            .map(r => `${r.base} at ${r.position}, ${r.groundMoved > 0 ? 'now under' : 'now above'} the ground by ${Math.abs(r.groundMoved)}`).join('; ')}` +
                          ': tell the user, or move them with the cell\'s references (skyrim-modding-world).'
                        : '';
                    const navmesh = result.cells.some(c => c.navmesh)
                        ? ` NPCs walk on the cells' navmesh, which the ground no longer fits where it moved much: generateNavmesh each of ${result.cells.map(c => c.cell).join(', ')} after the build.` : '';
                    return {
                        ...result,
                        nextStep: `The ground of ${result.cells.length} cell(s) is changed in the project: run buildProject next.${moved}${navmesh} ` +
                            'Distant terrain (LOD) still shows the old ground until xLODGen is run.'
                    };
                }
            },
            {
                definition: {
                    name: 'generateNavmesh',
                    annotations: {
                        title: 'Make a cell\'s navmesh',
                        readOnlyHint: false,
                        // Makes the project's navmesh of the cell anew each time, from the same objects the same one.
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Make a cell\'s navmesh — the triangles NPCs and followers walk on — from what stands in it: the collision ' +
                        'of its floors, walls, stairs and furniture, outdoors its ground, the project\'s own objects with the game\'s. ' +
                        'Written into the project over the cell\'s navmeshes (FormIDs kept; a new one for a room the project made), ' +
                        'its load doors linked, outdoors its edges linked with the cells beside it, deep water left out; no Creation ' +
                        'Kit needed. Run it after placing, moving or removing objects, after editTerrain, or for a new room; the ' +
                        'project is built first where it changed. Answers the triangles, the doors linked and, over the game\'s ' +
                        'navmesh, how much of the old one the new covers. Then buildProject.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            cell: { type: 'string', description: 'The cell by editor ID or FormID — an interior like "WhiterunBreezehome", one the project made, or an exterior cell like "Riverwood".' }
                        },
                        required: ['project', 'cell']
                    }
                },
                onFailure: 'Failed to make the navmesh',
                run: async args => {
                    const result = await asRequestError(this.backend.generateNavmesh({ project: requireString(args, 'project'), cell: requireString(args, 'cell') }));
                    const over = result.before
                        ? ` It replaces the game's navmesh of ${result.before.triangles} triangles: it covers ${Math.round(result.before.oldCovered * 100)}% of the old ` +
                          `one, and ${Math.round(result.before.newOnOld * 100)}% of it lies on the old one.`
                        : ' The cell had no navmesh: this one is new.';
                    const doors = result.doors.length
                        ? ` ${result.doors.length} load door(s) linked.`
                        : ' No load door stands in the cell: NPCs walk inside it, but none comes in until a door links it to another cell (skyrim-modding-world).';
                    const unread = result.objects?.unreadMeshes.length
                        ? ` ${result.objects.unreadMeshes.length} mesh(es) could not be read, so actors walk through them: ${result.objects.unreadMeshes.slice(0, 3).join(', ')}.`
                        : '';
                    const others = result.navmeshes?.length
                        ? ` The cell has ${result.navmeshes.length} navmeshes: each got its share, FormIDs kept.` : '';
                    const neighbours = result.neighbours?.length
                        ? ` The edges along the cell's sides are linked with ${result.neighbours.length} navmesh(es) of the cells beside it, written into the project too.` : '';
                    return {
                        ...result,
                        nextStep: `The navmesh of ${result.cell} is written into the project, ${result.triangles} triangles: run buildProject next.` +
                            `${over}${doors}${unread}${others}${neighbours} Tell the user it is generated: NPCs walking there in game show it works.`
                    };
                }
            },
            {
                definition: {
                    name: 'newWorldspace',
                    annotations: {
                        title: 'Make a new worldspace',
                        readOnlyHint: false,
                        destructiveHint: false,
                        // A second call with the same editor ID is refused: the worldspace is made once.
                        idempotentHint: false,
                        openWorldHint: false
                    },
                    description:
                        'Make a new worldspace — an outdoor land of the mod\'s own, apart from Skyrim, like Skuldafn or the Soul ' +
                        'Cairn: cells × cells exterior cells around grid 0, 0, each 4096 units (about 58 m) wide, with their ground ' +
                        'flat at `height` or shaped by a heightmap image (PNG or RAW, brighter higher, its top north) between `lowest` ' +
                        'and `highest`, under the game\'s sky and weather (`climate`), with water at `water` or none. Cell 0, 0 is ' +
                        'named <editorId>Start and holds a COC marker, so `coc <editorId>Start` in the console goes there and a ' +
                        'door can lead there. Creates the project when it does not exist, as a full plugin, and makes a light ' +
                        'one full: the game shows a light plugin\'s new worldspace without its ground. Then buildProject; the ground can be ' +
                        'shaped further with editTerrain or in Blender (exportCell), objects placed in its cells as anywhere, and ' +
                        'generateNavmesh makes each cell\'s navmesh.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            editorId: { type: 'string', description: 'The worldspace\'s editor ID, like "MyValleyWorld": letters and digits.' },
                            name: { type: 'string', description: 'Its in-game name, like "Hidden Valley". Defaults to the editor ID.' },
                            cells: { type: 'number', description: 'Its width in cells, 1 to 32: cells × cells of them. Defaults to 4 (about 230 m across).' },
                            height: { type: 'number', description: 'The ground\'s height everywhere, in game units, when no heightmap is given. Defaults to 0.' },
                            heightmap: { type: 'string', description: 'The full path of a heightmap image to shape the ground with: a PNG (grey or colour, 8 or 16 bits) or a square RAW of 16-bit heights.' },
                            lowest: { type: 'number', description: 'The height of the heightmap\'s darkest pixels, in game units. Defaults to 0.' },
                            highest: { type: 'number', description: 'The height of its brightest pixels. Defaults to 4096 (about 58 m above the lowest).' },
                            water: { type: 'number', description: 'The water\'s height, in game units: the ground below it is under water. Leave it out for none.' },
                            climate: { type: 'string', description: 'The sky and weather, a climate\'s editor ID. Defaults to SkyrimClimate, the weather of Skyrim\'s own land.' },
                            texture: { type: 'string', description: 'The ground\'s look, a landscape texture\'s editor ID like LFieldGrass01 (the default, grass), LSnow01 or LDirt02.' }
                        },
                        required: ['project', 'editorId']
                    }
                },
                onFailure: 'Failed to make the worldspace',
                run: async args => {
                    const project = requireString(args, 'project');
                    const made = await asRequestError(this.backend.newWorldspace({
                        project,
                        editorId: requireString(args, 'editorId'),
                        name: optionalString(args, 'name'),
                        cells: optionalNumber(args, 'cells'),
                        height: optionalNumber(args, 'height'),
                        heightmap: optionalString(args, 'heightmap'),
                        lowest: optionalNumber(args, 'lowest'),
                        highest: optionalNumber(args, 'highest'),
                        water: optionalNumber(args, 'water'),
                        climate: optionalString(args, 'climate'),
                        texture: optionalString(args, 'texture')
                    }));
                    const steep = made.ground.clamped
                        ? ` ${made.ground.clamped} step(s) of the ground were steeper than the game's landscape holds and were cut: give the heightmap a smaller range (lowest to highest) for smooth slopes.`
                        : '';
                    return {
                        ...made,
                        nextStep: `${made.editorId} is written into the project: ${made.cells} cells from ${made.grid.from} to ${made.grid.to}, ` +
                            `the ground from ${Math.round(made.ground.lowest)} to ${Math.round(made.ground.highest)}.${steep} Build next: buildProject ` +
                            `{"project": ${JSON.stringify(project)}}. In game, \`coc ${made.start.cell}\` goes there. To let players in, place a load ` +
                            `door pair — one where they come from, its twin in ${made.start.cell}, each teleporting to the other (skyrim-modding-world). ` +
                            `Once objects stand in a cell, generateNavmesh {"project": ${JSON.stringify(project)}, "cell": "<the cell>"} makes where NPCs walk. ` +
                            'Distant land shows only once LOD is made for it (xLODGen); until then the land ends at the loaded cells.'
                    };
                }
            },
            {
                definition: {
                    name: 'newCell',
                    annotations: {
                        title: 'Make a new interior cell',
                        readOnlyHint: false,
                        destructiveHint: false,
                        // A second call with the same editor ID is refused: the cell is made once.
                        idempotentHint: false,
                        openWorldHint: false
                    },
                    description:
                        'Make a new interior cell — a room of the mod\'s own: a cellar, a cave, a shop\'s back room — made like a room ' +
                        'of the game (`like`): its lighting and sound, and its walls, floor, lights and furniture copied as the mod\'s ' +
                        'own references, so it stands lit and walkable. With `door`, the player walks in: the room keeps its own load ' +
                        'door, and the door the game pairs with it is placed `at` a spot in the cell `in` — each teleporting to the ' +
                        'other — and the new room takes that cell\'s location and owner. A COC marker stands where the player arrives, ' +
                        'so `coc <cell>` goes there. Creates the project when it does not exist. Then generateNavmesh the new cell and ' +
                        'the door\'s cell, and buildProject. Move, add or remove what stands in the room as in any cell.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            cell: { type: 'string', description: 'The new cell\'s editor ID, like "BreezehomeCellar": letters and digits.' },
                            name: { type: 'string', description: 'Its in-game name, shown on the door, like "Breezehome Cellar". Defaults to the editor ID.' },
                            like: { type: 'string', description: 'The game\'s room it is made like, a cell\'s editor ID — findRecords with types ["CELL"] and "cellar", "cave"…: e.g. MorthalAlvasHouseCellar.' },
                            door: {
                                type: 'object',
                                description: 'A door pair into the room from the cell the player comes from.',
                                properties: {
                                    in: { type: 'string', description: 'The cell the door to the room stands in, its editor ID, like "WhiterunBreezehome".' },
                                    at: { type: 'array', items: { type: 'number', description: 'x, y or z, in game units.' }, minItems: 3, maxItems: 3, description: 'Where it stands there, [x, y, z] in game units: readRecord of the cell lists what is placed there, its doors too — a step from one of them, on its floor.' },
                                    rotation: { type: 'array', items: { type: 'number', description: 'About x, y or z, in radians.' }, minItems: 3, maxItems: 3, description: 'Its rotation, [x, y, z] in radians, as readRecord lists them. Defaults to [0, 0, 0].' },
                                    base: { type: 'string', description: 'The door placed there, a door record\'s editor ID. Defaults to the one the game pairs with the room\'s door.' }
                                },
                                required: ['in', 'at']
                            },
                            location: {
                                description: 'The location the room is in, which the game knows it by — on the map, in quests, cleared or not: a ' +
                                    'Location\'s editor ID, the game\'s like "WhiterunBreezehomeLocation" or the project\'s; or a location of the ' +
                                    'room\'s own, written as a new Location: {"name": "Ashen Camp", "parent": "WhiterunHoldLocation", "keywords": ' +
                                    '["LocTypeDwelling"]}, its parent the location it lies in, a hold\'s or a city\'s. Defaults to the location of ' +
                                    'the door\'s cell.'
                            }
                        },
                        required: ['project', 'cell', 'like']
                    }
                },
                onFailure: 'Failed to make the cell',
                run: async args => {
                    const project = requireString(args, 'project');
                    const door = doorOf(args?.door);
                    const location = locationOf(args?.location);
                    const made = await asRequestError(this.backend.newCell({
                        project,
                        cell: requireString(args, 'cell', 'The new cell\'s editor ID, like "BreezehomeCellar".'),
                        name: optionalString(args, 'name'),
                        like: requireString(args, 'like', 'The game\'s room it is made like, by editor ID, e.g. "MorthalAlvasHouseCellar".'),
                        ...(door ? { door } : {}),
                        ...(location ? { location } : {})
                    }));
                    const where = made.location
                        ? ` It is in the location ${made.location.editorId}` + (made.location.made
                            ? `, a new Location written beside it${made.location.parent ? `, in ${made.location.parent}` : ''}.` : '.')
                        : ' It is in no location.';
                    const navmesh = (cell: string) => `generateNavmesh {"project": ${JSON.stringify(project)}, "cell": ${JSON.stringify(cell)}}`;
                    const kinds = Object.entries(made.byKind).map(([kind, count]) => `${count} ${kind}`).join(', ');
                    return {
                        ...made,
                        nextStep: `${made.cell} is written into the project, made like ${made.like}: ${made.references} references (${kinds}).${where}` +
                            (made.doors
                                ? ` Its door ${made.doors.roomDoor.editorId} and ${made.doors.entrance.editorId} at ${made.doors.entrance.at} in ` +
                                  `${made.doors.in} lead to each other. Next: ${navmesh(made.cell)}, then ${navmesh(made.doors.in)} — the door ` +
                                  `there is new to its navmesh — then buildProject {"project": ${JSON.stringify(project)}}.`
                                : ` It has no door yet: the player gets there with \`coc ${made.cell}\` alone. Next: ${navmesh(made.cell)}, then ` +
                                  `buildProject {"project": ${JSON.stringify(project)}}.`) +
                            ' Change what stands in the room as in any cell: its references are files in its Temporary folder.'
                    };
                }
            },
            {
                definition: {
                    name: 'buildProject',
                    annotations: {
                        title: 'Build project',
                        readOnlyHint: false,
                        // Replaces only its own previous output. New records get a FormID once, never again.
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Build a project\'s plugin from its text: gives new records their FormIDs (written back into ' +
                        'their files, which keep their names), builds a link written as an editor ID — a new record of the project, or one of the ' +
                        'load order — as that record\'s FormID, the file keeping the editor ID (`resolvedLinks`), checks that every link points at a ' +
                        'record that exists, compiles the project\'s Papyrus scripts and writes the plugin with the ' +
                        'header this game version needs. Refuses a project with no records, fields a record type does ' +
                        'not have, and files below a group\'s folder — each named. What has one right fix — a FormKey copied ' +
                        'into a new record, escapes copied out of a JSON answer, a field written where its record has none ' +
                        'but one place it goes — it writes into the file and lists in `corrected`. ' +
                        'What builds but likely not as meant — record files outside the plugin folder, an override ' +
                        'renamed as if it were a new record, a woman\'s voice on a man — is answered with status ' +
                        '`built_with_warnings` and the `warnings` first: the plugin is written, and each warning says ' +
                        'what to fix. `built` lists each record the written plugin holds, with its name and numbers. ' +
                        'Does not touch the mod manager — deployProject does.',
                    inputSchema: { type: 'object', properties: { project: PROJECT }, required: ['project'] }
                },
                onFailure: 'Failed to build the project',
                run: args => this.build(requireString(args, 'project'))
            },
            {
                definition: {
                    name: 'deployProject',
                    annotations: {
                        title: 'Deploy project',
                        readOnlyHint: false,
                        // Replaces the project's mod folder in the mod manager.
                        destructiveHint: true,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Put a built project into the mod manager (Mod Organizer or Amethyst): make its mod folder ' +
                        'the build plus the project\'s assets, enable the mod, and activate the plugin — at the end ' +
                        'of the load order when it is new, so it wins over what it patches. Then a running Mod ' +
                        'Organizer re-reads its profile, or Amethyst deploys it into the game; when the user has to ' +
                        'act, `modManager.nextStep` says what to do — tell them. Refuses when the project changed ' +
                        'since its build, or when the deployed plugin was changed elsewhere (e.g. saved in the ' +
                        'Creation Kit) — pull that first; a pulled copy that would hide the mod is then removed.',
                    inputSchema: { type: 'object', properties: { project: PROJECT }, required: ['project'] }
                },
                onFailure: 'Failed to deploy the project',
                run: args => asRequestError(this.backend.deployProject(requireString(args, 'project')))
            },
            {
                definition: {
                    name: 'pullProject',
                    annotations: {
                        title: 'Pull project',
                        readOnlyHint: false,
                        // Rewrites the project's text from the deployed plugin.
                        destructiveHint: true,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Bring changes made to a deployed plugin outside the project — placing objects in the ' +
                        'Creation Kit, a fix in xEdit — back into the project\'s text. Refuses while the project folder ' +
                        'has uncommitted changes, so what the pull changes can be reviewed with git diff and undone; ' +
                        'where git cannot tell (no git, or no repository), it pulls and says so in `warning`. ' +
                        'Returns the full paths of the files added, changed and removed.',
                    inputSchema: { type: 'object', properties: { project: PROJECT }, required: ['project'] }
                },
                onFailure: 'Failed to pull the project',
                run: args => asRequestError(this.backend.pullProject(requireString(args, 'project')))
            },
            {
                definition: {
                    name: 'exportCell',
                    annotations: {
                        title: 'Export a cell for Blender',
                        readOnlyHint: false,
                        // Writes only into the project's blender/ folder, which a second export rewrites.
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Lay a cell out for Blender, to arrange it by eye: every object placed in it — the project\'s own build over ' +
                        'the load order\'s — with the meshes and textures they show, copied out of the mods and archives, and the ' +
                        'Python that places them in Blender through the PyNifly add-on, where the game places them. An interior ' +
                        '(WhiterunBreezehome) or an exterior cell (Riverwood), with its ground. Creates the project when it does not ' +
                        'exist. Answers the folder and the two scripts: importScript places the cell, exportScript writes what was ' +
                        'moved, copied (Shift+D) or deleted, the ground sculpted and the navmesh edited (the green wireframe: move, ' +
                        'delete or add its faces in Edit Mode) — run each in Blender\'s Scripting tab, or send its text through ' +
                        'Blender\'s MCP server — then importCell brings the changes into the project.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            cell: {
                                type: 'string',
                                description: 'The cell: an interior\'s editor ID like WhiterunBreezehome, an exterior cell\'s like Riverwood, or its FormID.'
                            },
                            textures: {
                                type: 'boolean',
                                description: 'Copy the textures the meshes name as well. Defaults to true; without them the meshes show grey.'
                            }
                        },
                        required: ['project', 'cell']
                    }
                },
                onFailure: 'Failed to export the cell',
                run: async args => {
                    const exported = await asRequestError(this.backend.exportCell({
                        project: requireString(args, 'project'),
                        cell: requireString(args, 'cell'),
                        textures: optionalBoolean(args, 'textures')
                    }));
                    const run = (script: string) => `exec(open(${JSON.stringify(script)}, encoding="utf-8").read())`;
                    const importCell = `importCell {"project": ${JSON.stringify(exported.project)}, "cell": ${JSON.stringify(path.basename(exported.folder))}}`;
                    return {
                        ...exported,
                        nextStep: exported.blendFile
                            ? `${exported.blendFile} holds the ${exported.references} objects of ${exported.cell}, each where the game places it and ` +
                              `named after its reference. Open it in Blender, or work on it without a window through Blender's MCP server: ` +
                              `execute_blender_code_for_cli with blend_file ${JSON.stringify(exported.blendFile)} and code that changes the objects ` +
                              `— bpy.data.objects["<name>"].location, rotation_euler, .copy() for one more, bpy.data.objects.remove for one less — ` +
                              `then ends with ${run(exported.exportScript)} and bpy.ops.wm.save_mainfile(). Then ${importCell}.`
                            : `In Blender, with the PyNifly add-on enabled, run ${exported.importScript} — the Scripting tab's Run ` +
                              `Script, or ${run(exported.importScript)} through Blender's MCP server: it places ${exported.references} ` +
                              `objects of ${exported.cell}. Move, turn, scale, copy (Shift+D) or delete them there; then run ` +
                              `${exported.exportScript} the same way — ${run(exported.exportScript)} — and call ${importCell}.`
                    };
                }
            },
            {
                definition: {
                    name: 'importCell',
                    annotations: {
                        title: 'Import a cell from Blender',
                        readOnlyHint: false,
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Bring a cell arranged in Blender back into the project, from the layout its exportScript wrote: each object ' +
                        'moved, turned or scaled is written as the project\'s copy of its reference with the new placement; each copy ' +
                        'made in Blender as a new reference of the project, in the cell\'s Temporary folder; each one deleted ' +
                        'disabled — another plugin\'s reference is never deleted, what names it would break — or, the project\'s own, ' +
                        'its file deleted. The ground sculpted is set in the cell, and the navmesh edited — its faces moved, deleted ' +
                        'or added — becomes the cell\'s navmesh, linked and with its doors as generateNavmesh makes one. Answers the ' +
                        'full path of each file written; run buildProject next.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            project: PROJECT,
                            cell: {
                                type: 'string',
                                description: 'The cell exported, as exportCell named it — its editor ID. Defaults to the only cell the project exported.'
                            }
                        },
                        required: ['project']
                    }
                },
                onFailure: 'Failed to import the cell',
                run: async args => {
                    const project = requireString(args, 'project');
                    const imported = await asRequestError(this.backend.importCell({ project, cell: optionalString(args, 'cell') }));
                    const changed = imported.moved.length + imported.added.length + imported.removed.length;
                    const ground = imported.ground ? ' The ground sculpted is set in the cell.' : '';
                    const made = imported.navmesh;
                    const navmesh = made
                        ? ` The navmesh edited is the cell's now: ${made.triangles} triangles, ${made.doors.length} load door(s) linked` +
                          (made.fromBlender?.outside ? `, ${made.fromBlender.outside} triangle(s) outside the cell left to its neighbour` : '') +
                          (made.fromBlender?.notBack.length
                              ? `; ${made.fromBlender.notBack.join(', ')}, deleted in Blender, stays as it was — a navmesh is never deleted, what links to it would break`
                              : '') + '.'
                        : '';
                    return {
                        ...imported,
                        nextStep: changed === 0 && !imported.ground && !made
                            ? `Nothing was changed in Blender: the layout holds every object where the game places it (${imported.unchanged}), ` +
                              'the ground and the navmesh as they were. Change them there, run the exportScript again, then importCell.'
                            : `Written: ${imported.moved.length} moved, ${imported.added.length} added, ${imported.removed.length} removed.${ground}${navmesh} ` +
                              `Build next: buildProject {"project": ${JSON.stringify(project)}}, and answer with what it says.`
                    };
                }
            }
        ];
    }

    /** A build's answer, as buildProject and editRecord's `build` give it: a refusal thrown, the warnings first. */
    private async build(project: string): Promise<BuildAnswer> {
        // A refusal answered as a build: 10 of the 13 runs of eval rounds 31–47 that ended on a refused build
        // told the user the mod was built — most fixed the file after it and never built again. Said at both ends.
        // After 12 of the 29 refusals of rounds 40–50 that named corrections it had written, the model's edits found
        // nothing — it made the build's moves itself, from the file as it had written it, and read it first once.
        const { warnings, combined, ...built } = await asRequestError(this.backend.buildProject(project)
            .catch((error: unknown) => {
                if (error instanceof Error) error.message = `Not built: ${error.message.replace(/\.?\s*$/, '.')} ` +
                    (error.message.includes('Corrected in the files already')
                        ? 'The corrections are made: redo none of them, and read a corrected file before editing it — it ' +
                            'no longer reads as you wrote it. Fix what else this names'
                        : 'Fix what this names') +
                    ', then build again — only a build that answers success writes the plugin.';
                throw error;
            }));
        // Told what the build had moved, the model made the moves itself — edits of the file as it had been, which
        // found nothing — then wrote it anew without the FormKey line the build gave it (eval round 45's
        // npc #3): 4 of the 209 builds with `corrected` in rounds 30–45 were followed so, and 2 of those runs failed.
        // Read as warnings to fix, the corrections were made again from memory, into a file that no longer read (eval round
        // 113's npc #3): nothing in them is left to do.
        const done = built.corrected?.length
            ? 'Each change in `corrected` is done: the build wrote it into the file, and the plugin has it — none is a ' +
                'warning, and nothing in them is left to fix or to make again. A file it changed no longer reads as it was ' +
                'written — read it before editing it again.'
            : undefined;
        // The model's answer named an enchantment and a value its files did not hold: it had written another name, and the call
        // that set the value had failed (eval round 87's sword+detailed #4). The plugin read back says what the game gets.
        // Told to answer from `built`, a model answered the value it had meant to write, 150, where `built` listed the 12 it
        // had written (eval round 102's sword+detailed #5): a mod of a few records — a sword and its enchantment — is said
        // in the nextStep itself, beside what to do with it. Told so, a model saw the value of 12 it had written where 150 was
        // asked, and answered that "the build assigned a Value of 12, not 150" (eval round 118's sword+detailed #4): the
        // names and numbers are the files' own.
        const lines = built.built ?? [];
        const own = 'the build writes each as it stands, and changes one only where `corrected` says so. Tell the user what you ' +
            'made with them; where one differs from what the user asked, fix it in its file and build again.';
        const told = lines.length > 0 && lines.length <= 3
            ? `The plugin holds: ${lines.join('. ')}. These names and numbers are your files' own — ${own}`
            : `The plugin holds what \`built\` lists, its names and numbers your files' own — ${own}`;
        if (!warnings?.length) return { nextStep: done ? `Built. ${done} ${told}` : `Built. ${told}`, ...built };
        // A warning at the end of an answer that said success was passed by: five of the eight misses
        // in eval round 21 end on one, each a build that did what was not asked. Its exception, written
        // "unless the user asked for it so", was read as "the user did not ask for a fix" in three runs of
        // rounds 27–36, two of which failed (eval round 36): it names what may stay instead.
        const one = warnings.length === 1;
        // The fixes of one file as one call: given four warnings on its sword, three with a call, a model made none of them
        // and answered with the four as a list, "please instruct me to fix them" (eval round 122's sword #1).
        const together = (combined ?? []).map(({ file, count, call }) =>
            ` ${count} of them fix ${file} with a call each: one call makes the ${count} and builds again — ${call} — with a value ` +
            'the user named in place of one a warning gives, as that warning says.').join('');
        // A warning with its call, answered rather than fixed: 6 of eval round 131's 13 misses ended on one — "if you wish to fix
        // these warnings … let me know" — while the 4 runs that ended on warnings and passed kept only ones that stay. The plugin's
        // check that would ask again is never run after a plugin's call (OpenClaw). The first warning that fixes something has
        // its call here; one that stays says "answer with the build" and is left as it is, and one whose call takes the user's
        // values in place of its own is pointed to.
        const fixing = together ? undefined : warnings.find(warning => !/answer with the build/.test(warning) && firstCall(warning));
        // A call with a value to fill in — `"<the record to add>"` — is a shape, not one to send as it is.
        const call = fixing && !/in place of/.test(fixing) && !/"<[^"]+>"/.test(firstCall(fixing) ?? '') ? firstCall(fixing) : undefined;
        const whose = fixing ? (one ? 'its' : `the warning on ${fixing.slice(0, Math.max(0, fixing.indexOf(': ')))}'s`) : '';
        // A reference filed under one cell and standing in another, beside what the game places there: its fix is addPatch of
        // that cell, then the move its warning names, then a build.
        const moved = !!call && call.startsWith('addPatch') && /\bthen moveRecord of\b/.test(fixing ?? '');
        const next = !fixing ? ''
            : moved ? ` Not done yet: ${whose} calls fix it — ${call}, then the moveRecord it names, then buildProject — send them next, ` +
                'then answer with what that build says.'
            : call ? ` Not done yet: ${whose} call fixes it and builds again — ${call} — send it next, then answer with what that build says.`
                : ` Not done yet: ${whose} call fixes it and builds again, with what the user asked for in place of what it says — send ` +
                    'it next, then answer with what that build says.';
        return {
            status: 'built_with_warnings',
            warnings,
            nextStep: `Built, with ${one ? 'a warning' : `${warnings.length} warnings`}: fix what ${one ? 'it' : 'each'} ` +
                `says and build again — that is part of the task.${next} ${one ? 'It' : 'One'} stays only where the user ` +
                'asked for just what it names.' + together + (done ? ` ${done}` : ''),
            ...built
        };
    }
}

type BuildAnswer = Omit<BuildResult, 'warnings'> & { status?: 'built_with_warnings'; warnings?: string[]; nextStep?: string };

/** The first tool call a warning gives — `editRecord {…}`, its arguments whole — or undefined. */
export function firstCall(text: string): string | undefined {
    const start = /\b(?:editRecord|deleteRecord|moveRecord|addPatch|buildProject) \{/.exec(text);
    if (!start) return undefined;
    let depth = 0;
    let quoted = false;
    for (let i = start.index + start[0].length - 1; i < text.length; i++) {
        const c = text[i];
        if (quoted) {
            if (c === '\\') i++;
            else if (c === '"') quoted = false;
        } else if (c === '"') quoted = true;
        else if (c === '{' || c === '[') depth++;
        else if ((c === '}' || c === ']') && --depth === 0) return text.slice(start.index, i + 1);
    }
    return undefined;
}
