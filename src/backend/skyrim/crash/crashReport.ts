import path from 'node:path';
import type { ChosenLog } from './crashLogFiles.js';
import type { SectionName } from './crashLog.js';
import { CrashView, evidenceRank, type Confidence, type CrashRule, type Evidence, type Finding, type Place } from './crashRules.js';
import { candidates, type Candidate } from './suspects.js';
import type { Attributor, Owner } from './attribution.js';
import { GAME_FOLDER } from './attribution.js';

/**
 * readCrashLog's answer: the crash, at most five diagnoses and five suspect mods, each with the lines
 * that point at it, the records the log names with the plugins behind them, and what to do next.
 */
export interface CrashReport {
    log: LogFacts;
    crash: {
        exception?: string;
        /** `SkyrimSE.exe+0D3D2E0`, `CrashTestDll.dll+0001234`. */
        at?: string;
        firstLine: string;
        /** What the game was doing (Crash Logger 1.27): the menus open, the player's cell. */
        gameState?: string[];
        /** The model the game was loading when it crashed (1.26 on). */
        loadingModel?: string;
    };
    diagnoses: ReportedDiagnosis[];
    suspects: Suspect[];
    records?: ReportedRecord[];
    /** The crash logs in the folder, by whose they are. */
    logs: ChosenLog['counts'];
    nextStep: string;
}

export interface LogFacts {
    file: string;
    crashTime?: string;
    game?: string;
    crashLogger?: string;
    /** False: another setup's game wrote it; absent when the log stops before saying. */
    thisSetup?: boolean;
    /** False when the game was gone before Crash Logger finished the log. */
    complete: boolean;
}

export interface ReportedDiagnosis {
    title: string;
    confidence: Confidence;
    explain: string;
    fix: string;
    /** Where in the log: `first line: …`, `call stack 3: …`. */
    evidence: string[];
    rule: string;
}

export interface Suspect {
    mod: string;
    /** What of the mod's the log names: `CrashTestMesh.esp (plugin)`, `meshes\x.nif (in CrashTestMesh.bsa)`. */
    files: string[];
    evidence: string[];
    nexus?: { modId: number };
}

export interface ReportedRecord {
    formId: string;
    type: string;
    name?: string;
    /** `Siege at Icemoth.esp (mod Siege at Icemoth)`. */
    definedBy?: string;
    lastChangedBy?: string;
}

export interface CrashLogSection {
    log: LogFacts;
    section: SectionName;
    heading?: string;
    lines: string[];
    /** Lines left out past the first SECTION_LINES. */
    more?: number;
}

export const MAX_DIAGNOSES = 5;
export const MAX_SUSPECTS = 5;
const MAX_RECORDS = 8;
const MAX_EVIDENCE = 3;
export const SECTION_LINES = 300;

const CONFIDENCE_ORDER: Record<Confidence, number> = { certain: 0, likely: 1, possible: 2 };

/** Every rule's findings, best first: by confidence, then by where their evidence is. A rule that throws is skipped. */
export function rankFindings(view: CrashView, rules: readonly CrashRule[]): (Finding & { rule: string })[] {
    const found: (Finding & { rule: string; order: number })[] = [];
    rules.forEach((rule, order) => {
        let result;
        try {
            result = rule.diagnose(view);
        } catch {
            return;
        }
        for (const finding of [result ?? []].flat()) found.push({ ...finding, rule: rule.id, order });
    });
    return found
        .sort((a, b) => {
            const [ra, rb] = [evidenceRank(a), evidenceRank(b)];
            return CONFIDENCE_ORDER[a.confidence] - CONFIDENCE_ORDER[b.confidence] || ra[0] - rb[0] || ra[1] - rb[1] || a.order - b.order;
        })
        .map(({ order: _order, ...finding }) => finding);
}

export function logFacts(chosen: ChosenLog): LogFacts {
    const { log } = chosen;
    return {
        file: chosen.file,
        ...(log.crashTime ? { crashTime: log.crashTime } : {}),
        ...(log.gameVersion ? { game: log.gameVersion } : {}),
        ...(log.loggerVersion ? { crashLogger: log.loggerVersion } : {}),
        ...(chosen.thisSetup !== undefined ? { thisSetup: chosen.thisSetup } : {}),
        complete: log.complete
    };
}

export function crashLogSection(chosen: ChosenLog, section: SectionName): CrashLogSection {
    const lines = section === 'header' ? chosen.log.sections.header ?? [] : chosen.log.sections[section] ?? [];
    return {
        log: logFacts(chosen),
        section,
        ...(chosen.log.headings[section] ? { heading: chosen.log.headings[section] } : {}),
        lines: lines.slice(0, SECTION_LINES),
        ...(lines.length > SECTION_LINES ? { more: lines.length - SECTION_LINES } : {})
    };
}

/**
 * @param nexusOf  A mod's Nexus record, for the suspects; undefined for a mod installed without one.
 */
export async function buildCrashReport(
    chosen: ChosenLog, rules: readonly CrashRule[], attributor: Attributor, nexusOf: (mod: string) => number | undefined
): Promise<CrashReport> {
    const { log } = chosen;
    const view = new CrashView(log);
    const diagnoses = rankFindings(view, rules).slice(0, MAX_DIAGNOSES).map(finding => ({
        title: finding.title,
        confidence: finding.confidence,
        explain: finding.explain,
        fix: finding.fix,
        evidence: finding.evidence.slice(0, MAX_EVIDENCE).map(describe),
        rule: finding.rule
    }));

    const suspects = await suspectMods(candidates(log), attributor, nexusOf);
    const owner = (plugin: string | undefined) => {
        if (!plugin) return undefined;
        const mod = attributor.plugin(plugin)?.mod;
        return mod && mod !== plugin ? `${plugin} (mod ${mod})` : plugin;
    };
    const records = log.records.slice(0, MAX_RECORDS).map(record => ({
        formId: record.formId,
        type: record.type,
        ...(record.name ? { name: record.name } : {}),
        ...(record.definedBy ? { definedBy: owner(record.definedBy) } : {}),
        ...(record.lastChangedBy ? { lastChangedBy: owner(record.lastChangedBy) } : {})
    }));
    const loading = log.models.find(model => model.inProgress);

    return {
        log: logFacts(chosen),
        crash: {
            ...(log.exception.name ? { exception: log.exception.name } : {}),
            ...(log.exception.module ? { at: `${log.exception.module}+${log.exception.offset}` } : {}),
            firstLine: log.firstLine,
            ...(log.sections.gameState?.length ? { gameState: log.sections.gameState.map(line => line.trim()) } : {}),
            ...(loading ? { loadingModel: loading.path } : {})
        },
        diagnoses,
        suspects,
        ...(records.length ? { records } : {}),
        logs: chosen.counts,
        nextStep: nextStep(chosen, diagnoses, suspects)
    };
}

/** The candidates the setup has, by the mod that gives the game each — one entry per mod, best first. */
async function suspectMods(found: Candidate[], attributor: Attributor, nexusOf: (mod: string) => number | undefined): Promise<Suspect[]> {
    const models = found.filter(candidate => candidate.kind === 'model').map(candidate => candidate.file);
    let modelOwners = new Map<string, Owner>();
    try {
        modelOwners = await attributor.files(models);
    } catch {
        // Archives that cannot be read leave their meshes unattributed; the rest of the answer stands.
    }
    const byMod = new Map<string, Suspect>();
    for (const candidate of found) {
        const owner = candidate.kind === 'dll' ? attributor.dll(candidate.file)
            : candidate.kind === 'plugin' ? attributor.plugin(candidate.file)
            : modelOwners.get(candidate.file);
        // Not in the setup — a system DLL — or the game's own files: nothing to disable or update.
        if (!owner || owner.mod === 'the game\'s Data folder') continue;
        const label = candidate.kind === 'dll' ? `${candidate.file} (${owner.mod === GAME_FOLDER ? 'DLL in the game folder' : 'SKSE plugin'})`
            : candidate.kind === 'plugin' ? `${candidate.file} (plugin)`
            : `${candidate.file}${owner.archive ? ` (in ${owner.archive})` : ' (loose)'}`;
        const known = byMod.get(owner.mod);
        if (known) {
            if (!known.files.includes(label)) known.files.push(label);
            for (const line of candidate.evidence.map(describe)) if (known.evidence.length < MAX_EVIDENCE && !known.evidence.includes(line)) known.evidence.push(line);
            continue;
        }
        if (byMod.size >= MAX_SUSPECTS) continue;
        const modId = owner.mod === GAME_FOLDER || owner.mod === 'overwrite' ? undefined : nexusOf(owner.mod);
        byMod.set(owner.mod, {
            mod: owner.mod,
            files: [label],
            evidence: candidate.evidence.slice(0, MAX_EVIDENCE).map(describe),
            ...(modId ? { nexus: { modId } } : {})
        });
    }
    return [...byMod.values()];
}

const PLACE_NAMES: Partial<Record<Place, string>> = {
    firstLine: 'first line', callStack: 'call stack', stackScan: 'stack scan', relevantObjects: 'relevant objects',
    modelRequests: 'model requests', modelPaths: 'model paths', sksePlugins: 'SKSE plugins', processInfo: 'process info',
    systemSpecs: 'system specs', missingMasters: 'missing masters', gameState: 'game state', accessViolation: 'access violation',
    threadContext: 'thread context', cppException: 'C++ exception'
};

function describe(item: Evidence): string {
    const text = item.text.length > 200 ? `${item.text.slice(0, 200)}…` : item.text;
    return `${PLACE_NAMES[item.place] ?? item.place}${item.place === 'firstLine' ? '' : ` ${item.index}`}: ${text}`;
}

function nextStep(chosen: ChosenLog, diagnoses: ReportedDiagnosis[], suspects: Suspect[]): string {
    const notes: string[] = [];
    if (chosen.thisSetup === false) {
        notes.push(`This log is from another setup's game (${path.basename(chosen.file)}): what it names may not be in this one.`);
    }
    if (!chosen.log.complete) notes.push('The log stops early: the game was gone before Crash Logger finished, so its load order and plugins are missing.');
    if (diagnoses.length === 0 && suspects.length === 0) {
        notes.push('No known crash matches and no mod stands out. Ask the user what they were doing when it crashed, and whether ' +
            'it happens again at the same place; read the call stack with `section: "callStack"`.');
    } else {
        notes.push('Tell the user the first diagnosis and the first suspect mod, and what each fix would do. Change nothing unless ' +
            'they ask: disableMod takes a suspect mod out, installMod installs a fixed or newer file' +
            (suspects.some(suspect => suspect.files.some(file => file.endsWith('(SKSE plugin)'))) ? ', checkGameVersion says whether an SKSE plugin fits the game' : '') + '.');
    }
    return notes.join(' ');
}
