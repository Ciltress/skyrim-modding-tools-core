/**
 * A Crash Logger SSE log, read into its sections (docs/research §4 of the workspace, and Crash Logger's
 * own CrashHandler.cpp): the header with the exception, then one section per heading in capitals at
 * the start of a line — POSSIBLE RELEVANT OBJECTS:, CALL STACK (…):, PLUGINS: and the rest. Logs from
 * 1.20 to 1.27 are read alike; a section a version does not write is absent, and a log the game cut
 * short keeps what it has (`complete` is false).
 *
 * Nothing here judges the crash: the rules (crashRules.ts) do, on what this reads.
 */

/** The sections a log has, by their place in it. `header` is everything before the first heading. */
export const SECTION_NAMES = [
    'header', 'accessViolation', 'cppException', 'relevantObjects', 'modelRequests', 'modelPaths', 'processInfo',
    'threadContext', 'systemSpecs', 'callStack', 'registers', 'stack', 'modules', 'sksePlugins', 'gameState',
    'plugins', 'missingMasters'
] as const;
export type SectionName = typeof SECTION_NAMES[number];

/** Each heading as Crash Logger writes it, from 1.20 on; the last three of callStack are older forms. */
const HEADINGS: readonly [RegExp, SectionName][] = [
    [/^ACCESS VIOLATION ANALYSIS:/, 'accessViolation'],
    [/^C\+\+ EXCEPTION:/, 'cppException'],
    [/^POSSIBLE RELEVANT OBJECTS:/, 'relevantObjects'],
    // 1.26; PR #53 on alandtse/CrashLoggerSSE may merge both model sections into MODEL LOADING (this thread):.
    [/^(RECENT MODEL REQUESTS|CRASHED WHILE LOADING A MODEL|MODEL LOADING)\b.*:\s*$/, 'modelRequests'],
    [/^MODEL PATHS FOR RESOURCE IDS\b.*:\s*$/, 'modelPaths'],
    [/^PROCESS INFO:/, 'processInfo'],
    [/^THREAD CONTEXT\b.*:/, 'threadContext'],
    [/^(SYSTEM|VR) SPECS:/, 'systemSpecs'],
    [/^CALL STACK\b.*:\s*$/, 'callStack'],
    [/^(PROBABLE CALL STACK|RECONSTRUCTED CALL STACK\b.*|RAW CALL STACK):\s*$/, 'callStack'],
    [/^REGISTERS:/, 'registers'],
    [/^STACK:/, 'stack'],
    [/^MODULES:/, 'modules'],
    [/^SKSE PLUGINS:/, 'sksePlugins'],
    [/^GAME STATE:/, 'gameState'],
    [/^PLUGINS:/, 'plugins'],
    [/^MISSING MASTERS\b.*:\s*$/, 'missingMasters']
];

/** One frame of the call stack: `[ 3][P] 0x7FF6… SkyrimSE.exe+10EB3DF	mov [r15], al |  FUN_…`. */
export interface Frame {
    index: number;
    /** False for a frame the stack scan found ([S]), which may be stale; frames of logs before 1.23 are all probable. */
    probable: boolean;
    module: string;
    offset: string;
    /** The rest of the line: instruction, symbol, source file, Address Library ID. */
    detail: string;
    /** The line as written, without the leading tab. */
    line: string;
}

/** A record the log names: `(TESObjectCELL*) [0x2008A848] ("FWMF - Ultimate Patch.esp")`. */
export interface NamedRecord {
    formId: string;
    type: string;
    name?: string;
    /** The plugin that changed it last, as the log names it; absent for a form made at runtime. */
    lastChangedBy?: string;
    /** The plugin its FormID's load-order index stands for, by the log's own PLUGINS list. */
    definedBy?: string;
}

export interface LoadOrderEntry {
    /** As a FormID begins: `00`, `20`, `FE:9E9`. */
    index: string;
    name: string;
}

export interface CrashLog {
    /** `2026-10-06 22:01:43`, as written. */
    crashTime?: string;
    /** `1.7.104`. */
    gameVersion?: string;
    /** `1.27.0`. */
    loggerVersion?: string;
    /** The line naming the exception: `Unhandled exception "EXCEPTION_ACCESS_VIOLATION" at 0x… SkyrimSE.exe+0FA9FF0	mov …`. */
    firstLine: string;
    exception: {
        name?: string;
        /** The module it happened in, `SkyrimSE.exe` or a DLL; absent when the address is in none. */
        module?: string;
        offset?: string;
        /** What follows the address: the instruction and the symbol, when Crash Logger found them. */
        detail?: string;
    };
    /** Each section's lines as written, without its heading and without trailing empty lines. */
    sections: Partial<Record<SectionName, string[]>>;
    /** Each section's heading line, which may carry text of its own. */
    headings: Partial<Record<SectionName, string>>;
    frames: Frame[];
    plugins: LoadOrderEntry[];
    sksePlugins: { name: string; version?: string }[];
    modules: string[];
    /** PROCESS INFO's lines, by their key: `Working Directory`, `Command Line`… */
    processInfo: Record<string, string>;
    records: NamedRecord[];
    /** Model files the crashing thread asked for, newest first, then those named on its stack (1.26 and 1.27). */
    models: { path: string; section: 'modelRequests' | 'modelPaths'; inProgress?: true }[];
    memory: { physicalUsedGb?: number; physicalTotalGb?: number; gpuUsedGb?: number; gpuTotalGb?: number };
    /** False when the log stops before its PLUGINS section — the game was gone before Crash Logger finished. */
    complete: boolean;
}

export function parseCrashLog(text: string): CrashLog {
    const lines = text.replace(/^﻿/, '').split(/\r?\n/).map(line => line.replace(/\s+$/, ''));
    const sections: Partial<Record<SectionName, string[]>> = {};
    const headings: Partial<Record<SectionName, string>> = {};
    let current: SectionName = 'header';
    sections.header = [];
    for (const line of lines) {
        const heading = /^[A-Z]/.test(line) ? HEADINGS.find(([pattern]) => pattern.test(line)) : undefined;
        if (heading) {
            current = heading[1];
            // A section written twice — older logs' probable and reconstructed call stacks — is one.
            sections[current] ??= [];
            headings[current] ??= line;
            continue;
        }
        sections[current]!.push(line);
    }
    for (const name of Object.keys(sections) as SectionName[]) {
        const body = sections[name]!;
        while (body.length > 0 && body[body.length - 1] === '') body.pop();
        while (body.length > 0 && body[0] === '' && name !== 'header') body.shift();
    }

    const header = sections.header ?? [];
    const firstLine = header.find(line => /^Unhandled (native )?exception/i.test(line)) ?? '';
    const plugins = parsePlugins(sections.plugins ?? []);
    return {
        ...headerFacts(header),
        firstLine,
        exception: parseException(firstLine),
        sections,
        headings,
        frames: (sections.callStack ?? []).map(parseFrame).filter((frame): frame is Frame => frame !== undefined),
        plugins,
        sksePlugins: (sections.sksePlugins ?? []).map(parseSksePlugin).filter(plugin => plugin !== undefined),
        modules: (sections.modules ?? []).map(line => /^\s*(\S.*?)\s+0x[0-9A-F]+\s*$/i.exec(line)?.[1] ?? line.trim()).filter(Boolean),
        processInfo: keyValues(sections.processInfo ?? []),
        records: parseRecords(sections.relevantObjects ?? [], plugins),
        models: [
            ...(sections.modelRequests ?? []).map(line => /^\s*[\d.]+\s*ms ago\s+(.+?)(\s+\(in progress\))?$/.exec(line))
                .filter(match => match !== null)
                .map(match => ({ path: match[1], section: 'modelRequests' as const, ...(match[2] ? { inProgress: true as const } : {}) })),
            ...(sections.modelPaths ?? []).map(line => /^\s*\[RSP\+0x[0-9A-F]+\]\s+(.+)$/i.exec(line))
                .filter(match => match !== null)
                .map(match => ({ path: match[1], section: 'modelPaths' as const }))
        ],
        memory: memoryFacts(sections.systemSpecs ?? []),
        complete: sections.plugins !== undefined || sections.missingMasters !== undefined
    };
}

function headerFacts(header: readonly string[]): Pick<CrashLog, 'crashTime' | 'gameVersion' | 'loggerVersion'> {
    const crashTime = header.map(line => /^CRASH TIME:\s*(.+)$/.exec(line)?.[1]).find(Boolean);
    const gameVersion = header.map(line => /^Skyrim (?:SSE|VR) v(\d+\.\d+\.\d+)/.exec(line)?.[1]).find(Boolean);
    // `CrashLoggerSSE v1-23-1-0 Jun  7 2026 10:09:53`: CommonLib writes a version's parts with dashes.
    const logger = header.map(line => /^CrashLogger(?:SSE|VR)? v(\d+)[-.](\d+)[-.](\d+)/.exec(line)).find(Boolean);
    return {
        ...(crashTime ? { crashTime } : {}),
        ...(gameVersion ? { gameVersion } : {}),
        ...(logger ? { loggerVersion: `${Number(logger[1])}.${Number(logger[2])}.${Number(logger[3])}` } : {})
    };
}

function parseException(line: string): CrashLog['exception'] {
    const match = /^Unhandled (?:native )?exception(?: "([^"]+)")? at 0x[0-9A-F]+(?:\s+(.+?)\+([0-9A-F]+))?(?:\s+(.*))?$/i.exec(line);
    if (!match) return {};
    return {
        ...(match[1] ? { name: match[1] } : {}),
        ...(match[2] ? { module: match[2].trim(), offset: match[3] } : {}),
        ...(match[4] ? { detail: match[4].trim() } : {})
    };
}

function parseFrame(line: string): Frame | undefined {
    const match = /^\s*\[\s*(\d+)\](?:\[([PS])\])?\s+0x[0-9A-F]+\s+(.+?)\+([0-9A-F]+)(?:\s+(.*))?$/i.exec(line);
    if (!match) return undefined;
    return {
        index: Number(match[1]),
        probable: match[2]?.toUpperCase() !== 'S',
        module: match[3].trim(),
        offset: match[4],
        detail: (match[5] ?? '').trim(),
        line: line.trim()
    };
}

/** `[ 0]     Skyrim.esm`, `[FE:  1] ccbgssse003-zombies.esl`: indexes padded with spaces, read as `00` and `FE:001`. */
function parsePlugins(lines: readonly string[]): LoadOrderEntry[] {
    return lines.map(line => /^\s*\[\s*(?:(FE):\s*([0-9A-F]{1,3})|([0-9A-F]{1,2}))\]\s+(.+)$/i.exec(line))
        .filter(match => match !== null)
        .map(match => ({
            index: match[1] ? `FE:${match[2].toUpperCase().padStart(3, '0')}` : match[3].toUpperCase().padStart(2, '0'),
            name: match[4].trim()
        }));
}

function parseSksePlugin(line: string): { name: string; version?: string } | undefined {
    const match = /^\s*(\S.*?\.dll)(?:\s+v(\S+))?\s*$/i.exec(line);
    if (!match) return undefined;
    return { name: match[1], ...(match[2] ? { version: match[2] } : {}) };
}

function keyValues(lines: readonly string[]): Record<string, string> {
    const values: Record<string, string> = {};
    for (const line of lines) {
        const match = /^\s+([^:]+?):\s*(.*)$/.exec(line);
        if (match) values[match[1]] = match[2];
    }
    return values;
}

/**
 * Records in POSSIBLE RELEVANT OBJECTS: `RSP+190: (TESWorldSpace*) "Hjorkvild Isles" [0x20005900] ("FWMF - Ultimate Patch.esp")`.
 * The quoted plugin is the one that changed it last; the FormID's top byte — or FE plus three digits for
 * a light plugin — is the load-order index of the one that defines it. FF is a form made while playing.
 */
function parseRecords(lines: readonly string[], plugins: readonly LoadOrderEntry[]): NamedRecord[] {
    const byIndex = new Map(plugins.map(plugin => [plugin.index, plugin.name]));
    const seen = new Set<string>();
    const records: NamedRecord[] = [];
    for (const line of lines) {
        const match = /^\s*[^:]+:\s+\(([^)]+?)\*?\)(?:\s+"([^"]*)")?\s+\[0x([0-9A-F]{8})\](?:\s+\("([^"]+)"\))?/i.exec(line);
        if (!match) continue;
        const formId = match[3].toUpperCase();
        if (seen.has(formId)) continue;
        seen.add(formId);
        const index = formId.startsWith('FE') ? `FE:${formId.slice(2, 5)}` : formId.slice(0, 2);
        const definedBy = formId.startsWith('FF') ? undefined : byIndex.get(index);
        records.push({
            formId: `0x${formId}`,
            type: match[1],
            ...(match[2] ? { name: match[2] } : {}),
            ...(match[4] ? { lastChangedBy: match[4] } : {}),
            ...(definedBy ? { definedBy } : {})
        });
    }
    return records;
}

function memoryFacts(lines: readonly string[]): CrashLog['memory'] {
    const specs = keyValues(lines);
    const pair = (value: string | undefined) => {
        const match = /([\d.]+)\s*(?:GB)?\s*\/\s*([\d.]+)/.exec(value ?? '');
        return match ? [Number(match[1]), Number(match[2])] as const : undefined;
    };
    const physical = pair(specs['PHYSICAL MEMORY']);
    const gpu = pair(specs['GPU MEMORY']);
    return {
        ...(physical ? { physicalUsedGb: physical[0], physicalTotalGb: physical[1] } : {}),
        ...(gpu ? { gpuUsedGb: gpu[0], gpuTotalGb: gpu[1] } : {})
    };
}
