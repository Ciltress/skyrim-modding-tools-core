import { BaseHandler } from './BaseHandler.js';
import type { ToolSpec } from './BaseHandler.js';
import { asRequestError, limitArg, optionalBoolean, optionalString, optionalStringList, recordRef, requireString } from './args.js';

/**
 * Looking records up across the active load order: who defines a record, who overrides it, and what
 * each version says — in the same text a project holds, so a version read here can be mirrored or
 * copied as it stands.
 */

const DEFAULT_LIMIT = 25;
const MAX_LIMIT = 200;

const FORM_ID_HINT = 'FormIDs are written 012EB7:Skyrim.esm.';

export class RecordsHandlers extends BaseHandler {
    protected toolSpecs(): ToolSpec[] {
        return [
            {
                definition: {
                    name: 'findRecords',
                    annotations: {
                        title: 'Find records',
                        readOnlyHint: true,
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Find records in the active load order — by text in the editor ID or name, or by exact ' +
                        'FormIDs and editor IDs. Each row says which plugin defines the record, which one wins, ' +
                        'and how many override it. Use `records` to turn the FormIDs in project text into names ' +
                        '(and back) before reasoning about them. Placed references, landscape and navmesh are ' +
                        'not searched: readRecord of a cell lists what is placed in it. `total` counts matches before the limit.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            text: {
                                type: 'string',
                                description: 'Case-insensitive text in the editor ID or the in-game name, e.g. "iron sword". Exact matches come first, then in-game names.'
                            },
                            records: {
                                type: 'array',
                                items: { type: 'string', description: 'A FormID like 012EB7:Skyrim.esm, or an editor ID like IronSword.' },
                                description: 'Exact lookups by FormID or editor ID instead of a text search — not in-game names, which `text` finds. Unknown ones come back under `notFound`.'
                            },
                            types: {
                                type: 'array',
                                items: { type: 'string', description: 'A signature like WEAP, or a type name like Weapon.' },
                                description: 'Only these record types, e.g. ["WEAP", "ARMO"].'
                            },
                            plugin: {
                                type: 'string',
                                description: 'Only records this plugin defines or overrides, e.g. "Dawnguard.esm".'
                            },
                            limit: {
                                type: 'number',
                                description: `How many rows to return, 1-${MAX_LIMIT}.`,
                                default: DEFAULT_LIMIT
                            }
                        }
                    },
                    narrowingHint: 'Add `types` or `plugin`, make `text` more specific, or lower `limit`.'
                },
                onFailure: 'Failed to search the load order',
                run: async args => {
                    const text = optionalString(args, 'text');
                    const records = optionalStringList(args, 'records', '["012EB7:Skyrim.esm", "IronSword"]')?.map(recordRef);
                    const result = await asRequestError(this.backend.findRecords({
                        text, records,
                        types: optionalStringList(args, 'types', '["WEAP"]'),
                        plugin: optionalString(args, 'plugin'),
                        limit: limitArg(args, DEFAULT_LIMIT, MAX_LIMIT)
                    }));
                    // What was sent and not used, said: `in` dropped and `text` set aside for `records`, both without a word, a model
                    // searched the whole game for Riverwood's grindstone and guessed where it stands (mini-round of 2026-11-08,
                    // outdoor #2 and #5).
                    const notes: string[] = [];
                    const place = args?.in;
                    if (typeof place === 'string' && place.trim()) {
                        notes.push(`findRecords has no \`in\`: where a record stands in a place, readRecord lists it — {"record": "<its editor ID>", "in": ${JSON.stringify(place.trim())}}.`);
                    }
                    const others = Object.keys(args ?? {}).filter(key => !['text', 'records', 'types', 'plugin', 'limit', 'in'].includes(key));
                    if (others.length > 0) {
                        notes.push(`${others.map(key => `\`${key}\``).join(', ')} ${others.length === 1 ? 'is no argument' : 'are no arguments'} of findRecords': not used.`);
                    }
                    if (text && records?.length) {
                        notes.push(`\`text\` was not searched: \`records\` looks up exact FormIDs and editor IDs alone — send {"text": ${JSON.stringify(text)}} without \`records\` to search.`);
                    }
                    if (notes.length === 0) return result;
                    const before = (result as { nextStep?: string }).nextStep;
                    return { ...result, nextStep: [notes.join(' '), before].filter(Boolean).join(' ') };
                }
            },
            {
                definition: {
                    name: 'readRecord',
                    annotations: {
                        title: 'Read record',
                        readOnlyHint: true,
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'One record as project text (YAML): by default the version that wins in the load order; ' +
                        'with `plugin`, that plugin\'s version; with `allVersions`, every plugin\'s version, winning ' +
                        'first — compare them to see a conflict. Each file carries `pathInPluginFolder`, where it ' +
                        'would sit in a project\'s plugin folder; to change a record, copy it in with addPatch rather ' +
                        'than writing this text yourself. Links inside are FormIDs, each followed by the editor ID ' +
                        'of the record it names as a comment (`Race: 013746:Skyrim.esm  # NordRace`); a project ' +
                        'file may link by either. A cell comes with `placed`: what the load order places in it, and ' +
                        'the file and lines a new reference placed there is written with.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            record: {
                                type: 'string',
                                description: `A FormID or an editor ID, e.g. "IronSword" — or the in-game name of the one record that has it, e.g. "Note From Maven". ${FORM_ID_HINT}`
                            },
                            plugin: {
                                type: 'string',
                                description: 'Read this plugin\'s version instead of the winning one, e.g. "Update.esm".'
                            },
                            allVersions: {
                                type: 'boolean',
                                description: 'Every plugin\'s version, winning first. Defaults to false.'
                            },
                            in: {
                                type: 'string',
                                description: 'For a thing the game places — a grindstone, a chest, a statue — where it stands only in the cells ' +
                                    'whose editor ID or name holds this text: a town or a place, e.g. "Riverwood".'
                            }
                        },
                        required: ['record']
                    },
                    narrowingHint: 'Name one `plugin` instead of asking for `allVersions`.'
                },
                onFailure: 'Failed to read the record',
                run: args => asRequestError(this.backend.readRecord({
                    record: recordRef(requireString(args, 'record', `Pass a FormID or an editor ID. ${FORM_ID_HINT}`)),
                    plugin: optionalString(args, 'plugin'),
                    allVersions: optionalBoolean(args, 'allVersions'),
                    in: optionalString(args, 'in')
                }))
            }
        ];
    }
}
