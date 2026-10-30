import type { CrashLog, SectionName } from './crashLog.js';
import { GAME_ADDRESSES, type GameAddressCode } from './gameAddresses.js';

/**
 * What a crash rule sees and says. The rules are Phostwood's Skyrim Crash Log Analyzer's
 * (github.com/Phostwood/crash-analyzer, GPL-3.0; docs/adr/0011), each rewritten as one entry of
 * rules/*.ts with a test of its own, and kept to Crash Logger SSE logs.
 *
 * A rule reads the log through Phostwood's own views of it — the first line, the top quarter, third or
 * half, the bottom half — and what it finds comes back with the lines it found it on, so a diagnosis
 * carries its evidence and can be ranked by where that is.
 */

/**
 * Phostwood's three marks: 🎯 the cause, ❗ a likely one, ❓ worth a look. A rule that only gives
 * advice whatever the log says is not ported.
 */
export type Confidence = 'certain' | 'likely' | 'possible';

/** Where a line is. A frame of the call stack is `callStack` when probable, `stackScan` when the scan found it. */
export type Place = Exclude<SectionName, 'callStack'> | 'firstLine' | 'callStack' | 'stackScan';

/**
 * The order evidence ranks in, best first: the first line, the probable call stack, the relevant
 * objects, the stack scan (the user's ranking, 2026-10-30), then everything else.
 */
const PLACE_RANK: Partial<Record<Place, number>> = {
    firstLine: 0, callStack: 1, relevantObjects: 2, stackScan: 3, modelRequests: 4, modelPaths: 4, registers: 5, stack: 6
};
export const placeRank = (place: Place): number => PLACE_RANK[place] ?? 7;

export interface Evidence {
    place: Place;
    /** The line's place in its section, from 0. */
    index: number;
    /** The line, trimmed. */
    text: string;
}

export interface Finding {
    /** What happened, in a few words: "Video memory ran out". */
    title: string;
    confidence: Confidence;
    /** What it means, in a sentence or three. */
    explain: string;
    /** What to do, concretely; a mod by its Nexus name and number, "SSE Engine Fixes (Nexus 17230)". */
    fix: string;
    evidence: Evidence[];
}

export interface CrashRule {
    /** kebab-case, unique. */
    id: string;
    /** What it ports: `diagnoses.js analyzeMeshIssues`, or `analyzeLog.js` and the inline rule's title. */
    ported: string;
    diagnose(view: CrashView): Finding | readonly Finding[] | undefined;
}

/**
 * Phostwood's views of a log, as its analyzer builds them by default: a section's first lines only — 50
 * of the relevant objects, call stack and registers, 100 of the stack — joined into
 * - `topQuarter`: the first line, relevant objects, call stack;
 * - `topThird`: and the registers;
 * - `topHalf`: and the stack (also Phostwood's `highestConfidenceIndicators`);
 * - `bottomHalf`: modules, SKSE plugins, plugins;
 * - `beforeCallStack`: everything before the call stack — the exception, relevant objects, process info, system specs
 *   (Phostwood's `header`);
 * - `all`: the whole log.
 * Or one section by its name, cut the same way; `callStack` holds the stack scan's frames too, as Phostwood's does.
 */
export type View = 'firstLine' | 'topQuarter' | 'topThird' | 'topHalf' | 'bottomHalf' | 'beforeCallStack' | 'all' | SectionName;

export interface Line extends Evidence {
    lower: string;
}

export type Needle = string | RegExp;

const CUT: Partial<Record<SectionName, number>> = { relevantObjects: 50, callStack: 50, registers: 50, stack: 100 };
const BEFORE_CALL_STACK: readonly SectionName[] = [
    'header', 'accessViolation', 'cppException', 'relevantObjects', 'modelRequests', 'modelPaths', 'processInfo', 'threadContext', 'systemSpecs'
];
const VIEWS: Record<Exclude<View, SectionName | 'firstLine' | 'all'>, readonly (SectionName | 'firstLine')[]> = {
    topQuarter: ['firstLine', 'relevantObjects', 'callStack'],
    topThird: ['firstLine', 'relevantObjects', 'callStack', 'registers'],
    topHalf: ['firstLine', 'relevantObjects', 'callStack', 'registers', 'stack'],
    bottomHalf: ['modules', 'sksePlugins', 'plugins'],
    beforeCallStack: BEFORE_CALL_STACK
};

export class CrashView {
    private readonly cache = new Map<string, readonly Line[]>();

    constructor(readonly log: CrashLog) { }

    /** The log's game version, `1.7.104`; undefined when its header lacks it. */
    get gameVersion(): string | undefined {
        return this.log.gameVersion;
    }

    /** Anniversary Edition's runtime or later — 1.6 and 1.7 — rather than 1.5.97's. */
    get isAE(): boolean {
        const [major, minor] = (this.log.gameVersion ?? '').split('.').map(Number);
        return major > 1 || (major === 1 && minor >= 6);
    }

    lines(view: View): readonly Line[] {
        let lines = this.cache.get(view);
        if (!lines) {
            lines = this.build(view);
            this.cache.set(view, lines);
        }
        return lines;
    }

    /** The lines of the view that hold any of the needles: text in any case, a pattern as it is written. */
    find(view: View, ...needles: Needle[]): Evidence[] {
        const lowered = needles.map(needle => typeof needle === 'string' ? needle.toLowerCase() : needle);
        return this.lines(view)
            .filter(line => lowered.some(needle => typeof needle === 'string' ? line.lower.includes(needle) : needle.test(line.text)))
            .map(evidence);
    }

    has(view: View, ...needles: Needle[]): boolean {
        return this.find(view, ...needles).length > 0;
    }

    /**
     * The lines of the view at a game address Phostwood names by its 1.6.1170 offset (gameAddresses.ts):
     * that offset moved to this log's game version, or the address's Address Library ID and distance.
     */
    findAddress(view: View, code: GameAddressCode): Evidence[] {
        const address = GAME_ADDRESSES[code];
        const offset = this.log.gameVersion ? (address.offsets as Record<string, string>)[this.log.gameVersion] : undefined;
        const patterns = [
            new RegExp(`-> ${address.id}\\+0x${address.delta.toString(16)}\\b`, 'i'),
            ...(offset ? [new RegExp(`SkyrimSE\\.exe\\+0*${offset}\\b`, 'i')] : [])
        ];
        return this.find(view, ...patterns);
    }

    private build(view: View): Line[] {
        if (view === 'firstLine') return this.log.firstLine ? [line('firstLine', 0, this.log.firstLine)] : [];
        if (view === 'all') return [...BEFORE_CALL_STACK, 'callStack', 'registers', 'stack', 'modules', 'sksePlugins', 'gameState', 'plugins', 'missingMasters']
            .flatMap(section => this.section(section as SectionName, false));
        if (view in VIEWS) return VIEWS[view as keyof typeof VIEWS].flatMap(part => part === 'firstLine' ? this.build('firstLine') : this.section(part, true));
        return this.section(view as SectionName, true);
    }

    /** A section's lines, each where it is; the first line is the first line, wherever the header holds it. */
    private section(name: SectionName, cut: boolean): Line[] {
        const body = this.log.sections[name] ?? [];
        const kept = cut && CUT[name] ? body.slice(0, CUT[name]) : body;
        return kept.flatMap((text, index) => {
            const trimmed = text.trim();
            if (!trimmed) return [];
            if (name === 'header' && text === this.log.firstLine) return [line('firstLine', 0, trimmed)];
            if (name === 'callStack') return [line(/^\[\s*\d+\]\[S\]/i.test(trimmed) ? 'stackScan' : 'callStack', index, trimmed)];
            return [line(name, index, trimmed)];
        });
    }
}

function line(place: Place, index: number, text: string): Line {
    return { place, index, text, lower: text.toLowerCase() };
}

function evidence({ place, index, text }: Line): Evidence {
    return { place, index, text };
}

/** The best place a finding's evidence is at: its rank, then its line; no evidence ranks last. */
export function evidenceRank(found: Finding): [number, number] {
    let best: [number, number] = [Number.MAX_SAFE_INTEGER, Number.MAX_SAFE_INTEGER];
    for (const item of found.evidence) {
        const rank: [number, number] = [placeRank(item.place), item.index];
        if (rank[0] < best[0] || (rank[0] === best[0] && rank[1] < best[1])) best = rank;
    }
    return best;
}
