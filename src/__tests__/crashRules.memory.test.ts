import { memoryRules } from '../backend/skyrim/crash/rules/memory';
import type { CrashRule, Finding } from '../backend/skyrim/crash/crashRules';
import { crashView, firstLineAt, frame, type LogParts } from './crashLogText';

/** Memory, the system and its programs: each rule fires on the log it is about, and not on another. */

const rule = (id: string): CrashRule => {
    const found = memoryRules.find(entry => entry.id === id);
    if (!found) throw new Error(`no rule ${id}`);
    return found;
};
const run = (id: string, parts: LogParts): Finding[] => [rule(id).diagnose(crashView(parts)) ?? []].flat();
const specs = (ram: string, gpu: string, cpu = 'GenuineIntel') =>
    [`OS: Microsoft Windows 11`, `CPU: ${cpu}`, `PHYSICAL MEMORY: ${ram}`, 'VIRTUAL MEMORY: 90.11 GB/131072.00 GB', `GPU MEMORY: ${gpu}`];
const moduleLine = (name: string) => `${name.padEnd(40, ' ')}0x7FFA00000000`;

describe('memory rules', () => {
    it('d6ddda-texture-or-memory: the first line at D6DDDA, moved to the log\'s game version', () => {
        const [found] = run('d6ddda-texture-or-memory', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+0F32ACA') });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('Nexus 17230');
        // 1.6.1170's own offset, where Phostwood found it.
        expect(run('d6ddda-texture-or-memory', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+0D6DDDA') })).toHaveLength(1);
        // That offset in 1.7.104 is another place.
        expect(run('d6ddda-texture-or-memory', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+0D6DDDA') })).toEqual([]);
        expect(run('d6ddda-texture-or-memory', { callStack: [frame(0, 'SkyrimSE.exe+0F32ACA')] })).toEqual([]);
    });

    it('high-memory-usage: under 2 GB of RAM or 1 GB of video memory free; under 1 GB or 0.5 GB is likely', () => {
        const [low] = run('high-memory-usage', { systemSpecs: specs('30.10 GB/31.68 GB', '3.10/14.41 GB') });
        expect(low).toMatchObject({ title: expect.stringMatching(/^RAM nearly full/), confidence: 'possible',
            evidence: [{ place: 'systemSpecs', text: 'PHYSICAL MEMORY: 30.10 GB/31.68 GB' }] });
        expect(low.explain).toContain('1.6 GB of RAM');
        const [critical] = run('high-memory-usage', { systemSpecs: specs('14.02 GB/31.68 GB', '14.10/14.41 GB') });
        expect(critical).toMatchObject({ title: expect.stringMatching(/^Video memory/), confidence: 'likely',
            evidence: [{ place: 'systemSpecs', text: 'GPU MEMORY: 14.10/14.41 GB' }] });
        expect(run('high-memory-usage', { systemSpecs: specs('29.60 GB/31.68 GB', '13.40/14.41 GB') })).toEqual([]);
        expect(run('high-memory-usage', {})).toEqual([]);
    });

    it('memory-issue-indicators: memory words in the top half; likely with tbbmalloc.dll on the first line', () => {
        const [possible] = run('memory-issue-indicators', { stack: ['[RSP+30] 0x7FF6A0000000 (char*) "out of memory"'],
            systemSpecs: specs('20.00 GB/31.68 GB', '3.10/14.41 GB') });
        expect(possible).toMatchObject({ confidence: 'possible', evidence: [{ place: 'stack' }] });
        expect(possible.explain).toContain('RAM 20 GB of 31.7 GB');
        const [likely] = run('memory-issue-indicators', { firstLine: firstLineAt('tbbmalloc.dll+0012345') });
        expect(likely).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        // Crash Logger 1.27's C++ exception section.
        const [cpp] = run('memory-issue-indicators', {
            firstLine: firstLineAt('KERNELBASE.dll+0012345', 'mov rax, [rcx]', 'C++ Exception'),
            header: ['', 'C++ EXCEPTION:', '\tType: std::bad_alloc', '\tInfo: bad allocation']
        });
        expect(cpp).toMatchObject({ confidence: 'possible', evidence: [{ place: 'cppException', text: 'Type: std::bad_alloc' }] });
        // VIRTUAL MEMORY in SYSTEM SPECS is not in the top half; D6DDDA has its own rule.
        expect(run('memory-issue-indicators', { systemSpecs: specs('20.00 GB/31.68 GB', '3.10/14.41 GB') })).toEqual([]);
        expect(run('memory-issue-indicators', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+0D6DDDA') })).toEqual([]);
    });

    it('low-system-ram: 16 GB of RAM or less; 8 GB or less is very low', () => {
        const [low] = run('low-system-ram', { systemSpecs: specs('10.00 GB/15.92 GB', '3.10/8.00 GB') });
        expect(low).toMatchObject({ title: 'Low system RAM: 15.9 GB', confidence: 'possible', evidence: [{ place: 'systemSpecs' }] });
        const [veryLow] = run('low-system-ram', { systemSpecs: specs('6.00 GB/7.90 GB', '3.10/8.00 GB') });
        expect(veryLow.title).toBe('Very low system RAM: 7.9 GB');
        expect(veryLow.fix).toContain('light mods');
        expect(run('low-system-ram', { systemSpecs: specs('10.00 GB/31.68 GB', '3.10/14.41 GB') })).toEqual([]);
        expect(run('low-system-ram', {})).toEqual([]);
    });

    it('intel-13th-14th-gen-cpu: a 13th gen or an i5/i7/i9 14xxx CPU in SYSTEM SPECS', () => {
        const [thirteenth] = run('intel-13th-14th-gen-cpu', {
            systemSpecs: specs('10 GB/31.68 GB', '3/14 GB', 'GenuineIntel 13th Gen Intel(R) Core(TM) i7-13700K')
        });
        expect(thirteenth).toMatchObject({ title: expect.stringContaining('13th'), confidence: 'possible', evidence: [{ place: 'systemSpecs' }] });
        expect(thirteenth.fix).toContain('0x129');
        const [fourteenth] = run('intel-13th-14th-gen-cpu', {
            systemSpecs: specs('10 GB/31.68 GB', '3/14 GB', 'GenuineIntel Intel(R) Core(TM) i9-14900K')
        });
        expect(fourteenth.title).toContain('14th');
        expect(run('intel-13th-14th-gen-cpu', { systemSpecs: specs('10 GB/31.68 GB', '3/14 GB', 'GenuineIntel Intel(R) Core(TM) i7-12700K') })).toEqual([]);
        expect(run('intel-13th-14th-gen-cpu', { systemSpecs: specs('10 GB/31.68 GB', '3/14 GB', 'GenuineIntel Intel(R) Core(TM) i3-14100') })).toEqual([]);
    });

    it('vc-runtime-dll: a Visual C++ runtime DLL on the first line, or in the top half under KERNELBASE.dll', () => {
        const [direct] = run('vc-runtime-dll', { firstLine: firstLineAt('VCRUNTIME140.dll+0001234') });
        expect(direct).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        const [viaKernel] = run('vc-runtime-dll', {
            firstLine: firstLineAt('KERNELBASE.dll+0012345'), callStack: [frame(0, 'KERNELBASE.dll+0012345'), frame(1, 'MSVCP140.dll+0005678')]
        });
        expect(viaKernel.evidence.map(item => item.place)).toEqual(['firstLine', 'callStack']);
        expect(viaKernel.explain).toContain('KERNELBASE.dll');
        // Without KERNELBASE.dll on the first line, a runtime DLL in the stack does not count.
        expect(run('vc-runtime-dll', { callStack: [frame(0, 'SkyrimSE.exe+0012345'), frame(1, 'MSVCP140.dll+0005678')] })).toEqual([]);
        expect(run('vc-runtime-dll', { firstLine: firstLineAt('vcruntime140_1.dll+0001234') })).toEqual([]);
    });

    it('kernelbase-crash: KERNELBASE.dll on the first line, but not the upscaler or JContainers crash', () => {
        const [found] = run('kernelbase-crash', { firstLine: firstLineAt('KERNELBASE.dll+0012345') });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('sfc /scannow');
        expect(run('kernelbase-crash', { firstLine: firstLineAt('KERNELBASE.dll+0012345'),
            callStack: [frame(0, 'KERNELBASE.dll+0012345'), frame(1, 'SkyrimUpscaler.dll+0001234')] })).toEqual([]);
        expect(run('kernelbase-crash', { firstLine: firstLineAt('KERNELBASE.dll+0012345'),
            callStack: [frame(0, 'KERNELBASE.dll+0012345'), frame(1, 'JContainers64.dll+0001234')] })).toEqual([]);
        expect(run('kernelbase-crash', { firstLine: firstLineAt('KERNELBASE.dll+0012345'),
            stack: ['[RSP+30] 0x7FF6A0000000 (char*) "[json.exception.parse_error.101] parse error"'] })).toEqual([]);
        expect(run('kernelbase-crash', { callStack: [frame(0, 'KERNELBASE.dll+0012345')] })).toEqual([]);
    });

    it('wintrust-security: wintrust.dll in the top half', () => {
        const [found] = run('wintrust-security', { callStack: [frame(0, 'SkyrimSE.exe+0012345'), frame(1, 'WINTRUST.dll+0001234')] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'callStack' }] });
        expect(run('wintrust-security', { modules: [moduleLine('WINTRUST.dll')] })).toEqual([]);
    });

    it('antivirus: a third-party antivirus anywhere; certain with usvfs_x64.dll in the top quarter', () => {
        const [loaded] = run('antivirus', { modules: [moduleLine('bdcore.dll')] });
        expect(loaded).toMatchObject({ title: 'Third-party antivirus running with the game', confidence: 'possible',
            evidence: [{ place: 'modules' }] });
        expect(loaded.explain).toContain('Bitdefender');
        const [direct] = run('antivirus', { modules: [moduleLine('avast.dll')], callStack: [frame(0, 'usvfs_x64.dll+0001234')] });
        expect(direct).toMatchObject({ confidence: 'certain' });
        expect(direct.evidence.map(item => item.place).sort()).toEqual(['callStack', 'modules']);
        const [defender] = run('antivirus', { stack: ['[RSP+30] 0x7FF6A0000000 (void* -> MpClient.dll+0012345)'] });
        expect(defender).toMatchObject({ title: 'Windows Security in the crash', confidence: 'possible', evidence: [{ place: 'stack' }] });
        // Signatures are whole file names: ESET's eks.dll is not weeks.dll.
        expect(run('antivirus', { modules: [moduleLine('weeks.dll'), moduleLine('MpClient.dll')] })).toEqual([]);
        // usvfs_x64.dll with no antivirus loaded is usvfs-mo2's.
        expect(run('antivirus', { callStack: [frame(0, 'usvfs_x64.dll+0001234')] })).toEqual([]);
    });

    it('overlay: an overlay other than Steam\'s or Discord\'s, loaded or in the crash', () => {
        const [loaded] = run('overlay', { modules: [moduleLine('RTSSHooks64.dll'), moduleLine('rtsshdrs64.dll'), moduleLine('DiscordHook64.dll')] });
        expect(loaded).toMatchObject({ title: 'Overlay software running with the game', confidence: 'possible' });
        expect(loaded.evidence.map(item => item.place)).toEqual(['modules', 'modules']);
        expect(loaded.explain).toContain('RivaTuner Statistics Server (rtsshdrs64.dll)');
        expect(loaded.fix).toContain('Discord');
        const [inCrash] = run('overlay', { callStack: [frame(0, 'SkyrimSE.exe+0012345'), frame(1, 'SomeOverlay.dll+0001234')] });
        expect(inCrash).toMatchObject({ title: 'Overlay software in the crash', evidence: [{ place: 'callStack' }] });
        expect(inCrash.explain).toContain('SomeOverlay.dll');
        // Steam and Discord alone, and Overlay Fix, are not overlays to turn off.
        expect(run('overlay', {
            modules: [moduleLine('DiscordHook64.dll'), moduleLine('OverlayFix.dll')], callStack: [frame(0, 'GameOverlayRenderer64.dll+0001234')]
        })).toEqual([]);
    });

    it('usvfs-mo2: usvfs_x64.dll in the top half', () => {
        const [found] = run('usvfs-mo2', { stack: ['[RSP+30] 0x7FF6A0000000 (void* -> usvfs_x64.dll+0012345)'] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'stack' }] });
        expect(run('usvfs-mo2', { modules: [moduleLine('usvfs_x64.dll')] })).toEqual([]);
    });

    it('file-access: file, save or OneDrive indicators in the top half', () => {
        const [found] = run('file-access', {
            relevantObjects: ['RSP+40: (char*) "C:\\Users\\Me\\OneDrive\\Documents\\My Games\\Skyrim Special Edition\\Saves\\a.ess"']
        });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'relevantObjects' }] });
        expect(found.explain).toContain('a OneDrive path and the Documents\\My Games\\Skyrim folder');
        const [skse] = run('file-access', { callStack: [frame(0, 'skse64_1_7_104.dll+0012345', 'SKSEPersistentObjectStorage::Load')] });
        expect(skse.evidence).toMatchObject([{ place: 'callStack' }]);
        expect(run('file-access', { processInfo: ['Working Directory: C:\\Users\\Me\\OneDrive\\Skyrim'] })).toEqual([]);
    });

    it('xaudio: XAudio in the top half', () => {
        const [found] = run('xaudio', { firstLine: firstLineAt('XAudio2_7.dll+0012345') });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('Nexus 71567');
        expect(run('xaudio', { modules: [moduleLine('XAudio2_7.dll')] })).toEqual([]);
    });

    it('gamepad-controlmap: the game\'s gamepad handlers in the top half, as written', () => {
        const [found] = run('gamepad-controlmap', { relevantObjects: ['RCX: (BSWin32GamepadDevice*)'] });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'relevantObjects' }] });
        expect(found.fix).toContain('Nexus 89649');
        expect(found.explain).toContain('1.6.1130');
        expect(run('gamepad-controlmap', { game: '1.5.97', relevantObjects: ['RCX: (BSPCGamepadDeviceHandler*)'] })[0].explain)
            .not.toContain('1.6.1130');
        expect(run('gamepad-controlmap', { relevantObjects: ['RCX: (bswin32gamepaddevice*)'] })).toEqual([]);
    });

    it('keyboard-input: the game\'s keyboard device in the top half', () => {
        const [found] = run('keyboard-input', {
            firstLine: firstLineAt('SkyrimSE.exe+0CDE0AE', 'mov [r14+rbx*1+0x168], r8b |  BSWin32KeyboardDevice::Process_CDE0AE)')
        });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('Nexus 34705');
        expect(run('keyboard-input', { gameState: ['BSWin32KeyboardDevice'] })).toEqual([]);
    });

    it('first-line-module: a file on the first line, likely; a framework or SKSE itself, possible', () => {
        const [found] = run('first-line-module', { firstLine: firstLineAt('wheeler.dll+002B361') });
        expect(found).toMatchObject({ title: 'The crash happened in wheeler.dll', confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        expect(found.fix).toContain('Nexus 32444');
        const [explained] = run('first-line-module', { firstLine: firstLineAt('dxgi.dll+011563E') });
        expect(explained.explain).toContain('DirectX Graphics Infrastructure');
        const [framework] = run('first-line-module', { firstLine: firstLineAt('po3_PapyrusExtender.dll+0012345') });
        expect(framework.confidence).toBe('possible');
        // SKSE's DLL for a game version Phostwood did not know.
        expect(run('first-line-module', { firstLine: firstLineAt('skse64_1_7_104.dll+0012345') })[0].confidence).toBe('possible');
        expect(run('first-line-module', { firstLine: firstLineAt('SkyrimSE.exe+0012345') })).toEqual([]);
        expect(run('first-line-module', { firstLine: firstLineAt('KERNELBASE.dll+0012345') })).toEqual([]);
        expect(run('first-line-module', { firstLine: 'Unhandled exception "EXCEPTION_ACCESS_VIOLATION" at 0x000400000005' })).toEqual([]);
    });

    it('skyrim-crash-guard: Crash Logger 1.27\'s warning in the header, or the DLL loaded', () => {
        const [warned] = run('skyrim-crash-guard', {
            header: [
                '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!',
                '!!! WARNING: SkyrimCrashGuard DETECTED !!!',
                'SkyrimCrashGuard attempts to recover from crashes by performing unsafe operations.',
                'RECOMMENDED ACTION: Remove SkyrimCrashGuard or seek support from the author at:'
            ],
            modules: [moduleLine('SkyrimCrashGuard.dll')]
        });
        expect(warned).toMatchObject({ confidence: 'certain', evidence: expect.arrayContaining([expect.objectContaining({ place: 'header' }),
            expect.objectContaining({ place: 'modules' })]) });
        expect(warned.explain).toContain('Crash Logger warns');
        const [loaded] = run('skyrim-crash-guard', { sksePlugins: ['SkyrimCrashGuard.dll v1.0.0'] });
        expect(loaded).toMatchObject({ evidence: [{ place: 'sksePlugins' }] });
        expect(loaded.explain).not.toContain('Crash Logger warns');
        expect(run('skyrim-crash-guard', { sksePlugins: ['CrashLogger.dll v1.27.0'] })).toEqual([]);
    });
});
