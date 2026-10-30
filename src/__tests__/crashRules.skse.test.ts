import { skseRules } from '../backend/skyrim/crash/rules/skse';
import type { CrashRule, Finding } from '../backend/skyrim/crash/crashRules';
import { BASE_PLUGINS, crashView, firstLineAt, frame, type LogParts } from './crashLogText';

/** SKSE plugins, their frameworks and the interface mods on them: each rule fires on the log it is about, and not on another. */

const rule = (id: string): CrashRule => {
    const found = skseRules.find(entry => entry.id === id);
    if (!found) throw new Error(`no rule ${id}`);
    return found;
};
const run = (id: string, parts: LogParts): Finding[] => {
    const found = [rule(id).diagnose(crashView(parts)) ?? []].flat();
    for (const finding of found) plain(finding);
    return found;
};

/** What a model reads: plain text, a short title, a fix of a few hundred characters. */
function plain(found: Finding): void {
    expect(found.title.length).toBeLessThanOrEqual(70);
    expect(found.fix.length).toBeLessThanOrEqual(460);
    for (const text of [found.title, found.explain, found.fix]) expect(text).not.toMatch(/<|https?:|\*\*|undefined/);
}

/** Eleven SKSE plugins and the base game: a mod list, written in full. */
const MODDED: LogParts = {
    sksePlugins: ['skse64_1_7_104.dll', 'po3_PapyrusExtender.dll v6.1.0', 'PapyrusUtil.dll v4.6', 'SkyUI.dll', 'ConsoleUtilSSE.dll v1.5.1',
        'powerofthree_Tweaks.dll v1.12', 'MCMHelper.dll v1.5', 'OpenAnimationReplacer.dll v3.0', 'SpellPerkItemDistributor.dll v7.2',
        'KeywordItemDistributor.dll v3.4', 'CrashLogger.dll v1.27.0'],
    plugins: BASE_PLUGINS
};

describe('skse rules', () => {
    it('engine-fixes-missing: a full mod list without EngineFixes.dll; certain with Wheeler on 1.6.1170 and later', () => {
        const [found] = run('engine-fixes-missing', MODDED);
        expect(found).toMatchObject({ confidence: 'likely', evidence: [] });
        expect(found.fix).toContain('Nexus 17230');
        expect(found.fix).toContain('Use version 7');

        const [wheeler] = run('engine-fixes-missing', { ...MODDED, game: '1.6.1170', sksePlugins: [...MODDED.sksePlugins!, 'wheeler.dll v1'] });
        expect(wheeler).toMatchObject({ confidence: 'certain', evidence: [{ place: 'sksePlugins', text: 'wheeler.dll v1' }] });

        // 1.6.640 has no Engine Fixes 7: its settings instead.
        const [old] = run('engine-fixes-missing', { ...MODDED, game: '1.6.640', sksePlugins: [...MODDED.sksePlugins!, 'wheeler.dll v1'] });
        expect(old.confidence).toBe('likely');
        expect(old.fix).toContain('MaxStdio = 8192');

        expect(run('engine-fixes-missing', { ...MODDED, sksePlugins: [...MODDED.sksePlugins!, 'EngineFixes.dll v7.0.20'] })).toEqual([]);
        // Cut before its plugin list: nothing to tell it is missing.
        expect(run('engine-fixes-missing', { sksePlugins: MODDED.sksePlugins })).toEqual([]);
    });

    it('engine-fixes-first-line: an Engine Fixes file on the first line', () => {
        const [found] = run('engine-fixes-first-line', { firstLine: firstLineAt('EngineFixes.dll+00F16A3') });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        expect(found.title).toContain('EngineFixes.dll');
        expect(run('engine-fixes-first-line', { firstLine: firstLineAt('tbbmalloc.dll+0012345') })[0].title).toContain('tbbmalloc.dll');
        expect(run('engine-fixes-first-line', { callStack: [frame(0, 'EngineFixes.dll+00F16A3')] })).toEqual([]);
    });

    it('engine-fixes-outdated: Engine Fixes before 7.0.20, or before 7.0.18 with ShadowSceneNode near the crash', () => {
        const [found] = run('engine-fixes-outdated', { sksePlugins: ['EngineFixes.dll v7.0.19'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'sksePlugins', text: 'EngineFixes.dll v7.0.19' }] });
        expect(found.title).toContain('7.0.20');

        const [shadow] = run('engine-fixes-outdated', {
            game: '1.6.1170', relevantObjects: ['RSP+48: (ShadowSceneNode*)'], sksePlugins: ['EngineFixes.dll v7.0.17']
        });
        expect(shadow).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }, { place: 'sksePlugins' }] });
        expect(shadow.title).toContain('may prevent');

        expect(run('engine-fixes-outdated', { relevantObjects: ['RSP+48: (ShadowSceneNode*)'], sksePlugins: ['EngineFixes.dll v7.0.20'] })).toEqual([]);
        // 1.6.640 has no Engine Fixes 7 to update to.
        expect(run('engine-fixes-outdated', { game: '1.6.640', sksePlugins: ['EngineFixes.dll v6.2.0'] })).toEqual([]);
    });

    it('sse-fixes-on-ae: an impact effect near the crash and FpsFixPlugin.dll loaded, on 1.6 or later', () => {
        const parts: LogParts = { game: '1.6.1170', relevantObjects: ['RSP+60: (BGSImpactData*) "BloodSprayImpact01" [0x0001F2A4] ("Skyrim.esm")'], sksePlugins: ['FpsFixPlugin.dll'] };
        const [found] = run('sse-fixes-on-ae', parts);
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'relevantObjects' }, { place: 'sksePlugins' }] });
        expect(found.fix).toContain('Nexus 10547');
        expect(run('sse-fixes-on-ae', { ...parts, game: '1.5.97' })).toEqual([]);
        expect(run('sse-fixes-on-ae', { ...parts, sksePlugins: ['EngineFixes.dll v7.0.20'] })).toEqual([]);
    });

    it('crash-logger-outdated: a log Crash Logger before 1.27.0 wrote', () => {
        const [found] = run('crash-logger-outdated', { logger: '1.23.1' });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'header', text: expect.stringMatching(/^CrashLoggerSSE v1-23-1/) }] });
        expect(found.fix).toContain('Nexus 59818');
        expect(run('crash-logger-outdated', { logger: '1.27.0' })).toEqual([]);
    });

    it('netscriptframework-installed: .NET Script Framework among the modules of a Crash Logger log', () => {
        const [found] = run('netscriptframework-installed', { modules: ['NetScriptFramework.Runtime.dll                0x7FFC006E0000'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'modules' }] });
        expect(found.fix).toContain('Nexus 21294');
        expect(run('netscriptframework-installed', { modules: ['CrashLogger.dll                0x7FFC006E0000'] })).toEqual([]);
    });

    it('jcontainers: JContainers on the first line, or KERNELBASE there with JContainers or Custom Skills in the call stack', () => {
        const [found] = run('jcontainers', { firstLine: firstLineAt('JContainers64.dll+0012345') });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'firstLine' }] });

        const [kernel] = run('jcontainers', {
            firstLine: firstLineAt('KERNELBASE.dll+0034567', 'mov rax, [rcx]', 'EXCEPTION_UNKNOWN'), callStack: [frame(0, 'KERNELBASE.dll+0034567'), frame(1, 'CustomSkills.dll+0001234')]
        });
        expect(kernel).toMatchObject({ confidence: 'certain', evidence: [{ place: 'firstLine' }, { place: 'callStack', index: 1 }] });
        expect(kernel.fix).toContain('Nexus 140833');

        expect(run('jcontainers', { firstLine: firstLineAt('KERNELBASE.dll+0034567'), sksePlugins: ['JContainers64.dll v4.2.9'] })).toEqual([]);
    });

    it('dbskse-functions: DbSkseFunctions.dll on the first line (certain) or in the top third (likely)', () => {
        const [first] = run('dbskse-functions', { firstLine: firstLineAt('DbSkseFunctions.dll+0045678') });
        expect(first).toMatchObject({ confidence: 'certain', evidence: [{ place: 'firstLine' }] });
        expect(first.fix).toContain('iMaxArrowsSavedPerReference=0');

        const [near] = run('dbskse-functions', { callStack: [frame(0, 'SkyrimSE.exe+0021C44'), frame(1, 'DbSkseFunctions.dll+0045678')] });
        expect(near).toMatchObject({ confidence: 'likely', evidence: [{ place: 'callStack', index: 1 }] });

        expect(run('dbskse-functions', { sksePlugins: ['DbSkseFunctions.dll v10.4'] })).toEqual([]);
    });

    it('skee64-racemenu: skee64.dll on the first line or in the top half', () => {
        const [first] = run('skee64-racemenu', { firstLine: firstLineAt('skee64.dll+00A1B2C'), relevantObjects: ['RBX: (BSFadeNode*) "meshes\\actors\\character\\hair\\longhair.nif"'] });
        expect(first).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        expect(first.title).toContain('in RaceMenu');
        expect(first.fix).toContain('longhair.nif');
        expect(first.fix).toContain('Nexus 138586');

        const [near] = run('skee64-racemenu', { stack: ['[RSP+48  ] 0x7FFC03990000     (void* -> skee64.dll+00A1B2C)'] });
        expect(near).toMatchObject({ confidence: 'likely', evidence: [{ place: 'stack' }] });
        expect(near.title).toContain('near the crash');

        expect(run('skee64-racemenu', { modules: ['skee64.dll                0x7FFC03990000'], sksePlugins: ['skee64.dll'] })).toEqual([]);
    });

    it('xpmse: XPMSE in the top half', () => {
        const [found] = run('xpmse', { relevantObjects: ['RSP+58: (TESRace*) "Nord" [0x00013746] ("XPMSE.esp")'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(run('xpmse', { plugins: [...BASE_PLUGINS, 'XPMSE.esp'] })).toEqual([]);
    });

    it('animation: B02235 on the first line (likely), or animation code or files in the top half (possible)', () => {
        const [hex] = run('animation', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+0B1C605') });
        expect(hex).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        expect(hex.title).toContain('B02235');
        expect(run('animation', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+0B02235') })).toHaveLength(1);

        const [code] = run('animation', { callStack: [frame(0, 'SkyrimSE.exe+0021C44', 'BSAnimationGraphManager::Update')] });
        expect(code).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(code.explain).toContain('BSAnimationGraphManager');

        // 1.6.1170's offset is another place in 1.7.104.
        expect(run('animation', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+0B02235') })).toEqual([]);
        expect(run('animation', { sksePlugins: ['OpenAnimationReplacer.dll v3.0'] })).toEqual([]);
    });

    it('animation-loader: behavior files or an animation loader in the top half', () => {
        const [found] = run('animation-loader', { callStack: [frame(0, 'SkyrimSE.exe+0021C44', 'hkbBehaviorGraph::update')] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(found.fix).toContain('Nexus 60033');

        // Phostwood's bare offset counts only in the version it is from.
        expect(run('animation-loader', { game: '1.6.1170', callStack: [frame(0, 'SkyrimSE.exe+067B88B')] })).toHaveLength(1);
        expect(run('animation-loader', { game: '1.7.104', callStack: [frame(0, 'SkyrimSE.exe+067B88B')] })).toEqual([]);
        expect(run('animation-loader', { sksePlugins: ['OpenAnimationReplacer.dll v3.0'] })).toEqual([]);
    });

    it('mco-bfco-together: MCO.dll and BFCO.dll both in the log', () => {
        const [found] = run('mco-bfco-together', { sksePlugins: ['MCO.dll', 'BFCO.dll v3.1'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'sksePlugins' }, { place: 'sksePlugins' }] });
        expect(found.fix).toContain('Nexus 117052');
        expect(found.fix).not.toContain('DMCO');

        const [dmco] = run('mco-bfco-together', { sksePlugins: ['MCO.dll', 'BFCO.dll v3.1'], plugins: [...BASE_PLUGINS, 'DodgeFramework.esl'] });
        expect(dmco.fix).toContain('Nexus 175129');

        expect(run('mco-bfco-together', { sksePlugins: ['DMCO.dll', 'BFCO.dll v3.1'] })).toEqual([]);
    });

    it('fisses-thread-safety: fiss.dll on the first line', () => {
        const [found] = run('fisses-thread-safety', { firstLine: firstLineAt('fiss.dll+0001234') });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('Nexus 13956');
        expect(run('fisses-thread-safety', { sksePlugins: ['fiss.dll v1.2'] })).toEqual([]);
    });

    it('hud: HUD, menus or maps in the top half', () => {
        const [found] = run('hud', { callStack: [frame(0, 'SkyrimSE.exe+0021C44', 'HUDMenu::ProcessMessage')] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(run('hud', { sksePlugins: ['moreHUD.dll v5.1'] })).toEqual([]);
    });

    it('wheeler: wheeler.dll in the top quarter with Engine Fixes installed', () => {
        const parts: LogParts = { callStack: [frame(0, 'wheeler.dll+002B361')], sksePlugins: ['EngineFixes.dll v7.0.20', 'wheeler.dll v1'] };
        const [found] = run('wheeler', parts);
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'callStack' }] });
        expect(found.fix).toContain('Nexus 132074');
        expect(run('wheeler', { ...parts, sksePlugins: ['wheeler.dll v1'] })).toEqual([]);
        expect(run('wheeler', { stack: ['[RSP+48  ] 0x7FFC006E0000     (void* -> wheeler.dll+002B361)'], sksePlugins: parts.sksePlugins })).toEqual([]);
    });

    it('quest-journal: the SkyUI journal in the top third', () => {
        const [found] = run('quest-journal', { relevantObjects: ['RSP+68: (JournalMenu*)'] });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'relevantObjects' }] });
        expect(found.fix).toContain('Nexus 60837');
        expect(run('quest-journal', { registers: ['RCX 0x1F2E3D4C5B0     (char*) "Interface\\Quest_Journal.swf"'] })[0].evidence[0].place).toBe('registers');
        expect(run('quest-journal', { stack: ['[RSP+48  ] 0x1F2E3D4C5B0     (JournalMenu*)'] })).toEqual([]);
    });

    it('journal-menu-stack: the journal on the stack only', () => {
        const [found] = run('journal-menu-stack', { stack: ['[RSP+48  ] 0x1F2E3D4C5B0     (char*) "Journal_SystemTab"'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'stack' }] });
        // Higher up, quest-journal says it.
        expect(run('journal-menu-stack', { relevantObjects: ['RSP+68: (JournalMenu*)'], stack: ['[RSP+48  ] 0x1F2E3D4C5B0     (JournalMenu*)'] })).toEqual([]);
        expect(run('journal-menu-stack', { stack: ['[RSP+48  ] 0x1F2E3D4C5B0     (MapMenu*)'] })).toEqual([]);
    });

    it('compass-navigation-overhaul: HUDMenu in the top half with Compass Navigation Overhaul installed', () => {
        const parts: LogParts = { callStack: [frame(0, 'SkyrimSE.exe+0021C44', 'HUDMenu::ProcessMessage')], sksePlugins: ['CompassNavigationOverhaul.dll'] };
        const [found] = run('compass-navigation-overhaul', parts);
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }, { place: 'sksePlugins' }] });
        expect(found.fix).toContain('Nexus 74484');
        expect(run('compass-navigation-overhaul', { ...parts, callStack: [frame(0, 'CompassNavigationOverhaul.dll+005990C')] })).toEqual([]);
    });

    it('a0d789: the first line at A0D789, moved to the log\'s game version', () => {
        const [found] = run('a0d789', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+0A25AA9') });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'firstLine' }] });
        expect(run('a0d789', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+0A0D789') })).toHaveLength(1);
        expect(run('a0d789', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+0A0D789') })).toEqual([]);
    });
});
