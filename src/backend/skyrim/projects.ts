import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { findCaseInsensitive } from './paths.js';
import type { ProjectInfo } from './SkyrimBackend.js';

/**
 * A project on disk (docs/adr/0004):
 *
 *   <projects>/<name>/plugin/    the plugin as text — RecordData.yaml and one YAML file per record
 *   <projects>/<name>/scripts/   Papyrus sources (*.psc), compiled into the mod's Scripts folder
 *   <projects>/<name>/assets/    loose files copied into the mod as they are: meshes/, textures/…
 *   <projects>/<name>/build/     what a build produces; not versioned
 *   <projects>/<name>/.deploy.json   what was deployed last; not versioned
 */

export class ProjectError extends Error { }

export interface ProjectPaths {
    root: string;
    plugin: string;
    scripts: string;
    assets: string;
    build: string;
    deployState: string;
}

/** Project names become folder names and Mod Organizer mod names. */
const NAME = /^[A-Za-z0-9][A-Za-z0-9 _.-]{0,63}$/;

export function projectPaths(projectsDir: string, name: string): ProjectPaths {
    if (!NAME.test(name) || name.endsWith('.')) {
        throw new ProjectError(
            `'${name}' cannot be a project name: use letters, digits, spaces, '_', '-' or '.', up to 64 characters.`);
    }
    const root = path.join(projectsDir, name);
    return {
        root,
        plugin: path.join(root, 'plugin'),
        scripts: path.join(root, 'scripts'),
        assets: path.join(root, 'assets'),
        build: path.join(root, 'build'),
        deployState: path.join(root, '.deploy.json')
    };
}

export function projectExists(paths: ProjectPaths): boolean {
    return fs.existsSync(path.join(paths.plugin, 'RecordData.yaml'));
}

/** The plugin file name and light flag, from the project's RecordData.yaml. */
export function readProjectHeader(paths: ProjectPaths): { plugin: string; light: boolean } {
    const file = path.join(paths.plugin, 'RecordData.yaml');
    if (!fs.existsSync(file)) {
        throw new ProjectError(`There is no project at '${paths.root}'. Create it with newProject or addPatch.`);
    }
    const text = fs.readFileSync(file, 'utf8');
    const plugin = /^ModKey:\s*(.+?)\s*$/m.exec(text)?.[1];
    if (!plugin) {
        throw new ProjectError(
            `'${file}' names no ModKey. Add a first line 'ModKey: <plugin file name>', as newProject writes it.`);
    }
    // The header's flags, as a block list (`- Small`) or a flow list (`Flags: [Small]`).
    const flowFlags = /^\s*Flags:\s*\[([^\]]*)\]/m.exec(text)?.[1].split(',').map(flag => flag.trim()) ?? [];
    return { plugin, light: /^\s*-\s*Small\s*$/m.test(text) || flowFlags.includes('Small') };
}

export function projectInfo(name: string, paths: ProjectPaths, created: boolean): ProjectInfo {
    const { plugin, light } = readProjectHeader(paths);
    return { project: name, plugin, light, path: paths.root, pluginFolder: paths.plugin, writeRecordsTo: whereRecordsGo(paths), created };
}

/** The group folders of the everyday record types, as Spriggit names them. */
const GROUP_FOLDERS = 'Weapons, Armors, Spells, MagicEffects, ObjectEffects (enchantments), Npcs, LeveledItems';

/**
 * Where a record's file goes, in full paths. Given an example relative to the plugin folder, small
 * models joined it to the project folder, and the build never read their records (eval round 9).
 */
export function whereRecordsGo(paths: ProjectPaths): string {
    return `One YAML file per record, in its type's group folder inside ${paths.plugin} — a new weapon as ` +
        `${path.join(paths.plugin, 'Weapons', 'MyWeapon.yaml')}. The group folders are named as readRecord's ` +
        `pathInPluginFolder names them: ${GROUP_FOLDERS}.`;
}

/**
 * Where a record file written outside the project's plugin folder belongs, as a full path: its group's
 * folder and name inside the plugin folder — `<project>/Spells/X.yaml`, `<projects>/Spells/X.yaml` and
 * `<projects>/plugin/Spells/X.yaml` (a path written without the project's folder) all belong at
 * `<project>/plugin/Spells/X.yaml`. Null for a file with no group folder above it. Told only that the
 * file was in no project, a model wrote it to the same path three builds in a row (eval probe 14).
 */
export function belongsAt(file: string, paths: ProjectPaths, projectsDir: string): string | null {
    const base = path.resolve(file).toLowerCase().startsWith(path.resolve(paths.root).toLowerCase() + path.sep) ? paths.root : projectsDir;
    const parts = path.relative(base, file).split(/[\\/]/).filter(part => part.length > 0);
    if (parts[0]?.toLowerCase() === 'plugin') parts.shift();
    return parts.length >= 2 ? path.join(paths.plugin, ...parts.slice(-2)) : null;
}

/**
 * Files in the plugin folder's group folders that the build does not read for their ending — an
 * enchantment saved as FlameBladeBurn.nhs (eval probe 16) — each with the name it reads.
 */
export function misnamedRecordFiles(pluginDir: string): { file: string; as: string }[] {
    const found: { file: string; as: string }[] = [];
    const walk = (dir: string, depth: number) => {
        if (!fs.existsSync(dir)) return;
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) walk(full, depth + 1);
            else if (depth > 0 && !/\.yaml$/i.test(entry.name)) found.push({ file: full, as: full.replace(/\.[^.\\/]*$/, '') + '.yaml' });
        }
    };
    walk(pluginDir, 0);
    return found.sort((a, b) => a.file.localeCompare(b.file));
}

/**
 * Record files with a markdown fence in them — a line of three or more backticks or tildes — and the
 * fence's line numbers. An answer shows a file that way, and a model copied the fence with it (eval
 * round 15's probes).
 */
export function fencedRecordFiles(pluginDir: string): { file: string; lines: number[] }[] {
    return recordFiles(pluginDir).flatMap(file => {
        const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/)
            .flatMap((line, i) => /^\s*(`{3,}|~{3,})/.test(line) ? [i + 1] : []);
        return lines.length > 0 ? [{ file, lines }] : [];
    });
}

/**
 * Lines of record files that carry the escapes a JSON answer shows — two backslashes where the record
 * has one, a backslash before a quote — copied as they read: the model wrote `File: Weapons\\Iron\\Sword.nif`
 * in about one run of seventeen (eval rounds 12–15), and the build took it. A double-quoted string is
 * judged once decoded, since YAML escapes there too; on 6,038 files Spriggit wrote, nothing is found.
 *
 * Each comes with the line as written and as it is to be: told only "line 12 (two backslashes)", the model
 * edited another line, built again and gave up (eval probe 17). A text's line breaks copied as `\n`
 * become the lines they were (the model copied a note's whole text onto one line, round 18), and an apostrophe
 * escaped in single quotes is written twice, as YAML writes it.
 */
export function copiedEscapes(pluginDir: string): CopiedEscape[] {
    return recordFiles(pluginDir).flatMap(file => {
        let block: number | undefined;
        const quest = path.relative(pluginDir, file).split(path.sep)[0] === 'Quests';
        return fs.readFileSync(file, 'utf8').split(/\r?\n/).flatMap((text, i) => {
            const indent = text.length - text.trimStart().length;
            // A block scalar's lines (`Value: >-` and the lines below it, deeper) are text, not fields.
            if (block !== undefined && text.trim() && indent <= block) block = undefined;
            const inBlock = block !== undefined;
            if (/:\s*[|>][-+]?\s*\\?\s*$/.test(text)) block = indent;
            // The Creation Kit's quest folders end in a backslash — `Filter: Bounty Quests\` in 1,992 of the official
            // plugins' 2,522 quests, and no other text of theirs: refused, addPatch's own copy of BQ01 did not build.
            const escape = lineEscape(text, inBlock, quest && /^Filter:/.test(text));
            return escape ? [{ file, line: i + 1, ...escape, text: text.trim() }] : [];
        });
    });
}

/**
 * Writes each line copiedEscapes finds as it is to be, a one-line fix at the line's own indentation, and
 * answers what it wrote. Refused with those lines instead, the escapes cost a build, and three of the seven
 * runs that met the refusal failed after it (eval rounds 24–31).
 */
export function correctCopiedEscapes(pluginDir: string): CopiedEscape[] {
    const escaped = copiedEscapes(pluginDir);
    for (const file of new Set(escaped.map(escape => escape.file))) {
        const text = fs.readFileSync(file, 'utf8');
        const lines = text.split(/\r?\n/);
        // From the last line up, so a fix that becomes several lines moves none still to be fixed.
        for (const { line, kind, fixed } of escaped.filter(escape => escape.file === file).sort((a, b) => b.line - a.line)) {
            const indent = /^\s*/.exec(lines[line - 1])![0];
            lines.splice(line - 1, 1, ...(kind === 'line breaks written as \\n' ? fixed : [fixed[0] ? indent + fixed[0] : '']));
        }
        fs.writeFileSync(file, lines.join(text.includes('\r\n') ? '\r\n' : '\n'));
    }
    return escaped;
}

/**
 * A line break a copy left as `\n` — `\r\n`, its backslash doubled or not — where a text goes on: before
 * another escape, a space or the line's end. A path's `Armor\nightingale` goes on with a letter.
 */
const COPIED_LINE_BREAK = /\\+(?:r\\+)?n(?=\\|\s|$)/;

/**
 * One line's copied escapes and what it is to be, or undefined; `inBlock` when it is a block scalar's text,
 * `endsInBackslash` when the field's own value ends in one.
 */
function lineEscape(text: string, inBlock: boolean, endsInBackslash = false): Pick<CopiedEscape, 'kind' | 'fixed'> | undefined {
    const kindOf = (part: string) =>
        /\\\\/.test(part) ? 'two backslashes' as const : /\\"/.test(part) ? 'a backslash before a quote' as const : undefined;
    let kind: CopiedEscape['kind'] | undefined;
    let fixed = '';
    let outside = '';
    let rest = text;
    for (const quoted of text.match(/"(?:[^"\\]|\\.)*"/g) ?? []) {
        const at = rest.indexOf(quoted);
        const before = rest.slice(0, at);
        outside += before;
        kind ??= kindOf(before);
        let decoded = '';
        try { decoded = JSON.parse(quoted); } catch { /* a YAML escape JSON does not know: not one a copy leaves */ }
        const inside = kindOf(decoded);
        kind ??= inside;
        // Inside double quotes YAML escapes too: the string is undone once and written with its own escapes again.
        fixed += unescape(before) + (inside ? JSON.stringify(unescape(decoded)) : quoted);
        rest = rest.slice(at + quoted.length);
    }
    outside += rest;
    kind ??= kindOf(rest);
    fixed += unescape(rest);
    if (COPIED_LINE_BREAK.test(outside)) return { kind: 'line breaks written as \\n', fixed: textLines(text, inBlock) };
    // What a `\n` leaves when its line break is written for real: a backslash at the line's end. `Value: >-\` broke a
    // note's YAML, and a text whose every line ended so kept them in the line given back for its quotes (round 21).
    if (!endsInBackslash && /\\\s*$/.test(outside)) {
        kind ??= 'a backslash at the end of the line';
        fixed = fixed.replace(/\\+\s*$/, '');
    }
    if (/\\'/.test(outside)) {
        kind ??= 'a backslash before an apostrophe';
        // In single quotes YAML writes an apostrophe twice; anywhere else, as it is.
        fixed = fixed.replace(/(:\s+)'(.*)'(\s*)$/, (_, colon, inner, end) => `${colon}'${inner.replace(/\\'/g, "''")}'${end}`)
            .replace(/\\'/g, '\'');
    }
    return kind ? { kind, fixed: [fixed.trim()] } : undefined;
}

/** The copy's one layer of escapes undone: `\\` is `\`, `\"` is `"`. */
const unescape = (text: string) => text.replace(/\\(["\\])/g, '$1');

/**
 * A line whose breaks were copied as `\n`, as the lines it was. Copied from a record's text, each piece
 * keeps its own indentation — the block's; a `Key: text` written on one line becomes a block of its own.
 */
function textLines(text: string, inBlock: boolean): string[] {
    const undone = (piece: string) => {
        for (let before = ''; before !== piece;) [before, piece] = [piece, piece.replace(/\\(["'\\])/g, '$1')];
        return piece;
    };
    const pieces = text.split(/\\+(?:r\\+)?n/).map(undone);
    while (pieces.length > 1 && !pieces[pieces.length - 1].trim()) pieces.pop();
    const inline = inBlock ? null : /^(\s*)([^\s:#'"][^:#]*):\s+(?![|>][-+]?\s*$)(\S.*)$/.exec(pieces[0]);
    if (!inline || pieces.length === 1) return pieces;
    const [, indent, key, value] = inline;
    const lines = [value, ...pieces.slice(1)];
    // A single-quoted text's quotes go; its doubled apostrophes are single ones in a block.
    if (/^'/.test(value) && /'\s*$/.test(lines[lines.length - 1])) {
        lines[0] = lines[0].slice(1);
        lines[lines.length - 1] = lines[lines.length - 1].replace(/'\s*$/, '');
        lines.forEach((line, k) => { lines[k] = line.replace(/''/g, '\''); });
    }
    return [`${indent}${key}: |-`, ...lines.map(line => line.trim() ? `${indent}  ${line.trimStart()}` : '')];
}

export interface CopiedEscape {
    file: string;
    line: number;
    kind: 'two backslashes' | 'a backslash before a quote' | 'a backslash before an apostrophe' | 'a backslash at the end of the line'
        | 'line breaks written as \\n';
    /** The line as written. */
    text: string;
    /** As it is to be: one line, the copy's escapes undone — or the lines it becomes, for line breaks. */
    fixed: string[];
}

/** YAML files in a project's folder but outside its plugin folder and build output: no build reads them. */
export function strayRecordFiles(paths: ProjectPaths): string[] {
    const skip = new Set([paths.plugin, paths.build].map(dir => path.resolve(dir).toLowerCase()));
    return yamlFilesUnder(paths.root, dir => !skip.has(path.resolve(dir).toLowerCase()));
}

/** YAML files in the projects folder that belong to no project: on its top, or in a folder that is no project. */
export function looseRecordFiles(projectsDir: string): string[] {
    let entries: fs.Dirent[];
    try {
        entries = fs.readdirSync(projectsDir, { withFileTypes: true });
    } catch {
        return [];
    }
    return entries.flatMap(entry => {
        const full = path.join(projectsDir, entry.name);
        if (entry.isFile()) return /\.ya?ml$/i.test(entry.name) ? [full] : [];
        const isProject = fs.existsSync(path.join(full, 'plugin', 'RecordData.yaml'));
        return entry.isDirectory() && !isProject && !entry.name.startsWith('.') ? yamlFilesUnder(full, () => true) : [];
    }).sort();
}

function yamlFilesUnder(root: string, descend: (dir: string) => boolean): string[] {
    const found: string[] = [];
    const walk = (dir: string) => {
        let entries: fs.Dirent[];
        try {
            entries = fs.readdirSync(dir, { withFileTypes: true });
        } catch {
            return;
        }
        for (const entry of entries) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) {
                if (!entry.name.startsWith('.') && descend(full)) walk(full);
            } else if (/\.ya?ml$/i.test(entry.name)) {
                found.push(full);
            }
        }
    };
    walk(root);
    return found.sort();
}

/**
 * A file of the projects folder as moveRecord and deleteRecord take it: its path in the project's plugin folder, the
 * record's EditorID, or a full path anywhere in the projects folder — a file written outside every project. Written
 * where the build does not read it, a file stayed there: OpenClaw gives a model no tool that moves or deletes a file,
 * and the model wrote it again where it belonged, the first copy left behind (eval round 66: 6 runs, one of them failed).
 *
 * A path the plugin folder has no file at — or only `besides`, where moveRecord puts the file — is looked for from the
 * projects folder, where OpenClaw's file tools put a Skyrim agent's relative path. The model gave moveRecord the path it had
 * written, `Spells/StormBolt.yaml` as `from` and `to`, and was refused 11 times in 3 runs, none of which built (eval round 67).
 */
export function projectsFile(projectsDir: string, paths: ProjectPaths, given: string, besides?: string): string {
    const name = given.trim();
    if (!name) throw new ProjectError('Name the file: its path in the plugin folder, like "Weapons/FlameBlade.yaml", or its full path.');
    const byEditorId = /[\\/]|\.\w+$/.test(name) ? undefined : recordFiles(paths.plugin).find(file =>
        new RegExp(`^EditorID:[ \\t]*['"]?${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}['"]?[ \\t]*$`, 'mi').test(fs.readFileSync(file, 'utf8')));
    const inPlugin = path.resolve(paths.plugin, name);
    const file = byEditorId ?? [inPlugin, path.resolve(projectsDir, name)]
        .find(candidate => candidate !== besides && fs.existsSync(candidate) && fs.statSync(candidate).isFile()) ?? inPlugin;
    // A path not there yet resolved through the nearest folder that is: Windows CI's temp folder has a short 8.3 name,
    // and a missing file's path kept it while the projects folder resolved to the long one.
    const real = (target: string): string => {
        const missing: string[] = [];
        let existing = path.resolve(target);
        while (!fs.existsSync(existing) && path.dirname(existing) !== existing) {
            missing.unshift(path.basename(existing));
            existing = path.dirname(existing);
        }
        return path.join(fs.existsSync(existing) ? fs.realpathSync.native(existing) : existing, ...missing);
    };
    const relative = path.relative(real(projectsDir), real(file));
    if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) {
        throw new ProjectError(`${file} is outside the projects folder, ${projectsDir}: only its record files are moved or deleted here.`);
    }
    if (relative.split(path.sep).some(part => part.toLowerCase() === '.git')) throw new ProjectError(`${file} is git's, no record file.`);
    if (path.basename(file).toLowerCase() === 'recorddata.yaml') throw new ProjectError(`${file} is the project's header, no record file: it stays.`);
    return file;
}

/** The project's record files, as a refusal names them: their paths in its plugin folder. */
/**
 * A record as editRecord takes it: a relative path from the projects folder to a file of the project's plugin folder — as
 * OpenClaw's file tools take a Skyrim agent's paths, `HildaTheNord/plugin/Npcs/Hilda.yaml` — as that file's full path, which
 * the helper takes; any other as given. Refused for it, the model sent the path in the plugin folder next (eval round 83's
 * npc+detailed #1, round 80's sword+detailed #1); moveRecord and deleteRecord take it so too (projectsFile). A path in the
 * plugin folder without its `.yaml`, `Npcs/Hilda`, is that file where it is there: it was refused, though `Npcs/Hilda.yaml`
 * was the only file it could name (eval round 91's npc+detailed #4).
 */
export function editedRecord(projectsDir: string, paths: ProjectPaths, given: string): string {
    const name = given.trim();
    const isFile = (file: string) => fs.existsSync(file) && fs.statSync(file).isFile();
    // Either slash, on either system: the helper reads a backslash as a separator too.
    const parts = name.split(/[\\/]+/).filter(part => part.length > 0);
    if (parts.length < 2 || path.isAbsolute(name)) return given;
    const inPlugin = path.resolve(paths.plugin, ...parts);
    if (isFile(inPlugin)) return given;
    if (!/\.ya?ml$/i.test(name) && isFile(`${inPlugin}.yaml`)) return `${inPlugin}.yaml`;
    const fromProjects = path.resolve(projectsDir, ...parts);
    const relative = path.relative(paths.plugin, fromProjects);
    return relative && !relative.startsWith('..') && !path.isAbsolute(relative) && isFile(fromProjects) ? fromProjects : given;
}

export function recordFileList(paths: ProjectPaths): string {
    const files = recordFiles(paths.plugin).map(file => path.relative(paths.plugin, file).split(path.sep).join('/'));
    return files.length === 0 ? 'none yet' : files.slice(0, 20).join(', ') + (files.length > 20 ? ` and ${files.length - 20} more` : '');
}

/**
 * The record files no build of the project reads — in its folder outside the plugin folder, or in no project — each with
 * its call, as a sentence for a refusal of a file not there; empty when there are none. Told only "No file", the model made the
 * same moveRecord call again, up to 7 times in a run (eval round 67).
 */
export function outsideRecordCalls(projectsDir: string, paths: ProjectPaths): string {
    const outside = [...strayRecordFiles(paths), ...looseRecordFiles(projectsDir)];
    return outside.length === 0 ? '' : ` The record files outside the plugin folder, each with the call that puts it where it belongs: ` +
        `${outside.slice(0, 10).map(file => placeCall(projectsDir, paths, file)).join('; ')}` +
        `${outside.length > 10 ? ` and ${outside.length - 10} more` : ''}.`;
}

function existingFile(projectsDir: string, file: string, given: string, paths: ProjectPaths, verb: string): void {
    if (!fs.existsSync(file) || !fs.statSync(file).isFile()) {
        throw new ProjectError(`No file at ${file} ('${given}'): nothing was ${verb}.${outsideRecordCalls(projectsDir, paths)} ` +
            `The project's record files: ${recordFileList(paths)}.`);
    }
}

/** Moves a record file into the project's plugin folder, its group folder made when there is none. */
export function moveRecordFile(projectsDir: string, paths: ProjectPaths, from: string, to: string): { from: string; to: string } {
    const target = path.resolve(paths.plugin, to.trim());
    const source = projectsFile(projectsDir, paths, from, target);
    existingFile(projectsDir, source, from, paths, 'moved');
    const inPlugin = path.relative(paths.plugin, target);
    if (!to.trim() || !inPlugin || inPlugin.startsWith('..') || path.isAbsolute(inPlugin)) {
        throw new ProjectError(`'${to}' is not in ${paths.plugin}: give where the file goes as its path there, like "Weapons/FlameBlade.yaml".`);
    }
    if (!/\.yaml$/i.test(target) || path.basename(target).toLowerCase() === 'recorddata.yaml') {
        throw new ProjectError(`'${to}': a record file is named for its record and ends in .yaml — the build reads no other — ` +
            `like "${inPlugin.split(path.sep).join('/').replace(/\.[^./]*$/, '')}.yaml".`);
    }
    // Another letter case of the same name is a rename; the same path is already there.
    if (source === target) throw new ProjectError(`${source} is already there: nothing to move.`);
    if (fs.existsSync(target) && source.toLowerCase() !== target.toLowerCase()) {
        throw new ProjectError(`${target} is there already. If ${source} is a copy of that record, delete it instead: ` +
            `${deleteCall(paths, source)}. If it is another record, give it a file name of its own.`);
    }
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.renameSync(source, target);
    return { from: source, to: target };
}

export function deleteRecordFile(projectsDir: string, paths: ProjectPaths, record: string): string {
    const file = projectsFile(projectsDir, paths, record);
    existingFile(projectsDir, file, record, paths, 'deleted');
    fs.rmSync(file);
    return file;
}

/**
 * A file as a call written into an answer names it: its path in the plugin folder, or its full path outside it, with
 * forward slashes — a path's backslashes reached the model escaped once per layer of JSON, four times in OpenClaw's
 * (eval round 65), and a model copies a call as it reads it.
 */
function callPath(paths: ProjectPaths, file: string): string {
    const relative = path.relative(paths.plugin, file);
    const inside = relative && !relative.startsWith('..') && !path.isAbsolute(relative);
    return (inside ? relative : path.resolve(file)).split(path.sep).join('/');
}

/** The moveRecord call that puts a file where it belongs. Written out whole, a call is made as written: 3 of 3 renamed copies (eval rounds 65–66). */
export function moveCall(paths: ProjectPaths, from: string, to: string): string {
    return `moveRecord {"project": ${JSON.stringify(path.basename(paths.root))}, "from": ${JSON.stringify(callPath(paths, from))}, ` +
        `"to": ${JSON.stringify(callPath(paths, to))}}`;
}

/**
 * A refused editRecord call, given back as the tool takes it — `set` as a list of `{path, value}` — with
 * `"build": true`, to send again once its file is there.
 */
export function editCall(paths: ProjectPaths, request: { record: string; set?: Record<string, unknown>; remove?: string[] }): string {
    return `editRecord ${callJson({
        project: path.basename(paths.root),
        record: request.record,
        ...(request.set ? { set: Object.entries(request.set).map(([field, value]) => ({ path: field, value })) } : {}),
        ...(request.remove ? { remove: request.remove } : {}),
        build: true
    })}`;
}

/** JSON as the other calls in answers are written: a space after each colon and comma. */
function callJson(value: unknown): string {
    if (Array.isArray(value)) return `[${value.map(callJson).join(', ')}]`;
    if (value && typeof value === 'object') {
        return `{${Object.entries(value).map(([key, item]) => `${JSON.stringify(key)}: ${callJson(item)}`).join(', ')}}`;
    }
    return JSON.stringify(value);
}

export function deleteCall(paths: ProjectPaths, file: string): string {
    return `deleteRecord {"project": ${JSON.stringify(path.basename(paths.root))}, "record": ${JSON.stringify(callPath(paths, file))}}`;
}

/** The call for a record file outside the plugin folder: moved where it belongs, or deleted where the plugin folder has its file already. */
export function placeCall(projectsDir: string, paths: ProjectPaths, file: string): string {
    const target = belongsAt(file, paths, projectsDir);
    return !target ? `${file} (move it into its type's group folder with moveRecord)`
        : fs.existsSync(target) ? deleteCall(paths, file) : moveCall(paths, file, target);
}

export function createProject(paths: ProjectPaths, plugin: string, light: boolean): void {
    if (!/\.(esp|esm)$/i.test(plugin)) {
        throw new ProjectError(`'${plugin}' is not a plugin file name: end it in .esp (or .esm for a master).`);
    }
    fs.mkdirSync(paths.plugin, { recursive: true });
    const flags = light ? 'ModHeader:\n  Flags:\n  - Small\n' : '';
    fs.writeFileSync(path.join(paths.plugin, 'RecordData.yaml'), `ModKey: ${plugin}\nGameRelease: SkyrimSE\n${flags}`);
}

// ---------------------------------------------------------------- FormIDs

const LIGHT_MAX = 0xFFF;
const FULL_MAX = 0xFFFFFF;
const FIRST_ID = 0x800;

/** Every record file of a plugin folder: everything but the plugin's own header. */
export function recordFiles(pluginDir: string): string[] {
    const files: string[] = [];
    const walk = (dir: string) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) walk(full);
            else if (entry.name.endsWith('.yaml') && full !== path.join(pluginDir, 'RecordData.yaml')) files.push(full);
        }
    };
    if (fs.existsSync(pluginDir)) walk(pluginDir);
    return files.sort();
}

/**
 * Gives every new record a FormID — a record file without a top-level `FormKey:` line, as an agent
 * writes one — written at the top of its file, below the kind line where it names one, and the file keeps its name;
 * a pull writes Spriggit's.
 * Once assigned, a FormID never moves: other plugins and saves will point at it.
 * Deterministic: files are numbered in path order after the highest number in use. The files in `skip`,
 * which the build leaves out, keep their text.
 */
export function assignFormIds(pluginDir: string, plugin: string, light: boolean, skip: ReadonlySet<string> = new Set()):
    { formId: string; editorId: string | null; file: string }[] {
    const used: number[] = [];
    const fresh: { file: string; text: string }[] = [];
    const ownKey = new RegExp(`^FormKey:[ \\t]*([0-9A-Fa-f]{6}):${escapeRegExp(plugin)}[ \\t]*(?:#.*)?$`, 'im');

    for (const file of recordFiles(pluginDir).filter(file => !skip.has(file))) {
        const text = fs.readFileSync(file, 'utf8');
        if (/^FormKey:/m.test(text)) {
            const own = ownKey.exec(text);
            if (own) used.push(parseInt(own[1], 16));
        } else if (!/^(Group)?RecordData\.yaml$/.test(path.basename(file))) {
            // The files Spriggit heads a block of cells or a cell's folder with are no new records:
            // numbered and renamed, they broke the next build of every project with a cell in it.
            fresh.push({ file, text });
        }
    }

    let next = Math.max(FIRST_ID - 1, ...used) + 1;
    const max = light ? LIGHT_MAX : FULL_MAX;
    const assigned: { formId: string; editorId: string | null; file: string }[] = [];
    for (const { file, text } of fresh) {
        if (next > max) {
            throw new ProjectError(
                `${plugin} is a light plugin and has no FormIDs left (the build numbers a light plugin's new records ` +
                `000800–000FFF, ${LIGHT_MAX - FIRST_ID + 1} in all). Remove the 'Small' flag from plugin/RecordData.yaml ` +
                `to make it a full plugin.`);
        }
        const id = next.toString(16).toUpperCase().padStart(6, '0');
        next++;
        // Where a group has several kinds — Globals: GlobalShort, GlobalFloat… — the reader takes the kind from the
        // first line and the FormKey from the next: written above the kind, no new global could be read.
        const at = /^MutagenObjectType:[^\n]*\n/.exec(text)?.[0].length ?? 0;
        const newline = text.includes('\r\n') ? '\r\n' : '\n';
        fs.writeFileSync(file, `${text.slice(0, at)}FormKey: ${id}:${plugin}${newline}${text.slice(at)}`);
        assigned.push({ formId: `${id}:${plugin}`, editorId: editorIdOf(text), file: relativeTo(pluginDir, file) });
    }
    return assigned;
}

/**
 * Keeps the record files' text as it stands, and answers a function that writes back each file changed
 * since. A build the helper refuses gives back the links it had written as FormIDs: a model that then
 * wrote the record they named anew, without its FormKey line, left a link to a number no record had
 * (eval probe 8 after round 11). FormIDs given by assignFormIds before stay.
 */
export function keepRecordTexts(pluginDir: string): () => void {
    const texts = new Map(recordFiles(pluginDir).map(file => [file, fs.readFileSync(file, 'utf8')]));
    return () => {
        for (const [file, text] of texts) {
            if (fs.existsSync(file) && fs.readFileSync(file, 'utf8') !== text) fs.writeFileSync(file, text);
        }
    };
}

// A comment after either line is no part of it: `FormKey: 000123:Skyrim.esm # Placeholder` went unread (eval round 14).
const editorIdOf = (text: string) => /^EditorID:[ \t]*([^\s#]+)/m.exec(text)?.[1] ?? null;
const relativeTo = (dir: string, file: string) => path.relative(dir, file).replace(/\\/g, '/');

function escapeRegExp(text: string): string {
    return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// ---------------------------------------------------------------- files

/**
 * The newest modification time of the project's sources: plugin, scripts and assets. Folders count
 * too, because deleting a file changes only its folder's time.
 */
export function newestSourceTime(paths: ProjectPaths): number {
    let newest = 0;
    const walk = (dir: string) => {
        if (!fs.existsSync(dir)) return;
        newest = Math.max(newest, fs.statSync(dir).mtimeMs);
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) walk(full);
            else newest = Math.max(newest, fs.statSync(full).mtimeMs);
        }
    };
    walk(paths.plugin);
    walk(paths.scripts);
    walk(paths.assets);
    return newest;
}

/**
 * Makes `target` hold exactly the files of `sources` — a later source wins over an earlier one —
 * plus whatever `keep` names among its own top-level entries. Returns the number of files.
 *
 * A file that is already there is written in place, never deleted and replaced: a mod manager that
 * deployed it into the game as a hard link (Amethyst) then sees the new content at once, rather than
 * keeping the old file alive in the game's Data. Names are matched without regard to case, as the
 * game matches them.
 */
export function syncFolder(sources: readonly string[], target: string, keep: (name: string) => boolean): number {
    const wanted = new Map<string, { relative: string; file: string }>();
    const walk = (root: string, dir: string) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) walk(root, full);
            else {
                const relative = path.relative(root, full);
                wanted.set(relative.toLowerCase(), { relative, file: full });
            }
        }
    };
    for (const source of sources) if (fs.existsSync(source)) walk(source, source);

    const prune = (dir: string, prefix: string) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const relative = path.join(prefix, entry.name);
            const full = path.join(dir, entry.name);
            if (!prefix && keep(entry.name)) continue;
            if (entry.isDirectory()) {
                prune(full, relative);
                if (fs.readdirSync(full).length === 0) fs.rmdirSync(full);
            } else if (!wanted.has(relative.toLowerCase())) {
                fs.rmSync(full);
            }
        }
    };
    if (fs.existsSync(target)) prune(target, '');

    for (const { relative, file } of wanted.values()) {
        const destination = findCaseInsensitive(target, relative) ?? path.join(target, relative);
        fs.mkdirSync(path.dirname(destination), { recursive: true });
        fs.writeFileSync(destination, fs.readFileSync(file));
    }
    return wanted.size;
}

/** A link with the editor ID readRecord and addPatch write beside it: `ObjectEffect: 069CE7:Skyrim.esm  # BoundSwordEnchantment`. */
const LINK_COMMENT = /^([ \t]*(?:- )?(?:\w+:[ \t]*)?[0-9A-Fa-f]{6}:\S+\.es[mpl])[ \t]+#[^\n]*$/gim;
const withoutLinkComments = (text: string) => text.replace(LINK_COMMENT, '$1');

/**
 * Makes `target` hold exactly the YAML files of `source`, touching only what differs — so git sees
 * the real change and nothing else. A file that differs only in the editor IDs beside its links, which
 * the plugin does not hold, is left as it is. Paths are relative to the folders, with forward slashes.
 */
export function syncYaml(source: string, target: string): { added: string[]; changed: string[]; removed: string[] } {
    const rel = (root: string, file: string) => path.relative(root, file).replace(/\\/g, '/');
    const list = (root: string) => {
        const files = new Map<string, string>();
        const walk = (dir: string) => {
            if (!fs.existsSync(dir)) return;
            for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
                const full = path.join(dir, entry.name);
                if (entry.isDirectory()) walk(full);
                else if (entry.name.endsWith('.yaml')) files.set(rel(root, full), full);
            }
        };
        walk(root);
        return files;
    };

    const incoming = list(source);
    const existing = list(target);
    const result = { added: [] as string[], changed: [] as string[], removed: [] as string[] };

    for (const [relative, file] of incoming) {
        const text = fs.readFileSync(file, 'utf8');
        const destination = path.join(target, relative);
        if (!existing.has(relative)) {
            result.added.push(relative);
        } else if (withoutLinkComments(fs.readFileSync(destination, 'utf8')) === withoutLinkComments(text)) {
            continue;
        } else {
            result.changed.push(relative);
        }
        fs.mkdirSync(path.dirname(destination), { recursive: true });
        fs.writeFileSync(destination, text);
    }
    for (const [relative, file] of existing) {
        if (!incoming.has(relative)) {
            fs.rmSync(file);
            result.removed.push(relative);
        }
    }
    removeEmptyFolders(target);
    return result;
}

function removeEmptyFolders(dir: string): void {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        if (!entry.isDirectory()) continue;
        const full = path.join(dir, entry.name);
        removeEmptyFolders(full);
        if (fs.readdirSync(full).length === 0) fs.rmdirSync(full);
    }
}

export function fileHash(file: string): string {
    return createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

// ---------------------------------------------------------------- deploy state

export interface DeployState {
    modFolder: string;
    plugin: string;
    /** sha256 of the plugin as it was deployed — or pulled, which makes it the project's again. */
    pluginHash: string;
    at: string;
}

export function readDeployState(paths: ProjectPaths): DeployState | undefined {
    try {
        return JSON.parse(fs.readFileSync(paths.deployState, 'utf8'));
    } catch {
        return undefined;
    }
}

export function writeDeployState(paths: ProjectPaths, state: DeployState): void {
    fs.writeFileSync(paths.deployState, `${JSON.stringify(state, null, 2)}\n`);
}
