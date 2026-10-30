import fs from 'node:fs';
import path from 'node:path';
import { parseCrashLog } from '../backend/skyrim/crash/crashLog';

/**
 * Crash Logger SSE's logs read into sections: ten real ones from a Wabbajack list (Crash Logger 1.23.1,
 * game 1.6.1170; scrubbed by scripts/scrub-crash-log.mjs), one of them cut short, and one written by
 * hand in 1.27's format from its source — the sections 1.26 and 1.27 added.
 */

const FIXTURES = path.join(__dirname, 'fixtures', 'crash-logs');
const read = (name: string) => parseCrashLog(fs.readFileSync(path.join(FIXTURES, name), 'utf8'));

describe('parseCrashLog on a 1.23 log', () => {
    const log = read('crash-2026-09-29-18-07-21.log');

    it('reads the header: when, which game, which Crash Logger, and the exception', () => {
        expect(log.crashTime).toBe('2026-09-29 18:07:21');
        expect(log.gameVersion).toBe('1.6.1170');
        expect(log.loggerVersion).toBe('1.23.1');
        expect(log.firstLine).toMatch(/^Unhandled exception "EXCEPTION_ACCESS_VIOLATION" at 0x7FF7DE578A95 SkyrimSE\.exe\+02B8A95/);
        expect(log.exception).toMatchObject({ name: 'EXCEPTION_ACCESS_VIOLATION', module: 'SkyrimSE.exe', offset: '02B8A95' });
        expect(log.exception.detail).toContain('TESObjectCELL::sub_1402651F0_2B8A95');
    });

    it('splits the sections by their headings', () => {
        for (const name of ['accessViolation', 'relevantObjects', 'processInfo', 'threadContext', 'systemSpecs', 'callStack',
            'registers', 'stack', 'modules', 'sksePlugins', 'plugins'] as const) {
            expect(log.sections[name]?.length, name).toBeGreaterThan(0);
        }
        expect(log.headings.callStack).toBe('CALL STACK ([P]robable / [S]tack scan):');
        expect(log.complete).toBe(true);
    });

    it('reads the call stack, probable frames apart from the stack scan', () => {
        expect(log.frames[0]).toMatchObject({ index: 0, probable: true, module: 'SkyrimSE.exe', offset: '02B8A95' });
        expect(log.frames.some(frame => !frame.probable)).toBe(true);
        expect(log.frames.every((frame, i) => frame.index === i)).toBe(true);
    });

    it('reads the load order, light plugins by their FE index', () => {
        expect(log.plugins[0]).toEqual({ index: '00', name: 'Skyrim.esm' });
        expect(log.plugins).toContainEqual({ index: 'FE:001', name: 'ccbgssse003-zombies.esl' });
        expect(log.plugins).toContainEqual({ index: '20', name: 'Siege at Icemoth.esp' });
        expect(log.plugins).toContainEqual({ index: 'FE:9E9', name: 'Big Tweaks.esp' });
    });

    it('names each record\'s defining plugin by its FormID, and the one that changed it last', () => {
        expect(log.records).toContainEqual({
            formId: '0x20005900', type: 'TESWorldSpace', name: 'Hjorkvild Isles',
            lastChangedBy: 'FWMF - Ultimate Patch.esp', definedBy: 'Siege at Icemoth.esp'
        });
        expect(log.records).toContainEqual({
            formId: '0x2008A848', type: 'TESObjectCELL', lastChangedBy: 'Siege at Icemoth.esp', definedBy: 'Siege at Icemoth.esp'
        });
    });

    it('reads SKSE plugins with their versions, modules and process info', () => {
        expect(log.sksePlugins.length).toBeGreaterThan(50);
        expect(log.sksePlugins.find(plugin => plugin.name === 'CrashLogger.dll')?.version).toBeDefined();
        expect(log.modules).toContain('SkyrimSE.exe');
        expect(log.processInfo['Working Directory']).toBe('G:\\LoreRim\\Stock Game');
        expect(log.memory.physicalTotalGb).toBeCloseTo(31.68);
    });

    it('reads a log with Windows line ends the same', () => {
        const text = fs.readFileSync(path.join(FIXTURES, 'crash-2026-09-29-18-07-21.log'), 'utf8');
        const crlf = parseCrashLog(text.replace(/\n/g, '\r\n'));
        expect(crlf.frames).toEqual(log.frames);
        expect(crlf.records).toEqual(log.records);
    });
});

describe('parseCrashLog on other logs', () => {
    it('reads every real log, each with an exception and a call stack', () => {
        const names = fs.readdirSync(FIXTURES).filter(name => name.startsWith('crash-'));
        expect(names.length).toBe(12);
        for (const name of names) {
            const log = read(name);
            expect(log.firstLine, name).toMatch(/^Unhandled exception/);
            if (log.complete) expect(log.frames.length, name).toBeGreaterThan(0);
        }
    });

    it('keeps what a log cut short has', () => {
        const log = read('crash-2026-09-18-11-24-30.log');
        expect(log.complete).toBe(false);
        expect(log.exception).toMatchObject({ module: 'dxgi.dll', offset: '0112039' });
        expect(log.sections.accessViolation?.[0]).toBe('\tInstruction: mov rax, [rcx]');
        expect(log.frames).toEqual([]);
    });

    it('reads an exception in no module', () => {
        const log = read('crash-2026-09-15-08-52-15.log');
        expect(log.exception).toEqual({ name: 'EXCEPTION_ACCESS_VIOLATION' });
    });

    it('reads 1.27\'s sections: model requests, model paths, game state', () => {
        const log = read('synthetic-1.27-mesh.log');
        expect(log.loggerVersion).toBe('1.27.0');
        expect(log.gameVersion).toBe('1.7.104');
        expect(log.models).toEqual([
            { path: 'meshes\\CrashTest\\CrashTestStatue.nif', section: 'modelRequests', inProgress: true },
            { path: 'meshes\\clutter\\common\\basket01.nif', section: 'modelRequests' },
            { path: 'meshes\\CrashTest\\CrashTestStatue.nif', section: 'modelPaths' }
        ]);
        expect(log.headings.modelRequests).toBe('CRASHED WHILE LOADING A MODEL (this thread, newest first):');
        expect(log.sections.gameState).toContain('\tOpen menus: HUD Menu');
        expect(log.records).toContainEqual({
            formId: '0x08000800', type: 'TESObjectSTAT', name: 'Crash Test Statue', lastChangedBy: 'CrashTestMesh.esp', definedBy: 'CrashTestMesh.esp'
        });
        expect(log.plugins).toContainEqual({ index: 'FE:002', name: '_ResourcePack.esl' });
    });

    it('reads 1.26\'s missing masters', () => {
        const log = parseCrashLog([
            'CRASH TIME: 2026-10-30 10:00:00', 'Skyrim SSE v1.7.104', 'CrashLoggerSSE v1-26-0-0 Oct 24 2026 09:00:00', '',
            'Unhandled exception "EXCEPTION_BREAKPOINT" at 0x7FF6A0F21C44 SkyrimSE.exe+0021C44', '',
            'PLUGINS:', '\tLight: 0\tRegular: 1\tTotal: 1', '\t[ 0] Skyrim.esm', '',
            'MISSING MASTERS (FATAL: data load aborted before any plugin was loaded):',
            '\tMyPatch.esp -> NotInstalled.esm',
            '\tFix: install the missing file(s), or disable the plugin(s) that depend on them:',
            '\t\tmissing: NotInstalled.esm', '\t\tdisable: MyPatch.esp (enabled in Plugins.txt)'
        ].join('\n'));
        expect(log.sections.missingMasters).toContain('\t\tmissing: NotInstalled.esm');
        expect(log.complete).toBe(true);
    });
});
