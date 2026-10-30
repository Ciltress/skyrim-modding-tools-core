import { CREATION_CLUB_FILES, pluginRules } from '../backend/skyrim/crash/rules/plugins';
import type { CrashRule, Finding } from '../backend/skyrim/crash/crashRules';
import { BASE_PLUGINS, crashView, firstLineAt, frame, type LogParts } from './crashLogText';

/** Plugins, records and the load order: each rule fires on the log it is about, and not on another. */

const rule = (id: string): CrashRule => {
    const found = pluginRules.find(entry => entry.id === id);
    if (!found) throw new Error(`no rule ${id}`);
    return found;
};
const run = (id: string, parts: LogParts): Finding[] => [rule(id).diagnose(crashView(parts)) ?? []].flat();
const many = (count: number, extension: string) => Array.from({ length: count }, (_, i) => `Mod ${i}.${extension}`);

/** A load order with mods in it. */
const MODS = [...BASE_PLUGINS, 'Mod A.esp', 'Mod B.esp'];
/** Crash Logger 1.26's own section, as it writes it. */
const missingMasters = (missing: string) => [
    `MyPatch.esp -> ${missing}`,
    'Fix: install the missing file(s), or disable the plugin(s) that depend on them:',
    `\t\tmissing: ${missing}`,
    '\t\tdisable: MyPatch.esp (enabled in Plugins.txt)'
];

describe('plugin rules', () => {
    it('rule ids are unique', () => {
        const ids = pluginRules.map(entry => entry.id);
        expect(new Set(ids).size).toBe(ids.length);
    });

    it('too-many-plugins: more than 254 full plugins; light ones do not count', () => {
        const [found] = run('too-many-plugins', { plugins: [...BASE_PLUGINS, ...many(250, 'esp')] });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'plugins', text: expect.stringMatching(/^Light: 0/) }] });
        expect(found.explain).toContain('255');
        expect(run('too-many-plugins', { plugins: [...BASE_PLUGINS, ...many(249, 'esp'), ...many(500, 'esl')] })).toEqual([]);
    });

    it('object-reference-none: a reference without its base object, by the plugin of the entry it is under', () => {
        const reference = [
            '\t\tObject Reference: None',
            '\t\tParentCell: ---',
            '\t\t\tFile: "ELFXEnhancer.esp"',
            '\t\t\tModified by: Skyrim.esm -> ELFXEnhancer.esp',
            '\t\tModified by: Skyrim.esm -> Crimson Blood Armor.esp -> SDA DX Crimson Blood Patch.esp'
        ];
        const found = run('object-reference-none', {
            registers: ['RDI 0x26F554CD4E0      (TESObjectREFR*) [0x3B08C78A] ("SDA DX Crimson Blood Patch.esp")', ...reference, 'R8  0xE                (size_t) [14]'],
            // The same reference again on the stack is one finding.
            stack: ['[RSP+48 ] 0x26F554CD4E0      (TESObjectREFR*) [0x3B08C78A] ("SDA DX Crimson Blood Patch.esp")', ...reference]
        });
        expect(found).toHaveLength(1);
        expect(found[0]).toMatchObject({
            confidence: 'certain',
            evidence: [
                { place: 'registers', index: 1, text: 'Object Reference: None' },
                { place: 'registers', index: 0, text: expect.stringMatching(/^RDI .*SDA DX Crimson Blood Patch\.esp/) }
            ]
        });
        expect(found[0].fix).toContain('Disable the mod with SDA DX Crimson Blood Patch.esp');
        expect(found[0].fix).toContain('ELFXEnhancer.esp');
        expect(found[0].explain).toContain('Crimson Blood Armor.esp -> SDA DX Crimson Blood Patch.esp');
        expect(found[0].explain).not.toContain('Skyrim.esm');
        // Crash Logger 1.23 writes the base object under an empty "Object Reference:".
        expect(run('object-reference-none', {
            registers: ['RDI 0x1735B089B20      (TESObjectREFR*) [0x2C08C78A] ("Siege at Icemoth.esp")', '\t\tObject Reference: ',
                '\t\tFormType: MovableStatic (36)']
        })).toEqual([]);
        // The game's own masters are never the culprit.
        expect(run('object-reference-none', {
            registers: ['RDI 0x1735B089B20      (TESObjectREFR*) [0x0001B743] ("Skyrim.esm")', '\t\tObject Reference: None']
        })).toEqual([]);
    });

    it('missing-masters: Crash Logger\'s own section names the fix; else the crash at 0198090 or a load order of the game alone', () => {
        const [named] = run('missing-masters', { plugins: ['Skyrim.esm'], missingMasters: missingMasters('NotInstalled.esm') });
        expect(named).toMatchObject({
            confidence: 'certain',
            fix: 'Install and enable the mod with NotInstalled.esm, or disable the mod with MyPatch.esp.',
            evidence: expect.arrayContaining([{ place: 'missingMasters', index: 0, text: 'MyPatch.esp -> NotInstalled.esm' }])
        });

        const [strong] = run('missing-masters', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+019D620'), plugins: MODS });
        expect(strong).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        const [se] = run('missing-masters', { game: '1.5.97', firstLine: firstLineAt('SkyrimSE.exe+05E1F22'), plugins: MODS });
        expect(se).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        expect(se.fix).toContain('Nexus 106441');

        const [gameAlone] = run('missing-masters', { plugins: [...BASE_PLUGINS, 'ccbgssse001-fish.esm'] });
        expect(gameAlone).toMatchObject({ confidence: 'possible', evidence: [{ place: 'plugins', text: expect.stringMatching(/^Light:/) }] });

        expect(run('missing-masters', { plugins: MODS })).toEqual([]);
        // 1.6.1170's offset is another place in 1.7.104; 5E1F22 is 1.5.97's.
        expect(run('missing-masters', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+0198090'), plugins: MODS })).toEqual([]);
        expect(run('missing-masters', { game: '1.6.1170', firstLine: firstLineAt('SkyrimSE.exe+05E1F22'), plugins: MODS })).toEqual([]);
        // A log cut short before its plugin list says nothing about them.
        expect(run('missing-masters', {})).toEqual([]);
    });

    it('missing-creation-club: a master seems missing on the Anniversary Edition and Creation Club files are not loaded', () => {
        const [found] = run('missing-creation-club', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+019D620'), plugins: MODS });
        expect(found).toMatchObject({ confidence: 'possible', title: 'Creation Club content is missing', evidence: [{ place: 'plugins' }] });
        expect(found.explain).toContain('0 of 75');

        const [named] = run('missing-creation-club', { game: '1.7.104', plugins: ['Skyrim.esm'], missingMasters: missingMasters('ccBGSSSE001-Fish.esm') });
        expect(named).toMatchObject({ confidence: 'likely', evidence: [{ place: 'missingMasters', text: 'MyPatch.esp -> ccBGSSSE001-Fish.esm' }, { place: 'missingMasters' }] });

        const everything = [...BASE_PLUGINS, ...Object.keys(CREATION_CLUB_FILES), 'Mod A.esp'];
        expect(run('missing-creation-club', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+019D620'), plugins: everything })).toEqual([]);
        expect(run('missing-creation-club', { game: '1.7.104', plugins: ['Skyrim.esm'], missingMasters: missingMasters('NotInstalled.esm') })).toEqual([]);
        expect(run('missing-creation-club', { game: '1.5.97', firstLine: firstLineAt('SkyrimSE.exe+05E1F22'), plugins: MODS })).toEqual([]);
        expect(run('missing-creation-club', { game: '1.7.104', plugins: MODS })).toEqual([]);
    });

    it('leveled-item-save: a leveled item list in the top half', () => {
        const [found] = run('leveled-item-save', { relevantObjects: ['RSP+50: (TESLevItem*) [0x0009AF0A] ("Skyrim.esm")'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'relevantObjects' }] });
        expect(found.fix).toContain('Nexus 129136');
        expect(run('leveled-item-save', { stack: ['[RSP+8  ] 0x1 (void*)', '\t\tFormType: LeveledItem (53)'] })).toHaveLength(1);
        expect(run('leveled-item-save', { relevantObjects: ['RSP+50: (TESLevCharacter*) [0x0009AF0A] ("Skyrim.esm")'] })).toEqual([]);
    });

    it('strings-language-setting: a .STRINGS file above the stack, with a story manager node and the player', () => {
        const relevantObjects = ['RSP+40: (BGSStoryManagerBranchNode*)', 'RSP+60: (PlayerCharacter*) "Prisoner" [0x00000014] ("Skyrim.esm")'];
        const strings = 'R14 0x1E2C3A0      (char*) "Strings\\Skyrim_English.STRINGS"';
        const [found] = run('strings-language-setting', { relevantObjects, registers: [strings] });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'registers' }, { place: 'relevantObjects' }, { place: 'relevantObjects' }] });
        expect(found.fix).toContain('sLanguage=ENGLISH');
        expect(run('strings-language-setting', { relevantObjects, stack: [strings] })).toEqual([]);
        expect(run('strings-language-setting', { relevantObjects: relevantObjects.slice(1), registers: [strings] })).toEqual([]);
    });

    it('new-game-missing-ini: the player, its queue, a fade node and skeleton.nif together', () => {
        const relevantObjects = ['RSP+40: (PlayerCharacter*) "Prisoner" [0x00000014] ("Skyrim.esm")', 'RSP+48: (QueuedPlayer*)',
            'RSP+50: (BSFadeNode*) "skeleton.nif"'];
        const [found] = run('new-game-missing-ini', { relevantObjects });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'relevantObjects' }, { place: 'relevantObjects' }, { place: 'relevantObjects' }, { place: 'relevantObjects' }] });
        expect(run('new-game-missing-ini', { relevantObjects: [relevantObjects[0], relevantObjects[2]] })).toEqual([]);
    });

    it('wi-dead-body-cleanup: the Hall of the Dead cleanup cell in the top half', () => {
        const [found] = run('wi-dead-body-cleanup', { stack: ['[RSP+60 ] 0x2 (TESObjectCELL*) "WIDeadBodyCleanupCell" [0x000F8C21] ("Skyrim.esm")'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'stack' }] });
        expect(found.fix).toContain('Nexus 124724');
        expect(run('wi-dead-body-cleanup', { callStack: [frame(0, 'SkyrimSE.exe+0123456', 'WIDeadBodyCleanupScript::sub')] })).toEqual([]);
    });

    it('lotd-karagas-tower-door: the crash at 04D26E7, or four signs of the door', () => {
        const [found] = run('lotd-karagas-tower-door', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+04D9DD7') });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }] });
        const signs = ['RSP+20: (TESObjectREFR*) "Karagas\' Tower Door" [0x2C06597B] ("LegacyoftheDragonborn.esm")', 'RSP+28: (MovementMessageActivateDoor*)'];
        expect(run('lotd-karagas-tower-door', { relevantObjects: signs, stack: ['[RSP+8  ] 0x1 (TESObjectDOOR*)', '\t\tFormType: Door (29)'] })).toHaveLength(1);
        expect(run('lotd-karagas-tower-door', { relevantObjects: signs })).toEqual([]);
        expect(run('lotd-karagas-tower-door', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+04D26E7') })).toEqual([]);
    });

    it('dawnguard-horse-sic: Skyrim Immersive Creatures, a Dawnguard Horse and Isran or Celann', () => {
        const horse = 'RSP+B0: (Character*) "Dawnguard Horse" [0xFF0033D6]';
        const isran = 'RSP+C0: (TESNPC*) "Isran" [0x2C012345] ("Skyrim Immersive Creatures.esp")';
        const [found] = run('dawnguard-horse-sic', { relevantObjects: [horse, isran] });
        expect(found).toMatchObject({ confidence: 'certain', evidence: [{ place: 'relevantObjects', index: 1 }, { place: 'relevantObjects', index: 0 }] });
        expect(found.fix).toContain('SIC_WERoad07');
        expect(run('dawnguard-horse-sic', { relevantObjects: [horse, isran.replace('Isran', 'Vigilant')] })).toEqual([]);
    });

    it('core-impact-dismembering: the crash at 1AD5B40 with an affected framework version, or a log cut short', () => {
        const complete = { modules: ['SkyrimSE.exe 0x7FF6A0000000'], plugins: MODS };
        const [found] = run('core-impact-dismembering', {
            game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+1B5F710'), sksePlugins: ['CoreImpactFramework.dll v1.1.1'], ...complete
        });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }, { place: 'sksePlugins' }] });
        expect(run('core-impact-dismembering', { game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+1B5F710') })).toHaveLength(1);
        expect(run('core-impact-dismembering', {
            game: '1.7.104', firstLine: firstLineAt('SkyrimSE.exe+1B5F710'), sksePlugins: ['CoreImpactFramework.dll v1.2.0'], ...complete
        })).toEqual([]);
    });

    it('gts-t-pose-animal: the warp-to-location message handler above the stack', () => {
        const handler = 'RCX 0x1A2B3C4D      (ActorMovementMessageMap<16>::MessageHandlerWrapper<MovementMessageWarpToLocation>*)';
        const [found] = run('gts-t-pose-animal', { registers: [handler] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'registers' }] });
        expect(found.fix).toContain('Nexus 97490');
        expect(run('gts-t-pose-animal', { stack: [handler] })).toEqual([]);
    });

    it('combat-navmesh-retreat: the combat retreat navmesh search', () => {
        const search = frame(0, 'SkyrimSE.exe+0A1B2C3', 'mov rax, [rcx] |  CombatNavmeshSearchT<CombatPathingGoalPolicyRetreat,CombatPathingSearchPolicyStandard>::sub_A1B2C3');
        const [found] = run('combat-navmesh-retreat', { callStack: [search], plugins: [...MODS, 'SkyValor.esp'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'callStack' }, { place: 'plugins', text: expect.stringContaining('SkyValor.esp') }] });
        expect(found.explain).toContain('in the load order');
        expect(run('combat-navmesh-retreat', { callStack: [search.replace('Retreat', 'Flee')] })).toEqual([]);
    });

    it('navmesh-pathing: pathing classes above the stack are likely, in the stack only possible', () => {
        const [found] = run('navmesh-pathing', { callStack: [frame(0, 'SkyrimSE.exe+0A1B2C3', 'mov rax, [rcx] |  PathingCell::sub_A1B2C3')] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'callStack' }] });
        const [weak] = run('navmesh-pathing', { stack: ['[RSP+10 ] 0x1A2B3C4D      (BSPathBuilder*)'] });
        expect(weak).toMatchObject({ confidence: 'possible', evidence: [{ place: 'stack' }] });
        expect(run('navmesh-pathing', { relevantObjects: ['RSP+20: (NavMesh*) [0x0001A2B3] ("Skyrim.esm")'] })).toEqual([]);
    });

    it('armor-weight-perk: 0CD9F7C at the log\'s game version, or an input event', () => {
        const [found] = run('armor-weight-perk', { game: '1.7.104', callStack: [frame(0, 'SkyrimSE.exe+0CFA7CC')] });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(found.fix).toContain('modArmorWeightPerkEntryPoint');
        expect(run('armor-weight-perk', { stack: ['[RSP+10 ] 0x1A2B3C4D      (void* -> FUN_PEBQEAVInputEvent)'] })).toHaveLength(1);
        expect(run('armor-weight-perk', { game: '1.7.104', callStack: [frame(0, 'SkyrimSE.exe+0CD9F7C')] })).toEqual([]);
    });

    it('combat-magic-caster-disarm: the disarm caster above the plugin lists', () => {
        const disarm = ['RSP+20: (CombatMagicCasterDisarm*)'];
        const [found] = run('combat-magic-caster-disarm', { relevantObjects: disarm, sksePlugins: ['EngineFixes.dll v7.0.19'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'relevantObjects' }] });
        expect(found.fix).toContain('Nexus 12631');
        expect(found.fix).toContain('Nexus 17230');
        expect(run('combat-magic-caster-disarm', { relevantObjects: disarm })[0].fix).not.toContain('Engine Fixes');
        expect(run('combat-magic-caster-disarm', { relevantObjects: ['RSP+20: (CombatMagicCasterStagger*)'] })).toEqual([]);
    });

    it('custom-hair-color: an art object clone task and a magic caster, with Custom Hair Color loaded', () => {
        const relevantObjects = ['RSP+20: (BGSArtObjectCloneTask*)', 'RSP+28: (ActorMagicCaster*)'];
        const [found] = run('custom-hair-color', { relevantObjects, sksePlugins: ['CustomHairColor.dll v1.0.0'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'relevantObjects' }, { place: 'relevantObjects' }, { place: 'sksePlugins' }] });
        expect(run('custom-hair-color', { relevantObjects })).toEqual([]);
    });

    it('arcanum-jace-eyes: the 00SPJaceEyes asset, with Arcanum loaded', () => {
        const relevantObjects = ['RSP+30: (BSFadeNode*) "00SPJaceEyes.nif"'];
        const [found] = run('arcanum-jace-eyes', { relevantObjects, plugins: [...MODS, 'Arcanum.esp'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'relevantObjects' }, { place: 'plugins' }] });
        expect(found.fix).toContain('Nexus 65221');
        expect(run('arcanum-jace-eyes', { relevantObjects, plugins: MODS })).toEqual([]);
    });

    it('npcs-potions-uapng: a draugr above the plugin lists, with both potion mods loaded', () => {
        const parts: LogParts = { plugins: [...MODS, 'NPCsUsePotions.esp'], sksePlugins: ['UAPNG.dll v1.0.0'] };
        const [found] = run('npcs-potions-uapng', { relevantObjects: ['RSP+40: (Character*) "Draugr Wight" [0xFF001234]'], ...parts });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'relevantObjects' }, { place: 'plugins' }, { place: 'sksePlugins' }] });
        expect(run('npcs-potions-uapng', { relevantObjects: ['RSP+40: (Character*) "Bandit" [0xFF001234]'], ...parts })).toEqual([]);
    });

    it('death-drop-overhaul: two of an outdated version, its DLL on the stack, BaseExtraList near the stack\'s top', () => {
        const onStack = frame(0, 'DeathDropOverhaul.dll+0012345');
        const [found] = run('death-drop-overhaul', { sksePlugins: ['DeathDropOverhaul.dll v1.2.1'], callStack: [onStack] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'sksePlugins' }, { place: 'callStack' }] });
        expect(found.fix).toContain('Nexus 151590');
        const extraList = '[RSP+8  ] 0x1A2B3C4D      (BaseExtraList*)';
        expect(run('death-drop-overhaul', { sksePlugins: ['DeathDropOverhaul.dll v1.2.2'], callStack: [onStack], stack: [extraList] })).toHaveLength(1);
        expect(run('death-drop-overhaul', { sksePlugins: ['DeathDropOverhaul.dll v1.2.2'], callStack: [onStack] })).toEqual([]);
        const filler = Array.from({ length: 55 }, (_, i) => `[RSP+${(i * 8).toString(16).toUpperCase()}] 0x0 (size_t) [0]`);
        expect(run('death-drop-overhaul', { sksePlugins: ['DeathDropOverhaul.dll v1.2.2'], callStack: [onStack], stack: [...filler, extraList] })).toEqual([]);
    });

    it('climates-of-tamriel-divide-by-zero: a divide by zero with Climates of Tamriel above the plugin lists', () => {
        const divide = firstLineAt('SkyrimSE.exe+0123456', 'idiv ecx', 'EXCEPTION_INT_DIVIDE_BY_ZERO');
        const weather = ['RSP+40: (TESWeather*) "CoT Storm" [0x2A001234] ("ClimatesOfTamriel.esm")'];
        const [found] = run('climates-of-tamriel-divide-by-zero', { firstLine: divide, relevantObjects: weather });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'firstLine' }, { place: 'relevantObjects' }] });
        expect(run('climates-of-tamriel-divide-by-zero', { firstLine: divide, plugins: [...MODS, 'ClimatesOfTamriel.esm'] })).toEqual([]);
        expect(run('climates-of-tamriel-divide-by-zero', { relevantObjects: weather })).toEqual([]);
    });

    it('bgs-save-load-manager: the save and load manager above the plugin lists', () => {
        const callStack = [frame(0, 'SkyrimSE.exe+05A1234', 'call |  BGSSaveLoadManager::Save_5A1234')];
        const [found] = run('bgs-save-load-manager', { callStack });
        expect(found).toMatchObject({ confidence: 'possible', evidence: [{ place: 'callStack' }] });
        expect(found.fix).toContain('Install SSE Engine Fixes (Nexus 17230)');
        const [old] = run('bgs-save-load-manager', { game: '1.6.1170', callStack, sksePlugins: ['EngineFixes.dll v6.2.0'] });
        expect(old.fix).toContain('to 7 or later, or set SaveGameMaxSize = true');
        // Engine Fixes 7 is not made for 1.6.640.
        expect(run('bgs-save-load-manager', { game: '1.6.640', callStack, sksePlugins: ['EngineFixes.dll v6.2.0'] })[0].fix)
            .toContain('Set SaveGameMaxSize = true');
        expect(run('bgs-save-load-manager', { callStack, sksePlugins: ['EngineFixes.dll v7.0.19'] })[0].fix).not.toContain('Engine Fixes');
        expect(run('bgs-save-load-manager', { callStack: [frame(0, 'SkyrimSE.exe+05A1234', 'call |  BGSSaveFormBuffer::sub')] })).toEqual([]);
    });

    it('bgs-water-collision-manager: one of the three water collision handlers', () => {
        const [found] = run('bgs-water-collision-manager', { callStack: [frame(0, 'SkyrimSE.exe+0A1B2C3', 'mov |  BGSWaterCollisionManager::bshkAutoWater::vfunc_1')] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'callStack' }] });
        expect(found.fix).toContain('Nexus 120946');
        expect(run('bgs-water-collision-manager', { callStack: [frame(0, 'SkyrimSE.exe+0A1B2C3', 'mov |  BGSWaterCollisionManager::sub_A1B2C3')] })).toEqual([]);
    });

    it('redundant-bees: Backported Extended ESL Support on game 1.6.1130 or later', () => {
        const [found] = run('redundant-bees', { game: '1.6.1170', sksePlugins: ['BackportedESLSupport.dll v1.0.0'] });
        expect(found).toMatchObject({ confidence: 'likely', evidence: [{ place: 'sksePlugins' }] });
        expect(found.explain).toContain('1.6.1170');
        expect(run('redundant-bees', { game: '1.7.104', sksePlugins: ['BackportedESLSupport.dll v1.0.0'] })).toHaveLength(1);
        expect(run('redundant-bees', { game: '1.6.640', sksePlugins: ['BackportedESLSupport.dll v1.0.0'] })).toEqual([]);
    });
});
