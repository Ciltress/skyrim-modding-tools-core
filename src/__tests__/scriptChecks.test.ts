import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { PapyrusSources, readPapyrus, scriptWarnings, type ScriptedRecord } from '../backend/skyrim/scriptChecks';

/**
 * The project's scripts held against their sources and the game's: what compiles and builds, and does nothing in game
 * (eval rounds 125 and 126, the quest task).
 */

const QUEST_PSC = 'Scriptname Quest extends Form Hidden\n\nbool Function Stop() native\n\n' +
    'Event OnStoryScript(Keyword akKeyword, Location akLocation, ObjectReference akRef1, \\\n  ObjectReference akRef2, int aiValue1, int aiValue2)\nEndEvent\n';
const FORM_PSC = 'Scriptname Form Hidden\n\nEvent OnUpdate()\nEndEvent\n\nEvent OnUpdateGameTime()\nEndEvent\n';

const GOOD = 'Scriptname StarterGoldScript extends Quest\n\nMiscObject Property Gold001 Auto\n\n' +
    'Event OnInit()\n    Game.GetPlayer().AddItem(Gold001, 500) ; the gold\n    Stop()\nEndEvent\n';

const quest = (properties: ScriptedRecord['scripts'][number]['properties'] = [{ name: 'Gold001', kind: 'Object', filled: true }]): ScriptedRecord =>
    ({ file: 'Quests/StarterGoldQuest.yaml', editorId: 'StarterGoldQuest', type: 'Quest', scripts: [{ name: 'StarterGoldScript', properties }] });

let root: string;
let scriptsDir: string;
let vanilla: string;
beforeEach(() => {
    root = fs.mkdtempSync(path.join(os.tmpdir(), 'skyrim-script-checks-'));
    scriptsDir = path.join(root, 'project', 'scripts');
    vanilla = path.join(root, 'Data', 'Source', 'Scripts');
    fs.mkdirSync(scriptsDir, { recursive: true });
    fs.mkdirSync(vanilla, { recursive: true });
    fs.writeFileSync(path.join(vanilla, 'Quest.psc'), QUEST_PSC);
    fs.writeFileSync(path.join(vanilla, 'Form.psc'), FORM_PSC);
});
afterEach(() => fs.rmSync(root, { recursive: true, force: true }));

const warn = (records: ScriptedRecord[], script?: string, gameHas = (_: string) => false) => {
    if (script !== undefined) fs.writeFileSync(path.join(scriptsDir, 'StarterGoldScript.psc'), script);
    return scriptWarnings(records, { scriptsDir, sources: new PapyrusSources([scriptsDir, vanilla]), gameHas });
};

describe('readPapyrus', () => {
    it('reads the name, the parent, events, properties and variables, comments and strings aside', () => {
        const source = readPapyrus('ScriptName A extends Quest Conditional\n{ a doc comment\n Event OnFake() }\n' +
            ';/ Event OnBlock()\n/;\nForm GoldCoins\nInt Property Count = 5 Auto\nActor Property PlayerRef Auto\n' +
            'Event OnInit() ; Event OnComment()\n  RegisterForModEvent("MyEvent", "OnMyEvent")\n  Form local = None\nEndEvent\n');
        expect(source).toMatchObject({ name: 'A', extends: 'Quest', events: ['OnInit'], strings: ['MyEvent', 'OnMyEvent'] });
        expect(source.properties).toEqual([
            { name: 'Count', type: 'Int', auto: true, valued: true },
            { name: 'PlayerRef', type: 'Actor', auto: true, valued: false }
        ]);
        expect(source.variables).toEqual([{ name: 'GoldCoins', type: 'Form', valued: false }]);
        expect(source.calls).toContain('registerformodevent');
    });
});

describe('scriptWarnings', () => {
    it('is quiet for a quest script that extends Quest, starts in OnInit and has its property filled', () => {
        expect(warn([quest()], GOOD)).toEqual([]);
    });

    it('names a script attached that neither the project nor the game has (round 126\'s quest #4)', () => {
        const [warning, ...rest] = warn([quest([])]);
        expect(rest).toEqual([]);
        expect(warning).toContain('StarterGoldQuest, a quest, has the script StarterGoldScript attached, which neither this project nor the game has');
        expect(warning).toContain('`ScriptName StarterGoldScript extends Quest`');
        expect(warn([quest([])], undefined, name => name === 'StarterGoldScript')).toEqual([]);
    });

    it('names an event the game never sends, and the one a quest starts in (round 125\'s quest+detailed #2)', () => {
        const [warning, ...rest] = warn([quest()], GOOD.replace('Event OnInit()', 'Event OnQuestInit()'));
        expect(rest).toEqual([]);
        expect(warning).toContain('Event OnQuestInit is no event the game sends a script that extends Quest — neither Quest nor Form declares it');
        expect(warning).toContain('write `Event OnInit()` in its place');
    });

    it('takes the events the parents declare, Papyrus\'s own, and one the script registers for by name', () => {
        const script = GOOD.replace('EndEvent\n', 'EndEvent\n\nEvent OnUpdate()\nEndEvent\n\nState Busy\n  Event OnBeginState()\n  EndEvent\nEndState\n\n' +
            'Event OnMyModEvent(string eventName, string strArg, float numArg, Form sender)\nEndEvent\n')
            .replace('    Stop()\n', '    Stop()\n    RegisterForModEvent("MyMod_Start", "OnMyModEvent")\n');
        expect(warn([quest()], script)).toEqual([]);
    });

    it('checks no event where a parent\'s source is missing', () => {
        expect(warn([quest()], GOOD.replace('extends Quest', 'extends SomeModQuestBase').replace('Event OnInit()', 'Event OnQuestInit()'))).toEqual([]);
    });

    it('names a script that extends nothing, or no Quest, on a quest', () => {
        const [warning] = warn([quest()], GOOD.replace(' extends Quest', '').replace('    Stop()\n', ''));
        expect(warning).toContain('StarterGoldScript extends nothing, and the game runs a script on StarterGoldQuest, a quest, only if it extends Quest');
        expect(warning).toContain('`ScriptName StarterGoldScript extends Quest`');
        fs.writeFileSync(path.join(vanilla, 'ObjectReference.psc'), 'Scriptname ObjectReference extends Form Hidden\n');
        expect(warn([quest()], GOOD.replace('extends Quest', 'extends ObjectReference'))[0]).toContain('extends ObjectReference, which is no Quest');
    });

    it('names a script with no code, nothing but its ScriptName line and comments (round 128\'s quest #1)', () => {
        const [warning, ...rest] = warn([quest([])], 'Scriptname StarterGoldScript extends Quest\n; gives the player 500 gold\n{ at the start }\n');
        expect(rest).toEqual([]);
        expect(warning).toContain('StarterGoldScript has no code — no event, no function, no property, nothing but its ScriptName line');
        expect(warning).toContain('`Event OnInit()`');
        // A script of properties alone, which other scripts read, is left alone.
        expect(warn([quest()], 'Scriptname StarterGoldScript extends Quest\nMiscObject Property Gold001 Auto\n')).toEqual([]);
    });

    it('names a property filled that the script has as a variable (round 125\'s quest+detailed #5)', () => {
        const [warning, ...rest] = warn([quest()], GOOD.replace('MiscObject Property Gold001 Auto', 'MiscObject Gold001'));
        expect(rest).toEqual([]);
        expect(warning).toContain('fills the property Gold001 of StarterGoldScript, which declares no property Gold001');
        expect(warning).toContain('`MiscObject Property Gold001 Auto` in its place');
    });

    it('names a record variable the script uses and never sets, but not one it sets', () => {
        const script = GOOD.replace('MiscObject Property Gold001 Auto', 'MiscObject Gold001');
        const [warning, ...rest] = warn([quest([])], script);
        expect(rest).toEqual([]);
        expect(warning).toContain('`MiscObject Gold001` is a variable of StarterGoldScript that nothing sets, so Gold001 is None in game');
        expect(warning).toContain('Make it a property, `MiscObject Property Gold001 Auto` in its place');
        expect(warn([quest([])], script.replace('Event OnInit()\n', 'Event OnInit()\n    Gold001 = Game.GetFormFromFile(0xF, "Skyrim.esm") as MiscObject\n'))).toEqual([]);
    });

    it('leaves alone a fill of a name the script has nowhere, which harms nothing', () => {
        const script = 'Scriptname StarterGoldScript extends Quest\n\nEvent OnInit()\n    GiveGold()\nEndEvent\n\nFunction GiveGold()\n' +
            '    Game.GetPlayer().AddItem(Game.GetFormFromFile(0x0000000F, "Skyrim.esm"), 500)\nEndFunction\n';
        expect(warn([quest([{ name: 'SteelSword', kind: 'Object', filled: true }])], script)).toEqual([]);
    });

    it('names a script of the project nothing runs, but not one a record file names or a script extends', () => {
        const [warning, ...rest] = warn([], GOOD);
        expect(rest).toEqual([]);
        expect(warning).toContain('StarterGoldScript is attached to no record of this project, and no script of it extends or calls StarterGoldScript');
        expect(warning).toContain('`  - Name: StarterGoldScript`');
        const quiet = (recordTexts: string) => scriptWarnings([], { scriptsDir, sources: new PapyrusSources([scriptsDir, vanilla]), gameHas: () => false, recordTexts });
        expect(quiet('  FileName: StarterGoldScript\n')).toEqual([]);
        fs.writeFileSync(path.join(scriptsDir, 'OtherScript.psc'), 'Scriptname OtherScript extends StarterGoldScript\n');
        expect(quiet('VirtualMachineAdapter:\n  Scripts:\n  - Name: OtherScript\n')).toEqual([]);
    });

    it('names a form property nothing fills, but not a value, an array or one with a value of its own', () => {
        const script = GOOD.replace('MiscObject Property Gold001 Auto',
            'MiscObject Property Gold001 Auto\nInt Property Amount Auto\nForm[] Property Extras Auto\nFloat Property Delay = 1.0 Auto');
        const [warning, ...rest] = warn([quest([])], script);
        expect(rest).toEqual([]);
        expect(warning).toContain('StarterGoldScript declares the property Gold001 (MiscObject), and StarterGoldQuest, a quest, fills it with nothing, so it is None in game');
        expect(warn([quest([{ name: 'Gold001', kind: 'Object', filled: false }])], script)).toHaveLength(1);
    });

    it('checks a quest alias\'s script against ReferenceAlias', () => {
        fs.writeFileSync(path.join(vanilla, 'Alias.psc'), 'Scriptname Alias extends Form Hidden\n');
        fs.writeFileSync(path.join(vanilla, 'ReferenceAlias.psc'), 'Scriptname ReferenceAlias extends Alias Hidden\n\nEvent OnActivate(ObjectReference akActionRef)\nEndEvent\n');
        const record: ScriptedRecord = { ...quest(), scripts: [], aliases: [{ alias: 0, scripts: [{ name: 'StarterGoldScript', properties: [] }] }] };
        expect(warn([record], 'Scriptname StarterGoldScript extends ReferenceAlias\n\nEvent OnActivate(ObjectReference akActionRef)\nEndEvent\n')).toEqual([]);
        expect(warn([record], GOOD.replace('MiscObject Property Gold001 Auto\n', ''))[0]).toContain('extends Quest, which is no Alias');
    });
});
