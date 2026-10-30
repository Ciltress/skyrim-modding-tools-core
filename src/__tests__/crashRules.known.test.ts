import { knownRules, percentAlphabetized } from '../backend/skyrim/crash/rules/known';
import type { CrashRule, Finding } from '../backend/skyrim/crash/crashRules';
import { BASE_PLUGINS, crashView, firstLineAt, frame, type LogParts } from './crashLogText';

/** Known crashes Phostwood checks inline: each rule fires on the log it is about, and not on another. */

const rule = (id: string): CrashRule => {
    const found = knownRules.find(entry => entry.id === id);
    if (!found) throw new Error(`no rule ${id}`);
    return found;
};

/** Plain text a model reads: no markup, no emoji, a short title. */
const plain = (found: Finding) => {
    for (const text of [found.title, found.explain, found.fix]) expect(text).not.toMatch(/[<>*`]|\p{Extended_Pictographic}/u);
    expect(found.title.length).toBeLessThanOrEqual(70);
    expect(found.fix.length).toBeLessThanOrEqual(420);
};

const run = (id: string, parts: LogParts): Finding[] => {
    const found = [rule(id).diagnose(crashView(parts)) ?? []].flat();
    found.forEach(plain);
    return found;
};

const places = (found: Finding) => [...new Set(found.evidence.map(item => item.place))];

describe('known-crash rules', () => {
    it('has unique ids', () => {
        expect(new Set(knownRules.map(entry => entry.id)).size).toBe(knownRules.length);
    });

    it('vramr-gorehowl: the first line at D6DDDA with Gorehowl among the objects', () => {
        const gorehowl = 'RSP+50: (TESObjectWEAP*) "Gorehowl" [0x0200B3A2] ("Dawnguard.esm")';
        const [found] = run('vramr-gorehowl', { firstLine: firstLineAt('SkyrimSE.exe+0F32ACA'), relevantObjects: [gorehowl] });
        expect(found).toMatchObject({ confidence: 'certain' });
        expect(places(found)).toEqual(['firstLine', 'relevantObjects']);
        expect(found.fix).toContain('Nexus 90557');
        expect(run('vramr-gorehowl', { relevantObjects: [gorehowl] })).toEqual([]);
        expect(run('vramr-gorehowl', { firstLine: firstLineAt('SkyrimSE.exe+0F32ACA') })).toEqual([]);
        // Gorehowl only on the stack is beyond the top quarter.
        expect(run('vramr-gorehowl', { firstLine: firstLineAt('SkyrimSE.exe+0F32ACA'), stack: [`[RSP+40 ] 0x1F2 ${gorehowl}`] })).toEqual([]);
    });

    it('wraith-of-sithis: a Wraith of Sithis from mihailmmasithis.esp in the top third', () => {
        const [found] = run('wraith-of-sithis', { relevantObjects: ['RSP+80: (Character*) "Wraith of Sithis" [0x2A000D62] ("mihailmmasithis.esp")'] });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'relevantObjects' }] });
        expect(found.fix).toContain('Nexus 108591');
        expect(run('wraith-of-sithis', { relevantObjects: ['RSP+80: (Character*) "Wraith of Sithis" [0x2A000D62] ("Other.esp")'] })).toEqual([]);
    });

    it('alphabetized-load-order: over 20 plugins, the .esp ones over 80% in A-Z order', () => {
        const sorted = Array.from({ length: 25 }, (_, i) => `Mod ${String.fromCharCode(65 + i)}.esp`);
        const [found] = run('alphabetized-load-order', { plugins: [...BASE_PLUGINS, ...sorted] });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'plugins', text: expect.stringMatching(/^Light:/) }] });
        expect(found.explain).toContain('100%');
        expect(run('alphabetized-load-order', { plugins: [...BASE_PLUGINS, ...[...sorted].reverse()] })).toEqual([]);
        // 20 plugins or fewer are not judged.
        expect(run('alphabetized-load-order', { plugins: [...BASE_PLUGINS, ...sorted.slice(0, 15)] })).toEqual([]);
        expect(percentAlphabetized(['b.esp', 'a.esp', 'c.esp', 'd.esp', 'x.esm'])).toBeCloseTo(66.67, 1);
        expect(percentAlphabetized(['a.esp'])).toBeUndefined();
    });

    it('physics-12f4797: the first line at 12F4797, the world camera and an unnamed room bound among the objects', () => {
        const camera = 'RSP+C50: (NiCamera*) "WorldRoot Camera"';
        const [found] = run('physics-12f4797', {
            firstLine: firstLineAt('SkyrimSE.exe+13148B7'),
            relevantObjects: [camera, 'RSP+D10: (BSMultiBoundRoom*)']
        });
        expect(found).toMatchObject({ confidence: 'certain' });
        expect(places(found)).toEqual(['firstLine', 'relevantObjects']);
        expect(run('physics-12f4797', {
            firstLine: firstLineAt('SkyrimSE.exe+13148B7'),
            relevantObjects: [camera, 'RSP+D10: (BSMultiBoundRoom*) "Room01"']
        })).toEqual([]);
        expect(run('physics-12f4797', { firstLine: firstLineAt('SkyrimSE.exe+13148B7'), relevantObjects: ['RSP+D10: (BSMultiBoundRoom*)'] })).toEqual([]);
    });

    it('medal-streaming: medal-hook64.dll anywhere in the log', () => {
        const [found] = run('medal-streaming', { modules: ['medal-hook64.dll 0x7FFA5C670000'] });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'modules' }] });
        expect(run('medal-streaming', { modules: ['nvwgf2umx.dll 0x7FFA5C670000'] })).toEqual([]);
    });

    it('upscaler-first-line: an upscaler DLL in the first line', () => {
        const [found] = run('upscaler-first-line', { firstLine: firstLineAt('PDPerfPlugin.dll+0012345') });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'firstLine' }] });
        expect(run('upscaler-first-line', { firstLine: firstLineAt('nvngx_dlss.dll+0A12345') })).toHaveLength(1);
        expect(run('upscaler-first-line', { callStack: [frame(0, 'PDPerfPlugin.dll+0012345')] })).toEqual([]);
    });

    it('kernelbase-json: KERNELBASE in the first line and a JSON parse error near it', () => {
        const kernelbase = firstLineAt('KERNELBASE.dll+005F35C', '');
        const message = '[json.exception.parse_error.101] parse error at line 1, column 1';
        const [found] = run('kernelbase-json', { firstLine: kernelbase, stack: [`[RSP+40 ] 0x1F2A3B4C5D0 (char*) "${message}"`] });
        expect(found).toMatchObject({ confidence: 'certain' });
        expect(places(found)).toEqual(['firstLine', 'stack']);
        expect(found.fix).toContain('MaxStdio = 8192');
        // Crash Logger 1.2x's own C++ exception section.
        const [cpp] = run('kernelbase-json', {
            firstLine: kernelbase,
            header: ['C++ EXCEPTION:', '\tType: nlohmann::json_abi_v3_11_2::detail::parse_error', `\tMessage: ${message}`]
        });
        expect(places(cpp)).toEqual(['firstLine', 'cppException']);
        expect(run('kernelbase-json', { firstLine: kernelbase })).toEqual([]);
        expect(run('kernelbase-json', { stack: [`[RSP+40 ] 0x1F2A3B4C5D0 (char*) "${message}"`] })).toEqual([]);
    });

    it('access-violation-c0000005: the code 0xC0000005 in the top half', () => {
        const [found] = run('access-violation-c0000005', { stack: ['[RSP+28 ] 0xC0000005         (size_t) [3221225477]'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'stack' }] });
        expect(found.fix).toContain('Nexus 106441');
        expect(run('access-violation-c0000005', { registers: ['RAX 0xC00000051234       (size_t) [211106232537652]'] })).toEqual([]);
        expect(run('access-violation-c0000005', { header: ['Exception Code: 0xC0000005'] })).toEqual([]);
    });

    it('compressed-archive-stream: CompressedArchiveStream in the top half, naming the textures there', () => {
        const stream = frame(3, 'SkyrimSE.exe+0D1E2F0', 'BSResource::CompressedArchiveStream::DoRead');
        const [found] = run('compressed-archive-stream', {
            callStack: [stream],
            relevantObjects: ['RSP+60: (char*) "textures\\armor\\steel\\cuirass.dds"']
        });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(found.explain).toContain('textures\\armor\\steel\\cuirass.dds');
        const [bare] = run('compressed-archive-stream', { callStack: [stream] });
        expect(bare.explain).toContain('less likely');
        expect(run('compressed-archive-stream', { callStack: [frame(3, 'SkyrimSE.exe+0D1E2F0', 'BSResource::ArchiveStream::DoRead')] })).toEqual([]);
    });

    it('monster-mod: the first line at 5999C7 or D02C2C', () => {
        const [found] = run('monster-mod', { firstLine: firstLineAt('SkyrimSE.exe+05A6D57'), plugins: [...BASE_PLUGINS, 'Monster Mod.esp'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(found.explain).toContain('is in this load order');
        const [other] = run('monster-mod', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+0D02C2C'), plugins: BASE_PLUGINS });
        expect(other.explain).toContain('is not in this load order');
        expect(run('monster-mod', { callStack: [frame(0, 'SkyrimSE.exe+05A6D57')] })).toEqual([]);
    });

    it('improved-camera-dll: ImprovedCameraSE.dll in the top half', () => {
        const [found] = run('improved-camera-dll', { callStack: [frame(0, 'ImprovedCameraSE.dll+0012AB0')] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(run('improved-camera-dll', { sksePlugins: ['ImprovedCameraSE.dll v2.1.0'] })).toEqual([]);
    });

    it('simplest-horses: SimplestHorses.esp in the top quarter', () => {
        const horse = '(Character*) "Horse" [0x2B000801] ("SimplestHorses.esp")';
        const [found] = run('simplest-horses', { relevantObjects: [`RSP+90: ${horse}`] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(run('simplest-horses', { stack: [`[RSP+30 ] 0x1F2 ${horse}`], plugins: [...BASE_PLUGINS, 'SimplestHorses.esp'] })).toEqual([]);
    });

    it('animated-ice-floes: Animated Ice Floes.esp in the top quarter', () => {
        const [found] = run('animated-ice-floes', { relevantObjects: ['RSP+A0: (TESObjectREFR*) [0x2C000D63] ("Animated Ice Floes.esp")'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(run('animated-ice-floes', { plugins: [...BASE_PLUGINS, 'Animated Ice Floes.esp'] })).toEqual([]);
    });

    it('forced-termination-0cb748e: the first line at 0CB748E, moved to the log\'s game version', () => {
        const [found] = run('forced-termination-0cb748e', { firstLine: firstLineAt('SkyrimSE.exe+0CD160E') });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(run('forced-termination-0cb748e', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+0CB748E') })).toHaveLength(1);
        expect(run('forced-termination-0cb748e', { firstLine: firstLineAt('SkyrimSE.exe+0CB748E') })).toEqual([]);
    });

    it('engine-fixes-durability-7428b1: the first line at 7428B1', () => {
        const [found] = run('engine-fixes-durability-7428b1', { firstLine: firstLineAt('SkyrimSE.exe+0755511') });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('Nexus 17230');
        expect(run('engine-fixes-durability-7428b1', { callStack: [frame(0, 'SkyrimSE.exe+0755511')] })).toEqual([]);
    });

    it('engine-fixes-display-tweaks-8bda97: the first line at 8BDA97', () => {
        const [found] = run('engine-fixes-display-tweaks-8bda97', { firstLine: firstLineAt('SkyrimSE.exe+08D3087') });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('Nexus 34705');
        // 1.7.99's offset in a 1.7.104 log is another place.
        expect(run('engine-fixes-display-tweaks-8bda97', { firstLine: firstLineAt('SkyrimSE.exe+08D2E27') })).toEqual([]);
    });

    it('controlmap-hotkeys-c1315c: the first line at C1315C', () => {
        const [found] = run('controlmap-hotkeys-c1315c', { firstLine: firstLineAt('SkyrimSE.exe+0C2D8DC') });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(run('controlmap-hotkeys-c1315c', {})).toEqual([]);
    });

    it('save-game-d2b923: the first line at D2B923', () => {
        const [found] = run('save-game-d2b923', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+0D2B923') });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(run('save-game-d2b923', { firstLine: firstLineAt('SkyrimSE.exe+0EF0323') })).toHaveLength(1);
        expect(run('save-game-d2b923', { callStack: [frame(0, 'SkyrimSE.exe+0EF0323')] })).toEqual([]);
    });

    it('skse64-loader: skse64_loader.exe in the top half', () => {
        const [found] = run('skse64-loader', { stack: ['[RSP+48 ] 0x1F2A3B4C5D0 (char*) "skse64_loader.exe"'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'stack' }] });
        expect(run('skse64-loader', { processInfo: ['Command Line: "G:\\Game\\skse64_loader.exe"'] })).toEqual([]);
    });

    it('zero-address: the first line at address 0x0, in no module', () => {
        const [found] = run('zero-address', { firstLine: 'Unhandled exception "EXCEPTION_ACCESS_VIOLATION" at 0x000000000000' });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(run('zero-address', { firstLine: 'Unhandled exception "EXCEPTION_ACCESS_VIOLATION" at 0x000400000005' })).toEqual([]);
        expect(run('zero-address', {})).toEqual([]);
    });

    it('dyndolod-esm: DynDOLOD.esm in the top half', () => {
        const [found] = run('dyndolod-esm', { relevantObjects: ['RSP+70: (TESObjectREFR*) [0x3F012345] ("DynDOLOD.esm")'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(run('dyndolod-esm', { plugins: [...BASE_PLUGINS, 'DynDOLOD.esm'] })).toEqual([]);
    });

    it('horse-follower-pathing: Pathing in the top half and a horse or pony in the top quarter', () => {
        const pathing = frame(2, 'SkyrimSE.exe+0612345', 'PathingTaskData::sub_140612345');
        const horse = 'RSP+90: (Character*) "Horse" [0x00097E1E] ("Skyrim.esm")';
        const [found] = run('horse-follower-pathing', { callStack: [pathing], relevantObjects: [horse] });
        expect(found).toMatchObject({ confidence: 'possible' });
        expect(places(found)).toEqual(['callStack', 'relevantObjects']);
        expect(run('horse-follower-pathing', { relevantObjects: [horse] })).toEqual([]);
        expect(run('horse-follower-pathing', { callStack: [pathing] })).toEqual([]);
        // Phostwood matches Pathing in its case.
        expect(run('horse-follower-pathing', { callStack: [frame(2, 'SkyrimSE.exe+0612345', 'pathing_sub')], relevantObjects: [horse] })).toEqual([]);
    });

    it('hdt-smp-physics: hdtSMP64.dll in the top half', () => {
        const [found] = run('hdt-smp-physics', { callStack: [frame(0, 'hdtSMP64.dll+0045A10')] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(run('hdt-smp-physics', { modules: ['hdtSMP64.dll 0x7FFA5C670000'] })).toEqual([]);
    });

    it('cbp-physics: cbp.dll in the top half', () => {
        const [found] = run('cbp-physics', { callStack: [frame(1, 'cbp.dll+0012340')] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(run('cbp-physics', { sksePlugins: ['cbp.dll v1.6.3'] })).toEqual([]);
    });
});
