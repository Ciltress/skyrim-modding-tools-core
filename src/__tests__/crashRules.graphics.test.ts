import fs from 'node:fs';
import path from 'node:path';
import { graphicsRules } from '../backend/skyrim/crash/rules/graphics';
import { parseCrashLog } from '../backend/skyrim/crash/crashLog';
import { CrashView, type CrashRule, type Finding } from '../backend/skyrim/crash/crashRules';
import { BASE_PLUGINS, crashView, firstLineAt, frame, type LogParts } from './crashLogText';

/** Graphics: drivers, ENB and upscalers, meshes and textures: each rule fires on the log it is about, and not on another. */

const rule = (id: string): CrashRule => {
    const found = graphicsRules.find(entry => entry.id === id);
    if (!found) throw new Error(`no rule ${id}`);
    return found;
};
const run = (id: string, parts: LogParts): Finding[] => [rule(id).diagnose(crashView(parts)) ?? []].flat();
const places = (found: Finding): string[] => found.evidence.map(item => item.place);

const FIXTURES = path.join(__dirname, 'fixtures', 'crash-logs');
const runOn = (id: string, fixture: string): Finding[] =>
    [rule(id).diagnose(new CrashView(parseCrashLog(fs.readFileSync(path.join(FIXTURES, fixture), 'utf8')))) ?? []].flat();

const inProgress = (model: string) => `    0.412 ms ago  ${model}  (in progress)`;

describe('graphics rules', () => {
    it('has unique ids', () => {
        expect(new Set(graphicsRules.map(entry => entry.id)).size).toBe(graphicsRules.length);
    });

    it('nvidia-driver: an NVIDIA driver file on the first line', () => {
        const [found] = run('nvidia-driver', {
            firstLine: firstLineAt('nvwgf2umx.dll+0A1B2C3'),
            callStack: [frame(0, 'nvwgf2umx.dll+0A1B2C3'), frame(1, 'nvapi64.dll+0012345')]
        });
        expect(found).toMatchObject({ confidence: 'certain', title: expect.stringContaining('nvwgf2umx.dll') });
        expect(places(found)).toEqual(['firstLine', 'callStack', 'callStack']);
        expect(found.fix).toContain('591.86');
        expect(run('nvidia-driver', { callStack: [frame(0, 'nvwgf2umx.dll+0A1B2C3')] })).toEqual([]);
        expect(run('nvidia-driver', { modules: ['nvwgf2umx.dll                                    0x7FFB73750000'] })).toEqual([]);
    });

    it('nvidia-driver-in-stack: NVIDIA driver files in the top half, not on the first line', () => {
        const [found] = run('nvidia-driver-in-stack', { callStack: [frame(0, 'SkyrimSE.exe+0021C44'), frame(1, 'nvgpucomp64.dll+0012345')] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'callStack', index: 1 }] });
        expect(run('nvidia-driver-in-stack', {
            firstLine: firstLineAt('nvgpucomp64.dll+0012345'),
            callStack: [frame(0, 'nvgpucomp64.dll+0012345')]
        })).toEqual([]);
        expect(run('nvidia-driver-in-stack', { modules: ['nvgpucomp64.dll                                  0x024320740000'] })).toEqual([]);
    });

    it('amd-driver: an AMD driver file on the first line', () => {
        const [found] = run('amd-driver', { firstLine: firstLineAt('atidxx64.dll+0012345') });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('Adrenalin');
        expect(run('amd-driver', { modules: ['atidxx64.dll                                     0x7FFB73750000'] })).toEqual([]);
    });

    it('amd-driver-in-stack: AMD driver files in the top half, not on the first line', () => {
        const [found] = run('amd-driver-in-stack', { stack: ['[RSP+68  ] 0x7FFA7CB22AF2     (void* -> amdxx64.dll+0102AF2\ttest r12, r12)'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'stack' }] });
        expect(run('amd-driver-in-stack', { firstLine: firstLineAt('amdxx64.dll+0102AF2'), callStack: [frame(0, 'amdxx64.dll+0102AF2')] })).toEqual([]);
    });

    it('dlaa-windows-24h2: KERNELBASE.dll on the first line and SkyrimUpscaler.dll in the call stack', () => {
        const [found] = run('dlaa-windows-24h2', {
            firstLine: firstLineAt('KERNELBASE.dll+010ED90', 'mov rax, [rcx]', 'EXCEPTION_ACCESS_VIOLATION'),
            callStack: [frame(0, 'KERNELBASE.dll+010ED90'), frame(1, 'SkyrimUpscaler.dll+0034567')]
        });
        expect(found).toMatchObject({ confidence: 'certain' });
        expect(places(found)).toEqual(['firstLine', 'callStack']);
        expect(found.fix).toContain('Nexus 130669');
        expect(run('dlaa-windows-24h2', { firstLine: firstLineAt('KERNELBASE.dll+010ED90'), callStack: [frame(0, 'KERNELBASE.dll+010ED90')] })).toEqual([]);
        expect(run('dlaa-windows-24h2', { callStack: [frame(0, 'SkyrimUpscaler.dll+0034567')] })).toEqual([]);
    });

    it('paid-fsr3-upscaler: nvngx_dlssg.dll on the first line', () => {
        const [found] = run('paid-fsr3-upscaler', { firstLine: firstLineAt('nvngx_dlssg.dll+0012345') });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'firstLine' }] });
        expect(run('paid-fsr3-upscaler', { callStack: [frame(0, 'nvngx_dlssg.dll+0012345')] })).toEqual([]);
    });

    it('upscaler-dll: Upscaler.dll or PDPerfPlugin.dll in the top third', () => {
        const [found] = run('upscaler-dll', { callStack: [frame(0, 'SkyrimSE.exe+0021C44'), frame(1, 'PDPerfPlugin.dll+0001234')] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(found.fix).toContain('Nexus 80343');
        // The stack is past the top third.
        expect(run('upscaler-dll', { stack: ['[RSP+68  ] 0x7FFA7CB22AF2     (void* -> SkyrimUpscaler.dll+0102AF2)'] })).toEqual([]);
    });

    it('dxgi-dll: dxgi.dll in the top half, as on a real log', () => {
        const [found] = run('dxgi-dll', { firstLine: firstLineAt('dxgi.dll+011563E') });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(run('dxgi-dll', { modules: ['dxgi.dll                                         0x7FFA7CA20000'] })).toEqual([]);
        const [real] = runOn('dxgi-dll', 'crash-2026-09-18-11-24-29.log');
        expect(real.evidence[0]).toMatchObject({ place: 'firstLine', text: expect.stringContaining('dxgi.dll+011563E') });
    });

    it('enb-d3d11: d3d11.dll on the first line', () => {
        const [found] = run('enb-d3d11', { game: '1.7.104', firstLine: firstLineAt('d3d11.dll+006881D') });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('1.7.104');
        expect(run('enb-d3d11', { callStack: [frame(0, 'd3d11.dll+006881D')] })).toEqual([]);
    });

    it('enb-shader-lighting: shader, lighting or ENB names in the top half; 12FDCC0 at the log\'s game version', () => {
        const [found] = run('enb-shader-lighting', { relevantObjects: ['RCX: (BSLightingShaderProperty*)'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(found.explain).toContain('BSLightingShaderProperty');
        expect(found.fix).not.toContain('Community Shaders');

        const [withCs] = run('enb-shader-lighting', { callStack: [frame(0, 'CommunityShaders.dll+0012345')] });
        expect(withCs.fix).toContain('Clear the Community Shaders cache');

        const [at1170] = run('enb-shader-lighting', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+12FDCC0') });
        expect(at1170).toMatchObject({ evidence: [{ place: 'firstLine' }], explain: expect.stringContaining('12FDCC0') });
        expect(run('enb-shader-lighting', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+131C0B0') })).toHaveLength(1);
        expect(run('enb-shader-lighting', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+12FDCC0') })).toEqual([]);
        expect(run('enb-shader-lighting', { modules: ['d3d11.dll                                        0x000180000000'] })).toEqual([]);
    });

    it('enb-water-boil-bubbles: MPSWaterBoilBubbles.nif loading, with Water for ENB in the load order', () => {
        const parts: LogParts = {
            modelRequests: [inProgress('meshes\\water\\MPSWaterBoilBubbles.nif')],
            plugins: [...BASE_PLUGINS, 'Water for ENB (Shades of Skyrim).esp']
        };
        const [found] = run('enb-water-boil-bubbles', parts);
        expect(found).toMatchObject({ confidence: 'possible' });
        expect(places(found)).toEqual(['modelRequests', 'plugins']);
        expect(found.fix).toContain('EnableVolumetricShadow');
        // Older logs name it among the relevant objects.
        expect(run('enb-water-boil-bubbles', { ...parts, modelRequests: undefined, relevantObjects: ['RBX: (BSFadeNode*) "MPSWaterBoilBubbles.nif"'] }))
            .toMatchObject([{ evidence: [{ place: 'relevantObjects' }, { place: 'plugins' }] }]);
        expect(run('enb-water-boil-bubbles', { ...parts, plugins: BASE_PLUGINS })).toEqual([]);
    });

    it('shadow-scene-node: 12FDD00 on the first line and BSCullingProcess in the call stack', () => {
        const culling = frame(1, 'SkyrimSE.exe+0D512F8', 'call rax |  BSCullingProcess::sub_140D51280_D512F8');
        const [found] = run('shadow-scene-node', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+12FDD00'), callStack: [frame(0, 'SkyrimSE.exe+12FDD00'), culling] });
        expect(found).toMatchObject({ confidence: 'possible' });
        expect(places(found)).toEqual(['firstLine', 'callStack']);
        expect(run('shadow-scene-node', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+131C0F0'), callStack: [culling] })).toHaveLength(1);
        expect(run('shadow-scene-node', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+12FDD00'), callStack: [frame(0, 'SkyrimSE.exe+12FDD00')] })).toEqual([]);
    });

    it('shadowrend: ccbgssse018-shadowrend.esl in the top quarter', () => {
        const [found] = run('shadowrend', { relevantObjects: ['RSP+58: (TESObjectWEAP*) "Shadowrend" [0xFE000801] ("ccBGSSSE018-shadowrend.esl")'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(found.fix).toContain('72 in-game hours');
        expect(run('shadowrend', { plugins: [...BASE_PLUGINS, 'ccBGSSSE018-shadowrend.esl'] })).toEqual([]);
    });

    it('efps-occlusion: BSGeometryListCullingProcess near the crash with eFPS loaded', () => {
        const parts: LogParts = {
            stack: ['[RSP+40  ] 0x2A7C0A0F980      (BSGeometryListCullingProcess*)'],
            plugins: [...BASE_PLUGINS, 'Occ_Skyrim_Tamriel.esp']
        };
        const [found] = run('efps-occlusion', parts);
        expect(found).toMatchObject({ confidence: 'possible', fix: expect.stringContaining('Nexus 54907') });
        expect(places(found)).toEqual(['plugins', 'stack']);
        expect(run('efps-occlusion', { ...parts, plugins: BASE_PLUGINS })).toEqual([]);
    });

    it('geometry-culling: BSGeometryListCullingProcess among the relevant objects or on the stack', () => {
        const [found] = run('geometry-culling', { relevantObjects: ['RSP+90: (BSGeometryListCullingProcess*)'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(found.fix).toContain('Nexus 149004');
        // Phostwood reads only those two sections.
        expect(run('geometry-culling', { callStack: [frame(0, 'SkyrimSE.exe+0021C44', 'BSGeometryListCullingProcess::sub_1')] })).toEqual([]);
    });

    it('mesh-issue: a model loading when the game crashed, a mesh crash place, or mesh names', () => {
        const [loading] = run('mesh-issue', { modelRequests: [inProgress('meshes\\CrashTest\\CrashTestStatue.nif'), '   12.903 ms ago  meshes\\clutter\\basket01.nif'] });
        expect(loading).toMatchObject({ confidence: 'likely', title: 'Crashed while loading the model CrashTestStatue.nif', evidence: [{ place: 'modelRequests', index: 0 }] });
        expect(loading.fix).toContain('meshes\\CrashTest\\CrashTestStatue.nif');
        expect(loading.fix).not.toContain('basket01');
        // A model loading that is no mesh is a lead only.
        expect(run('mesh-issue', { modelRequests: [inProgress('meshes\\actors\\character\\x.hkx')] })).toMatchObject([{ confidence: 'possible' }]);

        const [hex] = run('mesh-issue', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+12F5590') });
        expect(hex).toMatchObject({ confidence: 'likely', title: 'Mesh crash: a mesh failed to load or draw', evidence: [{ place: 'firstLine' }] });
        expect(run('mesh-issue', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+13156B0') })).toMatchObject([{ confidence: 'likely' }]);
        const [head] = run('mesh-issue', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+0132BEF') });
        expect(head.fix).toContain('FaceGen');

        const [named] = run('mesh-issue', { relevantObjects: ['RBX: (BSFadeNode*) "skeleton.nif"'] });
        expect(named).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(named.fix).toContain('skeleton.nif (a skeleton mesh is a less likely cause)');

        // Recent models alone are what any scene loads.
        expect(run('mesh-issue', { modelRequests: ['   12.903 ms ago  meshes\\clutter\\basket01.nif'] })).toEqual([]);

        const [real] = runOn('mesh-issue', 'synthetic-1.27-mesh.log');
        expect(real).toMatchObject({ confidence: 'likely', title: expect.stringContaining('CrashTestStatue.nif') });
        expect(real.evidence[0]).toMatchObject({ place: 'modelRequests', text: expect.stringContaining('(in progress)') });
    });

    it('texture-issue: texture names in the top half', () => {
        const [found] = run('texture-issue', { relevantObjects: ['RSP+40: (NiSourceTexture*) "textures\\armor\\steel.dds"'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(found.fix).toContain('textures\\armor\\steel.dds');

        const [withModel] = run('texture-issue', {
            relevantObjects: ['RSP+40: (BSShaderTextureSet*)'],
            modelRequests: [inProgress('meshes\\armor\\steel.nif')]
        });
        expect(withModel.explain).toContain('meshes\\armor\\steel.nif');
        expect(places(withModel)).toEqual(['modelRequests', 'relevantObjects']);

        expect(run('texture-issue', { modelRequests: [inProgress('meshes\\armor\\steel.nif')] })).toEqual([]);
    });

    it('dyndolod-3034: DynDOLOD DLL NG 3.0.34 installed and DynDOLOD near the crash', () => {
        const near: LogParts = {
            relevantObjects: ['RSP+58: (TESObjectREFR*) [0x0A000801] ("DynDOLOD.esm")'],
            stack: ['[RSP+60  ] 0x2A7C0A0F980      (char*) "DynDOLOD.DLL.NG"']
        };
        const [found] = run('dyndolod-3034', { ...near, sksePlugins: ['DynDOLOD.dll v3.0.34'] });
        expect(found).toMatchObject({ confidence: 'likely', fix: expect.stringContaining('Nexus 97720') });
        expect(places(found)).toEqual(['sksePlugins', 'relevantObjects', 'stack']);
        // DynDOLOD's own large-reference fix in a frame stands for the version.
        expect(run('dyndolod-3034', { ...near, callStack: [frame(0, 'DynDOLOD.dll+0012345', 'DynDOLOD::LargeREFRFix::Install')] })).toHaveLength(1);
        expect(run('dyndolod-3034', { ...near, sksePlugins: ['DynDOLOD.dll v3.0.36'] })).toEqual([]);
        expect(run('dyndolod-3034', { sksePlugins: ['DynDOLOD.dll v3.0.34'] })).toEqual([]);
    });

    it('dragons-eye-minimap: DragonsEyeMinimap.dll installed, and in the call stack or with its indicators', () => {
        const [found] = run('dragons-eye-minimap', { sksePlugins: ['DragonsEyeMinimap.dll v1.1.0'], callStack: [frame(0, 'DragonsEyeMinimap.dll+0012345')] });
        expect(found).toMatchObject({ confidence: 'likely', explain: expect.stringContaining('1.1.0') });
        expect(places(found)).toEqual(['callStack', 'sksePlugins']);
        expect(run('dragons-eye-minimap', { sksePlugins: ['DragonsEyeMinimap.dll v1.1.0'], relevantObjects: ['RSP+70: (HUDMenu*)'] }))
            .toMatchObject([{ explain: expect.stringContaining('HUDMenu') }]);
        expect(run('dragons-eye-minimap', { relevantObjects: ['RSP+70: (HUDMenu*)'] })).toEqual([]);
        expect(run('dragons-eye-minimap', { sksePlugins: ['DragonsEyeMinimap.dll v1.1.0'] })).toEqual([]);
    });

    it('snow-mods-incompatible: Simplicity of Snow and Better Dynamic Snow SE both loaded', () => {
        const [found] = run('snow-mods-incompatible', { plugins: [...BASE_PLUGINS, 'Simplicity of Snow.esp', 'Better Dynamic Snow SE.esp'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'plugins' }, { place: 'plugins' }] });
        expect(found.fix).toContain('Nexus 56235');
        expect(run('snow-mods-incompatible', { plugins: [...BASE_PLUGINS, 'Simplicity of Snow.esp'] })).toEqual([]);
    });

    it('hair-mod: hair names in the top half', () => {
        const [found] = run('hair-mod', {
            relevantObjects: ['RSP+58: (TESObjectARMA*) "HairFemaleNord05" [0x00012345] ("Skyrim.esm")'],
            sksePlugins: ['hdtSMP64.dll v2.0.0']
        });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'relevantObjects' }] });
        expect(found.explain).toContain('HairFemaleNord');
        expect(found.fix).toContain('Nexus 91616');
        expect(run('hair-mod', { relevantObjects: ['RSP+58: (TESObjectARMA*) "HairMaleOrc01"'] })).toMatchObject([{ fix: expect.not.stringContaining('91616') }]);
        expect(run('hair-mod', { plugins: [...BASE_PLUGINS, 'KS Hairdos.esp'] })).toEqual([]);
    });
});
