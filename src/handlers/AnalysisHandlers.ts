import { BaseHandler } from './BaseHandler.js';
import type { ToolSpec } from './BaseHandler.js';
import { asRequestError, optionalString } from './args.js';
import { ToolError, ToolErrorCode } from './errors.js';
import { SECTION_NAMES, type SectionName } from '../backend/skyrim/crash/crashLog.js';

/**
 * Why the game misbehaves, read from what it leaves behind: Crash Logger SSE's crash logs, and the mods in conflict. Reading only —
 * a fix it points at is made with the mod tools, on the user's word.
 */
export class AnalysisHandlers extends BaseHandler {
    protected toolSpecs(): ToolSpec[] {
        return [
            {
                definition: {
                    name: 'readCrashLog',
                    annotations: {
                        title: 'Read crash log',
                        readOnlyHint: true,
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Why the game crashed, from Crash Logger SSE\'s log: the newest of this setup\'s, or the one `log` names. ' +
                        'Answers the crash, up to 5 `diagnoses` — known crashes, most certain first, each with its fix — and up ' +
                        'to 5 `suspects`: the mods whose DLL, plugin, mesh or texture the log points at, with the lines that do; ' +
                        'and for each record the log names, the plugin that defines it and the one that changed it last. ' +
                        'Advice on the setup that holds whatever crashed — Engine Fixes not installed — comes apart, in `setup`. ' +
                        '`section` answers one part of the log as written instead. Reads files; changes nothing.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            log: {
                                type: 'string',
                                description: 'A log by its file name, `crash-2026-10-30-14-02-11.log`, or by when: `2026-10-30`, ' +
                                    '`2026-10-30 14:02`, a minute with no log reading the newest before it that day. Leave it ' +
                                    'out for the newest.'
                            },
                            // No enum: OpenClaw's argument check turns a null into "", which an enum refuses before the tool
                            // sees it (eval rounds 89 and 90, one crash run each); sectionArg checks the name.
                            section: {
                                type: 'string',
                                description: `Only this part of the log, line by line: one of ${SECTION_NAMES.join(', ')}.`
                            }
                        }
                    },
                    narrowingHint: 'Give `section` for one part of the log.'
                },
                onFailure: 'Failed to read the crash log',
                run: args => asRequestError(this.backend.readCrashLog({
                    log: optionalString(args, 'log'),
                    section: sectionArg(args)
                }))
            },
            {
                definition: {
                    name: 'readPapyrusLog',
                    annotations: {
                        title: 'Read Papyrus log',
                        readOnlyHint: true,
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'What the game\'s scripts did wrong, from its Papyrus log: script and install `problems`, grouped and ' +
                        'counted, bugs first — a call on None, a script or framework missing, a script on the wrong record — each ' +
                        'with its fix, its scripts by the mod whose .pex the game loads, its records by plugin; what the save still ' +
                        'holds of mods removed mid-playthrough; script lag in `load`, from stack dumps; harmless messages counted in ' +
                        '`noise`. Not a crash log: readCrashLog reads crashes. `script` lists one script\'s entries with their ' +
                        'stacks. With logging off, it says the editIni call that turns it on. Reads files; changes nothing.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            log: {
                                type: 'string',
                                description: 'Papyrus.0.log, the newest, by default; "1" to "3" for the sessions before it.'
                            },
                            script: { type: 'string', description: 'Only this script\'s entries, e.g. "MyQuestScript".' }
                        }
                    },
                    narrowingHint: 'Give `script` for one script\'s entries.'
                },
                onFailure: 'Failed to read the Papyrus log',
                run: args => asRequestError(this.backend.readPapyrusLog({
                    log: optionalString(args, 'log'),
                    script: optionalString(args, 'script')
                }))
            },
            {
                definition: {
                    name: 'findConflicts',
                    annotations: {
                        title: 'Find mod conflicts',
                        readOnlyHint: true,
                        destructiveHint: false,
                        idempotentHint: true,
                        openWorldHint: false
                    },
                    description:
                        'Mods in conflict, as the game sees them. `records`: each record whose winning version lost another ' +
                        'plugin\'s changes, with those changes plugin by plugin — a field\'s value, an item a list gained or lost ' +
                        '(`Keywords + WeapMaterialSilver`); a patch that names both plugins as masters settles a conflict, and ' +
                        'is not listed. `files`: each pair of mods giving the game the same files, the one it takes and how — ' +
                        'the mod order for loose files, the load order for archives, a loose file over any archive. Of one ' +
                        '`plugin`, one `mod`, one `record`, or the whole setup. `nextStep` says how to settle them: addPatch with ' +
                        '`merge` for records. Reads only; changes nothing.',
                    inputSchema: {
                        type: 'object',
                        properties: {
                            plugin: { type: 'string', description: 'Only the record conflicts of this plugin, e.g. "Dawnguard.esm".' },
                            mod: { type: 'string', description: 'Only this mod\'s: its files, and the record conflicts of its plugins — its name as listMods shows it.' },
                            record: { type: 'string', description: 'Only this record\'s conflict: a FormID like 012EB7:Skyrim.esm, or an editor ID.' },
                            kind: { type: 'string', description: 'Only "records" or only "files".' },
                            limit: { type: 'number', description: 'How many of each to list, 1-200.', default: 25 }
                        }
                    },
                    narrowingHint: 'Give `plugin`, `mod` or `record`, or a smaller `limit`.'
                },
                onFailure: 'Failed to find the conflicts',
                run: args => asRequestError(this.backend.findConflicts({
                    plugin: optionalString(args, 'plugin'),
                    mod: optionalString(args, 'mod'),
                    record: optionalString(args, 'record'),
                    kind: kindArg(args),
                    limit: typeof args?.limit === 'number' ? args.limit : undefined
                }))
            }
        ];
    }
}

function sectionArg(args: any): SectionName | undefined {
    const value = optionalString(args, 'section');
    if (value === undefined) return undefined;
    const found = SECTION_NAMES.find(name => name.toLowerCase() === value.toLowerCase());
    if (!found) throw new ToolError(ToolErrorCode.InvalidParams, `'section' is one of: ${SECTION_NAMES.join(', ')}.`);
    return found;
}

/** `kind`, any letter case: records or files; nothing for both. */
function kindArg(args: any): 'records' | 'files' | undefined {
    const value = optionalString(args, 'kind')?.trim().toLowerCase();
    if (!value) return undefined;
    if (value === 'records' || value === 'record') return 'records';
    if (value === 'files' || value === 'file') return 'files';
    throw new ToolError(ToolErrorCode.InvalidParams, `'kind' is "records" or "files" — or leave it out for both.`);
}
