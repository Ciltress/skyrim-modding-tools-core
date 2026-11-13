import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { declarationIn, papyrusHints } from '../backend/skyrim/externalTools';

/**
 * What a failed compile adds to the compiler's words from the game's sources: eval round 139's storymanager runs guessed
 * a location alias's functions and OnStoryChangeLocation's parameters a build each, the sources outside their sandbox.
 */

const roots: string[] = [];
afterAll(() => { for (const root of roots) fs.rmSync(root, { recursive: true, force: true }); });

function folders(): { scripts: string; vanilla: string } {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'papyrus-hints-'));
    roots.push(root);
    const scripts = path.join(root, 'scripts');
    const vanilla = path.join(root, 'Data', 'Source', 'Scripts');
    fs.mkdirSync(scripts, { recursive: true });
    fs.mkdirSync(vanilla, { recursive: true });
    // As the game's sources have them: an event's parameters continued on the next line, functions marked native.
    fs.writeFileSync(path.join(vanilla, 'Quest.psc'), 'Scriptname Quest extends Form Hidden\n\n' +
        'Function Stop() native\n\nEvent OnStoryChangeLocation(ObjectReference akActor, Location akOldLocation, \\\n\tLocation akNewLocation)\nEndEvent\n');
    fs.writeFileSync(path.join(vanilla, 'LocationAlias.psc'), 'Scriptname LocationAlias extends Alias Hidden\n\n' +
        '; Clears the alias\nFunction Clear() native\n\n; Returns the location\nLocation Function GetLocation() native\n\n' +
        'Function ForceLocationTo(Location akNewLocation) native\n');
    return { scripts, vanilla };
}

describe('papyrusHints', () => {
    it('quotes the parent\'s declaration of an event written with other parameters', () => {
        const { scripts, vanilla } = folders();
        const hints = papyrusHints(['C:\\p\\scripts\\ArrivalScript.psc(3,0): the parameter types of function onstorychangelocation in the empty ' +
            'state on script arrivalscript do not match the parent script quest'], scripts, vanilla);
        expect(hints).toEqual(['Quest.psc declares it so — write these parameter types: ' +
            'Event OnStoryChangeLocation(ObjectReference akActor, Location akOldLocation, Location akNewLocation)']);
    });

    it('names the functions of the type a call named none of', () => {
        const { scripts, vanilla } = folders();
        fs.writeFileSync(path.join(scripts, 'ArrivalScript.psc'), 'Scriptname ArrivalScript extends Quest\n\n' +
            'LocationAlias Property NewLocation Auto\n\nEvent OnInit()\n    Debug.Notification(NewLocation.GetLocationName())\nEndEvent\n');
        const hints = papyrusHints([`${path.join(scripts, 'ArrivalScript.psc')}(6,35): GetLocationName is not a function or does not exist`], scripts, vanilla);
        expect(hints).toEqual(['NewLocation is a LocationAlias, whose functions are Clear, GetLocation, ForceLocationTo, and Alias\'s, ' +
            'which it extends (LocationAlias.psc of the game\'s sources)']);
    });

    // Eval round 149's activator runs: 20 builds on lines like these, told only the compiler's words.
    it('says what to write for a line written unlike Papyrus', () => {
        const { scripts, vanilla } = folders();
        const file = path.join(scripts, 'GoldLeverScript.psc');
        fs.writeFileSync(file, 'ScriptName GoldLeverScript extends ObjectReference\n\nMiscObjectProperty Gold001 Auto\nMessage Welcome Auto\n' +
            'Event OnActivate(ObjectReference akActionRef)\n    If akActionRef != None Then\n        Player.AddItem(Gold001, 100)\n' +
            '        Game.GetPlayer().AddItem(000F00:Skyrim.esm, 100)\n    EndIf\n    # gold given\n    Game.GetPlayer().AddItem(Gold, Amount)\nEndEvent\n');
        const hints = papyrusHints([
            `${file}(3,31): mismatched input '\\n' expecting STATE`,
            `${file}(4,21): required (...)+ loop did not match anything at input 'Auto'`,
            `${file}(6,29): required (...)+ loop did not match anything at input 'Then'`,
            `${file}(7,8): variable Player is undefined`,
            `${file}(8,34): no viable alternative at character ':'`,
            `${file}(10,4): no viable alternative at character '#'`,
            `${file}(11,30): variable Gold is undefined`
        ], scripts, vanilla);
        expect(hints[0]).toBe('line 3 of GoldLeverScript.psc, `MiscObjectProperty Gold001 Auto`: a property is declared with its type and the word ' +
            'Property apart — `MiscObject Property Gold001 Auto`');
        expect(hints[1]).toBe('line 4 of GoldLeverScript.psc, `Message Welcome Auto`: a property is declared with the word Property between its type ' +
            'and its name — `Message Property Welcome Auto`');
        expect(hints[2]).toBe('line 6 of GoldLeverScript.psc, `If akActionRef != None Then`: Papyrus writes no `Then` — `If akActionRef != None` ' +
            'alone on its line, its block ended by `EndIf`');
        expect(hints[3]).toMatch(/^line 7 .*: Papyrus has no Player — the player is `Game.GetPlayer\(\)`, an Actor/);
        expect(hints[4]).toMatch(/^line 8 .*: a script names no record by its FormID — a record a script uses is a property, declared at its top — `MiscObject Property Gold001 Auto`/);
        expect(hints[5]).toBe('line 10 of GoldLeverScript.psc, `# gold given`: a Papyrus comment starts with `;` — `#` and `//` are none');
        expect(hints[6]).toMatch(/^line 11 .*: Gold is declared nowhere in the script — a record a script uses is a property.*; a number of its own is a variable, `Int Gold = 100`$/);
        expect(hints).toHaveLength(7);
    });

    // Eval round 150's activator runs: functions made up for gold and for a message on the screen.
    it('says how the game gives gold and shows a message where a made-up function was called', () => {
        const { scripts, vanilla } = folders();
        const file = path.join(scripts, 'LeverScript.psc');
        fs.writeFileSync(file, 'ScriptName LeverScript extends ObjectReference\nEvent OnActivate(ObjectReference akActionRef)\n' +
            '    Game.GetPlayer().AddGold(100)\n    Game.GetPlayer().DisplayNotification("Gold!")\n    Game.GetPlayer().Debug.Notification("Gold!")\nEndEvent\n');
        const hints = papyrusHints([
            `${file}(3,21): AddGold is not a function or does not exist.`,
            `${file}(4,21): DisplayNotification is not a function or does not exist.`,
            `${file}(5,21): the type name Debug cannot be used as a property.`
        ], scripts, vanilla);
        expect(hints[0]).toMatch(/^line 3 .*: Papyrus has no AddGold — gold is an item, given as `Game.GetPlayer\(\).AddItem\(Gold001, 100\)`/);
        expect(hints[1]).toMatch(/^line 4 .*: a message on the screen is `Debug.Notification\("…"\)`/);
        expect(hints[2]).toMatch(/^line 5 .*: a message on the screen is `Debug.Notification\("…"\)`/);
        expect(hints).toHaveLength(3);
    });

    it('names the script file left over beside the one named as its ScriptName', () => {
        const { scripts, vanilla } = folders();
        fs.writeFileSync(path.join(scripts, 'LeverScript.psc'), 'ScriptName GoldLeverScript extends ObjectReference\n');
        fs.writeFileSync(path.join(scripts, 'GoldLeverScript.psc'), 'ScriptName GoldLeverScript extends ObjectReference\n');
        const [hint] = papyrusHints([`${path.join(scripts, 'LeverScript.psc')}(0,0): filename does not match script name: goldleverscript.`], scripts, vanilla);
        expect(hint).toBe('LeverScript.psc declares `ScriptName GoldLeverScript`, as GoldLeverScript.psc does, and a script\'s file is named as its ' +
            `ScriptName: GoldLeverScript.psc holds that script, and LeverScript.psc is left over — deleteRecord {"project": ` +
            `${JSON.stringify(path.basename(path.dirname(scripts)))}, "record": ${JSON.stringify(path.join(scripts, 'LeverScript.psc').split(path.sep).join('/'))}}`);
    });

    it('says the ScriptName line whole where it was split or lost, and Papyrus\'s name of a type', () => {
        const { scripts, vanilla } = folders();
        const split = path.join(scripts, 'StrangeLeverScript.psc');
        fs.writeFileSync(split, 'Scriptname StrangeLeverScript\nextends Activator\n\nEvent OnActivate(ObjectReference akActionRef)\nEndEvent\n');
        const lost = path.join(scripts, 'GoldLeverScript.psc');
        fs.writeFileSync(lost, 'MiscObject Property Gold001 Auto\n\nInteger Property Amount Auto\n');
        const hints = papyrusHints([`${split}(2,0): missing EOF at 'extends'`, `${lost}(1,0): missing SCRIPTNAME at 'MiscObject'`,
            `${lost}(3,0): unknown type integer`], scripts, vanilla);
        expect(hints).toEqual([
            'line 2 of StrangeLeverScript.psc, `extends Activator`: a script\'s first line is its ScriptName and what it extends, on one line — ' +
            '`ScriptName StrangeLeverScript extends ObjectReference` (a script on a thing placed in a cell — a lever, a door, a chest — ' +
            'extends ObjectReference, whose event OnActivate is, not Activator)',
            'line 1 of GoldLeverScript.psc, `MiscObject Property Gold001 Auto`: a script\'s first line is its ScriptName and what it extends, ' +
            'on one line — `ScriptName GoldLeverScript extends ObjectReference`',
            'line 3 of GoldLeverScript.psc, `Integer Property Amount Auto`: Papyrus has no type integer — it is `Int` (Papyrus\'s types: Int, ' +
            'Float, Bool, String, and the game\'s, such as MiscObject)'
        ]);
    });

    it('adds nothing for an error the sources do not explain', () => {
        const { scripts, vanilla } = folders();
        expect(papyrusHints(['C:\\p\\scripts\\X.psc(1,0): mismatched input', 'Compilation failed'], scripts, vanilla)).toEqual([]);
        expect(declarationIn('Scriptname Quest extends Form', 'OnStoryChangeLocation')).toBeNull();
    });
});
