import type { PapyrusEntry, PapyrusFrame, PapyrusLog } from './papyrusLog.js';
import { gameOwn, kindOf, MESSAGE_KINDS, type MessageClass } from './papyrusMessages.js';

/**
 * What a Papyrus log says about the setup's scripts, grouped, counted and ranked rather than listed line by line — most of a
 * log is harmless (docs/research/papyrus-log-format.md §3): script and install problems first, by how often; what the save
 * holds of mods removed mid-playthrough; the script lag stack dumps show; the harmless messages as counts only. Each script
 * by the mod whose .pex the game loads, each FormID by the plugin of its load-order index.
 */
export interface PapyrusReport {
    log: { file: string; opened?: string; closed: boolean; lines: number; entries: number; loads: number; saves: number };
    /** Whether the game writes the log now, by the INIs it reads. */
    logging?: PapyrusLogging;
    problems: PapyrusProblem[];
    /** Script lag: the stack dumps, and the events most of their stacks run. */
    load?: { dumps: number; stacks: number; first: string; events: { event: string; stacks: number }[] };
    /** The messages of no known kind, by their text with numbers taken out: the most frequent. */
    other?: { count: number; messages: { text: string; count: number; line: number }[] };
    noise: { count: number; kinds: Record<string, number> };
    /** The header's [Papyrus] settings that differ from the game's. */
    settings?: Record<string, number>;
    nextStep: string;
}

export interface PapyrusLogging {
    enabled: boolean;
    trace: boolean;
    debugInfo: boolean;
    /** The INI the game reads them from. */
    file: string;
}

export interface PapyrusProblem {
    kind: string;
    class: Exclude<MessageClass, 'noise'>;
    title: string;
    count: number;
    explain: string;
    fix: string;
    scripts: { script: string; count: number; mod?: string; file?: string }[];
    forms?: { formId: string; name?: string; plugin?: string }[];
    first: { line: number; time: string; text: string; stack?: string[] };
    /** Fifty or more: a flood, where Papyrus spends its time. */
    flood?: true;
}

/** Where the setup has a script's .pex — `loose` or `in X.bsa` — and the mod that gives it; undefined for one not in the setup. */
export type ScriptOwners = (scripts: string[]) => Promise<Map<string, { mod: string; file: string }>>;

/** The plugin a runtime FormID's load-order index stands for, in the load order now; undefined where none does. */
export type PluginOf = (formId: string) => string | undefined;

const MAX_PROBLEMS = 10;
const MAX_SCRIPTS = 5;
const MAX_FORMS = 5;
const MAX_OTHER = 5;
/** The count from which one problem is a flood: Papyrus spends its time on it. */
const FLOOD = 50;
/** The game's own [Papyrus] header values, to tell the user's tweaks. */
const DEFAULTS = { updateBudgetMs: 1.2, extraTaskletMs: 1.2, loadScreenMs: 500 };
const DEFAULT_MEMORY = { min: 128, max: 512, total: 153600 };

const frameText = (frame: PapyrusFrame) => `[${frame.self}].${frame.script}.${frame.fn}() - ${frame.at}`;

export async function buildPapyrusReport(
    file: string, log: PapyrusLog, scriptOwners: ScriptOwners, pluginOf: PluginOf, logging?: PapyrusLogging
): Promise<PapyrusReport> {
    type Group = PapyrusProblem & { scriptCounts: Map<string, number>; formSet: Map<string, { name?: string }> };
    const groups = new Map<string, Group>();
    const noise: Record<string, number> = {};
    let noiseCount = 0;
    const other = new Map<string, { text: string; count: number; line: number }>();
    const missing = new Set<string>();

    for (const entry of log.entries) {
        const found = kindOf(entry);
        if (!found) {
            if (entry.severity === 'error' || entry.severity === 'warning') {
                const text = entry.message.replace(/\([0-9A-Fa-f]{8}\)/g, '(…)').replace(/\b\d+\b/g, 'N').slice(0, 160);
                const known = other.get(text);
                if (known) known.count++;
                else other.set(text, { text, count: 1, line: entry.line });
            }
            continue;
        }
        const { kind, match, form } = found;
        if (kind.class === 'noise') {
            noise[kind.id] = (noise[kind.id] ?? 0) + 1;
            noiseCount++;
            continue;
        }
        const script = kind.script?.(match, entry);
        if (kind.id === 'missing-script' || kind.id === 'bind-missing') { if (script) missing.add(script.toLowerCase()); }
        // What the game writes with no mod is no problem of the setup's: counted with the noise.
        if (gameOwn(kind.id, script, entry.message)) {
            noise['game-own'] = (noise['game-own'] ?? 0) + 1;
            noiseCount++;
            continue;
        }
        const key = kind.id;
        let group = groups.get(key);
        if (!group) {
            group = {
                kind: kind.id, class: kind.class, title: kind.title, count: 0, explain: kind.explain, fix: kind.fix, scripts: [],
                first: { line: entry.line, time: entry.time, text: entry.message, ...(entry.stack.length ? { stack: entry.stack.slice(0, 5).map(frameText) } : {}) },
                scriptCounts: new Map(), formSet: new Map()
            };
            groups.set(key, group);
        }
        group.count++;
        if (script) group.scriptCounts.set(script, (group.scriptCounts.get(script) ?? 0) + 1);
        for (const formId of formsOf(entry, form?.formId)) if (!group.formSet.has(formId)) group.formSet.set(formId, form?.formId === formId && form.name ? { name: form.name } : {});
    }

    // A script missing from the setup bound to the wrong type is the missing script again: in the save's history, not a bug.
    const mismatch = groups.get('base-type-mismatch');
    if (mismatch) {
        for (const script of [...mismatch.scriptCounts.keys()]) {
            if (!missing.has(script.toLowerCase())) continue;
            mismatch.count -= mismatch.scriptCounts.get(script)!;
            mismatch.scriptCounts.delete(script);
        }
        if (mismatch.count <= 0) groups.delete('base-type-mismatch');
    }

    const ranked = [...groups.values()]
        .sort((a, b) => (a.class === b.class ? 0 : a.class === 'bug' ? -1 : 1) || b.count - a.count)
        .slice(0, MAX_PROBLEMS);
    const scripts = [...new Set(ranked.flatMap(group => [...group.scriptCounts.keys()]))];
    let owners = new Map<string, { mod: string; file: string }>();
    try {
        owners = await scriptOwners(scripts);
    } catch {
        // Scripts are listed without their mods.
    }
    const problems: PapyrusProblem[] = ranked.map(({ scriptCounts, formSet, ...group }) => {
        const listed = [...scriptCounts.entries()].sort((a, b) => b[1] - a[1]).slice(0, MAX_SCRIPTS).map(([script, count]) => {
            const owner = owners.get(script.toLowerCase());
            return { script, count, ...(owner ? { mod: owner.mod, file: owner.file } : { file: 'not in the setup' }) };
        });
        const forms = [...formSet.entries()].slice(0, MAX_FORMS).map(([formId, { name }]) => {
            const plugin = pluginOf(formId);
            return { formId, ...(name ? { name } : {}), ...(plugin ? { plugin } : {}) };
        });
        return { ...group, scripts: listed, ...(forms.length ? { forms } : {}), ...(group.count >= FLOOD ? { flood: true as const } : {}) };
    });

    const stacks = log.dumps.flatMap(dump => dump.stacks);
    const events = new Map<string, number>();
    for (const stack of stacks) {
        // The event the engine called is the stack's outermost frame.
        const outer = stack.frames.at(-1);
        if (outer) events.set(`${outer.script}.${outer.fn}`, (events.get(`${outer.script}.${outer.fn}`) ?? 0) + 1);
    }
    for (const dump of log.dumps) for (const item of dump.summary) if (!stacks.length) events.set(item.event, (events.get(item.event) ?? 0) + item.frequency);
    const load = log.dumps.length ? {
        dumps: log.dumps.length, stacks: stacks.length, first: log.dumps[0].time,
        events: [...events.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5).map(([event, count]) => ({ event, stacks: count }))
    } : undefined;

    const settings: Record<string, number> = {};
    for (const [key, value] of Object.entries(DEFAULTS) as [keyof typeof DEFAULTS, number][]) {
        const seen = log.header[key];
        if (seen !== undefined && Math.abs(seen - value) > 1e-6) settings[key] = seen;
    }
    if (log.header.memoryPage && log.header.memoryPage.total !== DEFAULT_MEMORY.total) settings.memoryTotal = log.header.memoryPage.total;

    const otherList = [...other.values()].sort((a, b) => b.count - a.count).slice(0, MAX_OTHER);
    const report: PapyrusReport = {
        log: { file, ...(log.opened ? { opened: log.opened } : {}), closed: log.closed, lines: log.lines, entries: log.entries.length, loads: log.loads, saves: log.saves },
        ...(logging ? { logging } : {}),
        problems,
        ...(load ? { load } : {}),
        ...(otherList.length ? { other: { count: [...other.values()].reduce((sum, item) => sum + item.count, 0), messages: otherList } } : {}),
        noise: { count: noiseCount, kinds: noise },
        ...(Object.keys(settings).length ? { settings } : {}),
        nextStep: ''
    };
    report.nextStep = nextStep(report);
    return report;
}

/** The FormIDs an entry names: the form before its message, those its stack's frames run on. */
function formsOf(entry: PapyrusEntry, named?: string): string[] {
    const ids = [...(named ? [named] : []), ...entry.stack.map(frame => frame.formId).filter((id): id is string => !!id)];
    return [...new Set(ids)].filter(id => !/^FF/i.test(id));
}

function nextStep(report: PapyrusReport): string {
    const notCrash = 'The Papyrus log is a script log, not a crash log: never put a crash on it — readCrashLog reads why the game crashed.';
    const bug = report.problems.find(problem => problem.class === 'bug');
    const history = report.problems.find(problem => problem.class === 'history');
    const parts: string[] = [];
    if (bug) {
        const top = bug.scripts[0];
        parts.push(`Tell the user the first problem, "${bug.title}" (${bug.count}×${bug.flood ? ', a flood' : ''})` +
            (top ? `, of the script ${top.script}${top.mod ? ` from ${top.mod}` : ''}` : '') + ', and what its fix would do.');
    } else if (history) {
        parts.push(`No script bug: what the log shows is the save's history — "${history.title}" (${history.count}×). Say what it means and that it is usually harmless.`);
    } else {
        parts.push(`No script problem: ${report.noise.count} harmless messages${report.other ? ` and ${report.other.count} of no known kind` : ''}.`);
    }
    if (report.load) parts.push(`${report.load.dumps} stack dump${report.load.dumps === 1 ? '' : 's'}: script lag${report.load.events[0] ? `, most of it ${report.load.events[0].event}` : ''}.`);
    parts.push(notCrash, 'Change nothing unless the user asks: disableMod takes a mod out, installMod installs a fixed version.');
    return parts.join(' ');
}

/** The message kinds' ids, for the docs and tests. */
export const PAPYRUS_KINDS = MESSAGE_KINDS.map(kind => kind.id);
