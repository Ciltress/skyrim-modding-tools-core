import type { SectionName } from '../crashLog.js';
import type { CrashRule, CrashView, Evidence, Finding, Needle, View } from '../crashRules.js';

/**
 * Plugins, records and the load order. Ported from Phostwood's Skyrim Crash Log Analyzer
 * (github.com/Phostwood/crash-analyzer, GPL-3.0), diagnoses.js and analyzeLog.js.
 */

/** The game's own masters: in every load order, never the culprit (Phostwood's neverDisplay). */
const BASE_MASTERS = ['skyrim.esm', 'update.esm', 'dawnguard.esm', 'hearthfires.esm', 'dragonborn.esm'];

/** The Anniversary Edition's Creation Club files and their titles, lower case (Phostwood's expectedCCFiles). */
export const CREATION_CLUB_FILES: Readonly<Record<string, string>> = {
    'ccasvsse001-almsivi.esm': 'Ghosts of the Tribunal',
    'ccbgssse001-fish.esm': 'Fishing',
    'cctwbsse001-puzzledungeon.esm': 'Forgotten Seasons',
    'cceejsse001-hstead.esm': 'Tundra Homestead',
    'ccbgssse016-umbra.esm': 'Umbra',
    'ccbgssse031-advcyrus.esm': "Dead Man's Dread",
    'ccbgssse067-daedinv.esm': 'The Cause',
    'ccbgssse025-advdsgs.esm': 'Saints & Seducers',
    'cceejsse005-cave.esm': 'Bloodchill Manor',
    'ccafdsse001-dwesanctuary.esm': 'Nchuanthumz: Dwarven Home',
    'ccbgssse002-exoticarrows.esl': 'Arcane Archer Pack',
    'ccbgssse003-zombies.esl': 'Plague of the Dead',
    'ccbgssse004-ruinsedge.esl': "Ruin's Edge",
    'ccbgssse005-goldbrand.esl': 'Goldbrand',
    'ccbgssse006-stendarshammer.esl': "Stendarr's Hammer",
    'ccbgssse007-chrysamere.esl': 'Chrysamere',
    'ccbgssse010-petdwarvenarmoredmudcrab.esl': 'Dwarven Armored Mudcrab',
    'ccbgssse011-hrsarmrelvn.esl': 'Horse Armor - Elven',
    'ccbgssse012-hrsarmrstl.esl': 'Horse Armor - Steel',
    'ccbgssse014-spellpack01.esl': 'Arcane Accessories',
    'ccbgssse019-staffofsheogorath.esl': 'Staff of Sheogorath',
    'ccbgssse020-graycowl.esl': 'The Gray Cowl Returns!',
    'ccbgssse021-lordsmail.esl': "Lord's Mail",
    'ccmtysse001-knightsofthenine.esl': 'Divine Crusader',
    'ccqdrsse001-survivalmode.esl': 'Survival Mode',
    'ccqdrsse002-firewood.esl': 'Camping',
    'ccbgssse018-shadowrend.esl': 'Shadowrend',
    'ccbgssse035-petnhound.esl': 'Nix-Hound',
    'ccfsvsse001-backpacks.esl': "Adventurer's Backpack",
    'cckrtsse001_altar.esl': 'Bittercup',
    'cceejsse002-tower.esl': 'Myrwatch',
    'ccedhsse001-norjewel.esl': 'Nordic Jewelry',
    'ccvsvsse002-pets.esl': 'Pets of Skyrim',
    'ccbgssse037-curios.esl': 'Rare Curios',
    'ccbgssse034-mntuni.esl': 'Wild Horses',
    'ccbgssse045-hasedoki.esl': 'Staff of Hasedoki',
    'ccbgssse008-wraithguard.esl': 'Sunder & Wraithguard',
    'ccbgssse036-petbwolf.esl': 'Bone Wolf',
    'ccffbsse001-imperialdragon.esl': 'Civil War Champions',
    'ccmtysse002-ve.esl': 'Vigil Enforcer Armor Set',
    'ccbgssse043-crosselv.esl': 'Elite Crossbows',
    'ccvsvsse001-winter.esl': 'Saturalia Holiday Pack',
    'cceejsse003-hollow.esl': 'Shadowfoot Sanctum',
    'ccbgssse038-bowofshadows.esl': 'Bow of Shadows',
    'ccbgssse040-advobgobs.esl': 'Goblins',
    'ccbgssse050-ba_daedric.esl': 'Alternative Armors - Daedric Plate',
    'ccbgssse052-ba_iron.esl': 'Alternative Armors - Iron',
    'ccbgssse054-ba_orcish.esl': 'Alternative Armors - Orcish Plate',
    'ccbgssse058-ba_steel.esl': 'Alternative Armors - Steel Soldier',
    'ccbgssse059-ba_dragonplate.esl': 'Alternative Armors - Dragon Plate',
    'ccbgssse061-ba_dwarven.esl': 'Alternative Armors - Dwarven Plate',
    'ccpewsse002-armsofchaos.esl': 'Arms of Chaos',
    'ccbgssse041-netchleather.esl': 'Netch Leather Armor',
    'ccedhsse002-splkntset.esl': 'Spell Knight Armor',
    'ccbgssse064-ba_elven.esl': 'Alternative Armors - Elven Hunter',
    'ccbgssse063-ba_ebony.esl': 'Alternative Armors - Ebony Plate',
    'ccbgssse062-ba_dwarvenmail.esl': 'Alternative Armors - Dwarven Mail',
    'ccbgssse060-ba_dragonscale.esl': 'Alternative Armors - Dragonscale',
    'ccbgssse056-ba_silver.esl': 'Alternative Armors - Silver',
    'ccbgssse055-ba_orcishscaled.esl': 'Alternative Armors - Orcish Scaled',
    'ccbgssse053-ba_leather.esl': 'Alternative Armors - Leather',
    'ccbgssse051-ba_daedricmail.esl': 'Alternative Armors - Daedric Mail',
    'ccbgssse057-ba_stalhrim.esl': 'Alternative Armors - Stalhrim Fur',
    'ccbgssse066-staves.esl': 'Staves (Creation)',
    'ccbgssse068-bloodfall.esl': "Headman's Cleaver",
    'ccbgssse069-contest.esl': 'The Contest',
    'ccvsvsse003-necroarts.esl': 'Necromantic Grimoire',
    'ccvsvsse004-beafarmer.esl': 'Farming',
    'ccffbsse002-crossbowpack.esl': 'Expanded Crossbow Pack',
    'ccbgssse013-dawnfang.esl': 'Dawnfang & Duskfang',
    'ccrmssse001-necrohouse.esl': 'Gallows Hall',
    'ccedhsse003-redguard.esl': 'Redguard Elite Armaments',
    'cceejsse004-hall.esl': 'Hendraheim',
    'cccbhsse001-gaunt.esl': 'Fearsome Fists',
    '_resourcepack.esl': 'shared Creation Club resources'
};

/** Phostwood's EXPECTED_BASE_MODS: what a game with no mods loads. */
const BASE_GAME_FILES = new Set([...BASE_MASTERS, ...Object.keys(CREATION_CLUB_FILES)]);

/** Phostwood's crashIndicators.pathingAndNavMeshIssues. */
const PATHING_INDICATORS = [
    'AiNavigation', 'AutoRegisterPathBuilderFactory', 'BGSProcedureFollowExecState', 'BSPathBuilder',
    'BSPrecomputedNavmeshInfoSearch', 'CombatNavmeshSearchT', 'MovementControllerNPC', 'MovementPathManagerArbiter',
    'NavMeshInfoArea', 'NavMeshInfoMap', 'NavMeshObstacleManager', 'PathFinding', 'PathingCell', 'PathingDoor',
    'PathingLockData', 'PathingRequest', 'PathingTaskData', 'SIC_WERoad07', 'PathManagerClient',
    'NavMeshGenerationProperties', 'PathingCoverLocation', 'NavMeshPortal', 'DetourNavMesh', 'PathingStreamManager',
    'NavmeshProcessing', 'PathSmoothing', 'Path Following Movement State', 'MovementPostUpdateArbiter',
    'MovementPlannerAgentWarp'
];

/** Phostwood's crashIndicators.lotdKaragasTowerDoorCrashIssues, less its address, which is looked up as one. */
const KARAGAS_INDICATORS = [
    'LegacyoftheDragonborn.esm', 'LegacyoftheDragonborn0.esp', 'Karagas', 'MovementMessageActivateDoor', 'Door (29)', '0006597B'
];

const CREATION_CLUB_DOWNLOAD = 'If you own the Anniversary Edition: start the game once from Steam, not the mod manager, and in the ' +
    'main menu open Creations, Options, Download all owned Creation Club Creations (try again if it cannot connect). ' +
    'Close the game, refresh the mod manager, enable the cc plugins and _ResourcePack.esl, sort, and launch from the mod manager.';

export const pluginRules: CrashRule[] = [
    {
        id: 'too-many-plugins',
        ported: 'diagnoses.js checkForTooManyNonEslPlugins',
        diagnose: view => {
            // A full plugin is one with a two-digit index; light ones load under FE and do not count.
            const full = view.log.plugins.filter(plugin => !plugin.index.startsWith('FE:'));
            if (full.length <= 254) return undefined;
            return {
                title: 'More than 254 full plugins',
                confidence: 'certain',
                explain: `The load order has ${full.length} plugins that are not light (ESL-flagged); the game handles ` +
                    'at most 254, and more makes it unstable.',
                fix: 'Disable plugins until at most 254 full ones are left, or flag small plugins as light with ESLifier ' +
                    '(Nexus 145168).',
                evidence: view.find('plugins', /^Light:/i)
            };
        }
    },
    {
        id: 'object-reference-none',
        ported: 'diagnoses.js checkForObjectReferenceNone',
        diagnose: view => objectReferenceNone(view)
    },
    {
        id: 'missing-masters',
        ported: 'diagnoses.js checkForMissingMasters',
        diagnose: view => {
            const named = namedMissingMasters(view);
            if (named) {
                const fix = [
                    named.missing.length > 0 ? `install and enable the mod with ${named.missing.join(', ')}` : '',
                    named.disable.length > 0 ? `disable the mod with ${named.disable.join(', ')}` : ''
                ].filter(Boolean).join(', or ') || 'install each master Crash Logger names as missing, or disable the plugins that need it';
                return {
                    title: 'A plugin needs a master that is missing',
                    confidence: 'certain',
                    explain: 'Crash Logger found plugins whose masters (the plugins they need) are not loaded, so the game ' +
                        'stopped loading its data.',
                    fix: `${fix[0].toUpperCase()}${fix.slice(1)}.`,
                    evidence: named.evidence
                };
            }
            const signals = missingMasterSignals(view);
            if (!signals) return undefined;
            const extra = [
                !gameAtLeast(view, '1.6.1130') && !view.has('all', 'BackportedESLSupport.dll')
                    ? 'For mods made for game 1.6.1130 or later, install Backported Extended ESL Support (Nexus 106441).' : '',
                gameAtLeast(view, '1.6.1130') && view.has('bottomHalf', 'EngineFixes.dll', 'EngineFixesVR.dll')
                    ? 'Reinstall SSE Engine Fixes (Nexus 17230), both its SKSE plugin and the DLLs in the game folder.' : ''
            ].filter(Boolean);
            return {
                title: signals.strong ? 'A plugin probably lacks a master it needs' : 'Few or no plugins loaded: a master may be missing',
                confidence: signals.strong ? 'likely' : 'possible',
                explain: signals.strong
                    ? 'The crash matches the game stopping on a plugin whose master (a plugin it needs) is missing, disabled, ' +
                    'or loaded after it.'
                    : 'The log lists fewer than five plugins or only the game\'s own, so loading may have stopped early; a ' +
                    'missing master (a plugin another one needs) is the usual cause. A load order meant to have almost no ' +
                    'plugins can ignore this.',
                fix: ['Find the plugin with a missing master (Mod Organizer marks it with a warning triangle), then install ' +
                    'the master or disable that plugin, and sort masters before the plugins that need them (LOOT). Manage ' +
                    'mods only in the mod manager, never in the game\'s Creations menu.', ...extra].join(' '),
                evidence: signals.evidence
            };
        }
    },
    {
        id: 'missing-creation-club',
        ported: 'diagnoses.js checkMissingCreationClubContent',
        diagnose: view => {
            if (!view.isAE) return undefined;
            const named = namedMissingMasters(view);
            if (named) {
                // Crash Logger names what is missing: this only adds how to get Creation Club files.
                const missing = named.missing.filter(file => file.toLowerCase() in CREATION_CLUB_FILES);
                if (missing.length === 0) return undefined;
                return {
                    title: 'Creation Club content a mod needs is missing',
                    confidence: 'likely',
                    explain: `Crash Logger names missing Creation Club files: ${missing.join(', ')}. Mods that need the ` +
                        'Anniversary Edition\'s content stop the game when it is not downloaded.',
                    fix: CREATION_CLUB_DOWNLOAD,
                    evidence: named.evidence.filter(line => missing.some(file => line.text.toLowerCase().includes(file.toLowerCase())))
                };
            }
            const signals = missingMasterSignals(view);
            if (!signals) return undefined;
            const loaded = new Set(view.log.plugins.map(plugin => plugin.name.toLowerCase()));
            const files = Object.keys(CREATION_CLUB_FILES);
            const missing = files.filter(file => !loaded.has(file));
            // Phostwood's "plugins section cut short": a list too short to hold even the game's own masters.
            const cutShort = view.log.plugins.length < 5;
            if (missing.length === 0 && !cutShort) return undefined;
            const examples = missing.slice(0, 5).map(file => `${file} (${CREATION_CLUB_FILES[file]})`).join(', ');
            const more = missing.length > 5 ? ` and ${missing.length - 5} more` : '';
            const listLine = view.find('plugins', /^Light:/i);
            return {
                title: cutShort ? 'Creation Club content may be missing' : 'Creation Club content is missing',
                confidence: 'possible',
                explain: `A master seems missing, and the Anniversary Edition's Creation Club files that mods may need are ` +
                    (cutShort
                        ? 'not in the plugin list, though the list is too short to tell for sure.'
                        : `not all loaded: ${files.length - missing.length} of ${files.length}. Missing: ${examples}${more}.`),
                fix: CREATION_CLUB_DOWNLOAD,
                evidence: listLine.length > 0 ? listLine : signals.evidence
            };
        }
    },
    {
        id: 'leveled-item-save',
        ported: 'diagnoses.js checkLeveledItemCrash',
        diagnose: view => {
            const evidence = view.find('topHalf', 'LeveledItem (53)', 'TESLevItem');
            if (evidence.length === 0) return undefined;
            return {
                title: 'A broken leveled item list in the save',
                confidence: 'likely',
                explain: 'The crash names a leveled item list. This happens while saving, fast travelling or loading when ' +
                    'the save holds corrupt leveled list data.',
                fix: 'Install LeveledList Crash Fix (Nexus 129136; needs game 1.6.1130 or later). It may not mend a save ' +
                    'that is already broken: load an older save, or, with care and a backup, remove the leveled list\'s ' +
                    'FormID from the save with FallrimTools ReSaver (Nexus 5031).',
                evidence
            };
        }
    },
    {
        id: 'strings-language-setting',
        ported: 'diagnoses.js analyzeStringsCrash',
        diagnose: view => {
            const strings = view.find('topThird', '.STRINGS');
            const branch = view.find('topHalf', 'BGSStoryManagerBranchNode');
            const player = view.find('topHalf', 'PlayerCharacter');
            if (strings.length === 0 || branch.length === 0 || player.length === 0) return undefined;
            return {
                title: 'Bad sLanguage line in Skyrim.ini (.STRINGS crash)',
                confidence: 'certain',
                explain: 'The game failed to read its .STRINGS text files, which it finds by the sLanguage setting; a ' +
                    'stray or look-alike character in that line breaks it.',
                fix: 'Open Skyrim.ini (the mod manager profile\'s copy, if it keeps one), back it up, and make the line ' +
                    'read exactly sLanguage=ENGLISH (or your language) in plain letters: even a dotted capital I breaks ' +
                    'it. Save it and start the game again.',
                evidence: [...strings, ...branch, ...player]
            };
        }
    },
    {
        id: 'new-game-missing-ini',
        ported: 'diagnoses.js analyzeNewGameCrash',
        diagnose: view => {
            const found = ['PlayerCharacter', 'QueuedPlayer', 'BSFadeNode', 'skeleton.nif'].map(needle => findIn(view, ['topHalf', 'bottomHalf'], needle));
            if (found.some(lines => lines.length === 0)) return undefined;
            return {
                title: 'New game crash: the default ini files are missing',
                confidence: 'likely',
                explain: 'The player\'s skeleton failed to load as a new game started, which happens when the game\'s ' +
                    'launcher never wrote the default ini files.',
                fix: 'Start the Skyrim launcher once from Steam (not the mod manager), open its Options so it writes ' +
                    'Skyrim.ini and SkyrimPrefs.ini, and close it. Then launch through the mod manager again, and check ' +
                    'the ini files keep the launcher\'s default entries, such as sResourceArchiveList.',
                evidence: found.flat()
            };
        }
    },
    {
        id: 'wi-dead-body-cleanup',
        ported: 'diagnoses.js checkWIDeadBodyCleanupCell',
        diagnose: view => {
            const evidence = view.find('topHalf', 'WIDeadBodyCleanupCell');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Crash moving a dead NPC\'s items to the Hall of the Dead',
                confidence: 'likely',
                explain: 'The game\'s WIDeadBodyCleanupScript failed moving an important dead NPC\'s inventory to their ' +
                    'coffin, usually 12 in-game hours after the death.',
                fix: 'Install Yet Another WIDeadBodyCleanupScript Crash Fix (Nexus 124724; needs more requirements, may ' +
                    'work better) or WIDeadBodyCleanupScript Crash Fix (Nexus 62413; fewer requirements).',
                evidence
            };
        }
    },
    {
        id: 'lotd-karagas-tower-door',
        ported: 'diagnoses.js checkLotdKaragasTowerDoorCrash',
        diagnose: view => {
            const atAddress = view.findAddress('firstLine', '04D26E7');
            // Phostwood counts the address among the top half's indicators too.
            const indicators = [view.findAddress('topHalf', '04D26E7'), ...KARAGAS_INDICATORS.map(needle => view.find('topHalf', needle))]
                .filter(lines => lines.length > 0);
            if (!(atAddress.length > 0 && indicators.length > 0) && indicators.length < 4) return undefined;
            return {
                title: 'Karagas\' Tower door crash (Legacy of the Dragonborn)',
                confidence: 'likely',
                explain: 'A door in Legacy of the Dragonborn\'s Karagas\' Tower (0x6597B) has a broken navmesh link: the ' +
                    'game crashes when a follower paths through it.',
                fix: 'Load a save from before Karagas\' Maze, dismiss every follower and pet (Inigo, Meeko and the like), ' +
                    'and finish the dungeon alone; followers can rejoin once you are inside the tower.',
                evidence: unique(indicators.flat())
            };
        }
    },
    {
        id: 'dawnguard-horse-sic',
        ported: 'diagnoses.js analyzeDawnguardHorseIssue',
        diagnose: view => {
            // Phostwood matches these with their case.
            const sic = view.find('topHalf', /Skyrim Immersive Creatures/);
            const horse = view.find('topHalf', /Dawnguard Horse/);
            const rider = view.find('topHalf', /Isran|Celann/);
            if (sic.length === 0 || horse.length === 0 || rider.length === 0) return undefined;
            return {
                title: 'Mounted Dawnguard riders of Skyrim Immersive Creatures',
                confidence: 'certain',
                explain: 'Mounted Dawnguard NPCs with Isran or Celann, from Skyrim Immersive Creatures\' SIC_WERoad07 ' +
                    'encounter, get stuck pathing: a known navmesh crash.',
                fix: 'In SSEEdit (Nexus 164), load Skyrim Immersive Creatures\' plugin, delete the record with Editor ID ' +
                    'SIC_WERoad07, and save. Then load a save from before those NPCs appeared.',
                evidence: unique([...sic, ...horse, ...rider])
            };
        }
    },
    {
        id: 'core-impact-dismembering',
        ported: 'diagnoses.js checkCoreImpactCorruptionCrash',
        diagnose: view => {
            const atAddress = view.findAddress('firstLine', '1AD5B40');
            if (atAddress.length === 0) return undefined;
            // Phostwood's "log under 5000 characters", which ends in the call stack: here, a log that stops before its modules.
            const cutShort = !view.log.complete && view.log.sections.modules === undefined;
            const affected = [
                ...(versionIs(dllVersion(view, 'CoreImpactFramework.dll'), '1.1.1') ? view.find('sksePlugins', 'CoreImpactFramework.dll') : []),
                ...(versionIs(dllVersion(view, 'DismemberingFramework.dll'), '1.1.2') ? view.find('sksePlugins', 'DismemberingFramework.dll') : [])
            ];
            if (!cutShort && affected.length === 0) return undefined;
            return {
                title: 'Core Impact Framework and Dismembering Framework clash',
                confidence: 'likely',
                explain: 'This 1AD5B40 crash comes from older Core Impact Framework and Dismembering Framework working ' +
                    'together, and can leave the save broken.',
                fix: 'Update Core Impact Framework and Dismembering Framework together, to their newest versions. On the ' +
                    'Gate to Sovngarde list 84, upgrade the list to 89 or later instead. If the save still crashes, load ' +
                    'an older one.',
                evidence: [...atAddress, ...affected]
            };
        }
    },
    {
        id: 'gts-t-pose-animal',
        ported: 'diagnoses.js checkTPoseAnimalCrash',
        diagnose: view => {
            const evidence = view.find('topThird', '(ActorMovementMessageMap<16>::MessageHandlerWrapper<MovementMessageWarpToLocation>*)');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Animal T-pose crash (Gate to Sovngarde 90 and 91)',
                confidence: 'likely',
                explain: 'The crash is in warping an animal\'s movement, which an outdated T-Pose Animal Fix causes in the ' +
                    'Gate to Sovngarde lists 90 and 91.',
                fix: 'Download the newest T-Pose Animal Fix by SpinPigeon from JaySerpa\'s Small Mods and Resources ' +
                    '(Nexus 97490, Files tab) and install it over the old one, or update Gate to Sovngarde to 92 or later, ' +
                    'which has it.',
                evidence
            };
        }
    },
    {
        id: 'combat-navmesh-retreat',
        ported: 'diagnoses.js checkCombatNavmeshRetreatCrash',
        diagnose: view => {
            const signature = view.find('topHalf', 'CombatNavmeshSearchT<CombatPathingGoalPolicyRetreat,CombatPathingSearchPolicyStandard>');
            if (signature.length === 0) return undefined;
            const mods = view.find('bottomHalf', 'skytactics - dynamic combat styles.esp', 'skytactics.esp', 'skyvalor.esp');
            return {
                title: 'Combat retreat pathing crash (SkyTactics or SkyValor)',
                confidence: 'likely',
                explain: 'An NPC crashed searching the navmesh for a path to retreat in combat, which is commonly tied to ' +
                    'SkyTactics and SkyValor clashing with other mods. ' +
                    (mods.length > 0 ? 'One of them is in the load order.' : 'They may be installed even when this log does not list them.'),
                fix: 'Disable SkyTactics - Dynamic Combat Styles (Nexus 131148) and SkyValor (Nexus 106240), whichever is ' +
                    'installed. If the crash stays, disable related combat and AI mods one at a time to find the clashing pair.',
                evidence: [...signature, ...mods]
            };
        }
    },
    {
        id: 'navmesh-pathing',
        ported: 'diagnoses.js analyzePathingIssues',
        diagnose: view => {
            const evidence = view.find('topHalf', ...PATHING_INDICATORS);
            if (evidence.length === 0) return undefined;
            const strong = view.has('topThird', ...PATHING_INDICATORS);
            return {
                title: strong ? 'An NPC or creature could not find its path (navmesh)' : 'Signs of an NPC pathing (navmesh) problem',
                confidence: strong ? 'likely' : 'possible',
                explain: 'An NPC or creature was blocked from its path: an object a mod placed may cut the navmesh, or an ' +
                    'AI pathing mod misbehaves.',
                fix: 'Quit to desktop and load an older save; have followers and horses wait before travelling, or turn off ' +
                    'horse followers in the follower framework\'s menu. Install Navigator - Navmesh Fixes (Nexus 52641). The ' +
                    'console commands tai and tcai toggle NPC AI to get past the spot. Disable AI pathing or behaviour mods ' +
                    'one at a time to find a conflict.',
                evidence
            };
        }
    },
    {
        id: 'armor-weight-perk',
        ported: 'diagnoses.js checkArmorWeightPerkIssue',
        diagnose: view => {
            const evidence = unique([...view.findAddress('topHalf', '0CD9F7C'), ...view.find('topHalf', 'PEBQEAVInputEvent')]);
            if (evidence.length === 0) return undefined;
            return {
                title: 'Armor weight perk calculation crash',
                confidence: 'possible',
                explain: 'This crash signature is tied to a game bug in how armor weight perks are calculated when stats ' +
                    'or equipment change.',
                fix: 'Install Scrambled Bugs (Nexus 43532) if it is missing, then open Data/SKSE/Plugins/ScrambledBugs.json ' +
                    'and set "modArmorWeightPerkEntryPoint" to false. If OStim is installed, update it.',
                evidence
            };
        }
    },
    {
        id: 'combat-magic-caster-disarm',
        ported: 'diagnoses.js checkCombatMagicCasterDisarmIssue',
        diagnose: view => {
            const evidence = view.find('topHalf', 'CombatMagicCasterDisarm');
            if (evidence.length === 0) return undefined;
            const engineFixes = view.has('bottomHalf', 'EngineFixes.dll') || view.has('topHalf', 'EngineFixes.dll');
            return {
                title: 'Disarm crash in combat',
                confidence: 'likely',
                explain: 'The crash is in the disarm mechanic, when a weapon is knocked out of an NPC\'s or the player\'s ' +
                    'hands, usually by the Disarm shout.',
                fix: 'Install Disarmless (Nexus 12631), which removes the Disarm shout from draugr or from the whole game.' +
                    (engineFixes ? ' Some saw this with SSE Engine Fixes 7 (Nexus 17230): if Disarmless does not help, ' +
                        'update Engine Fixes to a release newer than 12 December 2024.' : ''),
                evidence
            };
        }
    },
    {
        id: 'custom-hair-color',
        ported: 'diagnoses.js checkCustomHairColorIssue',
        diagnose: view => {
            const clone = view.find('topHalf', 'BGSArtObjectCloneTask');
            const caster = view.find('topHalf', 'ActorMagicCaster');
            const dll = view.find('all', 'CustomHairColor.dll');
            if (clone.length === 0 || caster.length === 0 || dll.length === 0) return undefined;
            return {
                title: 'Custom Hair Color is incompatible',
                confidence: 'likely',
                explain: 'The crash is in cloning an art object on a magic caster with Custom Hair Color loaded, a commonly ' +
                    'reported pattern since recent game updates.',
                fix: 'Disable or uninstall Custom Hair Color (Nexus 102909).',
                evidence: unique([...clone, ...caster, ...dll])
            };
        }
    },
    {
        id: 'arcanum-jace-eyes',
        ported: 'diagnoses.js checkArcanumJaceEyesCrash',
        diagnose: view => {
            const eyes = view.find('topHalf', '00SPJaceEyes');
            const arcanum = view.find('all', 'Arcanum.esp');
            if (eyes.length === 0 || arcanum.length === 0) return undefined;
            return {
                title: 'Arcanum\'s broken 00SPJaceEyes asset',
                confidence: 'likely',
                explain: 'The crash involves the 00SPJaceEyes asset of Arcanum - A New Age of Magic, which is broken in the ' +
                    'original mod.',
                fix: 'Replace Arcanum - A New Age of Magic (Nexus 23488) with Arcanum - A New Age of Magic (Fixed) ' +
                    '(Nexus 65221). If that does not help, disable Arcanum.',
                evidence: unique([...eyes, ...arcanum])
            };
        }
    },
    {
        id: 'npcs-potions-uapng',
        ported: 'diagnoses.js checkNPCsPotionsUAPNGCrash',
        diagnose: view => {
            const draugr = view.find('topHalf', 'draugr');
            const potions = view.find('all', 'NPCsUsePotions.esp');
            const uapng = view.find('all', 'UAPNG.dll');
            if (draugr.length === 0 || potions.length === 0 || uapng.length === 0) return undefined;
            return {
                title: 'NPCs Use Potions clashes with Ultimate Animated Potions NG',
                confidence: 'likely',
                explain: 'NPCs Use Potions and Ultimate Animated Potions NG together break NPC skeletons, draugr above all, ' +
                    'when NPCs drink animated potions.',
                fix: 'Replace NPCs Use Potions (Nexus 67489) with Smart NPC Potions - Enemies Use Potions and Poisons ' +
                    '(Nexus 40102). Or disable one of the two: Ultimate Animated Potions NG (Nexus 97674) or NPCs Use Potions.',
                evidence: unique([...draugr, ...potions, ...uapng])
            };
        }
    },
    {
        id: 'death-drop-overhaul',
        ported: 'diagnoses.js checkDeathDropOverhaulCrash',
        diagnose: view => {
            const version = dllVersion(view, 'DeathDropOverhaul.dll');
            const outdated = version !== undefined && (versionIs(version, '1.2.0') || versionIs(version, '1.2.1'))
                ? view.find('sksePlugins', 'DeathDropOverhaul.dll') : [];
            const onStack = [...view.find('callStack', 'DeathDropOverhaul.dll'), ...view.find('stack', 'DeathDropOverhaul.dll')];
            const extraList = view.find('stack', 'BaseExtraList').filter(line => line.index < 50);
            const indicators = [outdated, onStack, extraList].filter(lines => lines.length > 0);
            if (indicators.length < 2) return undefined;
            return {
                title: 'Outdated Death Drop Overhaul',
                confidence: 'likely',
                explain: 'Death Drop Overhaul 1.2.0 and 1.2.1 crash while handling the extra data of items in an inventory.',
                fix: 'Update Death Drop Overhaul (Nexus 151590) to 1.2.2 or later.',
                evidence: indicators.flat()
            };
        }
    },
    {
        id: 'climates-of-tamriel-divide-by-zero',
        ported: 'diagnoses.js analyzeCoTDivideByZeroCrash',
        diagnose: view => {
            const divide = view.find('firstLine', 'EXCEPTION_INT_DIVIDE_BY_ZERO');
            const climates = view.find('topHalf', 'ClimatesOfTamriel.esm');
            if (divide.length === 0 || climates.length === 0) return undefined;
            return {
                title: 'Climates of Tamriel divide-by-zero crash',
                confidence: 'likely',
                explain: 'A known Climates of Tamriel bug made the game divide by zero.',
                fix: 'Replace Climates of Tamriel with another weather mod and start a new character. To keep this save ' +
                    '(advanced), remove the record\'s FormID from it with FallrimTools ReSaver (Nexus 5031) and SSEEdit ' +
                    '(Nexus 164).',
                evidence: [...divide, ...climates]
            };
        }
    },
    {
        id: 'bgs-save-load-manager',
        ported: 'diagnoses.js analyzeBGSSaveLoadManagerIssue',
        diagnose: view => {
            const evidence = view.find('topHalf', 'BGSSaveLoadManager');
            if (evidence.length === 0) return undefined;
            const engineFixes = dllVersion(view, 'EngineFixes.dll');
            // Phostwood offers Engine Fixes 7 for the game versions it is made for: 1.5.97, and 1.6.1170 on.
            const seven = view.gameVersion === '1.5.97' || gameAtLeast(view, '1.6.1170')
                ? 'Update SSE Engine Fixes (Nexus 17230) to 7 or later, or set' : 'Set';
            const saveSize = engineFixes === undefined
                ? 'Install SSE Engine Fixes (Nexus 17230), which lets saves grow larger. '
                : compareVersions(engineFixes, '7') < 0
                    ? `${seven} SaveGameMaxSize = true and MaxStdio = 8192 in EngineFixes.toml. `
                    : '';
            return {
                title: 'Crash while saving or loading a game',
                confidence: 'possible',
                explain: 'The crash is in the game\'s save and load manager: saving or loading a save went wrong.',
                fix: `If it crashes only while saving, look for a missing master. ${saveSize}Load the last save that ` +
                    'worked. With a backup, FallrimTools ReSaver (Nexus 5031) can clean a broken save. Avoid saving in ' +
                    'combat, autosaves, and loading a save without restarting the game.',
                evidence
            };
        }
    },
    {
        id: 'bgs-water-collision-manager',
        ported: 'diagnoses.js analyzeBGSWaterCollisionManagerCrash',
        diagnose: view => {
            const evidence = view.find('topHalf', 'BGSWaterCollisionManager::bhkWaterfall', 'BGSWaterCollisionManager::bshkAutoWater',
                'BGSWaterCollisionManager::bhkPlaceableWater');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Water collision crash (PG Patcher or CS Particle Patch)',
                confidence: 'likely',
                explain: 'The crash is in the game\'s water collision manager, a known pattern most often caused by PG ' +
                    'Patcher\'s "Fix Effect Lighting" option.',
                fix: 'In PG Patcher (Nexus 120946), turn off "Fix Effect Lighting" and generate its output again. Without ' +
                    'PG Patcher, CS Particle Patch (Nexus 147270) is the next suspect: update it if a fix is out, otherwise ' +
                    'disable it.',
                evidence
            };
        }
    },
    {
        id: 'redundant-bees',
        ported: 'diagnoses.js checkForRedundantBEES',
        diagnose: view => {
            if (!gameAtLeast(view, '1.6.1130')) return undefined;
            const evidence = view.find('all', 'BackportedESLSupport.dll');
            if (evidence.length === 0) return undefined;
            return {
                title: 'Backported Extended ESL Support is not needed on this game',
                confidence: 'likely',
                explain: `BEES brings the extended light-plugin range to older games, but game ${view.gameVersion} has it ` +
                    'built in (from 1.6.1130 on); the redundant copy may add to crashes.',
                fix: 'Uninstall Backported Extended ESL Support (Nexus 106441), the mod with BackportedESLSupport.dll.',
                evidence
            };
        }
    }
];

/** A plugin as Crash Logger names it after an object's type: `(TESObjectREFR*) [0x2C08C78A] ("Patch.esp")`. */
const PLUGIN_AFTER_TYPE = /\([^()]*\*\).*?\("([^"]+\.es[mpl])"\)/i;

/**
 * Each reference whose base object is missing (Crash Logger's `Object Reference: None`): the plugin of the
 * entry it is written under, the plugin of its cell, and the plugins that changed it. One finding per plugin
 * and cell; none for the game's own masters.
 */
function objectReferenceNone(view: CrashView): Finding[] {
    const findings: Finding[] = [];
    const seen = new Set<string>();
    for (const hit of view.find('topHalf', /object reference:\s*none/i)) {
        if (hit.place === 'firstLine') continue;
        const lines = view.log.sections[(hit.place === 'stackScan' ? 'callStack' : hit.place) as SectionName] ?? [];
        const own = depth(lines[hit.index]);
        // The nearest less-indented line naming a plugin is the entry the reference is written under.
        let entry: number | undefined;
        let plugin: string | undefined;
        for (let i = hit.index - 1, above = own; i >= 0 && above > 0; i--) {
            if (depth(lines[i]) >= above) continue;
            above = depth(lines[i]);
            const match = PLUGIN_AFTER_TYPE.exec(lines[i]);
            if (match) {
                entry = i;
                plugin = match[1];
                break;
            }
        }
        // The reference's own lines: its siblings at its depth, the cell's beneath them.
        let start = hit.index;
        while (start > 0 && depth(lines[start - 1]) >= own) start--;
        let end = hit.index;
        while (end + 1 < lines.length && depth(lines[end + 1]) >= own) end++;
        const block = lines.slice(start, end + 1);
        const field = (at: number, name: string) => block.find(line => depth(line) === at && line.trim().startsWith(`${name}:`))
            ?.trim().slice(name.length + 1).trim().replace(/^"|"$/g, '');
        // Logs before Crash Logger 1.20 name the reference's file on a line of its own.
        plugin ??= field(own, 'File');
        if (!plugin || BASE_MASTERS.includes(plugin.toLowerCase())) continue;
        const cell = field(own + 1, 'File');
        const chain = truncateChain((field(own, 'Modified by') ?? '').split('->').map(file => file.trim())
            .filter(file => file && !BASE_MASTERS.includes(file.toLowerCase())));
        const key = `${plugin}|${cell ?? ''}`.toLowerCase();
        if (seen.has(key)) continue;
        seen.add(key);
        findings.push({
            title: 'A placed object has no base record (Object Reference: None)',
            confidence: 'certain',
            explain: `A reference from ${plugin} points at a base object that does not exist: a mod refers to a missing ` +
                'record, from a conflict, a wrong version or a load-order problem.' +
                (cell ? ` Its cell comes from ${cell}.` : '') + (chain ? ` Changed by, last one most likely: ${chain}.` : ''),
            fix: `Disable the mod with ${plugin} to test. If that stops the crash, check the mod is the version for this ` +
                'game and the other mods, has its required mods and patches, and loads after its masters; then reinstall ' +
                'it (disable it, save without it, reinstall and enable it).' +
                (cell && cell.toLowerCase() !== plugin.toLowerCase() ? ` Look for a patch between it and ${cell}.` : ''),
            evidence: [hit, ...(entry === undefined ? [] : [{ place: hit.place, index: entry, text: lines[entry].trim() }])]
        });
    }
    return findings;
}

function depth(line: string | undefined): number {
    return /^\t*/.exec(line ?? '')![0].length;
}

/** Phostwood keeps the last 300 characters of a long chain, whole file names only. */
function truncateChain(files: string[]): string {
    const kept = [...files];
    while (kept.length > 1 && kept.join(' -> ').length > 300) kept.shift();
    return (kept.length < files.length ? '... ' : '') + kept.join(' -> ');
}

/** Crash Logger 1.26's own MISSING MASTERS section: what is missing and which plugins to disable. */
function namedMissingMasters(view: CrashView): { missing: string[]; disable: string[]; evidence: Evidence[] } | undefined {
    const lines = view.log.sections.missingMasters;
    if (!lines || lines.every(line => !line.trim())) return undefined;
    // `disable: MyPatch.esp (enabled in Plugins.txt)`: the plugin's name ends at its extension.
    const values = (key: string) => lines.map(line => new RegExp(`^\\s*${key}:\\s*(.+?\\.es[mpl])(?=\\s|$)`, 'i').exec(line)?.[1])
        .filter((value): value is string => value !== undefined);
    const evidence = view.find('missingMasters', /->|^missing:|^disable:/i);
    return {
        missing: values('missing'),
        disable: values('disable'),
        evidence: evidence.length > 0 ? evidence : view.find('missingMasters', /\S/)
    };
}

/**
 * Phostwood's signs of a missing master: the crash at 0198090 (5E1F22 on 1.5.97) or in an INI setting
 * collection, which are strong; or a plugin list with fewer than five plugins or only the game's own. A log
 * cut short before its plugin list says nothing about them, so that is not counted.
 */
function missingMasterSignals(view: CrashView): { strong: boolean; evidence: Evidence[] } | undefined {
    const strong = [
        ...(view.isAE ? view.findAddress('firstLine', '0198090') : view.find('firstLine', '5E1F22')),
        ...view.find('topHalf', 'SettingT<INISettingCollection>*')
    ];
    const plugins = view.log.plugins;
    const notLoaded = view.log.sections.plugins !== undefined
        && (plugins.length < 5 || plugins.every(plugin => BASE_GAME_FILES.has(plugin.name.toLowerCase())));
    if (strong.length === 0 && !notLoaded) return undefined;
    return { strong: strong.length > 0, evidence: [...strong, ...(notLoaded ? view.find('plugins', /^Light:/i) : [])] };
}

function findIn(view: CrashView, views: readonly View[], ...needles: Needle[]): Evidence[] {
    return unique(views.flatMap(name => view.find(name, ...needles)));
}

function unique(evidence: readonly Evidence[]): Evidence[] {
    const seen = new Set<string>();
    return evidence.filter(line => {
        const key = `${line.place}:${line.index}`;
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
    });
}

/** An SKSE plugin's version as the log lists it; undefined when it is not listed or has none. */
function dllVersion(view: CrashView, name: string): string | undefined {
    return view.log.sksePlugins.find(plugin => plugin.name.toLowerCase() === name.toLowerCase())?.version;
}

function compareVersions(a: string, b: string): number {
    const left = a.split('.').map(part => parseInt(part, 10) || 0);
    const right = b.split('.').map(part => parseInt(part, 10) || 0);
    for (let i = 0; i < Math.max(left.length, right.length); i++) {
        const difference = (left[i] ?? 0) - (right[i] ?? 0);
        if (difference !== 0) return Math.sign(difference);
    }
    return 0;
}

function versionIs(version: string | undefined, target: string): boolean {
    return version !== undefined && compareVersions(version, target) === 0;
}

function gameAtLeast(view: CrashView, target: string): boolean {
    return view.gameVersion !== undefined && compareVersions(view.gameVersion, target) >= 0;
}
