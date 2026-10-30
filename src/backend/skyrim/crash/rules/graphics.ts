import { placeRank, type CrashRule, type CrashView, type Evidence, type Finding, type Line, type View } from '../crashRules.js';
import type { GameAddress, GameAddressCode } from '../gameAddresses.js';

/**
 * Graphics: drivers, ENB, ReShade and upscalers, shaders and shadows, meshes and textures, and the mods
 * that draw the world. Ported from Phostwood's Skyrim Crash Log Analyzer
 * (github.com/Phostwood/crash-analyzer, GPL-3.0), diagnoses.js and its lists in crashIndicatorsMap.js.
 */

/** The lines of a view that pass a test, as evidence. */
function where(view: CrashView, name: View, test: (line: Line) => boolean): Evidence[] {
    return view.lines(name).filter(test).map(({ place, index, text }) => ({ place, index, text }));
}

/** Each code found in the views, with the lines it is on, in the list's order. */
function indicators(view: CrashView, names: readonly View[], codes: readonly string[]): { code: string; evidence: Evidence[] }[] {
    return codes
        .map(code => ({ code, evidence: names.flatMap(name => view.find(name, code)) }))
        .filter(found => found.evidence.length > 0);
}

/**
 * Evidence without repeats, best place first, cut so a rule with dozens of indicators stays readable;
 * the `main` lines a rule fired on stay ahead of the supporting ones and are never cut.
 */
function strongest(main: readonly Evidence[], support: readonly Evidence[] = [], max = 10): Evidence[] {
    const seen = new Set<string>();
    const fresh = (items: readonly Evidence[]) => [...items]
        .sort((a, b) => placeRank(a.place) - placeRank(b.place) || a.index - b.index)
        .filter(item => {
            const key = `${item.place}:${item.index}`;
            if (seen.has(key)) return false;
            seen.add(key);
            return true;
        });
    const first = fresh(main);
    return [...first, ...fresh(support)].slice(0, Math.max(max, first.length));
}

/** File names the view quotes, as Phostwood collects them: `"CrashTestStatue.nif"`, `` `meshes\x.nif` ``. */
function mentionedFiles(view: CrashView, name: View, extensions: string): string[] {
    const pattern = new RegExp(`[\`"]([^"\`]+\\.(?:${extensions}))[\`"]`, 'gi');
    return view.lines(name).flatMap(line => [...line.text.matchAll(pattern)].map(match => match[1].trim()));
}

/** Without repeats by file name, so `meshes\a\x.nif` and a bare `x.nif` count once; the first kept. */
function distinctFiles(files: readonly string[]): string[] {
    const seen = new Set<string>();
    return files.filter(file => {
        const key = (file.split(/[\\/]/).pop() ?? file).toLowerCase();
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
    });
}

const list = (items: readonly string[], max = 5): string =>
    items.length <= max ? items.join(', ') : `${items.slice(0, max).join(', ')} and ${items.length - max} more`;

const fileName = (path: string): string => path.split(/[\\/]/).pop() ?? path;

const cut = (title: string): string => title.length <= 70 ? title : `${title.slice(0, 67)}...`;

/**
 * 12FDCC0, which gameAddresses.ts lacks: 0x40 before 12FDD00 in the same function (Address Library ID
 * 448206), its offsets derived from 12FDD00's.
 */
const SHADER_12FDCC0: GameAddress = { id: 448206, delta: 0x90, offsets: { '1.6.1170': '12FDCC0', '1.7.99': '131BE50', '1.7.104': '131C0B0' } };

/** As CrashView.findAddress, for an address this file keeps itself. */
function findLocalAddress(view: CrashView, name: View, address: GameAddress): Evidence[] {
    const offset = view.gameVersion ? address.offsets[view.gameVersion] : undefined;
    return view.find(name,
        new RegExp(`-> ${address.id}\\+0x${address.delta.toString(16)}\\b`, 'i'),
        ...(offset ? [new RegExp(`SkyrimSE\\.exe\\+0*${offset}\\b`, 'i')] : []));
}

const LOOT = 'Sort the load order with LOOT, unless it is a premade mod list (Wabbajack, a Nexus collection).';

const DDU = 'If nothing helps, remove the driver with Display Driver Uninstaller (DDU) and install it fresh.';

const NVIDIA_FILES = ['nvwgf2umx.dll', 'nvlddmkm.sys', 'nvoglv32.dll', 'nvoglv64.dll', 'nvwgf2um.dll', 'nvapi64.dll', 'nvgpucomp64.dll'];
const AMD_FILES = ['amdxx64.dll', 'amdxc64.dll', 'atikmpag.sys', 'atikmdag.sys', 'atidxx64.dll', 'amd_ags_x64.dll', 'atig6pxx.dll'];

const NVIDIA_FIX = 'Update the NVIDIA graphics driver. On a 40 or 50 series card, turn Smooth Motion off in the NVIDIA app, or roll ' +
    'the driver back to 591.86 or lower. Reset any GPU overclock to stock speeds. If a graphics mod (ENB, ReShade, an ' +
    `upscaler) replaces a driver file named here, disable, reinstall or reconfigure it. ${DDU}`;
const AMD_FIX = 'Update the AMD graphics driver. Reset any GPU overclock, and any tuning profile in AMD Adrenalin, to stock. ' +
    'If a graphics mod (ENB, ReShade, an upscaler) replaces a driver file named here, disable, reinstall or reconfigure ' +
    `it. ${DDU}`;

/**
 * Phostwood's driver check runs twice: a driver file on the first line is the cause; one only further
 * down the top half is a lead.
 */
function driverRules(vendor: 'NVIDIA' | 'AMD', files: readonly string[], fix: string): CrashRule[] {
    const id = vendor.toLowerCase();
    const ported = `diagnoses.js analyze${vendor}DriverIssue`;
    return [
        {
            id: `${id}-driver`,
            ported,
            diagnose: view => {
                const file = files.find(code => view.has('firstLine', code));
                if (!file) return undefined;
                return {
                    title: `${vendor} graphics driver crashed (${file})`,
                    confidence: 'certain',
                    explain: `The crash happened in ${file}, a file of the ${vendor} graphics driver. The driver itself, a GPU ` +
                        'overclock, or a graphics mod hooking into the driver is the likely cause.',
                    fix,
                    evidence: strongest(view.find('firstLine', file), view.find('topHalf', ...files))
                };
            }
        },
        {
            id: `${id}-driver-in-stack`,
            ported,
            diagnose: view => {
                if (files.some(code => view.has('firstLine', code))) return undefined;
                const found = indicators(view, ['topHalf'], files);
                if (found.length === 0) return undefined;
                return {
                    title: `${vendor} graphics driver files near the crash`,
                    confidence: 'likely',
                    explain: `${vendor} graphics driver files (${list(found.map(item => item.code))}) are in the top of the log, ` +
                        'so the graphics driver may be involved.',
                    fix,
                    evidence: strongest([], found.flatMap(item => item.evidence))
                };
            }
        }
    ];
}

/** Phostwood's shader, lighting and ENB indicators, but 12FDCC0, which is matched as an address. */
const SHADER_CODES = [
    'AmbientColor', 'AmbientOcclusion', 'ApplyGameColorCorrection', 'BGSDecalNode', 'BSMultiBoundRoom', 'Boris Vorontsov',
    'BSDynamicTriShape', 'BSEffectShaderProperty', 'BSImagespaceShader', 'BSImagespaceShaderAlphaBlend', 'BSLightingShader',
    'BSLightingShaderMaterialBase', 'BSLightingShaderMaterialEnvmap', 'BSLightingShaderProperty', 'BSShader::',
    'BSShaderAccumulator', 'BSShaderTextureSet', 'BSTextureSet', 'BSUtilityShader', 'CellLighting', 'CommunityShaders.dll',
    'CSLighting', 'CSRendering', 'CSShader', 'd3d11.dll', 'd3d9.dll', 'd3dcompiler_47.dll', 'DeferredLighting',
    'DirectionalLight', 'DXGI.dll', 'DXGI_ERROR_DEVICE_REMOVED', 'ELFX - Exteriors.esp', 'ELFXEnhancer.esp', 'ENB Reference',
    'ENBEffect.fx', 'enbhelper.dll', 'enbhost.exe', 'enblocal.ini', 'enbseries.dll', 'enbseries.ini', 'ENBShaderCache', 'ENBSky',
    'ENBWaterShader', 'ImageSpace', 'LightingTemplate', 'Lux Via.esp', 'Lux.esp', 'NiAVObject', 'NiNode', 'PixelShader',
    'Refraction', 'RenderTarget', 'ShaderPipeline', 'ShadowSceneNode', 'SkyShader', 'VertexShader', 'WaterShader',
    'WindowShadersRT.esp', 'BSFadeNode', 'BSXFlags', 'BSWaterShaderMaterial', 'SubSurfaceScattering', 'VolumetricLighting',
    'ShadowMap', 'RenderTargetSize', 'TextureBlend', 'ENBLight', 'ComplexFire'
];

/** The mesh crash places Phostwood names, as the title and the explanation say them. */
const MESH_ADDRESSES: readonly { code: GameAddressCode; short: string; long: string }[] = [
    { code: '132BEF', short: 'a broken head mesh', long: 'a broken head mesh' },
    { code: '12FDD00', short: 'a corrupted .nif file', long: 'a corrupted .nif mesh file' },
    { code: '12F5590', short: 'a mesh failed to load or draw', long: 'a mesh that failed to load or draw, often an NPC face (FaceGen) but possibly any mesh' }
];

const MESH_CODES = [
    'bhkcollisionobject', 'bhkcompressedmeshshape', 'bsbloodsplattershader', 'bsdynamictrishape', 'bseffectshaderproperty',
    'bsfadenode', 'bsinvmarker', 'bslightingshaderproperty', 'bslodtrishape', 'bsmultiboundnode', 'bsmultiindextrishape',
    'bsordereddynamictrishape', 'bssegmentedtrishape', 'bssubindextrishape', 'bstrishape', 'nigeometry', 'nimaterialproperty',
    'ninode', 'niskininstance', 'nistringextradata', 'nitexturetransformcontroller', 'nitribasedgeom', 'nitrishape',
    'nitristrips', 'trishape', '.tri', '.nif', '.nif.dm', '.bto'
];

const TEXTURE_CODES = [
    'bsshadertextureset', 'nialphaproperty', 'nisourcetexture', 'compressedarchivestream', 'texture', '.dds', '.tga', '.bmp',
    '.btr', 'Texture not found'
];

const DRAGONS_EYE_CODES = ['BSImagespace', 'NiCamera', 'WorldRoot Camera', 'WorldRoot Node', 'Block (', 'HUDMenu'];

const HAIR_NAMES = [
    'HairMaleNord', 'HairFemaleNord', 'HairMaleImperial', 'HairFemaleImperial', 'HairMaleBreton', 'HairFemaleBreton',
    'HairMaleRedguard', 'HairFemaleRedguard', 'HairMaleElf', 'HairFemaleElf', 'HairMaleKhajiit', 'HairFemaleKhajiit',
    'HairMaleOrc', 'HairFemaleOrc', 'HairFemale', 'HairMale', 'KS Hairdos.esp', 'ApachiiSkyHair.esm', 'SGHairPackAIO.esp',
    'Dint999HairPack.esp', 'SaltAndWind.esp', 'StraightHairRetexture.esp', 'BedHead.esp', 'KaliliesBrows.esp',
    'HHairstyles.esp', 'LovelyHairstyles.esp', 'SuperiorLoreFriendlyHair.esp', 'HallgarthsAdditionalHair.esp',
    'HG Hairdos 2.esp', 'Hairdo'
];
const PHYSICS_NAMES = ['HDT-SMP', 'hdtSMP', 'CBPC', 'Physics'];

/** The model file a crash came in the middle of loading (Crash Logger 1.26 on), and its line. */
function modelInProgress(view: CrashView): { path: string; evidence: Evidence[] } | undefined {
    const model = view.log.models.find(entry => entry.inProgress);
    return model ? { path: model.path, evidence: view.find('modelRequests', /\(in progress\)\s*$/i) } : undefined;
}

export const graphicsRules: CrashRule[] = [
    ...driverRules('NVIDIA', NVIDIA_FILES, NVIDIA_FIX),
    ...driverRules('AMD', AMD_FILES, AMD_FIX),
    {
        id: 'dlaa-windows-24h2',
        ported: 'diagnoses.js analyzeWin24H2UpscalerCrash',
        diagnose: view => {
            const kernelBase = view.find('firstLine', 'KERNELBASE.dll');
            const upscaler = view.find('callStack', 'SkyrimUpscaler.dll');
            if (kernelBase.length === 0 || upscaler.length === 0) return undefined;
            return {
                title: 'Upscaler DLAA crash in KERNELBASE.dll (Windows 11 24H2)',
                confidence: 'certain',
                explain: 'The crash is in KERNELBASE.dll with SkyrimUpscaler.dll in the call stack. Windows 11 24H2 broke DLAA ' +
                    'in older versions of PureDark\'s upscaler; his newer, paid versions fix it.',
                fix: 'Switch to ENB Anti-Aliasing - AMD FSR 3.1 - NVIDIA DLAA (Nexus 130669), or try other versions of Skyrim ' +
                    'Upscaler - DLSS FSR2 XeSS (Nexus 80343). Or use TAA instead: disable the mods Upscaler Base Plugin and ' +
                    'Skyrim Upscaler, and set bUseTAA=1 in SkyrimPrefs.ini.',
                evidence: strongest(kernelBase, upscaler)
            };
        }
    },
    {
        id: 'paid-fsr3-upscaler',
        ported: 'diagnoses.js analyzePaidFSR3UpstalerIssue',
        diagnose: view => {
            const evidence = view.find('firstLine', 'nvngx_dlssg.dll');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Frame generation crash in nvngx_dlssg.dll',
                confidence: 'certain',
                explain: 'The crash is in nvngx_dlssg.dll, NVIDIA\'s frame generation file. It is often the graphics driver or ' +
                    'PureDark\'s paid upscaler (FG Build Alpha 03 and later).',
                fix: 'Update the NVIDIA graphics driver. Check the upscaler\'s settings: a wrong upscale type causes this. ' +
                    'Frame generation needs an NVIDIA RTX 40 series card or newer, not AMD, GTX or an older RTX; turn it off ' +
                    `otherwise. Reset any GPU overclock to stock speeds. ${DDU}`,
                evidence
            };
        }
    },
    {
        id: 'upscaler-dll',
        ported: 'diagnoses.js analyzeUpscalerIssues',
        diagnose: view => {
            const evidence = view.find('topThird', 'upscaler.dll', 'pdperfplugin.dll');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Upscaler mod near the crash',
                confidence: 'possible',
                explain: 'A file of PureDark\'s upscaler mods (Upscaler.dll or PDPerfPlugin.dll) is near the top of the log, ' +
                    'so the upscaler may have caused the crash.',
                fix: 'Make sure the upscaler version fits your graphics card and that every install step was followed; ' +
                    'SkyrimUpscaler.log tells more. Update the upscaler, such as Skyrim Upscaler - DLSS FSR2 XeSS ' +
                    '(Nexus 80343). To test, disable it and play on.',
                evidence: strongest([], evidence)
            };
        }
    },
    {
        id: 'dxgi-dll',
        ported: 'diagnoses.js analyzeDXGIIssues',
        diagnose: view => {
            const evidence = view.find('topHalf', 'dxgi.dll');
            if (evidence.length === 0) return undefined;
            return {
                title: 'dxgi.dll near the crash: ReShade or an upscaler',
                confidence: 'possible',
                explain: 'dxgi.dll is in the top of the log. ReShade and PureDark\'s upscaler each bring their own dxgi.dll, ' +
                    'and the two can clash.',
                fix: 'If you updated ReShade lately, it may not work with the dxgi.dll of PureDark\'s upscaler; newer ' +
                    'upscaler versions need a matching dxgi.dll from the upscaler\'s author. To get the old dxgi.dll back, ' +
                    'reinstall ReShade. To test, toggle ReShade off in game with the Delete key.',
                evidence: strongest([], evidence)
            };
        }
    },
    {
        id: 'enb-d3d11',
        ported: 'diagnoses.js checkENBIssue',
        diagnose: view => {
            const evidence = view.find('firstLine', 'd3d11.dll');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Crash in d3d11.dll: ENB or ReShade',
                confidence: 'likely',
                explain: 'The crash is in d3d11.dll, the file ENB and ReShade hook into the game through, so it is a ' +
                    'graphics crash.',
                fix: 'If you installed or updated an ENB or ReShade lately, make sure the ENB binary fits your game version' +
                    `${view.gameVersion ? ` (${view.gameVersion})` : ''} and the preset fits the binary (see the preset's ` +
                    'page), then reinstall it. With a premade mod list (Wabbajack, a Nexus collection), go back to the ENB ' +
                    'the list came with.',
                evidence
            };
        }
    },
    {
        id: 'enb-shader-lighting',
        ported: 'diagnoses.js analyzeENBShaderLightingIssues',
        diagnose: view => {
            const found = indicators(view, ['topHalf'], SHADER_CODES);
            const address = findLocalAddress(view, 'topHalf', SHADER_12FDCC0);
            if (address.length > 0) found.unshift({ code: '12FDCC0', evidence: address });
            if (found.length === 0) return undefined;
            const communityShaders = view.has('topHalf', 'communityshaders.dll');
            return {
                title: 'Shader, lighting or ENB code near the crash',
                confidence: 'possible',
                explain: `Lighting, shadow, shader or ENB names are near the crash (${list(found.map(item => item.code))}). ` +
                    'The crash showed there, but the cause is often elsewhere: conflicting cells, broken textures or ' +
                    'meshes, or memory.',
                fix: (communityShaders ? 'Clear the Community Shaders cache (press End in game, then Clear Shader Cache) ' +
                    'and restart the game. ' : '') +
                    'Look for mods that change the same places, and for patches between lighting mods and location ' +
                    'mods. Check that the ENB preset fits your weather mod. To test without ENB, rename d3d11.dll to ' +
                    'd3d11.dll.backup. Update the graphics driver.',
                evidence: strongest([], found.flatMap(item => item.evidence))
            };
        }
    },
    {
        id: 'enb-water-boil-bubbles',
        ported: 'diagnoses.js checkEnbWaterBoilBubblesIssue',
        diagnose: view => {
            // Crash Logger 1.26 on names the model being loaded in sections of their own.
            const model = [...view.find('topHalf', 'mpswaterboilbubbles.nif'), ...view.find('modelRequests', 'mpswaterboilbubbles.nif'),
                ...view.find('modelPaths', 'mpswaterboilbubbles.nif')];
            const waterForEnb = view.find('bottomHalf', 'water for enb');
            if (model.length === 0 || waterForEnb.length === 0) return undefined;
            return {
                title: 'ENB water feature loads a mesh Water for ENB lacks',
                confidence: 'possible',
                explain: 'The game tried to load MPSWaterBoilBubbles.nif, which some ENB water features ask for but Water ' +
                    'for ENB (Nexus 37061) does not include.',
                fix: 'In enbseries.ini, turn off these Water settings: EnableVolumetricShadow, EnableCloudsShadow and ' +
                    'EnableSelfReflection (set each to false).',
                evidence: strongest([...model, ...waterForEnb])
            };
        }
    },
    {
        id: 'shadow-scene-node',
        ported: 'diagnoses.js analyzeShadowSceneIssues',
        diagnose: view => {
            const firstLine = view.findAddress('firstLine', '12FDD00');
            // Older Crash Loggers named the frame BSCullingProcess::unk_D51280+78; newer ones name the class too.
            const culling = view.find('callStack', 'BSCullingProcess');
            if (firstLine.length === 0 || culling.length === 0) return undefined;
            return {
                title: 'Shadow scene node crash',
                confidence: 'possible',
                explain: 'The game crashed while working out which shadows and objects to draw (BSCullingProcess). Such ' +
                    'crashes often go away once the area resets.',
                fix: 'Load an earlier save and stay away from the crash area for a few in-game days, then come back. If ' +
                    'it repeats, look at the other findings for the cause.',
                evidence: strongest(firstLine, culling)
            };
        }
    },
    {
        id: 'shadowrend',
        ported: 'diagnoses.js analyzeShadowrend',
        diagnose: view => {
            const evidence = view.find('topQuarter', 'ccbgssse018-shadowrend.esl');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Shadowrend (ccbgssse018-shadowrend.esl) near the crash',
                confidence: 'possible',
                explain: 'The Creation Club plugin ccbgssse018-shadowrend.esl is near the crash. Shadowrend is often in crash ' +
                    'logs without being the cause.',
                fix: 'Load an earlier save in another area and play 72 in-game hours away from the crash area (waiting or ' +
                    'sleeping does not count), so the area resets. Avoid playing on from the save that crashed, which can ' +
                    `make the crashes more frequent. ${LOOT}`,
                evidence: strongest([], evidence)
            };
        }
    },
    {
        id: 'efps-occlusion',
        ported: 'diagnoses.js checkPossibleEfpsIssue',
        diagnose: view => {
            const culling = [...view.find('relevantObjects', 'BSGeometryListCullingProcess'), ...view.find('stack', 'BSGeometryListCullingProcess')];
            const efps = view.find('bottomHalf', 'occ_skyrim_tamriel.esp');
            if (culling.length === 0 || efps.length === 0) return undefined;
            return {
                title: 'eFPS occlusion data near the crash',
                confidence: 'possible',
                explain: 'The game crashed while working out which objects to hide (BSGeometryListCullingProcess), and ' +
                    'occ_skyrim_tamriel.esp of eFPS - Exterior FPS boost is loaded.',
                fix: 'Disable eFPS - Exterior FPS boost (Nexus 54907) and test without it. If that helps, look for patches ' +
                    'between eFPS and the other mods that change the same places.',
                evidence: strongest(efps, culling)
            };
        }
    },
    {
        id: 'geometry-culling',
        ported: 'diagnoses.js checkPossibleGeometryCullingIssue',
        diagnose: view => {
            const evidence = [...view.find('relevantObjects', 'BSGeometryListCullingProcess'), ...view.find('stack', 'BSGeometryListCullingProcess')];
            if (evidence.length === 0) return undefined;
            return {
                title: 'Crash while the game worked out which objects to hide',
                confidence: 'possible',
                explain: 'BSGeometryListCullingProcess is near the crash: the game was deciding which objects to draw or ' +
                    'hide. A known trigger is opening the map where mods add too many occlusion planes.',
                fix: 'If it crashes when opening the map in one area, disable mods that add occlusion planes there: eFPS - ' +
                    'Exterior FPS boost (Nexus 54907), Hyperspecific Occlusion Addon (Nexus 149004), Facelight Plus ' +
                    '(Nexus 16736), Capital Whiterun Expansion (Nexus 37982) or its Lite version (Nexus 112964). Lighting ' +
                    'mods such as Lux or ELFX are suspects too. Check them for updates and patches.',
                evidence: strongest([], evidence)
            };
        }
    },
    {
        id: 'mesh-issue',
        ported: 'diagnoses.js analyzeMeshIssues',
        diagnose: view => {
            const hex = MESH_ADDRESSES.map(address => ({ ...address, evidence: view.findAddress('firstLine', address.code) }))
                .find(address => address.evidence.length > 0);
            const loading = modelInProgress(view);
            const found = indicators(view, ['topHalf', 'modelPaths'], MESH_CODES);
            if (!hex && !loading && found.length === 0) return undefined;

            const files = distinctFiles([
                ...(loading ? [loading.path] : []),
                ...view.log.models.filter(model => model.section === 'modelPaths').map(model => model.path),
                ...mentionedFiles(view, 'topHalf', 'nif(?:\\.dm)?|tri|bto|bsa')
            ]);
            const skeleton = found.some(item => item.code === 'ninode');
            const title = loading ? `Crashed while loading the model ${fileName(loading.path)}`
                : hex ? `Mesh crash: ${hex.short}` : 'Possible mesh problem near the crash';
            const explain = [
                ...(hex ? [`Where the game crashed points to ${hex.long}.`] : []),
                ...(loading ? [`The game crashed in the middle of loading ${loading.path}.`] : []),
                ...(found.length > 0 ? [`Mesh names are near the crash: ${list(found.map(item => item.code))}.`] : [])
            ].join(' ');
            const skeletonFile = files.slice(0, 3).some(file => /skeleton/i.test(fileName(file)));
            const fix = [
                `Find which mod has ${files.length > 0 ? list(files, 3) : 'the meshes named in the log'}` +
                `${skeletonFile ? ' (a skeleton mesh is a less likely cause)' : ''}, download it again and reinstall it, ` +
                'and check it for updates and patches; the same mesh in later crash logs makes it the cause.',
                'If that does not help, repair the mesh with Cathedral Assets Optimizer (Nexus 23316) or SSE NIF Optimizer ' +
                '(Nexus 4089).',
                ...(hex?.code === '132BEF' ? ['For a head mesh, reinstall or update the mod of the NPCs named near the crash, ' +
                    'or regenerate their FaceGen in the Creation Kit.'] : []),
                ...(skeleton ? ['NiNode points to the skeleton: make sure one skeleton mod is installed and not overwritten.'] : [])
            ].join(' ');
            const confidence: Finding['confidence'] = hex || (loading && /\.nif$/i.test(loading.path)) ? 'likely' : 'possible';
            return {
                title: cut(title),
                confidence,
                explain,
                fix,
                evidence: strongest([...(hex?.evidence ?? []), ...(loading?.evidence ?? [])], found.flatMap(item => item.evidence))
            };
        }
    },
    {
        id: 'texture-issue',
        ported: 'diagnoses.js analyzeTextureIssues',
        diagnose: view => {
            // Phostwood knows no crash place that is a texture's, so this stays a lead.
            const found = indicators(view, ['topHalf'], TEXTURE_CODES);
            if (found.length === 0) return undefined;
            const loading = modelInProgress(view);
            const files = distinctFiles(mentionedFiles(view, 'topHalf', 'dds|btr|tga|bmp|bsa'));
            return {
                title: 'Possible texture problem near the crash',
                confidence: 'possible',
                explain: `Texture names are near the crash (${list(found.map(item => item.code))}).` +
                    (loading ? ` The game was loading ${loading.path} when it crashed; its textures are suspects too.` : ''),
                fix: `Find which mod has ${files.length > 0 ? list(files, 3) : 'the textures named in the log'}; if later crash ` +
                    'logs name the same ones, download that mod again and reinstall it, or disable it to test. If that does ' +
                    'not help, repair the textures with Cathedral Assets Optimizer (Nexus 23316). If video memory runs short, ' +
                    'use 1K or 2K textures instead of 4K.',
                evidence: strongest(loading?.evidence ?? [], found.flatMap(item => item.evidence))
            };
        }
    },
    {
        id: 'dyndolod-3034',
        ported: 'diagnoses.js analyzeDynDOLODv3034Issue',
        diagnose: view => {
            const version = where(view, 'bottomHalf', line => line.lower.includes('dyndolod.dll') && line.text.includes('3.0.34'));
            const largeRefrFix = view.find('all', 'dyndolod::largerefrfix');
            if (version.length === 0 && largeRefrFix.length === 0) return undefined;
            const ng = view.find('topHalf', 'dyndolod.dll.ng');
            const esm = view.find('topHalf', 'dyndolod.esm');
            if (ng.length === 0 || esm.length === 0) return undefined;
            return {
                title: 'DynDOLOD DLL NG 3.0.34 crash',
                confidence: 'likely',
                explain: (version.length > 0 ? 'DynDOLOD DLL NG 3.0.34 (Alpha-34) is installed' : 'DynDOLOD\'s LargeREFRFix is ' +
                    'in the log') + ', and DynDOLOD is near the crash. That version is known to be unstable.',
                fix: 'Update DynDOLOD DLL NG and Scripts (Nexus 97720) to Alpha-36, or go back to Alpha-33; Alpha-36 needs no ' +
                    'new DynDOLOD run. Remove the Alpha-34 files, and let DynDOLOD DLL NG and Scripts overwrite DynDOLOD ' +
                    'Resources SE.',
                evidence: strongest([...version, ...largeRefrFix], [...ng, ...esm])
            };
        }
    },
    {
        id: 'dragons-eye-minimap',
        ported: 'diagnoses.js analyzeDragonsEyeMinimapIssue',
        diagnose: view => {
            const installed = view.find('bottomHalf', 'dragonseyeminimap.dll');
            if (installed.length === 0) return undefined;
            const inCallStack = view.find('callStack', 'dragonseyeminimap.dll');
            const found = indicators(view, ['topHalf'], DRAGONS_EYE_CODES);
            if (inCallStack.length === 0 && found.length === 0) return undefined;
            const version = view.log.sksePlugins.find(plugin => plugin.name.toLowerCase() === 'dragonseyeminimap.dll')?.version;
            return {
                title: 'Dragon\'s Eye Minimap crash',
                confidence: 'likely',
                explain: `Dragon's Eye Minimap (DragonsEyeMinimap.dll${version ? ` ${version}` : ''}) is installed, and ` +
                    (inCallStack.length > 0 ? 'it is in the call stack.' :
                        `code it draws with is near the crash (${list(found.map(item => item.code))}).`),
                fix: 'Turn the minimap off with its hotkey (L by default) and play on until you leave this cell. If the ' +
                    'crash keeps coming back, update Dragon\'s Eye Minimap or disable it; the problem is still there in ' +
                    'version 1.1.',
                evidence: strongest([...inCallStack, ...installed], found.flatMap(item => item.evidence))
            };
        }
    },
    {
        id: 'snow-mods-incompatible',
        ported: 'diagnoses.js checkSnowModsCompatibility',
        diagnose: view => {
            const betterDynamicSnow = view.find('all', 'better dynamic snow se.esp');
            const simplicityOfSnow = view.find('all', 'simplicity of snow.esp');
            if (betterDynamicSnow.length === 0 || simplicityOfSnow.length === 0) return undefined;
            return {
                title: 'Simplicity of Snow and Better Dynamic Snow SE together',
                confidence: 'likely',
                explain: 'Both snow mods are loaded. They change the same snow and are not compatible; together they ' +
                    'usually lead to crashes.',
                fix: 'Disable one of them, with the mods that depend on it: Simplicity of Snow (Nexus 56235) or Better ' +
                    'Dynamic Snow SE (Nexus 9121). Simplicity of Snow is the more recently updated one.',
                evidence: strongest([...simplicityOfSnow, ...betterDynamicSnow])
            };
        }
    },
    {
        id: 'hair-mod',
        ported: 'diagnoses.js checkHairModCompatibility',
        diagnose: view => {
            const found = indicators(view, ['topHalf'], HAIR_NAMES);
            if (found.length === 0) return undefined;
            const dynamicShape = view.has('topHalf', 'NiRTTI_BSDynamicTriShape');
            const physics = view.has('all', ...PHYSICS_NAMES);
            return {
                title: 'Hair mod near the crash',
                confidence: 'likely',
                explain: `Hair names are near the crash: ${list(found.map(item => item.code))}.` +
                    (dynamicShape ? ' NiRTTI_BSDynamicTriShape points to a dynamic hair mesh.' : ''),
                fix: 'Vanilla Hair Remake (Nexus 63979) 1.0.2 and 1.0.3 are a common cause: update or disable it. Update the ' +
                    'hair mods and check their pages for patches. ' +
                    (physics ? 'Make sure the physics mods fit the hair mods and the game version, and install SMP-NPC crash ' +
                        'fix (Nexus 91616), which fixes a crash when NPCs with SMP hair load. ' : '') +
                    'If it goes on, disable hair mods one at a time to find the one.',
                evidence: strongest([], found.flatMap(item => item.evidence))
            };
        }
    }
];
