import { BaseHandler } from './BaseHandler.js';
import type { ToolSpec } from './BaseHandler.js';
import type { BuildResult } from '../backend/index.js';
import { asRequestError, optionalBoolean, optionalFieldValues, optionalString, optionalStringList, recordRef, requireString } from './args.js';
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
                        plugin: optionalString(args, 'plugin')
                    })).then(result => {
                        // Asked for a new sword "like the Iron Sword", a model patched the Iron Sword and renamed it:
                        // every Iron Sword in the game became its Flame Blade (eval probe 16). Said after every copied
                        // file's text, it left the model making its "new" sword or note this way in four of six misses
                        // (eval round 22): first now, naming the records, as a build's warnings went first (round 21).
                        // The copy was written empty while OpenClaw gave a model no tool to delete a file (round 26): deleteRecord now.
                        const copies = [
                            ...result.added.map(record => record.editorId ? `${record.editorId} (${record.formId})` : record.formId),
                            ...result.alreadyInProject.map(record => record.formId)
                        ];
                        const one = copies.length === 1;
                        // The copy's fields changed with the edit tool, its old text from memory: 32 of the 82 such edits in runs
                        // with addPatch of eval rounds 49–57 were refused, 3 of its 42 editRecord calls — and round 57's
                        // sword+detailed #5 ran out of turns on fifteen refused edits of addPatch's Iron Sword.
                        const first = result.added[0];
                        const example = first?.editorId ?? first?.formId ?? 'IronSword';
                        // A field of the copy's own kind; a name would rename the record everywhere, as the note says.
                        const field = ({
                            Weapon: '{"path": "BasicStats.Damage", "value": 12}', Armor: '{"path": "ArmorRating", "value": 30}',
                            Spell: '{"path": "BaseCost", "value": 60}', Npc: '{"path": "AIData.Aggression", "value": "Unaggressive"}'
                        } as Record<string, string>)[first?.type ?? 'Weapon'] ?? '{"path": "<the field>", "value": <its new value>}';
                        const folder = result.project.pluginFolder;
                        const copied = first?.files[0]?.path ?? result.alreadyInProject[0]?.files[0]?.path;
                        const drop = `deleteRecord {"project": ${JSON.stringify(result.project.project)}, "record": ` +
                            `${JSON.stringify(copied?.startsWith(folder) ? copied.slice(folder.length + 1).replace(/\\/g, '/') : example)}}`;
                        return {
                            note: `${copies.join(', ')} ${one ? 'is' : 'are'} copied as ${one ? 'an override' : 'overrides'}: ` +
                                `${one ? 'it changes that record' : 'each changes its record'} itself, everywhere the game uses ` +
                                `${one ? 'it' : 'them'} — a new name renames ${one ? 'it' : 'them'} everywhere. If a new record ` +
                                'like it was meant, write a new file instead, mirroring it without the FormKey line and with an ' +
                                `EditorID of its own, where newProject's writeRecordsTo says — and delete ` +
                                (one ? `the copy: ${drop}.` : `each copy, the first with ${drop}.`),
                            nextStep: `Change a copy's fields with editRecord, which needs no old text to match — {"record": ` +
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
                    if (!build) return { nextStep: 'Run buildProject next — the plugin has this change only once a build answers success.', ...edited };
                    // Without the file's text: the build may change the file again, and its answer says how.
                    const { text: _text, ...change } = edited;
                    const built = await this.build(project).catch((error: unknown) => {
                        if (error instanceof Error) error.message = `Changed ${change.file}: ${change.changed.join('; ')}. ${error.message}`;
                        throw error;
                    });
                    if (built.status) return { ...built, edited: change };
                    const { nextStep, ...rest } = built;
                    return { nextStep: nextStep?.replace(/^Built\./, 'Changed and built: the plugin has this change.'), ...rest, edited: change };
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
                            }
                        },
                        required: ['project', 'record']
                    }
                },
                onFailure: 'Failed to delete the record file',
                run: args => asRequestError(this.backend.deleteRecord({
                    project: requireString(args, 'project'),
                    record: requireString(args, 'record', 'The file: its path in the plugin folder, its EditorID, or its full path.')
                }).then(result => ({
                    nextStep: 'Run buildProject next — the plugin has this change only once a build answers success.',
                    ...result
                })))
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
            }
        ];
    }

    /** A build's answer, as buildProject and editRecord's `build` give it: a refusal thrown, the warnings first. */
    private async build(project: string): Promise<BuildAnswer> {
        // A refusal answered as a build: 10 of the 13 runs of eval rounds 31–47 that ended on a refused build
        // told the user the mod was built — most fixed the file after it and never built again. Said at both ends.
        // After 12 of the 29 refusals of rounds 40–50 that named corrections it had written, the model's edits found
        // nothing — it made the build's moves itself, from the file as it had written it, and read it first once.
        const { warnings, ...built } = await asRequestError(this.backend.buildProject(project)
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
        const done = built.corrected?.length
            ? 'Each change in `corrected` is done: the build wrote it into the file, and the plugin has it. A file it ' +
                'changed no longer reads as it was written — read it before editing it again.'
            : undefined;
        // The model's answer named an enchantment and a value its files did not hold: it had written another name, and the call
        // that set the value had failed (eval round 87's sword+detailed #4). The plugin read back says what the game gets.
        // Told to answer from `built`, a model answered the value it had meant to write, 150, where `built` listed the 12 it
        // had written (eval round 102's sword+detailed #5): a mod of a few records — a sword and its enchantment — is said
        // in the nextStep itself, beside what to do with it.
        const lines = built.built ?? [];
        const told = lines.length > 0 && lines.length <= 3
            ? `The plugin holds: ${lines.join('. ')}. Tell the user what you made with these names and numbers; where one ` +
                'differs from what the user asked, fix the file and build again.'
            : 'The plugin holds what `built` lists: tell the user what you made with its names and numbers. Where one ' +
                'differs from what the user asked, fix the file and build again.';
        if (!warnings?.length) return { nextStep: done ? `Built. ${done} ${told}` : `Built. ${told}`, ...built };
        // A warning at the end of an answer that said success was passed by: five of the eight misses
        // in eval round 21 end on one, each a build that did what was not asked. Its exception, written
        // "unless the user asked for it so", was read as "the user did not ask for a fix" in three runs of
        // rounds 27–36, two of which failed (eval round 36): it names what may stay instead.
        const one = warnings.length === 1;
        return {
            status: 'built_with_warnings',
            warnings,
            nextStep: `Built, with ${one ? 'a warning' : `${warnings.length} warnings`}: fix what ${one ? 'it' : 'each'} ` +
                `says and build again — that is part of the task. ${one ? 'It' : 'One'} stays only where the user ` +
                'asked for just what it names.' + (done ? ` ${done}` : ''),
            ...built
        };
    }
}

type BuildAnswer = Omit<BuildResult, 'warnings'> & { status?: 'built_with_warnings'; warnings?: string[]; nextStep?: string };
