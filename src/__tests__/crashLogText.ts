import { parseCrashLog } from '../backend/skyrim/crash/crashLog';
import { CrashView } from '../backend/skyrim/crash/crashRules';

/**
 * A Crash Logger SSE log for a test, written in 1.27's format with only the lines the test is about:
 * each section given is written under its heading, each line indented as Crash Logger indents it.
 * A rule's test builds the log it should fire on — and one it should not — from these.
 */
export interface LogParts {
    /** `1.7.104` unless given. */
    game?: string;
    /** `1.27.0` unless given. */
    logger?: string;
    /** The exception line; by default an access violation in SkyrimSE.exe. */
    firstLine?: string;
    /** Lines after the exception line, before the first heading: `Exception Flags: …`, a WARNING. */
    header?: string[];
    relevantObjects?: string[];
    modelRequests?: string[];
    modelPaths?: string[];
    processInfo?: string[];
    threadContext?: string[];
    systemSpecs?: string[];
    /** Frames as Crash Logger writes them; `frame()` writes one. */
    callStack?: string[];
    registers?: string[];
    stack?: string[];
    modules?: string[];
    /** `name.dll v1.2.3`, or a bare name. */
    sksePlugins?: string[];
    gameState?: string[];
    /** Plugin file names in load order: .esl files and `light` ones get FE indexes. */
    plugins?: string[];
    missingMasters?: string[];
}

export const DEFAULT_FIRST_LINE = 'Unhandled exception "EXCEPTION_ACCESS_VIOLATION" at 0x7FF6A0F21C44 SkyrimSE.exe+0021C44\tmov rax, [rcx] |  TESObjectREFR::Load3D_21C44)';

const HEADINGS: [keyof LogParts, string][] = [
    ['relevantObjects', 'POSSIBLE RELEVANT OBJECTS:'],
    ['modelRequests', 'RECENT MODEL REQUESTS (this thread, newest first):'],
    ['modelPaths', 'MODEL PATHS FOR RESOURCE IDS ON THIS THREAD\'S STACK (innermost first):'],
    ['processInfo', 'PROCESS INFO:'],
    ['threadContext', 'THREAD CONTEXT (HEURISTIC):'],
    ['systemSpecs', 'SYSTEM SPECS:'],
    ['callStack', 'CALL STACK ([P]robable / [S]tack scan):'],
    ['registers', 'REGISTERS:'],
    ['stack', 'STACK:'],
    ['modules', 'MODULES:'],
    ['sksePlugins', 'SKSE PLUGINS:'],
    ['gameState', 'GAME STATE:'],
    ['plugins', 'PLUGINS:'],
    ['missingMasters', 'MISSING MASTERS (FATAL: data load aborted before any plugin was loaded):']
];

export function crashLogText(parts: LogParts = {}): string {
    const out = [
        'CRASH TIME: 2026-10-30 12:00:00',
        `Skyrim SSE v${parts.game ?? '1.7.104'}`,
        `CrashLoggerSSE v${(parts.logger ?? '1.27.0').replace(/\./g, '-')}-0 Oct 25 2026 06:01:12`,
        '',
        parts.firstLine ?? DEFAULT_FIRST_LINE,
        ...(parts.header ?? []),
        ''
    ];
    for (const [key, heading] of HEADINGS) {
        const lines = parts[key] as string[] | undefined;
        if (!lines) continue;
        out.push(heading);
        if (key === 'plugins') {
            const light = lines.filter(name => /\.esl$/i.test(name));
            const regular = lines.filter(name => !/\.esl$/i.test(name));
            out.push(`\tLight: ${light.length}\tRegular: ${regular.length}\tTotal: ${lines.length}`);
            regular.forEach((name, i) => out.push(`\t[${i.toString(16).toUpperCase().padStart(2, ' ')}]${light.length ? '     ' : ' '}${name}`));
            light.forEach((name, i) => out.push(`\t[FE:${i.toString(16).toUpperCase().padStart(3, ' ')}] ${name}`));
        } else {
            out.push(...lines.map(line => line.startsWith('\t') ? line : `\t${line}`));
        }
        out.push('');
    }
    return out.join('\n');
}

export function crashView(parts: LogParts = {}): CrashView {
    return new CrashView(parseCrashLog(crashLogText(parts)));
}

/**
 * One call-stack frame: `frame(0, 'SkyrimSE.exe+0D6DDDA', 'mov rax, [rcx] |  FUN_x -> 72119+0x1DEA')`;
 * `scan` marks one the stack scan found.
 */
export function frame(index: number, at: string, detail = '', scan = false): string {
    return `[${String(index).padStart(2, ' ')}][${scan ? 'S' : 'P'}] 0x7FF6A0000000 ${at.padStart(40, ' ')}${detail ? `\t${detail}` : ''}`;
}

/** The first line of a crash at `at`, `SkyrimSE.exe+0D6DDDA` or `EngineFixes.dll+00F16A3`. */
export function firstLineAt(at: string, detail = 'mov rax, [rcx]', exception = 'EXCEPTION_ACCESS_VIOLATION'): string {
    return `Unhandled exception "${exception}" at 0x7FF6A1C3D2E0 ${at}\t${detail}`;
}

/** The base game's plugins, the start of every load order. */
export const BASE_PLUGINS = ['Skyrim.esm', 'Update.esm', 'Dawnguard.esm', 'HearthFires.esm', 'Dragonborn.esm'];
