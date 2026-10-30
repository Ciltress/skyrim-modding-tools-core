import type { CrashRule, CrashView, Evidence, Needle, View } from '../crashRules.js';

/**
 * SKSE plugins, their frameworks and the interface mods built on them. Ported from Phostwood's Skyrim
 * Crash Log Analyzer (github.com/Phostwood/crash-analyzer, GPL-3.0), diagnoses.js.
 */

/** `1.6.1170` against `1.6.640`, part by part; a missing part counts as 0. */
function compareVersions(a: string, b: string): number {
    const left = a.split('.').map(part => parseInt(part, 10) || 0);
    const right = b.split('.').map(part => parseInt(part, 10) || 0);
    for (let i = 0; i < Math.max(left.length, right.length); i++) {
        const difference = (left[i] ?? 0) - (right[i] ?? 0);
        if (difference !== 0) return Math.sign(difference);
    }
    return 0;
}

/** An SKSE plugin's version as the log lists it; `0.0.0.1` when listed without one, as Phostwood reads it. */
function sksePluginVersion(view: CrashView, name: string): string | undefined {
    const plugin = view.log.sksePlugins.find(entry => entry.name.toLowerCase() === name.toLowerCase());
    return plugin ? plugin.version ?? '0.0.0.1' : undefined;
}

/** Engine Fixes 7 exists for 1.5.97 and 1.6.1170 on; 1.7 is past 1.6.1170, so it counts. */
function hasEngineFixes7Build(view: CrashView): boolean {
    const version = view.gameVersion;
    return version !== undefined && (version === '1.5.97' || compareVersions(version, '1.6.1170') >= 0);
}

/** Lines found twice — the first line is in every top view — count once. */
function unique(evidence: Evidence[]): Evidence[] {
    const seen = new Set<string>();
    return evidence.filter(item => {
        const key = `${item.place}:${item.index}`;
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
    });
}

/** The needles a view holds, each as Phostwood names it. */
function presentNeedles(view: CrashView, place: View, needles: readonly string[]): string[] {
    return needles.filter(needle => view.has(place, needle));
}

/** Phostwood's reinstallEngineFixesInstructions, condensed. */
function engineFixesSteps(view: CrashView, verb: 'Install' | 'Reinstall'): string {
    const version = sksePluginVersion(view, 'EngineFixes.dll');
    const pre7 = !version || compareVersions(version, '7') < 0;
    const install = `${verb} both parts of SSE Engine Fixes (Nexus 17230), often done wrong: part 1, the SKSE plugin for ` +
        'this game version, with the mod manager; part 2, its DLL files, copied by hand into the game folder beside SkyrimSE.exe.';
    if (!pre7) return install;
    return install + (hasEngineFixes7Build(view)
        ? ' Use version 7; below 7, set SaveGameMaxSize = true and MaxStdio = 8192 in EngineFixes.toml.'
        : ' In EngineFixes.toml set SaveGameMaxSize = true and MaxStdio = 8192, or use the preconfigured TOML (Nexus 108069).');
}

const ENGINE_FIXES_FILES = ['EngineFixes.dll', 'engine_fixes.dll', 'd3dx9_42.dll', 'tbbmalloc.dll', 'tbb.dll'];
const ENGINE_FIXES_LATEST = '7.0.20';
const ENGINE_FIXES_SHADOW_SCENE = '7.0.18';
const CRASH_LOGGER_LATEST = '1.27.0';

// Matched as Phostwood does, case and all.
const SSE_FIXES_IMPACT_EFFECTS = [
    'BloodHitEffectBlunt', 'BloodHitEffectCut', 'BloodSprayAlduinArrowImpact', 'BloodSprayArrowImpact01',
    'BloodSprayHammerImpact01', 'BloodSprayImpact01', 'BloodSprayUnarmedImpact01', 'FistBloodSm', 'FistBloodMed',
    'FistBloodLg', 'ImpactArrowDirt01', 'ImpactArrowDust01', 'PierceBone'
];

const JOURNAL_INDICATORS: readonly Needle[] = [/Interface[\\/]Quest_Journal\.swf/i, 'Journal_SystemTab', 'JournalMenu'];

const ANIMATION_CODES = [
    '.hkx', 'ahkpCharacterRigidBody', 'animation', 'animationgraph', 'bhkCharRigidBodyController',
    'BSAnimationGraphManager', 'bshkbanimationgraph', 'hkpSimpleConstraintContactMgr'
];

const ANIMATION_LOADER_CODES = [
    '.hkb', '.hkx', '0_master.hkb', 'behavior', 'BShkbAnimationGraph', 'dynamicanimationreplacer.dll',
    'dynamicanimationreplacer.ini', 'hkbBehaviorGraph', 'hkbCharacter', 'hkbClipGenerator', 'hkbehaviorgraph',
    'hkbStateMachine', 'hkbvariablebindingset', 'openanimationreplacer.dll', 'openanimationreplacer.pdb'
];
// A bare offset not in gameAddresses.ts: kept for the version Phostwood's offsets are from, where it means what it says.
const ANIMATION_LOADER_OFFSET = '67B88B';

const BEHAVIOR_ENGINES = 'Nemesis (Nexus 60033) or Pandora (Nexus 133232); FNIS (Nexus 3038) users should move to one of them';

export const skseRules: CrashRule[] = [
    {
        id: 'engine-fixes-missing',
        ported: 'diagnoses.js analyzeEngineFixes',
        diagnose: view => {
            // A log cut short may not have reached the SKSE plugins it would list.
            if (!view.log.complete || view.lines('bottomHalf').length <= 10) return undefined;
            if (view.has('bottomHalf', 'EngineFixes.dll', 'EngineFixesVR.dll')) return undefined;
            const wheeler = view.find('bottomHalf', 'wheeler.dll');
            const wheelerCrashes = wheeler.length > 0 && view.gameVersion !== undefined && compareVersions(view.gameVersion, '1.6.1170') >= 0;
            return {
                title: 'SSE Engine Fixes is not installed',
                confidence: wheelerCrashes ? 'certain' : 'likely',
                explain: 'No EngineFixes.dll is loaded. SSE Engine Fixes fixes many engine bugs, over 100 mods require it, ' +
                    'and without it a save can become unplayable.' +
                    (wheelerCrashes ? ' Wheeler.dll v1 is confirmed to crash without it on game 1.6.1170 and later.' : ''),
                fix: engineFixesSteps(view, 'Install'),
                evidence: wheelerCrashes ? wheeler : [],
                // Without Wheeler it is true of the setup whatever crashed, and an answer ranked it above the logo mesh that
                // crashed the game (eval round 87's crash-mesh #4).
                ...(wheelerCrashes ? {} : { setup: true as const })
            };
        }
    },
    {
        id: 'engine-fixes-first-line',
        ported: 'diagnoses.js analyzeFirstLineEngineFixesCrash',
        diagnose: view => {
            const evidence = view.find('firstLine', ...ENGINE_FIXES_FILES);
            if (evidence.length === 0) return undefined;
            const file = ENGINE_FIXES_FILES.find(name => view.has('firstLine', name));
            return {
                title: `The crash is in ${file} (SSE Engine Fixes)`,
                confidence: 'likely',
                explain: `${file} on the first line may mean SSE Engine Fixes is installed wrong, or that a mod using it ` +
                    'has a problem or an incompatibility.',
                fix: `${engineFixesSteps(view, 'Reinstall')} If the crash comes back once it is right, disable the mods named ` +
                    'near the crash one by one or in shrinking groups.',
                evidence
            };
        }
    },
    {
        id: 'engine-fixes-outdated',
        ported: 'diagnoses.js checkEngineFixesUpdate',
        diagnose: view => {
            if (!hasEngineFixes7Build(view)) return undefined;
            const shadowScene = view.find('topHalf', 'ShadowSceneNode');
            const version = sksePluginVersion(view, 'EngineFixes.dll');
            const outdated = version !== undefined && compareVersions(version, ENGINE_FIXES_LATEST) < 0;
            const preventable = shadowScene.length > 0 && (version === undefined || compareVersions(version, ENGINE_FIXES_SHADOW_SCENE) < 0);
            if (!outdated && !preventable) return undefined;
            return {
                title: preventable ? 'Updating SSE Engine Fixes may prevent this crash' : `SSE Engine Fixes is older than ${ENGINE_FIXES_LATEST}`,
                confidence: 'possible',
                explain: (preventable
                    ? `ShadowSceneNode is near the crash; SSE Engine Fixes ${ENGINE_FIXES_SHADOW_SCENE} and later prevent many of these.`
                    : 'Newer versions of SSE Engine Fixes prevent more kinds of crashes.') +
                    (version ? ` The log lists EngineFixes.dll ${version}.` : ' The log lists no EngineFixes.dll.'),
                fix: `${version ? 'Update' : 'Install'} SSE Engine Fixes (Nexus 17230) to ${ENGINE_FIXES_LATEST} or later, following ` +
                    'the sticky posts in its forum, and carry over any custom settings from its .toml and .ini files.',
                evidence: [...(preventable ? shadowScene : []), ...view.find('sksePlugins', /^EngineFixes\.dll\b/i)],
                ...(preventable ? {} : { setup: true as const })
            };
        }
    },
    {
        id: 'sse-fixes-on-ae',
        ported: 'diagnoses.js analyzeSSEFixesIssues',
        diagnose: view => {
            if (!view.isAE) return undefined;
            const effects = view.find('topHalf', ...SSE_FIXES_IMPACT_EFFECTS.map(name => new RegExp(name)));
            const plugin = view.find('bottomHalf', 'FpsFixPlugin.dll');
            if (effects.length === 0 || plugin.length === 0) return undefined;
            return {
                title: 'SSE Fixes (FpsFixPlugin.dll) crashed on an impact effect',
                confidence: 'likely',
                explain: 'SSE Fixes is made for game 1.5.97 only; on later versions it crashes, mostly in combat on blood ' +
                    'spray and impact effects, and more often with Precision installed.',
                fix: 'Disable SSE Fixes (Nexus 10547), the mod with FpsFixPlugin.dll. It is not SSE Engine Fixes (Nexus 17230): ' +
                    'keep that. If you need SSE Fixes for its mutex locking, turn off every other tweak in its config, or use ' +
                    'another mod for mutex locking.',
                evidence: [...effects, ...plugin]
            };
        }
    },
    {
        id: 'crash-logger-outdated',
        ported: 'diagnoses.js checkCrashLoggerVersionUpdate',
        diagnose: view => {
            const version = view.log.loggerVersion;
            if (!version || compareVersions(version, CRASH_LOGGER_LATEST) >= 0) return undefined;
            return {
                title: `Crash Logger SSE ${version} is older than ${CRASH_LOGGER_LATEST}`,
                confidence: 'possible',
                explain: 'Newer versions of Crash Logger fix bugs and write fuller logs, which can make the cause of a crash easier to find.',
                fix: `Update Crash Logger SSE AE VR (Nexus 59818) to ${CRASH_LOGGER_LATEST} or later.`,
                evidence: view.find('header', /^CrashLogger/i),
                setup: true
            };
        }
    },
    {
        id: 'netscriptframework-installed',
        // Phostwood reads it from an NSF log's own header; a Crash Logger log shows it among the modules instead.
        ported: 'diagnoses.js checkNetScriptFrameworkStatus',
        diagnose: view => {
            const evidence = view.find('bottomHalf', 'NetScriptFramework');
            if (evidence.length === 0) return undefined;
            return {
                title: '.NET Script Framework is installed beside Crash Logger',
                confidence: 'possible',
                explain: '.NET Script Framework (NetScriptFramework) is an older crash logger, last updated in October 2021, ' +
                    'that does not support recent game versions. Crash Logger SSE replaces it.',
                fix: 'Disable .NET Script Framework (Nexus 21294) and any other crash logging mod; keep Crash Logger SSE AE VR ' +
                    '(Nexus 59818), which writes its logs to Documents\\My Games\\Skyrim Special Edition\\SKSE.',
                evidence,
                setup: true
            };
        }
    },
    {
        id: 'jcontainers',
        ported: 'diagnoses.js analyzeJContainersCrash',
        diagnose: view => {
            let evidence = view.find('firstLine', 'JContainers64.dll');
            if (evidence.length === 0 && view.has('firstLine', 'KERNELBASE.dll')) {
                const callers = view.find('callStack', 'JContainers64.dll', 'CustomSkills.dll');
                if (callers.length > 0) evidence = [...view.find('firstLine', 'KERNELBASE.dll'), ...callers];
            }
            if (evidence.length === 0) return undefined;
            return {
                title: 'JContainers crashed',
                confidence: 'certain',
                explain: 'Usually Windows permissions block the storage JContainers uses, or one of its .json storage files is broken.',
                fix: 'If you use Custom Skills Menu, install Custom Skills Menu - JContainers Crash Workaround (Nexus 140833). ' +
                    'Otherwise reset Windows file permissions to their defaults, or make a new Windows user and play from it. ' +
                    'Or find the broken .json file, often in Documents\\My Games\\Skyrim Special Edition\\JCUser, and repair it, ' +
                    'restore a backup, or delete it so its mod makes a new one.',
                evidence
            };
        }
    },
    {
        id: 'dbskse-functions',
        ported: 'diagnoses.js analyzeDbSkseFunctionsCrash',
        diagnose: view => {
            const needle = /DbSkseFunctions\.dll/;
            const onFirstLine = view.find('firstLine', needle);
            const evidence = onFirstLine.length > 0 ? onFirstLine : view.find('topThird', needle);
            if (evidence.length === 0) return undefined;
            return {
                title: onFirstLine.length > 0 ? 'The crash is in DbSkseFunctions.dll' : 'DbSkseFunctions.dll is near the crash',
                confidence: onFirstLine.length > 0 ? 'certain' : 'likely',
                explain: 'These crashes usually come from the projectile tracking of DbSkseFunctions (Dylbill\'s Papyrus ' +
                    'Functions), most often in combat or spellcasting with projectiles mods add.',
                fix: 'In Data\\SKSE\\Plugins\\DbSkseFunctions.ini set iMaxArrowsSavedPerReference=0, which turns its ' +
                    'projectile tracking off. If the crash continues, disable DbSkseFunctions and the mods that need it, then ' +
                    'enable them again one by one.',
                evidence
            };
        }
    },
    {
        id: 'skee64-racemenu',
        ported: 'diagnoses.js analyzeSkee64Issue',
        diagnose: view => {
            const evidence = view.find('topHalf', 'skee64.dll');
            if (evidence.length === 0) return undefined;
            const onFirstLine = evidence.some(item => item.place === 'firstLine');
            const meshes = [...new Set(view.lines('topHalf').flatMap(line => line.text.match(/[^\s"'()|]+\.nif\b/gi) ?? []))].slice(0, 3);
            return {
                title: onFirstLine ? 'The crash is in RaceMenu (skee64.dll)' : 'RaceMenu (skee64.dll) is near the crash',
                confidence: 'likely',
                explain: 'RaceMenu, or a mod that uses it, is involved: overlays (tattoos, body paints, piercings, scars), ' +
                    'hair, eyes, face sculpts, head or body morphs.',
                fix: 'Try RaceMenu OverlayFix and Various Mod Fixes (Nexus 138586). Overlays are a common cause: remove a ' +
                    'wearable one in game before disabling its mod. Update RaceMenu (Nexus 19080) and its add-ons for this game ' +
                    'version; check mods recently changed that touch character models, then disable them one by one.' +
                    (meshes.length > 0 ? ` Meshes named near the crash: ${meshes.join(', ')}.` : ''),
                evidence
            };
        }
    },
    {
        id: 'xpmse',
        ported: 'diagnoses.js analyzeXPMSECrash',
        diagnose: view => {
            const evidence = view.find('topHalf', 'XPMSE');
            if (evidence.length === 0) return undefined;
            return {
                title: 'XPMSE is near the crash',
                confidence: 'possible',
                explain: 'Usually a false lead. If it comes back often with no better explanation, XP32 Maximum Skeleton ' +
                    'Extended or a mod that depends on it may be in conflict.',
                fix: 'Make sure XP32 Maximum Skeleton Extended is installed right and loads at the right place; that mods ' +
                    'needing it fit the installed version; update it and those mods for this game version; check skeleton or ' +
                    'animation mods recently added or removed.',
                evidence
            };
        }
    },
    {
        id: 'animation',
        ported: 'diagnoses.js analyzeAnimationIssues',
        diagnose: view => {
            const hex = view.findAddress('firstLine', 'B02235');
            const codes = presentNeedles(view, 'topHalf', ANIMATION_CODES);
            if (hex.length === 0 && codes.length === 0) return undefined;
            return {
                title: hex.length > 0 ? 'Probable animation crash (B02235)' : 'Animation code or files are near the crash',
                confidence: hex.length > 0 ? 'likely' : 'possible',
                explain: (hex.length > 0 ? 'The first line is at B02235, a place animations crash at. ' : '') +
                    (codes.length > 0 ? `The top of the log names ${codes.join(', ')}; more of these make an animation cause likelier.` : ''),
                fix: 'Regenerate animations with the behavior engine: FNIS (Nexus 3038), Nemesis (Nexus 60033) or Pandora ' +
                    '(Nexus 133232). Then disable animation mods one by one, running the engine after each change, until it ' +
                    'stops; look for updates or patches. Replace Dynamic Animation Replacer (Nexus 33746) with Open Animation ' +
                    'Replacer (Nexus 92109). Keep FNIS.esp; use SE .hkx files, not LE ones.',
                evidence: unique([...hex, ...view.find('topHalf', ...ANIMATION_CODES)])
            };
        }
    },
    {
        id: 'animation-loader',
        ported: 'diagnoses.js analyzeAnimationLoaderIssues',
        diagnose: view => {
            const codes = presentNeedles(view, 'topHalf', ANIMATION_LOADER_CODES);
            const offset = view.gameVersion === '1.6.1170' ? view.find('topHalf', ANIMATION_LOADER_OFFSET) : [];
            if (offset.length > 0) codes.push(ANIMATION_LOADER_OFFSET);
            if (codes.length === 0) return undefined;
            return {
                title: 'Behavior files or the animation loader are near the crash',
                confidence: 'possible',
                explain: `The top of the log names ${codes.join(', ')}: the behavior engine's output or the animation ` +
                    'loader (DAR, OAR) may be out of date or broken. More of these make it likelier.',
                fix: `Clear the behavior engine's cache and regenerate with every option your mods need: ${BEHAVIOR_ENGINES}. ` +
                    'If that fails, run it as administrator; check animation mods recently added or updated; update DAR or OAR ' +
                    'and the skeleton; keep FNIS.esp enabled when the engine made it.',
                evidence: unique([...view.find('topHalf', ...ANIMATION_LOADER_CODES), ...offset])
            };
        }
    },
    {
        id: 'mco-bfco-together',
        ported: 'diagnoses.js analyzeMcoBfcoCompatibility',
        diagnose: view => {
            // A whole file name, so DMCO's own files are not MCO's.
            const mco = view.find('all', /\bmco\.dll/i);
            const bfco = view.find('all', /\bbfco\.dll/i);
            if (mco.length === 0 || bfco.length === 0) return undefined;
            const dmco = view.has('all', 'dodgeframework.esl', 'dodge_mco-dxp.esl', 'dodgeframework.dll');
            return {
                title: 'MCO and BFCO are both installed',
                confidence: 'possible',
                explain: 'Modern Combat Overhaul and Behavior Framework Combat Overhaul do the same job and are not made to ' +
                    'run together; BFCO lists MCO as incompatible.' +
                    (dmco ? ' DMCO is installed, which BFCO lists as compatible: MCO may be there only for it.' : ''),
                fix: 'Keep one. BFCO (Nexus 117052) is newer and usually preferred; MCO to BFCO Converter (Nexus 119926) ' +
                    'converts MCO animations. Uninstall the other (MCO is Nexus 85491), then regenerate animations with ' +
                    `${BEHAVIOR_ENGINES}.` +
                    (dmco ? ' To keep DMCO (Nexus 175129), install in order: BFCO Universal Support (Nexus 120091) on AE, ' +
                        'MCO Universal Support (only its DLL), DMCO, BFCO.' : ''),
                evidence: [...mco, ...bfco]
            };
        }
    },
    {
        id: 'fisses-thread-safety',
        ported: 'diagnoses.js checkFISSESThreadSafetyCrash',
        diagnose: view => {
            const evidence = view.find('firstLine', 'fiss.dll');
            if (evidence.length === 0) return undefined;
            return {
                title: 'FISSES (fiss.dll) crashed',
                confidence: 'likely',
                explain: 'FISS.dll is not thread-safe: it crashes when several mods load their data files at once, at game start.',
                fix: 'Find the mods that use FISSES (Nexus 13956). Where you can, replace them with ones that use PapyrusUtil SE ' +
                    '(Nexus 13048), which is thread-safe; disable them one at a time to find the conflict, or keep only one of them.',
                evidence
            };
        }
    },
    {
        id: 'hud',
        ported: 'diagnoses.js analyzeHUDCrash',
        diagnose: view => {
            const evidence = view.find('topHalf', /HUD|menus|maps/i);
            if (evidence.length === 0) return undefined;
            return {
                title: 'The interface (HUD, menus or maps) is near the crash',
                confidence: 'possible',
                explain: 'HUD, menus or maps near the crash may mean a conflict among interface mods: health bars, compass, ' +
                    'active effects and the like.',
                fix: 'Disable or adjust mods that change the HUD or interface to see if it stops. Update SkyUI and SKSE; make ' +
                    'sure the HUD mods work together and load in the right order.',
                evidence
            };
        }
    },
    {
        id: 'wheeler',
        ported: 'diagnoses.js analyzeWheelerCrash',
        diagnose: view => {
            const wheeler = view.find('topQuarter', 'wheeler.dll');
            if (wheeler.length === 0 || !view.has('all', 'EngineFixes.dll')) return undefined;
            return {
                title: 'Wheeler (wheeler.dll) crashed',
                confidence: 'likely',
                explain: 'Wheeler is at the crash; Wheeler CTD-Fix fixes known Wheeler crashes, and a misinstalled SSE Engine ' +
                    'Fixes causes others.',
                fix: `Install Wheeler CTD-Fix (Nexus 132074). If it is installed and the crash continues: ${engineFixesSteps(view, 'Reinstall')}`,
                evidence: wheeler
            };
        }
    },
    {
        id: 'quest-journal',
        ported: 'diagnoses.js analyzeQuestJournalCrash',
        diagnose: view => {
            const evidence = view.find('topThird', ...JOURNAL_INDICATORS);
            if (evidence.length === 0) return undefined;
            return {
                title: 'The SkyUI quest journal crashed',
                confidence: 'certain',
                explain: 'A known SkyUI interface bug: the journal menu is at the crash.',
                fix: 'Disable your journal mod and install Dear Diary Dark Mode - SkyUI Menus Replacer SE (Nexus 60837) or Dear ' +
                    'Diary - Paper SkyUI Menus Replacer SE (Nexus 23010), choosing the journal option in its installer; if menus ' +
                    'look squished, add Widescreen Scale Removed for 1-6-1130 and higher (Nexus 136793). Less reliable: Quest ' +
                    'Journal Fix for SkyUI (Nexus 108618).',
                evidence
            };
        }
    },
    {
        id: 'journal-menu-stack',
        ported: 'diagnoses.js analyzeJournalMenuCrash',
        diagnose: view => {
            // quest-journal already says it, and more surely, when the journal is higher up.
            if (view.has('topThird', ...JOURNAL_INDICATORS)) return undefined;
            const evidence = view.find('stack', ...JOURNAL_INDICATORS);
            if (evidence.length === 0) return undefined;
            return {
                title: 'The journal menu is on the stack: the journal may be broken',
                confidence: 'possible',
                explain: 'The SkyUI journal menu is on the stack; a broken journal is a known cause of crashes.',
                fix: 'Disable your journal mod and install Dear Diary Dark Mode - SkyUI Menus Replacer SE (Nexus 60837) or Dear ' +
                    'Diary - Paper SkyUI Menus Replacer SE (Nexus 23010), choosing the journal option in its installer; if menus ' +
                    'look squished, add Widescreen Scale Removed for 1-6-1130 and higher (Nexus 136793).',
                evidence
            };
        }
    },
    {
        id: 'compass-navigation-overhaul',
        ported: 'diagnoses.js checkCompassNavigationOverhaulCrash',
        diagnose: view => {
            const hudMenu = view.find('topHalf', 'HUDMenu');
            const dll = view.find('all', 'CompassNavigationOverhaul.dll');
            if (hudMenu.length === 0 || dll.length === 0) return undefined;
            return {
                title: 'Compass Navigation Overhaul may be out of date',
                confidence: 'possible',
                explain: 'HUDMenu is near the crash and Compass Navigation Overhaul is installed: outdated versions of its DLL ' +
                    'often crash on HUDMenu. Its DLL has no version number, so the log cannot tell which one this is.',
                fix: 'Update Compass Navigation Overhaul (Nexus 74484) to its latest version.',
                evidence: [...hudMenu, ...dll]
            };
        }
    },
    {
        id: 'a0d789',
        ported: 'diagnoses.js analyzeA0D789Crash',
        diagnose: view => {
            const evidence = view.findAddress('firstLine', 'A0D789');
            if (evidence.length === 0) return undefined;
            return {
                title: 'A0D789 crash',
                confidence: 'certain',
                explain: 'A known game bug at A0D789, which kingeric1992\'s A0D789 patch fixes.',
                fix: 'Install [SE/AE] A0D789patch by kingeric1992 (published on Patreon, not Nexus); it adds no plugin.',
                evidence
            };
        }
    }
];
