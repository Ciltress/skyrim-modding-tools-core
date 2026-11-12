import { execFile } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { promisify } from 'node:util';
import type { Instance, ModEntry } from './modManager.js';
import { findCaseInsensitive } from './paths.js';
import type { Platform } from './platform.js';

const run = promisify(execFile);

/** Papyrus and git, the two outside programs a project build and pull rely on. */

/** A script did not compile — something to fix in the project's sources. */
export class ScriptCompileError extends Error { }

/** The compiler cannot run on this machine — something to install, not something in the project. */
export class CompilerUnavailableError extends Error { }

/** Where the vanilla script sources can be: the Creation Kit unpacks them to the first. */
export const SOURCE_FOLDERS = ['Data/Source/Scripts', 'Data/Scripts/Source'];
const MOD_SOURCE_FOLDERS = ['Source/Scripts', 'Scripts/Source'];

export interface CompileOptions {
    platform: Platform;
    /** SKYRIM_PAPYRUS_COMPILER, when set. */
    compiler?: string;
    /** Where a compiler is looked for without one — Platform.papyrusCompilerCandidates. */
    searched?: readonly string[];
    creationKitDir: string;
    scriptsDir: string;
    outputDir: string;
    instance: Instance;
    mods: readonly ModEntry[];
}

/**
 * Compiles a project's Papyrus sources into `outputDir`. Returns how many there were; none is no
 * work, and needs no compiler.
 */
export async function compileScripts(options: CompileOptions): Promise<number> {
    const { scriptsDir } = options;
    const sources = fs.existsSync(scriptsDir) ? fs.readdirSync(scriptsDir).filter(f => f.toLowerCase().endsWith('.psc')) : [];
    if (sources.length === 0) return 0;

    const { executable, args } = await papyrusCommand(options);
    fs.mkdirSync(options.outputDir, { recursive: true });
    try {
        await run(executable, args, { windowsHide: true, timeout: 300_000, maxBuffer: 10 * 1024 * 1024 });
    } catch (error: any) {
        // The compiler reports problems as `<file>(line,col): message`, then a summary.
        const lines = `${error?.stdout ?? ''}\n${error?.stderr ?? ''}`.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
        const problems = lines.filter(line => /\(\d+,\d+\):|failed|unable/i.test(line));
        const vanilla = SOURCE_FOLDERS.map(folder => findCaseInsensitive(options.creationKitDir, folder)).find(Boolean);
        const hints = vanilla ? papyrusHints(problems, scriptsDir, vanilla) : [];
        throw new ScriptCompileError(
            `Papyrus compilation failed:\n${(problems.length ? problems : lines).slice(0, 25).join('\n') || error?.message}` +
            (hints.length ? `\n${hints.join('\n')}` : ''));
    }
    return sources.length;
}

/** A game script's source by its name, from the vanilla sources, with its file's own name; null where it has none. */
function vanillaSource(vanilla: string, script: string): { text: string; file: string } | null {
    const file = findCaseInsensitive(vanilla, `${script}.psc`);
    return file && fs.existsSync(file) ? { text: fs.readFileSync(file, 'latin1'), file: path.basename(file) } : null;
}

/** A function's or event's declaration in a script's source, its continued lines joined; null where it declares none. */
export function declarationIn(source: string, name: string): string | null {
    const lines = source.split(/\r?\n/);
    const at = lines.findIndex(line => new RegExp(`^\\s*(?:\\w+(?:\\[\\])?\\s+)?(?:Event|Function)\\s+${name}\\s*\\(`, 'i').test(line));
    if (at < 0) return null;
    let declared = lines[at].trim();
    for (let i = at + 1; declared.endsWith('\\') && i < lines.length; i++) declared = `${declared.slice(0, -1).trim()} ${lines[i].trim()}`;
    return declared.replace(/\s*;.*$/, '').replace(/\s+native\b.*$/i, '').trim();
}

/**
 * What the compiler's words leave out, from the game's sources the model's file tools cannot read: the parent's declaration
 * of an event written with other parameters, and the functions of the type a call named none of. Eval round 139's
 * storymanager runs guessed six names of a location alias's functions and two parameter lists of OnStoryChangeLocation, a
 * build each, and the sources were outside the sandbox.
 */
export function papyrusHints(problems: readonly string[], scriptsDir: string, vanilla: string): string[] {
    const hints = new Set<string>();
    for (const hint of writingHints(problems, scriptsDir)) hints.add(hint);
    for (const problem of problems) {
        const mismatch = /parameter types of function (\w+) .*do not match the parent script (\w+)/i.exec(problem);
        if (mismatch) {
            const [, name, parent] = mismatch;
            const declared = vanillaSource(vanilla, parent);
            const line = declared ? declarationIn(declared.text, name) : null;
            if (declared && line) hints.add(`${declared.file} declares it so — write these parameter types: ${line}`);
            continue;
        }
        const missing = /^(.+?\.psc)\((\d+),\d+\):\s*(\w+) is not a function or does not exist/i.exec(problem);
        if (!missing) continue;
        const [, file, lineNumber, called] = missing;
        const script = path.isAbsolute(file) ? file : path.join(scriptsDir, path.basename(file));
        if (!fs.existsSync(script)) continue;
        const source = fs.readFileSync(script, 'latin1');
        const line = source.split(/\r?\n/)[Number(lineNumber) - 1] ?? '';
        const variable = new RegExp(`(\\w+)\\s*\\.\\s*${called}\\s*\\(`, 'i').exec(line)?.[1];
        if (!variable) continue;
        const type = new RegExp(`^\\s*(\\w+)\\s+(?:Property\\s+)?${variable}\\b`, 'im').exec(source)?.[1];
        const declared = type ? vanillaSource(vanilla, type) : null;
        if (!type || !declared) continue;
        const functions = [...declared.text.matchAll(/^\s*(?:\w+(?:\[\])?\s+)?Function\s+(\w+)\s*\(/gim)].map(m => m[1]);
        const parent = /^\s*Scriptname\s+\w+\s+extends\s+(\w+)/im.exec(declared.text)?.[1];
        if (functions.length) {
            hints.add(`${variable} is ${/^[AEIOU]/i.test(type) ? 'an' : 'a'} ${type}, whose functions are ${functions.join(', ')}` +
                (parent ? `, and ${parent}'s, which it extends` : '') + ` (${declared.file} of the game's sources)`);
        }
    }
    return [...hints];
}

/** How a record a script uses is declared and given to it, said where a script wrote one otherwise. */
const PROPERTY_FILLED = 'a record a script uses is a property, declared at its top — `MiscObject Property Gold001 Auto`, the record\'s type ' +
    'first (the game\'s gold, Gold001, is a MiscObject) — and given to it by the record the script is attached to, in its ' +
    'VirtualMachineAdapter\'s Properties: `- MutagenObjectType: ScriptObjectProperty`, `Name: Gold001`, `Object: Gold001`';

/**
 * The compiler's words for what is written unlike Papyrus, said as what to write: the line each names, read from the project's
 * source. Eval round 149's activator runs spent 20 builds on five such lines — `If x Then`, `MiscObjectProperty Gold001 Auto`,
 * `Player.AddItem(…)`, a FormID in a call, a script whose file had another name — told only "required (...)+ loop did not match
 * anything at input 'Then'", "mismatched input '\n' expecting STATE" or "variable Player is undefined"; eval round 144's message
 * and storymanager runs wrote `#` comments and `Message WelcomeMessage Auto`.
 */
function writingHints(problems: readonly string[], scriptsDir: string): string[] {
    const hints: string[] = [];
    const project = path.basename(path.dirname(scriptsDir));
    for (const problem of problems) {
        const at = /^(.+?\.psc)\((\d+),\d+\):\s*(.*)$/i.exec(problem);
        if (!at) continue;
        const [, file, lineNumber, message] = at;
        const script = path.isAbsolute(file) ? file : path.join(scriptsDir, path.basename(file));
        const name = path.basename(script);
        const source = fs.existsSync(script) ? fs.readFileSync(script, 'latin1') : '';
        const line = (source.split(/\r?\n/)[Number(lineNumber) - 1] ?? '').trim();
        const where = `line ${lineNumber} of ${name}, \`${line}\``;
        const renamed = /filename does not match script name:\s*(\w+)/i.exec(message);
        if (renamed) {
            const declared = /^\s*Scriptname\s+(\w+)/im.exec(source)?.[1] ?? renamed[1];
            const twin = fs.existsSync(scriptsDir) ? fs.readdirSync(scriptsDir).find(f => f.toLowerCase() === `${declared.toLowerCase()}.psc`) : undefined;
            const remove = `deleteRecord {"project": ${JSON.stringify(project)}, "record": ${JSON.stringify(script.split(path.sep).join('/'))}}`;
            hints.push(twin
                ? `${name} declares \`ScriptName ${declared}\`, as ${twin} does, and a script's file is named as its ScriptName: ${twin} holds ` +
                  `that script, and ${name} is left over — ${remove}`
                : `${name} declares \`ScriptName ${declared}\`, and a script's file is named as its ScriptName: write it as ${declared}.psc, ` +
                  `then delete ${name} — ${remove}`);
            continue;
        }
        if (/^(#|\/\/)/.test(line) || /\s(#|\/\/)\s/.test(line) && /no viable alternative at (character|input)|mismatched input '\/'/i.test(message)) {
            hints.push(`${where}: a Papyrus comment starts with \`;\` — \`#\` and \`//\` are none`);
            continue;
        }
        if (/^(Else)?If\b/i.test(line) && /\bThen\s*$/i.test(line)) {
            hints.push(`${where}: Papyrus writes no \`Then\` — \`${line.replace(/\s+Then\s*$/i, '')}\` alone on its line, its block ended by \`EndIf\``);
            continue;
        }
        const glued = /^(\w+?)Property\s+(\w+)(?:\s+Auto\b.*)?$/i.exec(line);
        if (glued && glued[1].length > 0) {
            hints.push(`${where}: a property is declared with its type and the word Property apart — \`${glued[1]} Property ${glued[2]} Auto\``);
            continue;
        }
        const bare = /^Property\s+(\w+)(?:\s+Auto\b.*)?$/i.exec(line);
        if (bare) {
            hints.push(`${where}: ${PROPERTY_FILLED}`);
            continue;
        }
        const keywordless = /^(\w+)\s+(\w+)\s+Auto\b/i.exec(line);
        if (keywordless && !/\bProperty\b/i.test(line)) {
            hints.push(`${where}: a property is declared with the word Property between its type and its name — \`${keywordless[1]} Property ${keywordless[2]} Auto\``);
            continue;
        }
        if (/\b(variable Player is undefined|unknown type player)\b/i.test(message) || /^Player\s*\./.test(line) && /\bnone is not\b/i.test(message)) {
            hints.push(`${where}: Papyrus has no Player — the player is \`Game.GetPlayer()\`, an Actor: \`Game.GetPlayer().AddItem(Gold001, 100)\`, ` +
                'or `Actor player = Game.GetPlayer()` first');
            continue;
        }
        if (/\(\s*[0-9A-Fa-f]{6,8}(?::[^,)]+)?\s*[,)]/.test(line)) {
            hints.push(`${where}: a script names no record by its FormID — ${PROPERTY_FILLED}`);
            continue;
        }
        const undefinedName = /variable (\w+) is undefined/i.exec(message);
        if (undefinedName) {
            hints.push(`${where}: ${undefinedName[1]} is declared nowhere in the script — ${PROPERTY_FILLED}; a number of its own is a ` +
                `variable, \`Int ${undefinedName[1]} = 100\``);
        }
    }
    return [...new Set(hints)];
}

/**
 * The compiler command for a project's scripts: the compiler `SKYRIM_PAPYRUS_COMPILER` names, else
 * the Creation Kit's where the platform can run it. Two command lines are spoken, told apart by the
 * executable's name: the Creation Kit's `PapyrusCompiler.exe`, and russo-2025's `papyrus-compiler`,
 * which runs on Linux (docs/research/2026-10-15-linux-mo2-and-platform.md §7).
 *
 * Imports, in order of precedence: the project's own sources, every enabled mod's script sources
 * (SKSE's among them, when installed as a mod), then the vanilla sources the Creation Kit unpacked.
 */
export async function papyrusCommand(options: CompileOptions): Promise<{ executable: string; args: string[] }> {
    const { platform, creationKitDir, scriptsDir, outputDir, instance, mods } = options;
    const compiler = options.compiler || platform.creationKitCompiler(creationKitDir) || options.searched?.find(file => fs.existsSync(file));
    if (!compiler) throw new CompilerUnavailableError((await platform.creationKitCompilerProblem())!);
    const vanilla = SOURCE_FOLDERS.map(folder => findCaseInsensitive(creationKitDir, folder)).find(Boolean);
    if (!fs.existsSync(compiler) || !vanilla) {
        throw new CompilerUnavailableError(
            `The project has Papyrus scripts, but ${!fs.existsSync(compiler) ? `there is no Papyrus compiler at '${compiler}'` :
                `the vanilla script sources are not in '${creationKitDir}'`}. Install the Creation Kit (Steam) and open it ` +
            `once so it unpacks Scripts.zip, or set SKYRIM_CK_DIR to its folder` +
            `${options.compiler ? '' : ', or SKYRIM_PAPYRUS_COMPILER to a compiler'}.`);
    }

    const modSources = modSourceFolders(instance, mods);

    if (/^papyruscompiler(\.exe)?$/i.test(path.basename(compiler))) {
        const problem = await platform.creationKitCompilerProblem();
        if (problem) throw new CompilerUnavailableError(problem);
        const flags = findCaseInsensitive(vanilla, 'TESV_Papyrus_Flags.flg');
        if (!flags) throw new CompilerUnavailableError(`No TESV_Papyrus_Flags.flg in '${vanilla}': open the Creation Kit once so it unpacks Scripts.zip.`);
        return {
            executable: compiler,
            args: [scriptsDir, '-all', `-f=${flags}`, `-i=${[scriptsDir, ...modSources, vanilla].join(';')}`, `-o=${outputDir}`, '-quiet']
        };
    }
    // russo-2025's compiler: the project's sources are the input, everything else is read as headers.
    return {
        executable: compiler,
        args: ['compile', '-nocache', '-i', scriptsDir, '-o', outputDir, ...[...modSources, vanilla].flatMap(dir => ['-h', dir])]
    };
}

/** Every enabled mod's script sources, in mod order: what the compiler imports before the vanilla sources. */
export function modSourceFolders(instance: Instance, mods: readonly ModEntry[]): string[] {
    return mods
        .filter(mod => mod.enabled && mod.managed)
        .flatMap(mod => MOD_SOURCE_FOLDERS.map(folder => findCaseInsensitive(path.join(instance.modsDir, mod.name), folder)))
        .filter((dir): dir is string => !!dir);
}

/**
 * The folders a script's source is read from, in the compiler's order of precedence: the project's own, every enabled
 * mod's, then the vanilla sources the Creation Kit unpacked — those of them that exist.
 */
export function scriptSourceFolders(scriptsDir: string, creationKitDir: string, instance: Instance, mods: readonly ModEntry[]): string[] {
    const vanilla = SOURCE_FOLDERS.map(folder => findCaseInsensitive(creationKitDir, folder)).find(Boolean);
    return [scriptsDir, ...modSourceFolders(instance, mods), ...(vanilla ? [vanilla] : [])].filter(dir => fs.existsSync(dir));
}

/**
 * Whether a folder has uncommitted changes, by git's own account. `undefined` when git cannot say —
 * not installed, or the folder is not in a repository.
 */
export async function hasUncommittedChanges(dir: string): Promise<boolean | undefined> {
    try {
        const { stdout } = await run('git', ['status', '--porcelain', '--', '.'], { cwd: dir, windowsHide: true, timeout: 30_000 });
        return stdout.trim().length > 0;
    } catch {
        return undefined;
    }
}
