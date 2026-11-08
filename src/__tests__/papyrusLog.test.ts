import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { parsePapyrusLog } from '../backend/skyrim/papyrus/papyrusLog';
import { PapyrusLogNotFoundError, papyrusLogFile } from '../backend/skyrim/papyrus/papyrusLogFiles';
import { buildPapyrusReport } from '../backend/skyrim/papyrus/papyrusReport';
import { kindOf } from '../backend/skyrim/papyrus/papyrusMessages';
import { SkyrimEngine } from '../backend/skyrim/engine';
import type { HelperTransport } from '../backend/skyrim/helperClient';
import { Mo2Manager } from '../backend/skyrim/mo2Manager';
import { currentPlatform } from '../backend/skyrim/platform';
import { SkyrimInstanceBackend } from '../backend/skyrim/SkyrimInstanceBackend';

/**
 * The Papyrus log read: its grammar, what its messages mean, and the report — problems ranked, scripts by mod, records by
 * plugin, stack dumps as script lag, harmless messages counted. The fixture is made of lines quoted from real logs
 * (docs/research/papyrus-log-format.md), the game's own and mods' names kept.
 */

const FIXTURE = path.join(__dirname, 'fixtures', 'papyrus', 'Papyrus.0.log');
const text = () => fs.readFileSync(FIXTURE, 'utf8');
const roots: string[] = [];
afterAll(() => { for (const root of roots) fs.rmSync(root, { recursive: true, force: true }); });

describe('parsePapyrusLog', () => {
    it('reads the header, the session, each entry with its stack, and a stack dump', () => {
        const log = parsePapyrusLog(text());
        expect(log).toMatchObject({
            opened: '09/03/2022 - 10:20:49AM', platform: 'PC', closed: true, loads: 1, saves: 1,
            header: { updateBudgetMs: 1.2, extraTaskletMs: 1.2, loadScreenMs: 2000, memoryPage: { min: 128, max: 512, total: 524288 } }
        });
        const none = log.entries.find(entry => entry.message.startsWith('Cannot call Start()'))!;
        expect(none).toMatchObject({ severity: 'error', time: '09/03/2022 - 10:21:10AM' });
        expect(none.stack).toEqual([{ self: 'VDR_NPCUpdate (B8000813)', formId: 'B8000813', script: 'VDR_NPCUpdateScript', fn: 'OnUpdate', at: '"VDR_NPCUpdateScript.psc" Line 5' }]);
        const getForm = log.entries.find(entry => entry.message.startsWith('File "Frostfall.esp"'))!;
        expect(getForm.stack.map(frame => frame.script)).toEqual(['Game', '_wetquestscript']);
        expect(getForm.stack[0].self).toBe('<unknown self>');
        // Mod output with no severity is an entry too; the VM's own lines and the dump are not.
        expect(log.entries.some(entry => entry.message.startsWith('=====Wet and Cold'))).toBe(true);
        expect(log.entries.some(entry => /^VM is|Dumping stack|Frame count/.test(entry.message))).toBe(false);
        expect(log.dumps).toHaveLength(1);
        expect(log.dumps[0].stacks.map(stack => ({ id: stack.id, state: stack.state, type: stack.type, frames: stack.frames.map(frame => `${frame.script}.${frame.fn}`) }))).toEqual([
            { id: '1082', state: 'Waiting on other stack for call (Freeze state: Freezing)', type: 'Initialization Event', frames: ['CWFieldCOCrimeFactionScript.OnInit'] },
            { id: '1083', state: 'Running (Freeze state: Freezing)', type: 'Normal', frames: ['VDR_NPCUpdateScript.UpdateNPC', 'VDR_NPCUpdateScript.OnUpdate'] }
        ]);
    });

    it('reads Papyrus Tweaks NG\'s summary before a dump', () => {
        const log = parsePapyrusLog([
            '[01/03/2023 - 09:01:51PM] ###############Stack Dump Summary Start################',
            '[01/03/2023 - 09:01:51PM] Event: CWRikkeGalmarScript.OnPackageChange, Frequency: 2',
            '###############Stack Dump Summary End##################',
            '[01/03/2023 - 09:01:51PM] Suspended stack count is over our warning threshold, dumping stacks:'
        ].join('\r\n'));
        expect(log.dumps[0].summary).toEqual([{ event: 'CWRikkeGalmarScript.OnPackageChange', frequency: 2 }]);
    });

    it('knows the vanilla and Papyrus Tweaks NG wordings, and a form named before the message', () => {
        const kind = (message: string) => kindOf({ line: 1, time: '', message, stack: [] })?.kind.id;
        expect(kind('Script dlc1scwispwallscript cannot be bound to  (020030D0) because the script does not exist or is not currently loaded')).toBe('bind-missing');
        expect(kind('Incorrect number of arguments passed to function Foo. Expected 2, got 1 instead!. ')).toBe('bad-arguments');
        expect(kind('Function SetValue received incompatible arguments! Received types Int instead!')).toBe('bad-arguments');
        const found = kindOf({ line: 1, time: '', message: 'weroad08 (001065F4): attempting to start event scoped quest outside of story manager.', stack: [] });
        expect(found).toMatchObject({ kind: { id: 'misc-noise' }, form: { name: 'weroad08', formId: '001065F4' } });
    });
});

describe('buildPapyrusReport', () => {
    it('ranks script bugs before the save\'s history, by mod and plugin, folds noise into counts, and says it is no crash log', async () => {
        const owners = async (scripts: string[]) => new Map(scripts.filter(script => /VDR|XPMSE/.test(script))
            .map(script => [script.toLowerCase(), { mod: /VDR/.test(script) ? 'VDR Updates' : 'XP32 Skeleton', file: 'loose' }]));
        const plugins: Record<string, string> = { B8: 'VDR.esp', '02': 'Dawnguard.esm', '00': 'Skyrim.esm' };
        const report = await buildPapyrusReport('Papyrus.0.log', parsePapyrusLog(text()), owners, formId => plugins[formId.slice(0, 2)]);

        expect(report.problems.map(problem => [problem.kind, problem.class, problem.count])).toEqual([
            ['none-call', 'bug', 2], ['base-type-mismatch', 'bug', 1], ['native-missing', 'bug', 1], ['static-missing', 'bug', 1],
            ['save-type-missing', 'history', 2], ['missing-script', 'history', 1]
        ]);
        const none = report.problems[0];
        expect(none.scripts).toEqual([{ script: 'VDR_NPCUpdateScript', count: 2, mod: 'VDR Updates', file: 'loose' }]);
        expect(none.forms).toEqual([{ formId: 'B8000813', plugin: 'VDR.esp' }]);
        expect(none.first.stack).toEqual(['[VDR_NPCUpdate (B8000813)].VDR_NPCUpdateScript.OnUpdate() - "VDR_NPCUpdateScript.psc" Line 5']);
        // The wisp wall script is missing: its type mismatch is the missing script again, not a bug.
        expect(report.problems.find(problem => problem.kind === 'base-type-mismatch')!.scripts.map(script => script.script)).toEqual(['WIDeadBody01']);
        expect(report.problems.find(problem => problem.kind === 'missing-script')!.scripts).toEqual([{ script: 'dlc1scwispwallscript', count: 1, file: 'not in the setup' }]);
        expect(report.noise).toEqual({ count: 5, kinds: { 'property-gone': 1, 'save-variable-gone': 1, 'file-not-loaded': 1, 'assign-none': 1, 'misc-noise': 1 } });
        expect(report.load).toEqual({ dumps: 1, stacks: 2, first: '09/19/2021 - 05:49:05PM', events: [
            { event: 'CWFieldCOCrimeFactionScript.OnInit', stacks: 1 }, { event: 'VDR_NPCUpdateScript.OnUpdate', stacks: 1 }
        ] });
        expect(report.settings).toEqual({ loadScreenMs: 2000, memoryTotal: 524288 });
        expect(report.nextStep).toMatch(/^Tell the user the first problem, "A call on nothing \(None\)" \(2×\), of the script VDR_NPCUpdateScript from VDR Updates/);
        expect(report.nextStep).toContain('The Papyrus log is a script log, not a crash log: never put a crash on it');
    });

    it('reads the game\'s own logs of two provoked mods: the flood first, the save\'s history alone, the game\'s own lines as noise', async () => {
        // The test instance on 2026-11-08: PapyrusNoneFlood's quest calls AddItem on a property nothing fills, each second;
        // PapyrusGoneScript's names a script no file has. The second log loads the save with PapyrusNoneFlood removed.
        const read = (name: string) => parsePapyrusLog(fs.readFileSync(path.join(__dirname, 'fixtures', 'papyrus', name), 'utf8'));
        const none = async () => new Map();
        const flood = await buildPapyrusReport('Papyrus.1.log', read('game-none-flood.log'), none, () => undefined);
        expect(flood.problems.map(problem => [problem.kind, problem.class, problem.count, problem.scripts.map(script => script.script)])).toEqual([
            ['none-call', 'bug', 85, ['PNF_QuestScript']], ['missing-script', 'history', 1, ['PGS_GoneScript']]
        ]);
        expect(flood.problems[0]).toMatchObject({ flood: true, first: { stack: ['[PNF_Quest (FE041800)].PNF_QuestScript.OnUpdate() - "PNF_QuestScript.psc" Line 12'] } });
        // The DLC's and Creation Club's scripts the game ships without, the consoles' Amiibo natives: the game's own, every start.
        expect(flood.noise.kinds).toMatchObject({ 'game-own': 36, 'property-gone': 192 });
        expect(flood.nextStep).toMatch(/Tell the user the first problem, "A call on nothing \(None\)" \(85×, a flood\), of the script PNF_QuestScript/);

        const history = await buildPapyrusReport('Papyrus.0.log', read('game-save-history.log'), none, () => undefined);
        expect(history.problems.map(problem => [problem.kind, problem.class, problem.scripts.map(script => script.script)])).toEqual([
            ['missing-script', 'history', ['PGS_GoneScript', 'PNF_QuestScript']], ['save-type-missing', 'history', ['PNF_QuestScript']]
        ]);
        expect(history.nextStep).toMatch(/No script bug: what the log shows is the save's history/);
    });

    it('calls fifty of one problem a flood', async () => {
        const lines = ['[09/03/2022 - 10:20:49AM] Papyrus log opened (PC)'];
        for (let i = 0; i < 50; i++) lines.push('[09/03/2022 - 10:21:10AM] Error: Cannot call Start() on a None object, aborting function call', 'stack:', '\t[Q (0A000813)].QScript.OnUpdate() - "QScript.psc" Line 5');
        const report = await buildPapyrusReport('Papyrus.0.log', parsePapyrusLog(lines.join('\n')), async () => new Map(), () => undefined);
        expect(report.problems[0]).toMatchObject({ kind: 'none-call', count: 50, flood: true });
        expect(report.nextStep).toContain('(50×, a flood)');
    });
});

describe('papyrusLogFile', () => {
    it('takes Papyrus.0.log by default, the others by name or number, and refuses other names', () => {
        const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'papyrus-logs-'));
        roots.push(folder);
        fs.writeFileSync(path.join(folder, 'papyrus.2.log'), '');
        expect(papyrusLogFile(folder)).toBe(path.join(folder, 'Papyrus.0.log'));
        expect(papyrusLogFile(folder, '2')).toBe(path.join(folder, 'papyrus.2.log'));
        expect(papyrusLogFile(folder, 'Papyrus.2.log')).toBe(path.join(folder, 'papyrus.2.log'));
        expect(() => papyrusLogFile(folder, 'crash.log')).toThrow(PapyrusLogNotFoundError);
    });
});

describe('readPapyrusLog', () => {
    function write(file: string, content = ''): void {
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(file, content);
    }
    const qt = (value: string) => value.replace(/\\/g, '\\\\');

    /** A Mod Organizer instance whose profile keeps its own INIs; VDR Updates has the script of the log's None calls. */
    function instance(papyrusIni: string) {
        const root = fs.mkdtempSync(path.join(os.tmpdir(), 'skyrim-papyrus-test-'));
        roots.push(root);
        const game = path.join(root, 'game');
        const dir = path.join(root, 'mo2');
        write(path.join(game, 'SkyrimSE.exe'));
        for (const file of ['Skyrim.esm', 'Update.esm', 'Dawnguard.esm']) write(path.join(game, 'Data', file));
        write(path.join(dir, 'ModOrganizer.ini'), ['[General]', 'gameName=Skyrim Special Edition', `gamePath=@ByteArray(${qt(game)})`,
            'selected_profile=@ByteArray(Default)', 'game_edition=Steam', ''].join('\r\n'));
        const profile = path.join(dir, 'profiles', 'Default');
        write(path.join(profile, 'settings.ini'), '[General]\r\nLocalSettings=true\r\n');
        write(path.join(profile, 'skyrim.ini'), papyrusIni);
        write(path.join(profile, 'modlist.txt'), '+VDR Updates\r\n');
        write(path.join(profile, 'loadorder.txt'), 'Skyrim.esm\r\nUpdate.esm\r\nDawnguard.esm\r\nVDR.esp\r\n');
        write(path.join(profile, 'plugins.txt'), '*VDR.esp\r\n');
        write(path.join(dir, 'mods', 'VDR Updates', 'VDR.esp'));
        write(path.join(dir, 'mods', 'VDR Updates', 'Scripts', 'VDR_NPCUpdateScript.pex'));
        fs.mkdirSync(path.join(dir, 'overwrite'), { recursive: true });
        const logs = path.join(root, 'Logs', 'Script');
        fs.mkdirSync(logs, { recursive: true });
        let running = false;
        const helper: HelperTransport = {
            get running() { return running; },
            async start() { running = true; },
            stop() { running = false; },
            async call(method: string, params: any = {}): Promise<any> {
                if (method === 'hello') return { helper: 'test', protocol: 1 };
                if (method === 'gameVersion') return { version: '1.7.104.0' };
                if (method === 'setLoadOrder') return { plugins: params.plugins.length, missing: [] };
                if (method === 'pluginHeaders') return { plugins: params.paths.map((file: string) => ({ path: file, light: false })) };
                if (method === 'bsaFiles') return { found: {} };
                throw new Error(`unexpected ${method}`);
            }
        };
        const engine = new SkyrimEngine(new Mo2Manager(dir, currentPlatform()), helper, path.join(root, 'projects'), { platform: currentPlatform(), papyrusLogDir: logs });
        return { logs, profile, backend: new SkyrimInstanceBackend({ engine }) };
    }

    it('reads the newest log: each script by its mod, each record by its plugin in the load order now, logging as the INIs say', async () => {
        // The load order's fourth full plugin, index 03, is VDR.esp.
        const fixture = instance('[Papyrus]\r\nbEnableLogging=1\r\nbEnableTrace=1\r\n');
        fs.writeFileSync(path.join(fixture.logs, 'Papyrus.0.log'), text().replace(/B8000813/g, '03000813'));
        const report = await fixture.backend.readPapyrusLog({});
        if (!('problems' in report)) throw new Error('entries came back');
        expect(report.logging).toEqual({ enabled: true, trace: true, debugInfo: false, file: path.join(fixture.profile, 'skyrim.ini') });
        expect(report.problems[0]).toMatchObject({
            kind: 'none-call', scripts: [{ script: 'VDR_NPCUpdateScript', count: 2, mod: 'VDR Updates', file: 'loose' }],
            forms: [{ formId: '03000813', plugin: 'VDR.esp' }]
        });
        // Its stacks have `Line ?`, and bLoadDebugInformation is off.
        expect(report.nextStep).toMatch(/no line numbers: bLoadDebugInformation=1/);

        const one = await fixture.backend.readPapyrusLog({ script: 'VDR_NPCUpdateScript' });
        if (!('entries' in one)) throw new Error('a report came back');
        expect(one.entries.map(entry => entry.message)).toEqual([
            'Cannot call Start() on a None object, aborting function call', 'Assigning None to a non-object variable named "::temp3"',
            'Cannot call Start() on a None object, aborting function call'
        ]);
    });

    it('with no log and logging off, says so and gives the editIni call that turns it on, on the user\'s word', async () => {
        const fixture = instance('[General]\r\nsLanguage=ENGLISH\r\n');
        await expect(fixture.backend.readPapyrusLog({})).rejects.toThrow(
            /^There is no Papyrus\.0\.log in .*: the game writes none, Papyrus logging is off in .*skyrim\.ini\. Turning it on is a change to the user's setup — on their word: editIni \{"set": \[\{"section": "Papyrus", "key": "bEnableLogging", "value": "1"\}/);
    });

    it('with no log and logging on in the profile, gives no call that turns it on again', async () => {
        // The test instance's game wrote none with the lines in the profile's Skyrim.ini alone (2026-11-08): where My Games is
        // found, the answer names its Skyrim.ini.
        const fixture = instance('[Papyrus]\r\nbEnableLogging=1\r\nbEnableTrace=1\r\nbLoadDebugInformation=1\r\n');
        const error = await fixture.backend.readPapyrusLog({}).catch((thrown: Error) => thrown);
        expect((error as Error).message).toMatch(/^There is no Papyrus\.0\.log in .*\. Papyrus logging is on in .*skyrim\.ini\./);
        expect((error as Error).message).not.toContain('editIni');
    });

    it('with another session\'s log asked for and the newest there, names the logs there and the call that reads the newest', async () => {
        const fixture = instance('[General]\r\nsLanguage=ENGLISH\r\n');
        fs.writeFileSync(path.join(fixture.logs, 'Papyrus.0.log'), text());
        const error = await fixture.backend.readPapyrusLog({ log: '1' }).catch((thrown: Error) => thrown);
        expect((error as Error).message).toMatch(/^There is no Papyrus\.1\.log in .*: the game keeps Papyrus\.0\.log there, Papyrus\.0\.log the newest\. readPapyrusLog \{\} reads the newest/);
        expect((error as Error).message).not.toMatch(/writes none|editIni/);
    });
});
