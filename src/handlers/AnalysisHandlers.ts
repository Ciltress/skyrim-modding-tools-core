import { BaseHandler } from './BaseHandler.js';
import type { ToolSpec } from './BaseHandler.js';
import { asRequestError, optionalString } from './args.js';
import { ToolError, ToolErrorCode } from './errors.js';
import { SECTION_NAMES, type SectionName } from '../backend/skyrim/crash/crashLog.js';

/**
 * Why the game misbehaves, read from what it leaves behind: Crash Logger SSE's crash logs. Reading only —
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
