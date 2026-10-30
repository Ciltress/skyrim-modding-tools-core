import type { CrashRule, CrashView, Evidence, Finding, View } from '../crashRules.js';

/**
 * Known crashes Phostwood's analyzer checks inline: the rules written straight into analyzeLog.js
 * rather than as functions of diagnoses.js. Ported from Phostwood's Skyrim Crash Log Analyzer
 * (github.com/Phostwood/crash-analyzer, GPL-3.0).
 */

type Text = Omit<Finding, 'evidence'>;

/** A finding when every group found something; its evidence is all of them, each line once. */
function allOf(groups: readonly Evidence[][], text: Text): Finding | undefined {
    if (groups.some(group => group.length === 0)) return undefined;
    const seen = new Set<string>();
    const evidence = groups.flat().filter(item => {
        const key = `${item.place}:${item.index}:${item.text}`;
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
    });
    return { ...text, evidence };
}

/**
 * Phostwood's Utils.getPercentAlphabetized: of each two .esp plugins next to each other in the load
 * order, the share that is in A-Z order. Undefined with fewer than two .esp plugins.
 */
export function percentAlphabetized(names: readonly string[]): number | undefined {
    const esps = names.filter(name => /\.esp$/i.test(name.trim())).map(name => name.trim().toLowerCase());
    const pairs = esps.length - 1;
    if (pairs < 1) return undefined;
    let ordered = 0;
    for (let i = 0; i < pairs; i++) if (esps[i].localeCompare(esps[i + 1]) < 0) ordered++;
    return ordered / pairs * 100;
}

/** Phostwood's Utils.getMatchingFilePaths: file paths in quotes or backticks with one of the extensions. */
function quotedFiles(view: CrashView, where: View, extensions: string): string[] {
    const pattern = new RegExp(`[\`"]([^"\`]+\\.(?:${extensions}))[\`"]`, 'gi');
    const files = new Set<string>();
    for (const line of view.lines(where)) for (const match of line.text.matchAll(pattern)) files.add(match[1].trim());
    return [...files];
}

const ENGINE_FIXES = 'SSE Engine Fixes (Nexus 17230)';

export const knownRules: CrashRule[] = [
    {
        id: 'vramr-gorehowl',
        ported: 'analyzeLog.js: VRAMr Gorehowl Crash Detected (diagnosis and insight)',
        diagnose: view => allOf([view.findAddress('firstLine', 'D6DDDA'), view.find('topQuarter', 'Gorehowl')], {
            title: 'VRAMr Gorehowl crash (D6DDDA with Gorehowl)',
            confidence: 'certain',
            explain: 'The game crashed loading a texture (D6DDDA) with the weapon Gorehowl among the objects. VRAMr ' +
                'versions before April 19, 2024 made Gorehowl textures that crash during the quest Night at the Museum.',
            fix: 'Update VRAMr (Nexus 90557) to its April 19, 2024 version or later and run it again. Otherwise disable ' +
                'VRAMr\'s output mod until Night at the Museum is done, then enable it again; or delete the clgorehowl ' +
                '.dds textures from VRAMr\'s output.'
        })
    },
    {
        id: 'wraith-of-sithis',
        ported: 'analyzeLog.js: Gravelord / "Wraith of Sithis" Crash Detected',
        diagnose: view => allOf([view.find('topThird', 'Wraith of Sithis'), view.find('topThird', 'mihailmmasithis.esp')], {
            title: 'Wraith of Sithis crash (MihailMods with Odin)',
            confidence: 'certain',
            explain: 'MihailMods\' Wraith of Sithis (mihailmmasithis.esp) conflicts with Odin - Skyrim Magic Overhaul and ' +
                'often crashes soon after someone summons a Wraith of Sithis. It is also known as the Gravelord crash.',
            fix: 'Install the Odin and Gravelords Compatibility Patch from Discrepancy\'s patch settings hub (Nexus 108591), ' +
                'choosing the option for the standalone mihailmmasithis.esp. It can be installed mid-game.'
        })
    },
    {
        id: 'alphabetized-load-order',
        ported: 'analyzeLog.js: Alphabetized Load Order Detected',
        diagnose: view => {
            const percent = percentAlphabetized(view.log.plugins.map(plugin => plugin.name));
            if (view.log.plugins.length <= 20 || percent === undefined || percent <= 80) return undefined;
            return {
                title: 'Load order sorted alphabetically',
                confidence: 'certain',
                explain: `${Math.round(percent)}% of the .esp plugins follow each other in alphabetical order: the load ` +
                    'order was sorted by name, not by what each plugin needs, which breaks mods and makes the game unstable.',
                fix: 'Back up the load order, then sort it with LOOT; LOOT misplaces a few plugins, so check its result, ' +
                    'more so on a large list. On a premade mod list (Nexus Collection, Wabbajack) do not sort with LOOT: ' +
                    'restore the order the list ships with, or reinstall the list.',
                evidence: view.find('plugins', /^Light:/i)
            };
        }
    },
    {
        id: 'physics-12f4797',
        ported: 'analyzeLog.js: 12F4797 Physics Crash Detected',
        // Phostwood matched .NET Script Framework's `NiCamera(Name: `WorldRoot Camera`)` and
        // `BSMultiBoundRoom(Name: null)`; Crash Logger writes `(NiCamera*) "WorldRoot Camera"`, and no name at all.
        diagnose: view => allOf([
            view.findAddress('firstLine', '12F4797'),
            view.find('relevantObjects', /\(NiCamera\*\)\s*"WorldRoot Camera"/i),
            view.find('relevantObjects', /\(BSMultiBoundRoom\*\)(?!\s*")/i)
        ], {
            title: '12F4797 physics crash: too many physics objects at once',
            confidence: 'certain',
            explain: 'The game crashed in its physics code (12F4797) with the world camera and an unnamed room bound among ' +
                'the objects. It is known from Ancestor Glade, and saves made there, with many physics-enabled items ' +
                'active at once, such as the player\'s armor and hair.',
            fix: 'Swap physics-enabled armor, clothing and hair on the player and followers for non-physics versions; over ' +
                'physics hair, wear a non-physics helmet. Then load the save again.'
        })
    },
    {
        id: 'medal-streaming',
        ported: 'analyzeLog.js: Medal Streaming Software Detected',
        diagnose: view => allOf([view.find('all', 'medal-hook64.dll')], {
            title: 'Medal streaming software is hooked into the game',
            confidence: 'certain',
            explain: 'medal-hook64.dll is loaded in the game. Medal often causes dark or distorted graphics and crashes to ' +
                'desktop in Skyrim, more so after its recent updates.',
            fix: 'Turn Medal off in its settings before starting the game. If it still loads, end the Medal process in Task ' +
                'Manager first; as a last resort, uninstall Medal.'
        })
    },
    {
        id: 'upscaler-first-line',
        ported: 'analyzeLog.js: Upscaler Issue Detected',
        diagnose: view => allOf([view.find('firstLine', 'nvngx_dlss.dll', 'PDPerfPlugin.dll', 'SkyrimUpscaler.dll')], {
            title: 'Upscaler crash: an upscaler DLL in the first line',
            confidence: 'certain',
            explain: 'The crash happened in an upscaler file (SkyrimUpscaler.dll, PDPerfPlugin.dll or nvngx_dlss.dll). It ' +
                'usually comes from the NVIDIA graphics driver or one of PureDark\'s upscalers.',
            fix: 'Update the NVIDIA graphics driver; if that does not help, reinstall it cleanly with Display Driver ' +
                'Uninstaller. Reset any GPU overclock. With one of PureDark\'s paid FSR2/FSR3 upscalers, check the card ' +
                'fits it (NVIDIA RTX; RTX 40 series for FSR3) and its recommended settings. Otherwise disable the upscaler ' +
                'and use TAA.'
        })
    },
    {
        id: 'kernelbase-json',
        ported: 'analyzeLog.js: KERNELBASE JSON Crash Detected',
        // Crash Logger 1.2x writes a C++ exception's message in a section of its own, before the call stack.
        diagnose: view => allOf([
            view.find('firstLine', 'KERNELBASE.dll'),
            [...view.find('topHalf', 'json.exception.parse_error'), ...view.find('cppException', 'json.exception.parse_error')]
        ], {
            title: 'KERNELBASE crash on a broken JSON file',
            confidence: 'certain',
            explain: 'A mod failed to read a .json file (json.exception.parse_error) and the error ended the game. The file ' +
                'is corrupt or was edited by hand, Windows permissions block it, or SSE Engine Fixes before version 7 ' +
                'has MaxStdio set too low.',
            fix: `With ${ENGINE_FIXES} before version 7, set MaxStdio = 8192 in EngineFixes.toml. Otherwise find the ` +
                '.json file: repair it, restore a backup, or delete it so its mod writes a new one. If Windows permissions ' +
                'block it, reset them; a new Windows user with a new save also works around it.'
        })
    },
    {
        id: 'access-violation-c0000005',
        ported: 'analyzeLog.js: Memory Access Violation Detected',
        // Phostwood matches the code as written, case and all; a longer number holding it is not the code.
        diagnose: view => allOf([view.find('topHalf', /0xC0000005(?![0-9A-Fa-f])/)], {
            title: 'Memory access violation (0xC0000005)',
            confidence: 'likely',
            explain: 'The code 0xC0000005 is a memory access violation: the game read or wrote memory it should not. ' +
                'Common causes are a missing master file, incompatible mods, and plugins in a file format this game ' +
                'version does not read.',
            fix: 'Check that every plugin\'s masters are installed; if a mod was removed, put it back or remove it as a ' +
                'master from the plugins that need it. Install Backported Extended ESL Support (Nexus 106441) if a new ' +
                'mod needs it. Check mods for conflicts, and that each is made for this game version.'
        })
    },
    {
        id: 'compressed-archive-stream',
        ported: 'analyzeLog.js: CompressedArchiveStream Issue Detected',
        diagnose: view => {
            const textures = quotedFiles(view, 'topHalf', 'dds|btr|tga|bmp|bsa');
            const named = textures.length > 0
                ? `Files it names: ${textures.slice(0, 5).join(', ')}.`
                : 'No texture file is named in the log, so this is less likely the cause.';
            return allOf([view.find('topHalf', 'CompressedArchiveStream')], {
                title: 'CompressedArchiveStream: a corrupt compressed texture',
                confidence: 'possible',
                explain: 'The game failed reading a compressed file from a .bsa archive, which usually means a corrupt ' +
                    `texture, often one a mod overwrites from the game or a DLC. ${named}`,
                fix: 'Check which mod overwrites game or DLC textures. Disable the texture mods tied to the crash one at a ' +
                    'time to find it, then reinstall that mod. BSA Browser can extract a mod\'s .bsa archives to inspect ' +
                    'the textures in them.'
            });
        }
    },
    {
        id: 'monster-mod',
        ported: 'analyzeLog.js: 5999C7 or D02C2C Detected',
        diagnose: view => {
            const plugin = view.find('plugins', /\]\s+Monster Mod\.esp$/i);
            return allOf([[...view.findAddress('firstLine', '5999C7'), ...view.findAddress('firstLine', 'D02C2C')]], {
                title: '5999C7 or D02C2C crash, often from Monster Mod',
                confidence: 'possible',
                explain: 'Crashes at 5999C7 or D02C2C are often caused by Monster Mod.esp, which crashes to desktop even ' +
                    'with its unofficial patches and latest updates. ' +
                    (plugin.length > 0 ? 'Monster Mod.esp is in this load order.' : 'Monster Mod.esp is not in this load order.'),
                fix: 'Update Monster Mod and apply its unofficial patch; check its compatibility with other mods and its ' +
                    'place in the load order (LOOT can sort it, though not on a premade mod list). If it still crashes, ' +
                    'disable it and clean the save with FallrimTools ReSaver (Nexus 5031).'
            });
        }
    },
    {
        id: 'improved-camera-dll',
        ported: 'analyzeLog.js: ImprovedCameraSE.dll+ Issue Detected',
        diagnose: view => allOf([view.find('topHalf', 'ImprovedCameraSE.dll')], {
            title: 'Improved Camera SE (ImprovedCameraSE.dll) near the crash',
            confidence: 'possible',
            explain: 'ImprovedCameraSE.dll is named near the crash, so it may come from Improved Camera SE; its exact ' +
                'cause is hard to pin down.',
            fix: 'Add -forcesteamloader to the SKSE launch arguments. If ReShade is installed, set MenuMode=0 in its .ini ' +
                'file. If it still crashes, ask on the mod\'s page with the crash log.'
        })
    },
    {
        id: 'simplest-horses',
        ported: 'analyzeLog.js: Potential Conflict with Simplest Horses',
        diagnose: view => allOf([view.find('topQuarter', 'SimplestHorses.esp')], {
            title: 'Simplest Horses near the crash',
            confidence: 'possible',
            explain: 'SimplestHorses.esp is among the crash\'s objects. Simplest Horses can conflict with other mods, ' +
                'especially in customized mod lists.',
            fix: 'Before fast travel, command the horse to wait (H while not targeting it). Disable horse followers in the ' +
                'follower framework\'s MCM (such as Nether\'s Follower Framework). If it still crashes, disable recently ' +
                'added mods, or Simplest Horses and Simplest Horses - Animated Whistling Patch.'
        })
    },
    {
        id: 'animated-ice-floes',
        ported: 'analyzeLog.js: Potential Conflict with Animated Ice Floes',
        diagnose: view => allOf([view.find('topQuarter', 'Animated Ice Floes.esp')], {
            title: 'Animated Ice Floes near the crash',
            confidence: 'possible',
            explain: 'Animated Ice Floes.esp is among the crash\'s objects. It is thought to conflict with some other ' +
                'mods in customized mod lists.',
            fix: 'Disable the mods added most recently; if it still crashes, disable Animated Ice Floes.'
        })
    },
    {
        id: 'forced-termination-0cb748e',
        ported: 'analyzeLog.js: 0CB748E Detected',
        diagnose: view => allOf([view.findAddress('firstLine', '0CB748E')], {
            title: '0CB748E: the game was ended from outside',
            confidence: 'possible',
            explain: 'A crash at 0CB748E usually means the game process was forcibly terminated rather than a fault in ' +
                'the game: it skipped its normal shutdown, and the log may be incomplete.',
            fix: 'No mod needs changing for this log. If the game was closed from Task Manager or by another program, that ' +
                'is the cause; if it ended on its own, read the next crash log.'
        })
    },
    {
        id: 'engine-fixes-durability-7428b1',
        ported: 'analyzeLog.js: 7428B1 Detected',
        diagnose: view => allOf([view.findAddress('firstLine', '7428B1')], {
            title: '7428B1 crash: SSE Engine Fixes or Equipment Durability System',
            confidence: 'possible',
            explain: 'Crashes at 7428B1 are often tied to SSE Engine Fixes, to Equipment Durability System when an ' +
                'enchanted weapon breaks, or to mods that change characters holding a weapon (animations, equipment, ' +
                'models).',
            fix: `Reinstall ${ENGINE_FIXES} as the sticky posts on its Nexus page describe. If Equipment Durability ` +
                'System is installed, disable it; otherwise disable recently added mods that change animations, ' +
                'equipment or character models, one at a time.'
        })
    },
    {
        id: 'engine-fixes-display-tweaks-8bda97',
        ported: 'analyzeLog.js: 8BDA97 Detected',
        diagnose: view => allOf([view.findAddress('firstLine', '8BDA97')], {
            title: '8BDA97 crash: SSE Engine Fixes and SSE Display Tweaks conflict',
            confidence: 'possible',
            explain: 'Crashes at 8BDA97 may come from SSE Engine Fixes and SSE Display Tweaks overriding each other\'s ' +
                'settings.',
            fix: `Update ${ENGINE_FIXES} and SSE Display Tweaks (Nexus 34705), and set them so the two do not change the ` +
                'same thing, as each one\'s documentation recommends. If it still crashes, run with one of them at a time ' +
                'to find the setting.'
        })
    },
    {
        id: 'controlmap-hotkeys-c1315c',
        ported: 'analyzeLog.js: C1315C Detected',
        diagnose: view => allOf([view.findAddress('firstLine', 'C1315C')], {
            title: 'C1315C crash: controlmap.txt changed by a hotkey mod',
            confidence: 'possible',
            explain: 'Crashes at C1315C are often tied to controlmap.txt, which mods with hotkeys replace or change.',
            fix: 'Find the mods installed or updated recently that change controlmap.txt, and check their pages for known ' +
                'issues. Restore the original controlmap.txt or a backup of it; if it still crashes, disable the suspected ' +
                'mods one at a time.'
        })
    },
    {
        id: 'save-game-d2b923',
        ported: 'analyzeLog.js: D2B923 Detected',
        diagnose: view => allOf([view.findAddress('firstLine', 'D2B923')], {
            title: 'D2B923 crash: a save game problem',
            confidence: 'possible',
            explain: 'Crashes at D2B923 are often tied to saves, and to mods that change the save system, such as Save ' +
                'System Overhaul (SSO) or Alternate Start - Live Another Life.',
            fix: 'If SkyUI shows flashing save entries, install SkyUI SE - Flashing Savegames Fix on a game before ' +
                '1.6.1130, or SkyUI SE - Difficulty Persistence Fix (which includes it) on 1.6.1130 and later. Follow ' +
                'Jerilith\'s Safe Save Guide.'
        })
    },
    {
        id: 'skse64-loader',
        ported: 'analyzeLog.js: skse64_loader.exe Issue Detected',
        diagnose: view => allOf([view.find('topHalf', 'skse64_loader.exe')], {
            title: 'skse64_loader.exe near the crash: an SKSE problem',
            confidence: 'possible',
            explain: 'skse64_loader.exe is named near the crash, which points to Skyrim Script Extender (SKSE) or a mod ' +
                'that uses it.',
            fix: 'Start the game through skse64_loader.exe. Check that Skyrim Script Extender (Nexus 30379) matches the ' +
                'game version and that every SKSE plugin is up to date for it. Running as administrator, or adding SKSE ' +
                'and the mod manager as antivirus exceptions, can also help.'
        })
    },
    {
        id: 'zero-address',
        ported: 'analyzeLog.js: 0x0 on thread Issue Detected',
        // .NET Script Framework wrote `at 0x0 on thread`; Crash Logger writes the address padded, with no module after it.
        diagnose: view => allOf([view.find('firstLine', /\bat 0x0+(?![0-9A-F])(?!\s+\S+\+[0-9A-F]+)/i)], {
            title: 'Crash at address 0x0: often face lighting or shadows',
            confidence: 'possible',
            explain: 'The game ran code at address 0x0, a rare engine fault often tied to face lighting or shadows.',
            fix: `Install or update ${ENGINE_FIXES} to the latest version for this game. Check mods that change ` +
                'lighting, shadows or weather for conflicts and disable them one at a time. Update the graphics driver.'
        })
    },
    {
        id: 'dyndolod-esm',
        ported: 'analyzeLog.js: DynDOLOD.esm Detected',
        diagnose: view => allOf([view.find('topHalf', 'DynDOLOD.esm')], {
            title: 'DynDOLOD.esm near the crash',
            confidence: 'possible',
            explain: 'DynDOLOD.esm is named near the crash. DynDOLOD and Occlusion plugins often appear in crash logs ' +
                'without being the cause, but LOD made for another load order can crash the game or load forever.',
            fix: 'Use the latest DynDOLOD and generate its LOD again for the current load order; read its log and summary ' +
                'for warnings known to cause crashes. Avoid TreeFullFallBack=0. With a large load order, set Temporary=1 ' +
                'in DynDOLOD_SSE.ini, or better, convert large new land plugins to ESM.'
        })
    },
    {
        id: 'horse-follower-pathing',
        ported: 'analyzeLog.js: Horse Follower Pathing Issue',
        // Phostwood matches Pathing in its case; "mount" was dropped there for matching "Mountain".
        diagnose: view => allOf([view.find('topHalf', /Pathing/), view.find('topQuarter', 'horse', 'pony')], {
            title: 'Horse follower pathing crash',
            confidence: 'possible',
            explain: 'The crash happened in pathing with a horse or pony among the objects: a horse or pony follower that ' +
                'fails to find its way to the player can crash the game.',
            fix: 'Disable horse followers in the follower framework\'s MCM (such as Nether\'s Follower Framework), or ' +
                'always command the horse to wait before fast travel.'
        })
    },
    {
        id: 'hdt-smp-physics',
        ported: 'analyzeLog.js: hdtSMP64.dll Physics Issue Detected',
        diagnose: view => allOf([view.find('topHalf', 'hdtSMP64.dll')], {
            title: 'hdtSMP64.dll (HDT-SMP physics) near the crash',
            confidence: 'possible',
            explain: 'hdtSMP64.dll is often in crash logs without being the cause. Seen often, it points to its settings ' +
                'or to NPCs wearing HDT-SMP physics armor, clothing or hair.',
            fix: 'Check that hdtSMP64.dll matches the game and SKSE versions, and update the mod it comes with. Review its ' +
                'HDT-SMP settings. As a workaround, wear non-physics armor, clothing and hair, or a non-physics helmet over ' +
                'physics hair. If it still crashes, disable HDT-SMP mods one at a time.'
        })
    },
    {
        id: 'cbp-physics',
        ported: 'analyzeLog.js: cbp.dll Physics Issue Detected',
        diagnose: view => allOf([view.find('topHalf', 'cbp.dll')], {
            title: 'cbp.dll (CBP physics) near the crash',
            confidence: 'possible',
            explain: 'cbp.dll is often in crash logs without being the cause. Seen often, it points to its settings or to ' +
                'NPCs wearing CBP physics clothing.',
            fix: 'Check that cbp.dll matches the game and SKSE versions, and update the mod it comes with. Review its CBP ' +
                'settings. As a workaround, wear non-physics armor, clothing and hair. If it still crashes, disable CBP ' +
                'mods one at a time; restarting the PC or verifying the game files on Steam has also helped.'
        })
    }
];
