import type { CrashRule, CrashView, Evidence, Line } from '../crashRules.js';

/**
 * Memory, the system and its programs. Ported from Phostwood's Skyrim Crash Log Analyzer
 * (github.com/Phostwood/crash-analyzer, GPL-3.0), diagnoses.js and analyzeLog.js.
 */

/** Phostwood's page-file advice, which several rules give. */
export const PAGEFILE = 'Set the Windows page file to a fixed 40 GB (minimum and maximum), unless the mod list asks for another size.';

/** Phostwood's thresholds (utils.js): free RAM and video memory under these, in GB, is low or critical. */
const LOW_FREE_RAM = 2;
const CRITICAL_FREE_RAM = 1;
const LOW_FREE_VRAM = 1;
const CRITICAL_FREE_VRAM = 0.5;

/** crashIndicatorsMap.js memoryIssues. Its other hex code, D6DDDA, has a rule of its own. */
const MEMORY_HEX_CODES: readonly [string, string][] = [['0xc0000005', 'a Windows memory access violation']];
const MEMORY_CODES: readonly [string, string][] = [
    ['memory allocation failed', 'a failed memory allocation'],
    ['out of memory', 'the system ran out of memory'],
    ['heap corruption', 'memory heap corruption'],
    ['bad_alloc', 'a failed memory allocation'],
    ['no_alloc', 'memory allocation not possible'],
    ['tbbmalloc.dll', 'the memory allocator from SSE Engine Fixes part 2, a less likely culprit'],
    ['virtual memory', 'a virtual memory problem'],
    ['memory leak', 'a memory leak'],
    ['stack overflow', 'a stack overflow'],
    ['buffer overflow', 'a buffer overflow'],
    ['memory fragmentation', 'memory fragmentation']
];

/** crashIndicatorsMap.js vcRuntimeIssues. */
const VC_RUNTIME_DLLS = ['msvcp100.dll', 'msvcp110.dll', 'msvcp110_win.dll', 'msvcp140.dll', 'vcruntime140.dll', 'ucrtbase.dll'];

/** antivirusSignatures.js. Windows Security is not in it: it is the one recommended. */
const ANTIVIRUS: Readonly<Record<string, readonly string[]>> = {
    '360 Total Security': ['360safe.dll'],
    'AhnLab V3': ['v3svc.dll'],
    'AOL Active Virus Shield': ['aolav.dll'],
    'ArcaBit': ['arcabitengine.dll'],
    'AVG': ['avgui.dll'],
    'Avast': ['avast.dll', 'aswEngineA.dll'],
    'Avira/Surfshark': ['avamsi.dll', 'avamsicli.dll', 'avcuf32.dll'],
    'Baidu Antivirus': ['bdhsvc.dll'],
    'Bitdefender': ['bdcore.dll', 'avc3.dll', 'atcuf64.dll', 'antimalware_provider64.dll', 'bdhkm64.dll'],
    'BullGuard': ['bullguard.dll', 'bgadriver.dll'],
    'ClamAV': ['libclamav.dll'],
    'Comodo': ['cavwp.dll'],
    'Dr.Web': ['dwengine.dll', 'drwebsp.dll'],
    'Emsisoft': ['a2cmd.dll'],
    'ESET': ['egui.dll', 'eks.dll'],
    'F-Secure': ['fsaus.dll', 'fsdfw.dll'],
    'Fortinet': ['fnetwizard.dll', 'fortinet_krn.dll'],
    'G Data': ['gdata_avi.dll', 'gdavflt.dll'],
    'K7 AntiVirus': ['k7sentry.dll', 'k7avgent.dll'],
    'Kaspersky': ['kavprot.dll', 'klif.dll', 'com_antivirus.dll'],
    'Malwarebytes': ['mbamswissarmy.dll'],
    'McAfee': ['mcafee.dll', 'mfehidk.dll'],
    'Norton': ['npsystem.dll', 'symcorpui.dll'],
    'Nscore': ['nscore64.dll'],
    'Panda': ['panda_url_filtering.dll', 'pavbootstartdriver.dll'],
    'Quick Heal': ['qhsafemain.dll', 'qhwtp.dll'],
    'Rising': ['ravmon.dll', 'rfapi.dll'],
    'Sophos': ['sophos_detoured.dll', 'savapi.dll', 'sav.dll'],
    'SurfShark': ['ssav.dll', 'surfshark.dll', 'sscore.dll', 'sskill.dll', 'ssprotect.dll', 'ssfirewall.dll', 'ssantivirus.dll',
        'ssmonitor.dll', 'ssscanner.dll', 'surfshark'],
    'Symantec': ['symnav.dll', 'symcheck.dll'],
    'TotalDefense': ['sbamsvc.dll', 'tdrtac.dll'],
    'Trend Micro': ['tmntsrv.dll', 'tmwlutil.dll', 'vsapi32.dll'],
    'VirusBuster': ['vba32core.dll', 'vbadeint.dll'],
    'Webroot': ['wrsvc.dll', 'wrsa.dll', 'wrusr.dll', 'wrlogon.dll', 'wrweb.dll', 'webroot'],
    'ZoneAlarm': ['zavcore.dll', 'zlclient.dll']
};
const WINDOWS_SECURITY = ['mpsvc.dll', 'mpclient.dll'];

/** overlaySignatures.js. Steam's overlay is in nearly every log, so it counts only in the crash itself. */
const OVERLAYS: Readonly<Record<string, readonly string[]>> = {
    'Discord': ['discordhook64.dll'],
    'Overwolf': ['overwolf.dll', 'owclient.dll', 'ow-graphics-hook64.dll', 'owutils.dll', 'owexplorer.dll'],
    'Raptr': ['raptr64.dll'],
    'Fraps': ['fraps.dll'],
    'NVIDIA GeForce Experience': ['nv_ags.dll'],
    'RivaTuner Statistics Server': ['rtsshdrs64.dll'],
    'MSI Afterburner': ['msiafterburner.dll']
};
const NOT_OVERLAYS = new Set(['overlayfix.dll', 'gameoverlayrenderer64.dll']);

/** checkPossibleFilesystemIssue's indicators, with the name a finding gives each. */
const FILE_ACCESS: readonly [string, string][] = [
    ['filesystem_error', 'filesystem_error'],
    ['ifilestream', 'IFileStream'],
    ['savestoragewrapper', 'SaveStorageWrapper'],
    ['savefilehandlereaderwriter', 'SaveFileHandleReaderWriter'],
    ['win32filetype', 'Win32FileType'],
    ['sksepersistentobjectstorage', 'SKSEPersistentObjectStorage'],
    ['onedrive', 'a OneDrive path'],
    ['documents\\my games\\skyrim', 'the Documents\\My Games\\Skyrim folder'],
    ['documents/my games/skyrim', 'the Documents\\My Games\\Skyrim folder']
];

/**
 * analyzeFirstLine passes over the game itself, files too basic to blame, and files other rules diagnose
 * better: the NVIDIA, AMD and Engine Fixes files of crashIndicatorsMap.js among them.
 */
const FIRST_LINE_IGNORED = new Set([
    'dawnguard.esm', 'dragonborn.esm', 'skyrimse.exe', 'skyrim.esm', 'skyrimvr.exe', 'vcruntime140.dll', 'ntdll.dll',
    'jcontainers64.dll', 'skee64.dll', 'kernelbase.dll', 'dbsksefunctions.dll', 'nvngx_dlssg.dll', 'xaudio2_7.dll', 'd3d11.dll',
    'nvwgf2umx.dll', 'nvlddmkm.sys', 'nvoglv32.dll', 'nvoglv64.dll', 'nvwgf2um.dll', 'nvapi64.dll', 'nvgpucomp64.dll',
    'amdxx64.dll', 'amdxc64.dll', 'atikmpag.sys', 'atikmdag.sys', 'atidxx64.dll', 'amd_ags_x64.dll', 'atig6pxx.dll',
    'enginefixes.dll', 'engine_fixes.dll', 'd3dx9_42.dll', 'tbbmalloc.dll', 'tbb.dll'
]);
/** Frameworks many mods build on, rarely at fault themselves; SKSE's own DLL for any game version. */
const FIRST_LINE_FRAMEWORKS = new Set(['po3_papyrusextender.dll', 'papyrustweaks.dll', 'fiss.dll']);
const SKSE_DLL = /^(skse64|sksevr)_\d+_\d+_\d+\.dll$/i;
const FIRST_LINE_FILE = /\b([^/\\\s]+\.(?:esm|exe|esp|esl|dll|pex|skse|skse64))\b/i;

const toEvidence = ({ place, index, text }: Line): Evidence => ({ place, index, text });

/** Each line once, in the order found. */
function unique(...lists: Evidence[][]): Evidence[] {
    const seen = new Set<string>();
    return lists.flat().filter(item => {
        const key = `${item.place}:${item.index}`;
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
    });
}

/** A file's name as a whole name, not the tail of a longer one: `eks.dll` is not in `weeks.dll`. */
const fileName = (name: string): RegExp => new RegExp(`(?<![\\w.-])${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`, 'i');

/** The log's game version is `min` or later; false when the log does not say. */
function versionAtLeast(version: string | undefined, min: readonly number[]): boolean {
    if (!version) return false;
    const parts = version.split('.').map(Number);
    for (let i = 0; i < min.length; i++) {
        const part = parts[i] ?? 0;
        if (part !== min[i]) return part > min[i];
    }
    return true;
}

const gb = (value: number) => `${Number(value.toFixed(1))} GB`;

const list = (items: readonly string[]) =>
    items.length <= 1 ? items.join('') : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;

/** RAM and video memory in use, from SYSTEM SPECS, as a sentence; empty when the log has neither. */
function memoryStatus(view: CrashView): string {
    const { physicalUsedGb, physicalTotalGb, gpuUsedGb, gpuTotalGb } = view.log.memory;
    const parts = [
        ...(physicalUsedGb !== undefined && physicalTotalGb ? [`RAM ${gb(physicalUsedGb)} of ${gb(physicalTotalGb)}`] : []),
        ...(gpuUsedGb !== undefined && gpuTotalGb ? [`video memory ${gb(gpuUsedGb)} of ${gb(gpuTotalGb)}`] : [])
    ];
    return parts.length ? ` In use at the crash: ${parts.join(', ')}.` : '';
}

const memoryLine = (view: CrashView, key: 'PHYSICAL' | 'GPU'): Evidence[] => view.find('systemSpecs', new RegExp(`^${key} MEMORY:`, 'i'));

export const memoryRules: CrashRule[] = [
    {
        id: 'd6ddda-texture-or-memory',
        ported: 'diagnoses.js checkForD6dddaAdvancedVersion',
        diagnose: view => {
            const evidence = view.findAddress('firstLine', 'D6DDDA');
            if (evidence.length === 0) return undefined;
            return {
                title: 'D6DDDA crash: a broken texture or mesh, or memory ran short',
                confidence: 'likely',
                explain: 'The game failed where it loads textures and meshes into memory. Most often a .dds texture ' +
                    '(less often a .nif mesh) is corrupt; otherwise the system ran short of memory.',
                fix: 'Update SSE Engine Fixes (Nexus 17230) to 7.0.18 or later, which prevents many of these. If later ' +
                    'crash logs name the same texture or mesh, download its mod again and reinstall it. Otherwise close ' +
                    `programs running beside the game and undo overclocks. ${PAGEFILE}`,
                evidence
            };
        }
    },
    {
        id: 'high-memory-usage',
        ported: 'diagnoses.js checkForHighMemoryUsage',
        diagnose: view => {
            const { physicalUsedGb, physicalTotalGb, gpuUsedGb, gpuTotalGb } = view.log.memory;
            const freeRam = physicalUsedGb !== undefined && physicalTotalGb !== undefined ? physicalTotalGb - physicalUsedGb : undefined;
            const freeVram = gpuUsedGb !== undefined && gpuTotalGb !== undefined ? gpuTotalGb - gpuUsedGb : undefined;
            const lowRam = freeRam !== undefined && freeRam < LOW_FREE_RAM;
            const lowVram = freeVram !== undefined && freeVram < LOW_FREE_VRAM;
            if (!lowRam && !lowVram) return undefined;
            const critical = (freeRam !== undefined && freeRam < CRITICAL_FREE_RAM) || (freeVram !== undefined && freeVram < CRITICAL_FREE_VRAM);
            const free = [...(lowRam ? [`${gb(freeRam)} of RAM`] : []), ...(lowVram ? [`${gb(freeVram)} of video memory`] : [])];
            return {
                title: `${lowRam && lowVram ? 'RAM and video memory' : lowRam ? 'RAM' : 'Video memory'} nearly full when the game crashed`,
                confidence: critical ? 'likely' : 'possible',
                explain: `Only ${list(free)} was free. Running this short of memory makes the game unstable, though ` +
                    'another problem in the log can also be what used the memory up.',
                fix: `Close programs running beside the game. ${PAGEFILE} Use 1K or 2K texture mods instead of 4K, or ` +
                    'optimize all textures with VRAMr (Nexus 90557). Cut down on mods that add many objects, such as trees ' +
                    'or city clutter. In game, the console command pcb frees cached cells.',
                evidence: [...(lowRam ? memoryLine(view, 'PHYSICAL') : []), ...(lowVram ? memoryLine(view, 'GPU') : [])]
            };
        }
    },
    {
        id: 'memory-issue-indicators',
        ported: 'diagnoses.js analyzeMemoryIssues',
        diagnose: view => {
            const hex = MEMORY_HEX_CODES.filter(([code]) => view.has('firstLine', code));
            // Crash Logger 1.27 writes a C++ exception's type, std::bad_alloc, in a section of its own.
            const codes = MEMORY_CODES.filter(([code]) => view.has('topHalf', code) || view.has('cppException', code));
            if (hex.length === 0 && codes.length === 0) return undefined;
            const probable = hex.length > 0 || view.has('firstLine', 'tbbmalloc.dll');
            const found = [...hex, ...codes];
            return {
                title: probable ? 'Probable memory problem in the crash' : 'Signs of a memory problem in the crash',
                confidence: probable ? 'likely' : 'possible',
                explain: `The log has ${list(found.map(([code, what]) => `${code} (${what})`))}.${memoryStatus(view)}`,
                fix: `Restart the PC and close other programs. ${PAGEFILE} Undo CPU and GPU overclocks (XMP or EXPO RAM ` +
                    'profiles can usually stay). Use 1K or 2K texture mods, or optimize textures with VRAMr (Nexus 90557). ' +
                    'If the log names a texture or mesh, reinstall its mod. Test the RAM with Windows Memory Diagnostic.',
                evidence: unique(...found.map(([code]) => [...view.find('topHalf', code), ...view.find('cppException', code)]))
            };
        }
    },
    {
        id: 'low-system-ram',
        ported: 'diagnoses.js checkLowSystemRAM',
        diagnose: view => {
            const total = view.log.memory.physicalTotalGb;
            if (!total || total > 16) return undefined;
            const veryLow = total <= 8;
            return {
                title: `${veryLow ? 'Very low' : 'Low'} system RAM: ${gb(total)}`,
                confidence: 'possible',
                explain: `The PC has ${gb(total)} of RAM. Heavily modded Skyrim with 16 GB or less runs short of memory ` +
                    'and crashes more often.' + (veryLow ? ' That is at or below the 8 GB Steam recommends for the unmodded game.' : ''),
                fix: `Close all other programs before starting the game. ${PAGEFILE}` + (veryLow
                    ? ' Keep to light mods: leave out physics mods, ENB, Community Shaders and heavy lighting or texture overhauls.'
                    : ''),
                evidence: memoryLine(view, 'PHYSICAL')
            };
        }
    },
    {
        id: 'intel-13th-14th-gen-cpu',
        ported: 'diagnoses.js checkIntelCPUIssue',
        diagnose: view => {
            const thirteenth = view.find('beforeCallStack', /13th Gen Intel\(R\)/);
            const evidence = thirteenth.length ? thirteenth : view.find('beforeCallStack', /Core\(TM\) i[579]-14\d{3}/);
            if (evidence.length === 0) return undefined;
            return {
                title: `Intel ${thirteenth.length ? '13th' : '14th'} gen CPU: unstable without its microcode update`,
                confidence: 'possible',
                explain: 'Intel 13th and 14th gen CPUs without microcode 0x129 or later, which comes with a BIOS update, ' +
                    'crash at random and wear out early.',
                fix: 'Check the microcode in PowerShell: \'0x{0:X}\' -f [BitConverter]::ToUInt32((Get-ItemProperty ' +
                    '"HKLM:\\HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0").\'Update Revision\',0). Below 0x129 (good: ' +
                    '0x129, 0x12B, 0x12F), install the latest BIOS from the motherboard maker; Windows Update does not bring ' +
                    'it. Then test the CPU with the Intel Processor Diagnostic Tool.',
                evidence,
                setup: true
            };
        }
    },
    {
        id: 'vc-runtime-dll',
        ported: 'diagnoses.js analyzeVCRuntimeIssue',
        diagnose: view => {
            const inFirstLine = VC_RUNTIME_DLLS.filter(dll => view.has('firstLine', dll));
            const kernelBase = view.has('firstLine', 'kernelbase.dll');
            const inTopHalf = kernelBase ? VC_RUNTIME_DLLS.filter(dll => view.has('topHalf', dll)) : [];
            if (inFirstLine.length === 0 && inTopHalf.length === 0) return undefined;
            const viaKernelBase = inFirstLine.length === 0;
            return {
                title: 'Crash in a Visual C++ runtime DLL',
                confidence: 'likely',
                explain: `The crash runs through ${list([...new Set([...inFirstLine, ...inTopHalf])])}` +
                    `${viaKernelBase ? ', with KERNELBASE.dll on the first line' : ''}: the Microsoft Visual C++ runtime ` +
                    'SKSE plugins are built on. Either the runtime is outdated or damaged, or an SKSE plugin fails in it.',
                fix: 'Install the latest Microsoft Visual C++ Redistributable (x64) from Microsoft and restart the PC. If it ' +
                    'repeats, disable, update or reinstall the SKSE plugins (.dll files) the log names, 5 to 10 at a time, ' +
                    'keeping SSE Engine Fixes enabled; test on a new character or without saving.',
                evidence: unique(
                    view.find('firstLine', ...inFirstLine, ...(viaKernelBase ? ['kernelbase.dll'] : [])),
                    ...inTopHalf.map(dll => view.find('topHalf', dll))
                )
            };
        }
    },
    {
        id: 'kernelbase-crash',
        ported: 'diagnoses.js checkKernelbaseCrash',
        diagnose: view => {
            const evidence = view.find('firstLine', 'kernelbase.dll');
            if (evidence.length === 0) return undefined;
            // The upscaler crash on Windows 24H2 and the JContainers crash have rules of their own that say more.
            if (view.has('callStack', 'SkyrimUpscaler.dll', 'JContainers64.dll', 'CustomSkills.dll')) return undefined;
            if (view.has('topHalf', 'json.exception.parse_error')) return undefined;
            return {
                title: 'Crash in KERNELBASE.dll, a Windows system file',
                confidence: 'possible',
                explain: 'Windows raised the error on behalf of something else: most often a mod, usually an SKSE plugin; ' +
                    'less often hardware, a Windows update, malware, drive corruption or broken file permissions.',
                fix: `Restart the PC; act only if it repeats. ${PAGEFILE} Disable, update or reinstall the SKSE plugins ` +
                    '(.dll files) the log names, 5 to 10 at a time, keeping SSE Engine Fixes. Then as administrator run ' +
                    'dism /online /cleanup-image /restorehealth and sfc /scannow, update Windows, drivers and BIOS, and scan ' +
                    'for malware.',
                evidence
            };
        }
    },
    {
        id: 'wintrust-security',
        ported: 'diagnoses.js checkWintrustCrash',
        diagnose: view => {
            const evidence = view.find('topHalf', 'wintrust.dll');
            if (evidence.length === 0) return undefined;
            return {
                title: 'wintrust.dll in the crash: security software may block files',
                confidence: 'likely',
                explain: 'wintrust.dll is the part of Windows that checks digital signatures. It shows up when antivirus ' +
                    'software, a broken signature or too strict file permissions keep the game or a mod from its files.',
                fix: 'In third-party antivirus software, add exclusions for the game folder, the mod manager folder and ' +
                    'Documents\\My Games\\Skyrim Special Edition, or switch to the built-in Windows Security. Download again ' +
                    'and reinstall the mods the log names. Make sure the game and mod folders are not read-only.',
                evidence
            };
        }
    },
    {
        id: 'antivirus',
        ported: 'diagnoses.js analyzeAntivirusIssues',
        diagnose: view => {
            const found = Object.entries(ANTIVIRUS)
                .map(([name, files]) => ({ name, evidence: view.find('all', ...files.map(fileName)) }))
                .filter(entry => entry.evidence.length > 0);
            const usvfsTopQuarter = view.has('topQuarter', 'usvfs_x64.dll');
            const usvfs = found.length ? view.find('topHalf', 'usvfs_x64.dll') : [];
            const defender = found.length === 0 ? view.find('topHalf', ...WINDOWS_SECURITY) : [];
            // usvfs_x64.dll with no antivirus found is usvfs-mo2's finding, whose top half holds the top quarter.
            if (found.length === 0 && defender.length === 0) return undefined;
            if (found.length === 0) {
                return {
                    title: 'Windows Security in the crash',
                    confidence: 'possible',
                    explain: 'Files of Windows Security, the antivirus built into Windows, are in the crash. It is usually ' +
                        'harmless with Skyrim, but may need exclusions.',
                    fix: 'If crashes like this repeat, add exclusions for the game folder and the mod manager folder in ' +
                        'Windows Security.',
                    evidence: defender
                };
            }
            const names = list(found.map(entry => entry.name));
            const direct = usvfsTopQuarter;
            return {
                title: direct ? 'Antivirus blocks Mod Organizer 2\'s virtual file system' : 'Third-party antivirus running with the game',
                confidence: direct ? 'certain' : 'possible',
                explain: `${names} ${found.length > 1 ? 'are' : 'is'} loaded into the game; third-party antivirus often ` +
                    'interferes with heavily modded Skyrim.' + (usvfs.length ? ' usvfs_x64.dll, Mod Organizer 2\'s virtual ' +
                    `file system, is in the crash${direct ? ', which the antivirus blocking it would cause' : ', possibly blocked by it'}.` : ''),
                fix: `Add exclusions for the whole game folder and the mod manager folder in ${names}, or switch to the ` +
                    'built-in Windows Security, which needs no exclusions for Skyrim.',
                evidence: unique(...found.map(entry => entry.evidence), usvfs)
            };
        }
    },
    {
        id: 'overlay',
        ported: 'diagnoses.js analyzeOverlayIssues',
        diagnose: view => {
            const found = new Map<string, Set<string>>();
            const evidence: Evidence[] = [];
            let inCrash = false;
            const add = (name: string, file: string) => found.set(name, (found.get(name) ?? new Set()).add(file));
            const known = (file: string) => Object.entries(OVERLAYS).find(([, files]) => files.includes(file.toLowerCase()))?.[0];
            for (const line of view.lines('topHalf')) {
                for (const [file] of line.text.matchAll(/\b\w*overlay\w*\.dll\b/gi)) {
                    if (NOT_OVERLAYS.has(file.toLowerCase())) continue;
                    inCrash = true;
                    add(known(file) ?? file, file);
                    evidence.push(toEvidence(line));
                }
            }
            for (const [name, files] of Object.entries(OVERLAYS)) {
                for (const file of files) {
                    const lines = view.find('all', fileName(file));
                    if (lines.length === 0) continue;
                    add(name, file);
                    evidence.push(...lines);
                    if (view.has('topHalf', fileName(file))) inCrash = true;
                }
            }
            const steam = view.find('topHalf', 'gameoverlayrenderer64.dll');
            if (steam.length) add('Steam', 'gameoverlayrenderer64.dll');
            const others = [...found.keys()].filter(name => !/steam|discord/i.test(name));
            if (others.length === 0) return undefined;
            const named = [...found].map(([name, files]) => files.has(name) ? name : `${name} (${[...files].join(', ')})`);
            return {
                title: inCrash ? 'Overlay software in the crash' : 'Overlay software running with the game',
                confidence: 'possible',
                explain: `Overlays are loaded: ${list(named)}. ` + (inCrash
                    ? 'One of them is in the crash itself, so it may have contributed.'
                    : 'Overlays draw over the game and can make heavily modded Skyrim unstable.') +
                    (steam.length ? ' Steam\'s overlay shows up even when it is turned off.' : ''),
                fix: `Turn off every overlay while testing: ${list([...found.keys()])}, in their own settings or by closing ` +
                    'them before starting the game. If the crashes stop, turn them back on one at a time to find the one at fault.',
                evidence: unique(evidence, steam)
            };
        }
    },
    {
        id: 'usvfs-mo2',
        ported: 'diagnoses.js checkUsvfsIssue',
        diagnose: view => {
            const evidence = view.find('topHalf', 'usvfs_x64.dll');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Crash in Mod Organizer 2\'s virtual file system (usvfs_x64.dll)',
                confidence: 'possible',
                explain: 'usvfs_x64.dll is the layer through which Mod Organizer 2 shows mod files to the game. The game ' +
                    'failed while going through it; antivirus software interfering with it is the most common cause.',
                fix: 'Add exclusions for the Mod Organizer 2 folder and the game folder in the antivirus software, or switch ' +
                    'to the built-in Windows Security. Update Mod Organizer 2 if it is not the latest stable release.',
                evidence
            };
        }
    },
    {
        id: 'file-access',
        ported: 'diagnoses.js checkPossibleFilesystemIssue',
        diagnose: view => {
            const found = FILE_ACCESS.filter(([needle]) => view.has('topHalf', needle));
            if (found.length === 0) return undefined;
            return {
                title: 'Signs the game could not read or write its files',
                confidence: 'possible',
                explain: `The crash involves ${list([...new Set(found.map(([, name]) => name))])}. Long paths, antivirus ` +
                    'software, read-only folders, OneDrive syncing or a full drive can keep the game from its files and saves.',
                fix: 'Keep the game and mod folders on short paths (Windows allows 260 characters). Exclude the game, mod ' +
                    'manager and Documents\\My Games\\Skyrim Special Edition folders from antivirus scans; make sure they are ' +
                    'not read-only and OneDrive does not sync them. Keep 10-20% of the drive free. Turn off autosaves: ' +
                    'bDisableAutoSave=1 in Skyrim.ini, or Disable Auto Save (Nexus 81502).',
                evidence: view.find('topHalf', ...found.map(([needle]) => needle))
            };
        }
    },
    {
        id: 'xaudio',
        ported: 'diagnoses.js analyzeXAudioIssue',
        diagnose: view => {
            const evidence = view.find('topHalf', 'xaudio');
            if (evidence.length === 0) return undefined;
            return {
                title: 'XAudio in the crash: a sound problem',
                confidence: 'possible',
                explain: 'XAudio is the Windows audio library the game plays sound through. Crashes there come from audio ' +
                    'drivers, the Windows sound format, or a mod\'s sound; a looping .xwm sound is a suspected cause.',
                fix: 'Install Sound Fix for Large Sector Drives (Nexus 71567). Update the sound card driver. Set the Windows ' +
                    'sound output format to 16 bit, 44100 Hz. On a touchscreen PC, disable the Touch Keyboard and Handwriting ' +
                    'Panel Service. If it repeats, disable recently added sound mods one at a time.',
                evidence
            };
        }
    },
    {
        id: 'gamepad-controlmap',
        ported: 'diagnoses.js analyzeGamepadCrash',
        diagnose: view => {
            // As Phostwood matches them: in this case only.
            const evidence = view.find('topHalf', /BSWin32GamepadDevice|BSPCGamepadDeviceHandler/);
            if (evidence.length === 0) return undefined;
            return {
                title: 'Gamepad crash: a controlmap that does not fit the game version',
                confidence: 'certain',
                explain: 'The crash is in the game\'s gamepad handling: a connected gamepad or controller crashes the game ' +
                    'when the controlmap is outdated.' +
                    (versionAtLeast(view.gameVersion, [1, 6, 1130]) ? ' Game versions from 1.6.1130 on need an updated controlmap.' : ''),
                fix: 'Install a controlmap made for this game version, for example Modern Controlmap (Gamepad and Keyboard) ' +
                    '(Nexus 89649), and remove older controlmap mods. Or unplug the gamepad if it is not used.',
                evidence
            };
        }
    },
    {
        id: 'keyboard-input',
        ported: 'diagnoses.js analyzeKeyboardCrash',
        diagnose: view => {
            const evidence = view.find('topHalf', 'bswin32keyboarddevice');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Crash in the game\'s keyboard input',
                confidence: 'possible',
                explain: 'The game failed while handling keyboard input. Alt+Tab out of the game is a common trigger; an ' +
                    'outdated controlmap or an input or hotkey mod is another.',
                fix: 'Avoid Alt+Tab, above all in fullscreen or while loading or saving; borderless window mode from SSE ' +
                    'Display Tweaks (Nexus 34705) lowers the risk. Update the keyboard driver. If it repeats, remove the old ' +
                    'controlmap and install one for this game version, such as Modern Controlmap (Gamepad and Keyboard) ' +
                    '(Nexus 89649), and check hotkey mods.',
                evidence
            };
        }
    },
    {
        id: 'first-line-module',
        ported: 'diagnoses.js analyzeFirstLine',
        diagnose: view => {
            const file = FIRST_LINE_FILE.exec(view.log.firstLine)?.[1];
            if (!file || FIRST_LINE_IGNORED.has(file.toLowerCase())) return undefined;
            const framework = FIRST_LINE_FRAMEWORKS.has(file.toLowerCase()) || SKSE_DLL.test(file);
            const what = WHAT_FILE_IS.get(file.toLowerCase());
            return {
                title: `The crash happened in ${file}`,
                confidence: framework ? 'possible' : 'likely',
                explain: `${file}${what ? ` (${what})` : ''} is where the crash happened. ` + (framework
                    ? 'It is a framework many mods build on and rarely at fault itself; a mod that uses it more likely is.'
                    : 'It is directly involved but may not be the root cause: a missing requirement, a version mismatch or ' +
                    'another mod using it can be.'),
                fix: framework
                    ? 'Make sure SKSE (Nexus 30379) and Address Library for SKSE Plugins (Nexus 32444) match this game ' +
                    `version, and update the mod that contains ${file}. Look at the other findings first; if this repeats, ` +
                    'disable the mods that use it a few at a time, on a new character, to find the one at fault.'
                    : `Make sure the mod that contains ${file} is enabled, then download it again and reinstall it. Check ` +
                    'that it supports this game version and update it, and that its requirements are installed: SKSE ' +
                    '(Nexus 30379), Address Library for SKSE Plugins (Nexus 32444) and the Microsoft Visual C++ ' +
                    'Redistributable (x64). As a test, disable it and the mods that need it, on a new character.',
                evidence: view.find('firstLine', file)
            };
        }
    },
    {
        id: 'skyrim-crash-guard',
        ported: 'diagnoses.js checkSkyrimCrashGuard',
        diagnose: view => {
            // Crash Logger 1.27 warns about it at the top of the log, before the exception.
            const header = view.find('beforeCallStack', /skyrimcrashguard/i);
            const loaded = view.find('bottomHalf', /skyrimcrashguard\.dll/i);
            if (header.length === 0 && loaded.length === 0) return undefined;
            return {
                title: 'SkyrimCrashGuard is installed: crash logs are unreliable',
                confidence: 'certain',
                explain: 'SkyrimCrashGuard keeps the game running past crashes with unsafe changes that corrupt the game ' +
                    'state, so the crash it finally lets through may point at the wrong mod.' +
                    (header.some(line => /WARNING: SkyrimCrashGuard DETECTED/i.test(line.text)) ? ' Crash Logger warns about it too.' : ''),
                fix: 'Uninstall SkyrimCrashGuard. Go back to a save made before it was installed, or start a new character; ' +
                    'do not keep playing a save it was used with. Do not blame other mods for crashes logged while it was installed.',
                evidence: unique(header, loaded)
            };
        }
    }
];

/** explainersMap.js, its .dll and .exe files: what each is, for the first line's file. */
const WHAT_FILE_IS: ReadonlyMap<string, string> = new Map([
    ['_nvngx.dll', 'NVIDIA NGX Runtime Library'],
    ['a0d789patch.dll', 'recommended bug fix'],
    ['achievements.dll', 'Achievements Mods Enabler SKSE plugin'],
    ['addresslibrarysse.dll', 'Address Library for SKSE Plugins'],
    ['amdxc64.dll', 'AMD graphics component'],
    ['amdxx64.dll', 'AMD graphics component'],
    ['animationdatasystem.dll', 'Animation Data System SKSE plugin'],
    ['animationmotion.dll', 'Animation Motion Revolution SKSE plugin'],
    ['antimalware_provider64.dll', 'antimalware provider'],
    ['applicationtargetedfeaturedatabase.dll', 'feature database, MS Windows'],
    ['asm.plugin.audiodevprops2.dll', 'Nahimic audio component'],
    ['atcuf64.dll', 'Bitdefender Active Threat Control Userland API'],
    ['atidxx64.dll', 'AMD graphics component'],
    ['atiumd6a.dll', 'AMD graphics component'],
    ['atiuxp64.dll', 'AMD graphics component'],
    ['avamsi.dll', 'Avira Antimalware Scan Interface, also used by Surfshark'],
    ['avamsicli.dll', 'Avira Antimalware Client Interface'],
    ['avast.dll', 'Avast Antivirus Core Component'],
    ['avgui.dll', 'AVG Antivirus User Interface'],
    ['backportedeslsupport.dll', 'aka BEES, SKSE plugin'],
    ['bdcore.dll', 'Bitdefender Core Antivirus Module'],
    ['bdhkm64.dll', 'Bitdefender Hooking Module, antivirus'],
    ['bink2w64.dll', 'Bink Video codec, newer version'],
    ['binkw64.dll', 'Bink Video codec for in-game videos'],
    ['bsa.dll', 'BSA handling library for mod managers'],
    ['bufferpatch.dll', 'SSE Display Tweaks SKSE plugin'],
    ['categorizedmodmenu.dll', 'Categorized Favorite Menu SKSE plugin'],
    ['cbp.dll', 'Collision Body Physics, less likely culprit'],
    ['cef.dll', 'Chromium Embedded Framework, used by some UI mods'],
    ['chargen.dll', 'RaceMenu character generation plugin'],
    ['clib64.dll', 'CommonLib shared library for SKSE plugins'],
    ['com_antivirus.dll', 'Comodo Antivirus Component'],
    ['comctl32.dll', 'Common Controls Library, MS Windows'],
    ['comdlg32.dll', 'Common Dialog Box Library, MS Windows'],
    ['compstui.dll', 'Common Property Sheet UI, MS Windows printing'],
    ['con_bypass.dll', 'Console Bypass SKSE plugin'],
    ['concrt140.dll', 'Concurrency Runtime Library, MS Windows'],
    ['console.dll', 'Enhanced Console Commands SKSE plugin'],
    ['crashfixes.dll', 'Various crash fixes for Skyrim'],
    ['crashlogger.dll', 'Crash Logger SSE plugin'],
    ['d3d10.dll', 'Direct3D 10 Runtime, MS Windows graphics'],
    ['d3d10_1.dll', 'Direct3D 10.1 Runtime, MS Windows graphics'],
    ['d3d10_1core.dll', 'Direct3D 10.1 Core Runtime, MS Windows graphics'],
    ['d3d10core.dll', 'Direct3D 10 Core Runtime, MS Windows graphics'],
    ['d3d10warp.dll', 'CPU emulation of GPU, MS Windows graphics'],
    ['d3d11.dll', 'Direct3D 11, MS Windows graphics, frequent ENB indicator'],
    ['d3d11on12.dll', 'Direct3D 11On12 Runtime, MS Windows graphics'],
    ['d3d12core.dll', 'Direct3D 12 Core Runtime, MS Windows graphics'],
    ['d3dcompiler_42.dll', 'DirectX HLSL Compiler, MS Windows graphics'],
    ['d3dcompiler_46e.dll', 'ENB shader compiler'],
    ['d3dcompiler_47.dll', 'DirectX Shader Compiler, used by some graphics mods'],
    ['d3dscache.dll', 'Direct3D Shader Cache, MS Windows graphics'],
    ['d3dx10_42.dll', 'DirectX 10.1 Extensions, MS Windows graphics'],
    ['d3dx11_42.dll', 'Direct3D 11 Extensions, MS Windows graphics'],
    ['d3dx9_42.dll', 'DirectX 9 library, likely from SSE Engine Fixes, Part 2, less likely culprit'],
    ['d3dx9_43.dll', 'Direct3D 9 Extensions, MS Windows graphics'],
    ['dbsksefunctions.dll', 'Dylbill\'s Papyrus Functions SKSE plugin'],
    ['detoured.dll', 'Microsoft Detours Library, Windows'],
    ['deviceaccess.dll', 'Device Broker And Policy COM Server, MS Windows'],
    ['dinput8.dll', 'DirectInput library, used for input handling'],
    ['discordhook64.dll', 'Discord Overlay Component'],
    ['dkaf.dll', 'Skyrim modding tool for binding keys to animations'],
    ['dmenu.dll', 'Dynamic Menu for Skyrim SE Mods, MS Windows'],
    ['dsound.dll', 'DirectSound library for audio processing'],
    ['dxcompiler.dll', 'Direct3D Shader Compiler, MS Windows'],
    ['dxgi.dll', 'DirectX Graphics Infrastructure, part of Windows, but altered by some mods'],
    ['dxil.dll', 'DirectX Intermediate Language Library, MS Windows'],
    ['dxilconv.dll', 'DXBC to DXIL Converter, MS Windows'],
    ['dynamicanimationreplacer.dll', 'aka DAR, older animation replacer mod'],
    ['eamsi.dll', 'ESET Antimalware Scan Interface'],
    ['edputil.dll', 'EDP Utility Library, MS Windows'],
    ['egui.dll', 'ESET Graphical User Interface, antivirus'],
    ['enb123.dll', 'Older version of ENB graphics mod'],
    ['enbhelper.dll', 'ENB helper library'],
    ['engine_fixes.dll', 'SSE Engine Fixes SKSE plugin, less likely culprit'],
    ['enginefixes.dll', 'SSE Engine Fixes SKSE plugin, less likely culprit'],
    ['equippablekittails.dll', 'Equippable Khajiit Tails SKSE plugin'],
    ['f4se_1_10_163.dll', 'Fallout 4 Script Extender, mistakenly present'],
    ['ffx_backend_dx12_x64.dll', 'FidelityFX S.R. 3.0 Backend Library, NVIDIA'],
    ['ffx_frameinterpolation_x64.dll', 'FidelityFX S.R. 3.0 Frame Interpolation, NVIDIA'],
    ['ffx_fsr2_api_dx11_x64.dll', 'FidelityFX S.R. 2 API for DirectX 11, AMD'],
    ['ffx_fsr2_api_dx12_x64.dll', 'FidelityFX S.R. 2.0 API for DirectX 12, AMD'],
    ['ffx_fsr2_api_x64.dll', 'FidelityFX S.R. 2 API, AMD'],
    ['ffx_fsr3_x64.dll', 'FidelityFX S.R. 3.0 Core Library, NVIDIA driver'],
    ['ffx_fsr3upscaler_x64.dll', 'FidelityFX S.R. 3.0 Upscaler, NVIDIA'],
    ['ffx_opticalflow_x64.dll', 'Optical Flow for FidelityFX S.R. 3.0, NVIDIA'],
    ['fileregistry64.dll', 'File Registry SKSE plugin'],
    ['flexrelease_x64.dll', 'NVIDIA PhysX library, used by some physics mods'],
    ['fmod.dll', 'FMOD audio engine used by Skyrim'],
    ['fnis.dll', 'Fores New Idles in Skyrim animation framework'],
    ['fraps.dll', 'Fraps Screen Capture and Overlay'],
    ['freezeprotect.dll', 'Freeze Protection SKSE plugin'],
    ['fsaus.dll', 'F-Secure Antivirus User-mode Service'],
    ['gameoverlayrenderer64.dll', 'Steam Overlay Component'],
    ['gamepad++.dll', 'Gamepad++ controller mod'],
    ['glu32.dll', 'OpenGL Utility Library, graphics component'],
    ['gridhud.dll', 'Grid HUD interface mod, UI'],
    ['havok.dll', 'Havok Physics engine used by Skyrim'],
    ['hdtphysicsextensions.dll', 'HDT-SMP Physics mod'],
    ['hdtsmp64.dll', 'Skinned Mesh Physics, less likely culprit'],
    ['hooksharedsse.dll', 'HookShare SKSE plugin'],
    ['iequip.dll', 'iEquip SKSE plugin'],
    ['igc64.dll', 'Intel Graphics Compiler for OpenCL, graphics'],
    ['igd10iumd64.dll', 'Intel Graphics Accelerator Drivers, graphics'],
    ['igd10um64gen11.dll', 'Intel Graphics Accelerator Driver, graphics'],
    ['igd10um64xe.dll', 'Intel HD Graphics Driver'],
    ['igdgmm64.dll', 'Intel HD Graphics Drivers'],
    ['immersivefp.dll', 'Immersive First Person View SKSE plugin'],
    ['inputhook.dll', 'Input hook library used by some controller mods'],
    ['intelcontrollib.dll', 'Intel Graphics Control Lib Runtime'],
    ['jcontainers.dll', 'JContainers SKSE plugin for advanced scripting'],
    ['jsproxy.dll', 'JScript Proxy Auto-Configuration, associated with Internet Explorer'],
    ['kavprot.dll', 'Kaspersky Antivirus Protection Module'],
    ['kerberos.dll', 'Kerberos Security Package, MS Windows security'],
    ['libxess.dll', 'Xe Super Sampling Library, NVIDIA'],
    ['llcbhook.dll', 'Load Library Copy Back hook for mod managers'],
    ['loadgamefix.dll', 'Load Game CTD Fix SKSE plugin'],
    ['locksys.dll', 'Lock System SKSE plugin for enhanced lockpicking'],
    ['maxsu.dll', 'MaxSU SKSE plugin for script utility functions'],
    ['mcafee.dll', 'McAfee Antivirus Core Component'],
    ['mcmhelper.dll', 'MCM Helper SKSE plugin for mod configuration'],
    ['medal-hook64.dll', 'Medal.tv Hook Library, used for game recording'],
    ['mfgfix.dll', 'SKSE plugin for facial animation fixes'],
    ['mlang.dll', 'Multilanguage Support DLL, MS Windows'],
    ['modorganizer.dll', 'Mod Organizer 2 virtual file system'],
    ['msacm32.dll', 'Microsoft ACM Audio Filter'],
    ['msiso.dll', 'Internet Explorer Isolation Library, MS Windows'],
    ['msvcp100.dll', 'Microsoft Visual C++ 2010 Redistributable, Windows'],
    ['msvcp110.dll', 'Microsoft Visual C++ 2012 Redistributable, Windows'],
    ['msvcp110_win.dll', 'Microsoft Visual C++ 2015 Redistributable, Windows'],
    ['msvcp140.dll', 'Microsoft Visual C++ 2015 Redistributable, Windows'],
    ['n64hooks.dll', 'N64 hooks library, emulator'],
    ['nativepatch.dll', 'SSE Native Patch SKSE plugin'],
    ['nemesis unlimited behavior engine.exe', 'Animation framework, alternative to FNIS'],
    ['netimmerse.dll', 'NetImmerse Override SKSE plugin'],
    ['ninput.dll', 'Microsoft Pen and Touch Input Component'],
    ['npsystem.dll', 'Norton Protection System Component, antivirus'],
    ['nscore64.dll', 'Nscore library, antivirus'],
    ['nv_ags.dll', 'NVIDIA Advanced Graphics SDK'],
    ['nvapi64.dll', 'NVIDIA Driver API Library'],
    ['nvcuda.dll', 'NVIDIA CUDA Driver'],
    ['nvdxgdmal64.dll', 'NVIDIA DirectX Graphics Device Memory Allocation Library'],
    ['nvgpucomp64.dll', 'NVIDIA GPU Component, graphics driver'],
    ['nvngx_dlss.dll', 'NVIDIA DLSS Driver'],
    ['nvngx_dlssg.dll', 'NVIDIA DLSS Driver for FSR3'],
    ['nvofapi64.dll', 'NVIDIA Optical Flow API'],
    ['nvoglv32.dll', 'NVIDIA OpenGL Driver for 32-bit'],
    ['nvoglv64.dll', 'NVIDIA OpenGL Driver for 64-bit'],
    ['nvspcap64.dll', 'NVIDIA Video Capture API'],
    ['nvwgf2um.dll', 'NVIDIA D3D10 User Mode Driver for 32-bit'],
    ['nvwgf2umx.dll', 'NVIDIA D3D10 User Mode Driver for 64-bit'],
    ['nxmhandler.dll', 'Nexus Mod Manager URL handler, mod manager'],
    ['oleacc.dll', 'Active Accessibility Core Component, MS Windows'],
    ['onecorecommonproxystub.dll', 'Windows system component'],
    ['onecoreuapcommonproxystub.dll', 'OneCore UAP Common Proxy Stub, MS Windows'],
    ['openanimationreplacer.dll', 'aka OAR, newer animation replacer mod'],
    ['opengl32.dll', 'OpenGL Client DLL, graphics component'],
    ['overwolf.dll', 'Overwolf Overlay Component'],
    ['ow-graphics-hook64.dll', 'Overwolf Graphics Hook gaming component, overlay'],
    ['owclient.dll', 'Overwolf gaming component, overlay'],
    ['owexplorer.dll', 'Overwolf gaming component, overlay'],
    ['owutils.dll', 'Overwolf gaming component, overlay'],
    ['papyrus compiler.exe', 'Compiler for Skyrim\'s scripting language'],
    ['papyrusextender64.dll', 'Papyrus Script Extender'],
    ['papyrusextenderse.dll', 'Papyrus Script Extender'],
    ['papyrusextenderse64.dll', 'Papyrus Script Extender'],
    ['papyrustweaks.dll', 'Papyrus Script Tweaks'],
    ['papyrusutil.dll', 'Papyrus Script Utility'],
    ['parallaxfix.dll', 'Parallax Fix SKSE plugin'],
    ['particlepatch.dll', 'Particle Patch for ENB'],
    ['pcb.dll', 'Payload Control Box used by some SKSE plugins'],
    ['pdperfplugin.dll', 'Puredark\'s Performance Plugin, SKSE'],
    ['pluginmanager.dll', 'Plugin Manager for mod organizers'],
    ['po3_papyrusextender.dll', 'Papyrus Script Extender'],
    ['po3_tweaks.dll', 'Powerof3\'s Tweaks SKSE plugin'],
    ['product_info.dll', 'A-Volute 3D Sound Experts, audio component'],
    ['quicklootee.dll', 'QuickLoot EE SKSE plugin'],
    ['raptr64.dll', 'Raptr Gaming Overlay'],
    ['reshade64.dll', 'ReShade post-processing injector'],
    ['rtsshdrs64.dll', 'RivaTuner Statistics Server Hooks, monitoring'],
    ['rtsshooks64.dll', 'RivaTuner Statistics Server Hooks for graphics card monitoring'],
    ['sandboxingext.dll', 'Sandboxing Extended SKSE plugin'],
    ['savecleanerpolyfill.dll', 'Save Cleaner SKSE plugin'],
    ['scaleform.dll', 'Scaleform UI middleware used by Skyrim'],
    ['simplemap.dll', 'Simple Map SKSE plugin'],
    ['skee64.dll', 'RaceMenu SKSE plugin'],
    ['skinned mesh physics.dll', 'Physics mod for character models'],
    ['skinshader.dll', 'Custom skin shader used by some texture mods'],
    ['skse64_1_5_97.dll', 'Skyrim Script Extender'],
    ['skse64_1_6_1170.dll', 'Skyrim Script Extender, unlikely culprit'],
    ['skse64_steam_loader.dll', 'SKSE Steam loader'],
    ['skse64_loader.exe', 'SKSE loader executable'],
    ['skyprocednpp.dll', 'SkyProc Patchers SKSE plugin'],
    ['skyrimautoreloader.dll', 'recommended bug fix'],
    ['skyrimupscaler.dll', 'Puredark\'s Upscaler'],
    ['sl.common.dll', 'Streamline Common Library, NVIDIA'],
    ['sl.dlss_g.dll', 'NVIDIA DLSS Frame Generation'],
    ['sl.interposer.dll', 'NVIDIA Streamline Interposer Module'],
    ['sl.reflex.dll', 'NVIDIA Reflex Library'],
    ['spis.dll', 'Spell Perk Item Distributor SKSE plugin'],
    ['sse-hooks.dll', 'SSE Fixes SKSE plugin'],
    ['streamcore.dll', 'Streaming Core library used by some audio mods'],
    ['subsurfacescattering.dll', 'Subsurface Scattering shader mod'],
    ['symamsi.dll', 'Windows Security Antimalware Scan Interface'],
    ['symnav.dll', 'Symantec Antivirus Core Component'],
    ['symsrv.dll', 'Symbol Server, MS Windows debugging component'],
    ['tbb.dll', 'Intel Threading Building Blocks Memory Allocator, likely from SSE Engine Fixes, Part 2, less likely culprit'],
    ['tbbmalloc.dll', 'Intel Threading Building Blocks Memory Allocator, likely from SSE Engine Fixes, Part 2, less likely culprit'],
    ['tes5conv.dll', 'TES5Edit Converter library, modding tool'],
    ['threadpoolwinrt.dll', 'Windows Runtime Thread Pool Library'],
    ['tmntsrv.dll', 'Trend Micro Network Service, antivirus'],
    ['truehud.dll', 'TrueHUD SKSE plugin'],
    ['uapng.dll', 'Ultimate Animated Potions NG mod'],
    ['uiautomationcore.dll', 'Microsoft UI Automation Core, Windows'],
    ['ucrtbase.dll', 'Universal C Runtime Library, MS Windows'],
    ['und.dll', 'Ultimate NPC Dodging mod'],
    ['user32.dll', 'Microsoft User Interface Library, Windows'],
    ['usvfs_x64.dll', 'User Space Virtual File System, MO2, possible indicator of antivirus issue'],
    ['vcruntime140.dll', 'Visual C++ Runtime Library, MS Windows'],
    ['versionlib.dll', 'VersionLib SKSE plugin'],
    ['vsfilter.dll', 'DirectShow subtitle rendering, used by some video mods'],
    ['vulkan-1.dll', 'Vulkan Runtime Library, MS Windows'],
    ['vulkaninfo.exe', 'Vulkan Info Utility, MS Windows'],
    ['windows.shell.servicehostbuilder.dll', 'Windows Shell Service Host Builder'],
    ['windows.staterepositorycore.dll', 'Windows State Repository Core'],
    ['windows.system.launcher.dll', 'Windows System Launcher'],
    ['winevulkan.dll', 'Wine Vulkan Driver, Windows emulator'],
    ['wintrust.dll', 'Windows Trust Manager, Windows, possible indicator of antivirus issue'],
    ['wrsvc.dll', 'Webroot SecureAnywhere Service, antivirus'],
    ['wtsapi32.dll', 'Windows Terminal Server SDK API'],
    ['xaudio2_7.dll', 'XAudio library for audio processing'],
    ['xefx.dll', 'Intel Xe Graphics Architecture'],
    ['xefx_loader.dll', 'Intel Xe Graphics Loader'],
    ['xinput1_3.dll', 'XInput library for controller support'],
    ['zavcore.dll', 'ZoneAlarm Core Antivirus Module']
]);
