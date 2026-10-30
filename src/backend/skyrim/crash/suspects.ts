import type { CrashLog } from './crashLog.js';
import { placeRank, type Evidence, type Place } from './crashRules.js';

/**
 * The files a crash log points at — DLLs in the first line and the call stack, plugins of the records it
 * names, meshes and textures it was loading — before attribution says whose they are. Ranked as the
 * diagnoses are, by where their evidence is: the first line, the probable call stack, the relevant
 * objects, the stack scan. Files that are in nearly every crash are ranked last, as Phostwood's analyzer
 * treats them.
 */
export interface Candidate {
    file: string;
    kind: 'dll' | 'plugin' | 'model';
    evidence: Evidence[];
    /** In nearly every crash log, whatever crashed: ranked after every other. */
    unlikely?: true;
    /** The model the game was loading when it crashed: ranked right after the first line. */
    loading?: true;
}

/** The game's own masters: in every log, never the suspect (Phostwood's neverDisplay). */
const BASE_MASTERS = new Set(['skyrim.esm', 'update.esm', 'dawnguard.esm', 'hearthfires.esm', 'dragonborn.esm']);

/** The game, Crash Logger and SKSE themselves: in every log of a game with mods. */
const NOT_SUSPECTS = new Set(['skyrimse.exe', 'skyrimvr.exe', 'crashlogger.dll']);

/** Phostwood's unlikelyCulprits: system DLLs a crash passes through on its way out, whatever caused it. */
const UNLIKELY = new Set([
    'clr.dll', 'd3d12core.dll', 'd3dcompiler_47.dll', 'kernel32.dll', 'kernelbase.dll', 'msvcp140.dll', 'ntdll.dll',
    'runtime.dll', 'steamclient64.dll', 'system.ni.dll', 'ucrtbase.dll', 'uiautomationcore.dll', 'win32u.dll', 'xinput1_3.dll'
]);

const MODEL = /((?:meshes|textures)[\\/][^"\t|<>:*?]+?\.(?:nif|dds|tri|hkx|btr|bto))\b/i;

export function candidates(log: CrashLog): Candidate[] {
    const found = new Map<string, Candidate>();
    // A model is named as each part of the game spells it — `meshes\interface/logo/logo.nif`, `MESHES\interface\…`: one file.
    const keyOf = (kind: Candidate['kind'], file: string) => `${kind}:${kind === 'model' ? modelKey(file) : file.toLowerCase()}`;
    const add = (file: string, kind: Candidate['kind'], place: Place, index: number, text: string) => {
        const key = keyOf(kind, file);
        if (kind === 'model') file = file.replace(/\//g, '\\');
        const known = found.get(key);
        const item = { place, index, text };
        if (known) {
            if (!known.evidence.some(e => e.place === place && e.index === index)) known.evidence.push(item);
            return;
        }
        found.set(key, { file, kind, evidence: [item], ...(UNLIKELY.has(file.toLowerCase()) ? { unlikely: true as const } : {}) });
    };

    const isDll = (module: string) => /\.dll$/i.test(module) && !NOT_SUSPECTS.has(module.toLowerCase()) && !/^skse64_\d+_\d+_\d+\.dll$/i.test(module);
    if (log.exception.module && isDll(log.exception.module)) add(log.exception.module, 'dll', 'firstLine', 0, log.firstLine);
    for (const frame of log.frames) {
        if (isDll(frame.module)) add(frame.module, 'dll', frame.probable ? 'callStack' : 'stackScan', frame.index, frame.line);
    }

    const objects = log.sections.relevantObjects ?? [];
    objects.forEach((line, index) => {
        for (const record of log.records) {
            if (!line.includes(`[${record.formId}]`)) continue;
            for (const plugin of [record.lastChangedBy, record.definedBy]) {
                if (plugin && !BASE_MASTERS.has(plugin.toLowerCase())) add(plugin, 'plugin', 'relevantObjects', index, line.trim());
            }
        }
    });

    const models = log.models.map((model, index) => ({ ...model, index }));
    for (const model of models) {
        add(model.path, 'model', model.section, model.index, model.path);
        if (model.inProgress) found.get(keyOf('model', model.path))!.loading = true;
    }
    for (const [place, lines] of [['relevantObjects', objects], ['stack', (log.sections.stack ?? []).slice(0, 100)]] as const) {
        lines.forEach((line, index) => {
            const match = MODEL.exec(line);
            if (match) add(match[1], 'model', place, index, line.trim());
        });
    }

    return [...found.values()].sort(compareCandidates);
}

/** A model's path as one key, whatever its case, separators, or a leading `data\`. */
function modelKey(file: string): string {
    return file.replace(/\//g, '\\').replace(/^\\+/, '').replace(/^data\\/i, '').toLowerCase();
}

function compareCandidates(a: Candidate, b: Candidate): number {
    if (!!a.unlikely !== !!b.unlikely) return a.unlikely ? 1 : -1;
    const [ra, rb] = [best(a), best(b)];
    return ra[0] - rb[0] || ra[1] - rb[1];
}

function best(candidate: Candidate): [number, number] {
    if (candidate.loading) return [0.5, 0];
    return candidate.evidence
        .map(item => [placeRank(item.place), item.index] as [number, number])
        .sort((x, y) => x[0] - y[0] || x[1] - y[1])[0];
}
