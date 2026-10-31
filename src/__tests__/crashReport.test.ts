import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Attributor, belongsTo, dataPath } from '../backend/skyrim/crash/attribution';
import { CrashLogNotFoundError, chooseCrashLog, gameFolderOf, listCrashLogs } from '../backend/skyrim/crash/crashLogFiles';
import { parseCrashLog } from '../backend/skyrim/crash/crashLog';
import { buildCrashReport, rankFindings } from '../backend/skyrim/crash/crashReport';
import { CrashView, type CrashRule } from '../backend/skyrim/crash/crashRules';
import { candidates } from '../backend/skyrim/crash/suspects';
import { SkyrimEngine } from '../backend/skyrim/engine';
import type { HelperTransport } from '../backend/skyrim/helperClient';
import { Mo2Manager } from '../backend/skyrim/mo2Manager';
import { currentPlatform } from '../backend/skyrim/platform';
import { SkyrimInstanceBackend } from '../backend/skyrim/SkyrimInstanceBackend';
import { crashLogText, frame } from './crashLogText';

/**
 * readCrashLog end to end against a Mod Organizer instance laid out in a temporary folder: which log it
 * reads from a folder every setup shares, which mod gives the game each file the log names — loose, or
 * in an archive — and how diagnoses and suspects are ranked. The plugin helper is a scripted stand-in.
 */

const FIXTURES = path.join(__dirname, 'fixtures', 'crash-logs');
const roots: string[] = [];
afterAll(() => { for (const root of roots) fs.rmSync(root, { recursive: true, force: true }); });

function write(file: string, text = ''): void {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text);
}

const qt = (value: string) => value.replace(/\\/g, '\\\\');
const platform = currentPlatform();

/**
 * An instance whose game holds the base masters and their archives; the mod CrashTestMesh has a plugin and
 * an archive with the statue's mesh, Overrider a loose texture, and CrashTestDll an SKSE plugin.
 */
function makeInstance() {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'skyrim-crash-test-'));
    roots.push(root);
    const game = path.join(root, 'game');
    const instanceDir = path.join(root, 'mo2');
    const logs = path.join(root, 'SKSE');
    write(path.join(game, 'SkyrimSE.exe'));
    write(path.join(game, 'd3d11.dll'));
    for (const file of ['Skyrim.esm', 'Update.esm', 'Skyrim - Meshes0.bsa', 'Skyrim - Textures0.bsa']) write(path.join(game, 'Data', file));
    write(path.join(instanceDir, 'ModOrganizer.ini'), [
        '[General]', 'gameName=Skyrim Special Edition', `gamePath=@ByteArray(${qt(game)})`, 'selected_profile=@ByteArray(Default)', 'game_edition=Steam', ''
    ].join('\r\n'));
    const profile = path.join(instanceDir, 'profiles', 'Default');
    write(path.join(profile, 'modlist.txt'), '+Overrider\r\n+CrashTestMesh\r\n+CrashTestDll\r\n-Sleeping\r\n');
    write(path.join(profile, 'loadorder.txt'), 'Skyrim.esm\r\nUpdate.esm\r\nCrashTestMesh.esp\r\n');
    write(path.join(profile, 'plugins.txt'), '*CrashTestMesh.esp\r\n');
    write(path.join(instanceDir, 'mods', 'CrashTestMesh', 'CrashTestMesh.esp'));
    write(path.join(instanceDir, 'mods', 'CrashTestMesh', 'CrashTestMesh.bsa'));
    write(path.join(instanceDir, 'mods', 'CrashTestMesh', 'meta.ini'), '[General]\r\nmodid=4242\r\nversion=1.0\r\n');
    write(path.join(instanceDir, 'mods', 'Overrider', 'textures', 'CrashTest', 'statue.dds'));
    write(path.join(instanceDir, 'mods', 'CrashTestDll', 'SKSE', 'Plugins', 'CrashTestDll.dll'));
    write(path.join(instanceDir, 'mods', 'Sleeping', 'SKSE', 'Plugins', 'Sleeping.dll'));
    fs.mkdirSync(path.join(instanceDir, 'overwrite'), { recursive: true });
    fs.mkdirSync(logs, { recursive: true });
    return { root, game, instanceDir, logs, manager: new Mo2Manager(instanceDir, platform) };
}

/** What the helper's bsaFiles answers: the statue's mesh is in CrashTestMesh.bsa, a basket in the game's. */
function bsaStandIn(calls: { archives: string[]; files: string[] }[]) {
    return async (request: { archives: string[]; files: string[] }) => {
        calls.push(request);
        const found: Record<string, string> = {};
        for (const file of request.files) {
            const archive = /crashtest/i.test(file) ? request.archives.find(a => a.endsWith('CrashTestMesh.bsa'))
                : request.archives.find(a => a.endsWith('Skyrim - Meshes0.bsa'));
            if (archive) found[file] = archive;
        }
        return { found };
    };
}

/** The synthetic 1.27 mesh crash, as this instance's game wrote it. */
function meshCrash(game: string): string {
    return fs.readFileSync(path.join(FIXTURES, 'synthetic-1.27-mesh.log'), 'utf8')
        .replace(/G:\\Steam\\steamapps\\common\\Skyrim Special Edition/g, () => asGameSees(game));
}

/** A folder as the game names it in a log: on Windows as it is; under Wine on Z:, the root. */
const asGameSees = (native: string) => /^[a-z]:/i.test(native) ? native : `Z:${native.replace(/\//g, '\\')}`;

describe('choosing a crash log', () => {
    it('reads the newest of this setup\'s, and counts the other setups\' logs beside it', () => {
        const fixture = makeInstance();
        for (const name of ['crash-2026-09-29-18-07-21.log', 'crash-2026-10-06-22-01-43.log', 'crash-2026-09-18-11-24-30.log']) {
            fs.copyFileSync(path.join(FIXTURES, name), path.join(fixture.logs, name));
        }
        write(path.join(fixture.logs, 'crash-2026-10-01-09-00-00.log'), meshCrash(fixture.game));
        write(path.join(fixture.logs, 'CrashLogger.log'), 'not a crash log');
        expect(listCrashLogs(fixture.logs).map(file => file.name)).toEqual([
            'crash-2026-10-06-22-01-43.log', 'crash-2026-10-01-09-00-00.log', 'crash-2026-09-29-18-07-21.log', 'crash-2026-09-18-11-24-30.log'
        ]);

        const isGame = (seen: string) => seen.toLowerCase() === asGameSees(fixture.game).toLowerCase();
        const chosen = chooseCrashLog(fixture.logs, undefined, isGame);
        expect(path.basename(chosen.file)).toBe('crash-2026-10-01-09-00-00.log');
        expect(chosen.thisSetup).toBe(true);
        expect(chosen.counts).toEqual({ thisSetup: 1, otherSetups: 2, unknown: 1 });

        // Asked for, another setup's log is read, and says so.
        expect(chooseCrashLog(fixture.logs, '2026-10-06', isGame)).toMatchObject({ thisSetup: false });
        expect(path.basename(chooseCrashLog(fixture.logs, 'crash-2026-09-29-18-07-21', isGame).file)).toBe('crash-2026-09-29-18-07-21.log');
        expect(path.basename(chooseCrashLog(fixture.logs, '2026-09-18 11:24', isGame).file)).toBe('crash-2026-09-18-11-24-30.log');
        expect(() => chooseCrashLog(fixture.logs, '2025-01-01', isGame)).toThrow(/No crash log .* named or dated '2025-01-01'/);
    });

    it('reads the newest log before a minute that has none, that day, and says so', () => {
        // Asked about a crash "just now", a model sent the time of asking: three of ten crash runs in eval round 90.
        const fixture = makeInstance();
        write(path.join(fixture.logs, 'crash-2026-10-01-09-00-00.log'), meshCrash(fixture.game));
        write(path.join(fixture.logs, 'crash-2026-10-01-07-30-00.log'), meshCrash(fixture.game));
        const isGame = (seen: string) => seen.toLowerCase() === asGameSees(fixture.game).toLowerCase();

        const chosen = chooseCrashLog(fixture.logs, '2026-10-01 12:42', isGame);
        expect(path.basename(chosen.file)).toBe('crash-2026-10-01-09-00-00.log');
        expect(chosen.instead).toBe('No crash log is dated \'2026-10-01 12:42\'; this is the newest before it that day, crash-2026-10-01-09-00-00.log.');
        expect(path.basename(chooseCrashLog(fixture.logs, '2026-10-01 08:00', isGame).file)).toBe('crash-2026-10-01-07-30-00.log');
        // Never one from after it, nor from another day, nor for a day alone.
        expect(() => chooseCrashLog(fixture.logs, '2026-10-01 07:00', isGame)).toThrow(/named or dated '2026-10-01 07:00'/);
        expect(() => chooseCrashLog(fixture.logs, '2026-10-02 12:00', isGame)).toThrow(CrashLogNotFoundError);
        expect(() => chooseCrashLog(fixture.logs, '2026-09-30', isGame)).toThrow(CrashLogNotFoundError);
        // The minute a log has is that log, with nothing to say.
        expect(chooseCrashLog(fixture.logs, '2026-10-01 09:00', isGame).instead).toBeUndefined();
    });

    it('reads a time as a chat\'s message stamp writes it: its brackets, weekday and time zone aside', () => {
        // A model sent its prompt's stamp, `Sat 2026-10-31 09:26`, and was refused (eval round 100's crash-mesh #3).
        const fixture = makeInstance();
        write(path.join(fixture.logs, 'crash-2026-10-01-09-00-00.log'), meshCrash(fixture.game));
        const isGame = (seen: string) => seen.toLowerCase() === asGameSees(fixture.game).toLowerCase();

        for (const stamp of ['Sat 2026-10-01 09:00', '[Sat 2026-10-01 09:00 GMT+1]', 'Saturday, 2026-10-01 09:00 UTC', '2026-10-01T09:00:00+01:00', '2026-10-01T09:00Z']) {
            const chosen = chooseCrashLog(fixture.logs, stamp, isGame);
            expect(path.basename(chosen.file), stamp).toBe('crash-2026-10-01-09-00-00.log');
            expect(chosen.instead, stamp).toBeUndefined();
        }
        expect(path.basename(chooseCrashLog(fixture.logs, '[Sat 2026-10-01 12:42 GMT+1]', isGame).file)).toBe('crash-2026-10-01-09-00-00.log');
        expect(() => chooseCrashLog(fixture.logs, 'Sat 2026-10-01 08:59', isGame)).toThrow(/named or dated 'Sat 2026-10-01 08:59'/);
    });

    it('says why when there is no log of this setup\'s, or none at all', () => {
        const fixture = makeInstance();
        expect(() => chooseCrashLog(fixture.logs, undefined, () => false)).toThrow(CrashLogNotFoundError);
        expect(() => chooseCrashLog(fixture.logs, undefined, () => false)).toThrow(/no crash log .*Crash Logger SSE is not installed/);
        fs.copyFileSync(path.join(FIXTURES, 'crash-2026-09-29-18-07-21.log'), path.join(fixture.logs, 'crash-2026-09-29-18-07-21.log'));
        expect(() => chooseCrashLog(fixture.logs, undefined, () => false)).toThrow(/None of the 1 crash logs .* is this setup's: 1 came from another game folder/);
    });

    it('takes the game folder from the executable on the command line, as the game saw it', () => {
        expect(gameFolderOf(parseCrashLog(fs.readFileSync(path.join(FIXTURES, 'crash-2026-09-29-18-07-21.log'), 'utf8')))).toBe('G:\\LoreRim\\Stock Game');
        expect(gameFolderOf(parseCrashLog(crashLogText({ processInfo: ['Working Directory: Z:\\home\\deck\\Skyrim'] })))).toBe('Z:\\home\\deck\\Skyrim');
        expect(gameFolderOf(parseCrashLog(crashLogText()))).toBeUndefined();
    });
});

describe('attribution', () => {
    it('names the mod behind a plugin, an SKSE plugin, a DLL in the game folder and a loose file', async () => {
        const fixture = makeInstance();
        const instance = fixture.manager.readInstance();
        const attributor = new Attributor(fixture.manager, instance, fixture.manager.readModList(instance), ['Skyrim.esm', 'Update.esm', 'CrashTestMesh.esp']);
        expect(attributor.plugin('crashtestmesh.esp')).toEqual({ mod: 'CrashTestMesh' });
        expect(attributor.dll('CrashTestDll.dll')).toEqual({ mod: 'CrashTestDll' });
        expect(attributor.dll('d3d11.dll')).toEqual({ mod: 'the game folder' });
        expect(attributor.dll('Sleeping.dll')).toBeUndefined();
        expect(attributor.dll('ntdll.dll')).toBeUndefined();
        const owners = await attributor.files(['textures\\CrashTest\\Statue.dds']);
        expect(owners.get('textures\\CrashTest\\Statue.dds')).toEqual({ mod: 'Overrider' });
    });

    it('looks for a file no mod has loose in the archives, the game\'s first, then each plugin\'s in load order', async () => {
        const fixture = makeInstance();
        const instance = fixture.manager.readInstance();
        const calls: { archives: string[]; files: string[] }[] = [];
        const attributor = new Attributor(fixture.manager, instance, fixture.manager.readModList(instance),
            ['Skyrim.esm', 'Update.esm', 'CrashTestMesh.esp'], bsaStandIn(calls));
        const owners = await attributor.files(['meshes\\CrashTest\\CrashTestStatue.nif', 'CrashTest\\Other.nif', 'textures\\CrashTest\\statue.dds']);
        expect(owners.get('meshes\\CrashTest\\CrashTestStatue.nif')).toEqual({ mod: 'CrashTestMesh', archive: 'CrashTestMesh.bsa' });
        // A model loader's path without its top folder is a mesh's.
        expect(owners.get('CrashTest\\Other.nif')).toEqual({ mod: 'CrashTestMesh', archive: 'CrashTestMesh.bsa' });
        expect(calls).toHaveLength(1);
        expect(calls[0].files).toEqual(['meshes\\CrashTest\\CrashTestStatue.nif', 'meshes\\CrashTest\\Other.nif']);
        expect(calls[0].archives.map(file => path.basename(file))).toEqual(['Skyrim - Meshes0.bsa', 'Skyrim - Textures0.bsa', 'CrashTestMesh.bsa']);
    });

    it('knows which archives load with a plugin, and where a path is under Data', () => {
        expect(belongsTo('CrashTestMesh.bsa', 'CrashTestMesh.esp')).toBe(true);
        expect(belongsTo('crashtestmesh - textures.bsa', 'CrashTestMesh.esp')).toBe(true);
        expect(belongsTo('CrashTestMeshes.bsa', 'CrashTestMesh.esp')).toBe(false);
        expect(dataPath('Data/Meshes/x.nif')).toBe('Meshes\\x.nif');
        expect(dataPath('Actors\\x.nif')).toBe('meshes\\Actors\\x.nif');
        expect(dataPath('textures\\x.dds')).toBe('textures\\x.dds');
    });
});

describe('suspects and ranking', () => {
    it('collects the DLLs, plugins and models a log points at, best evidence first, system DLLs last', () => {
        const log = parseCrashLog(crashLogText({
            firstLine: 'Unhandled exception "EXCEPTION_ACCESS_VIOLATION" at 0x7FFA5C69B361 CrashTestDll.dll+002B361\tmov rax, [rcx]',
            relevantObjects: ['RSP+58: (TESObjectSTAT*) "Crash Test Statue" [0x05000800] ("Patch.esp")'],
            callStack: [frame(0, 'CrashTestDll.dll+002B361'), frame(1, 'KERNELBASE.dll+0012345'), frame(2, 'SkyrimSE.exe+0021C44'), frame(3, 'Other.dll+0000100', '', true)],
            modelRequests: ['    0.412 ms ago  meshes\\CrashTest\\CrashTestStatue.nif  (in progress)'],
            plugins: ['Skyrim.esm', 'Update.esm', 'Dawnguard.esm', 'HearthFires.esm', 'Dragonborn.esm', 'CrashTestMesh.esp']
        }));
        const found = candidates(log);
        expect(found.map(candidate => `${candidate.kind}:${candidate.file}`)).toEqual([
            // The model the game was loading when it crashed comes right after the first line.
            'dll:CrashTestDll.dll', 'model:meshes\\CrashTest\\CrashTestStatue.nif', 'plugin:Patch.esp', 'plugin:CrashTestMesh.esp', 'dll:Other.dll', 'dll:KERNELBASE.dll'
        ]);
        expect(found[0].evidence.map(item => item.place)).toEqual(['firstLine', 'callStack']);
    });

    it('ranks findings by confidence, then by where their evidence is; a rule that throws is skipped', () => {
        const view = new CrashView(parseCrashLog(crashLogText({ relevantObjects: ['RSP+8: (Foo*)'], callStack: [frame(0, 'SkyrimSE.exe+0021C44')] })));
        const rule = (id: string, confidence: 'certain' | 'likely' | 'possible', needle: string, place: 'relevantObjects' | 'callStack' | 'firstLine'): CrashRule => ({
            id, ported: 'test', diagnose: v => ({ title: id, confidence, explain: '', fix: '', evidence: v.find(place, needle) })
        });
        const ranked = rankFindings(view, [
            rule('possible-first-line', 'possible', 'Unhandled', 'firstLine'),
            rule('likely-objects', 'likely', 'Foo', 'relevantObjects'),
            { id: 'broken', ported: 'test', diagnose: () => { throw new Error('a broken rule'); } },
            rule('likely-call-stack', 'likely', 'SkyrimSE', 'callStack'),
            rule('likely-nowhere', 'likely', 'nothing', 'callStack')
        ]);
        expect(ranked.map(found => found.rule)).toEqual(['likely-call-stack', 'likely-objects', 'likely-nowhere', 'possible-first-line']);
    });
});

describe('readCrashLog', () => {
    it('reads this setup\'s newest log: the crash, the suspects by mod, the records and their plugins\' mods', async () => {
        const fixture = makeInstance();
        fs.copyFileSync(path.join(FIXTURES, 'crash-2026-10-06-22-01-43.log'), path.join(fixture.logs, 'crash-2026-10-06-22-01-43.log'));
        write(path.join(fixture.logs, 'crash-2026-10-01-09-00-00.log'), meshCrash(fixture.game));
        const calls: { method: string; params: any }[] = [];
        let running = false;
        const helper: HelperTransport = {
            get running() { return running; },
            async start() { running = true; },
            stop() { running = false; },
            async call(method: string, params: any = {}): Promise<any> {
                calls.push({ method, params });
                if (method === 'hello') return { helper: 'test', protocol: 1 };
                if (method === 'gameVersion') return { version: '1.7.104.0' };
                if (method === 'setLoadOrder') return { plugins: params.plugins.length, missing: [] };
                if (method === 'bsaFiles') return bsaStandIn([])(params);
                throw new Error(`unexpected ${method}`);
            }
        };
        const engine = new SkyrimEngine(fixture.manager, helper, path.join(fixture.root, 'projects'), { platform, crashLogDir: fixture.logs });
        const backend = new SkyrimInstanceBackend({ engine });

        const report = await backend.readCrashLog({});
        if (!('crash' in report)) throw new Error('a section came back');
        expect(report.log).toMatchObject({ file: path.join(fixture.logs, 'crash-2026-10-01-09-00-00.log'), game: '1.7.104', crashLogger: '1.27.0', thisSetup: true });
        expect(report.crash).toMatchObject({ exception: 'EXCEPTION_ACCESS_VIOLATION', at: 'SkyrimSE.exe+0D3D2E0', loadingModel: 'meshes\\CrashTest\\CrashTestStatue.nif' });
        expect(report.crash.gameState).toContain('Open menus: HUD Menu');
        expect(report.suspects[0]).toEqual({
            mod: 'CrashTestMesh',
            files: ['meshes\\CrashTest\\CrashTestStatue.nif (in CrashTestMesh.bsa)', 'CrashTestMesh.esp (plugin)'],
            evidence: ['model requests 0: meshes\\CrashTest\\CrashTestStatue.nif', expect.any(String),
                expect.stringMatching(/^relevant objects 1: RSP\+58: \(TESObjectSTAT\*\) "Crash Test Statue"/)],
            nexus: { modId: 4242 }
        });
        expect(report.records).toContainEqual({
            formId: '0x08000800', type: 'TESObjectSTAT', name: 'Crash Test Statue',
            definedBy: 'CrashTestMesh.esp (mod CrashTestMesh)', lastChangedBy: 'CrashTestMesh.esp (mod CrashTestMesh)'
        });
        expect(report.logs).toEqual({ thisSetup: 1, otherSetups: 1, unknown: 0 });
        expect(report.diagnoses.length).toBeLessThanOrEqual(5);
        expect(report.nextStep).toMatch(/Change nothing unless they ask/);
        expect(calls.filter(call => call.method === 'bsaFiles')).toHaveLength(1);

        const section = await backend.readCrashLog({ section: 'modelPaths' });
        expect(section).toEqual({
            log: report.log, section: 'modelPaths', heading: 'MODEL PATHS FOR RESOURCE IDS ON THIS THREAD\'S STACK (innermost first):',
            lines: ['\t[RSP+0x48] meshes\\CrashTest\\CrashTestStatue.nif']
        });

        // A minute with no log reads the newest before it that day, and its nextStep says which, first.
        const before = await backend.readCrashLog({ log: '2026-10-01 12:42' });
        if (!('crash' in before)) throw new Error('a section came back');
        expect(before.log.file).toBe(report.log.file);
        expect(before.nextStep).toMatch(/^No crash log is dated '2026-10-01 12:42'; this is the newest before it that day, crash-2026-10-01-09-00-00\.log\. /);
    });
});
