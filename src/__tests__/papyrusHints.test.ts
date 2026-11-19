import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { compilerProblems, declarationIn, papyrusHints } from '../backend/skyrim/externalTools';

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
    fs.writeFileSync(path.join(vanilla, 'Message.psc'), 'Scriptname Message extends Form Hidden\n\nint Function Show() native\n');
    fs.writeFileSync(path.join(vanilla, 'Actor.psc'), 'Scriptname Actor extends ObjectReference Hidden\n\nFunction AddPerk(Perk akPerk) native\n');
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

    // Eval round 157, on Linux: russo-2025's compiler words its errors otherwise, and 12 of 30 runs were told only its summary.
    it('reads russo-2025\'s compiler as the Creation Kit\'s', () => {
        const { scripts, vanilla } = folders();
        fs.writeFileSync(path.join(scripts, 'ArrivalScript.psc'), 'Scriptname ArrivalScript extends Quest\n\n' +
            'LocationAlias Property NewLocation Auto\n\nEvent OnInit()\n    Debug.Notification(NewLocation.GetLocationName())\nEndEvent\n');
        const lever = path.join(scripts, 'GoldLeverScript.psc');
        fs.writeFileSync(lever, 'ScriptName GoldLeverScript extends ObjectReference\n\nMiscObjectProperty Gold001 Auto\nInteger Property Amount Auto\n' +
            'Event OnActivate(ObjectReference akActionRef)\n    If akActionRef != None Then\n        Player.AddItem(Gold001, 100)\n' +
            '        Game.GetPlayer().AddItem(000F00:Skyrim.esm, 100)\n    EndIf\n    # gold given\n    Game.GetPlayer().AddItem(Gold, 100)\n' +
            '    Game.GetPlayer().AddGold(100)\n    Game.GetPlayer().Debug.Notification("Gold!")\n    Actor Player = Game.GetPlayer()\n' +
            '    Player.GiveItem(Gold001, 100)\n    Player.AddItem("Gold", 100)\nEndEvent\n');
        fs.writeFileSync(path.join(scripts, 'WelcomeScript.psc'), 'ScriptName WelcomeScript extends Quest\n\nMessageObjectProperty WelcomeMessage Auto\n');
        fs.writeFileSync(path.join(scripts, 'LeverScript.psc'), 'ScriptName GoldLeverScript extends ObjectReference\n');
        fs.writeFileSync(path.join(scripts, 'Lost.psc'), 'Integer Property Amount Auto\n');
        // As russo's compiler names them: the whole path it was given, or one from the folder it ran in.
        const hints = papyrusHints([
            `${path.join(scripts, 'ArrivalScript.psc')}:6:36: Checker error: undefined function \`GetLocationName\``,
            `${lever}:3:20: Checker error: undefined type: \`MiscObjectProperty\``,
            `${lever}:4:9: Checker error: undefined type: \`Integer\``,
            `${lever}:6:28: Checker error: undefined identifier \`Then\``,
            `${lever}:7:9: Checker error: undefined identifier \`Player\``,
            `${lever}:7:16: Checker error: undefined function \`AddItem\``,
            'GoldLever/scripts/GoldLeverScript.psc:8:40: Scanner error: invalid character `:`',
            `${lever}:10:5: Scanner error: invalid character \`#\``,
            `${lever}:11:30: Checker error: undefined identifier \`Gold\``,
            `${lever}:12:22: Checker error: undefined function \`AddGold\``,
            `${lever}:13:22: Checker error: field or property \`Debug\` not found`,
            `${lever}:13:28: Checker error: undefined function \`Notification\``,
            `${lever}:15:12: Checker error: undefined function \`GiveItem\``,
            `${lever}:16:12: Checker error: cannot convert type \`String\` to type \`Form\``,
            `${path.join(scripts, 'LeverScript.psc')}:1:22: Parser error: script name \`GoldLeverScript\` does not match file name \`LeverScript\``,
            `${path.join(scripts, 'Lost.psc')}:1:1: Parser error: unexpected \`Integer\`, expecting \`scriptname\` or \`scriptplus\``,
            `${path.join(scripts, 'WelcomeScript.psc')}:3:23: Checker error: undefined type: \`MessageObjectProperty\``
        ], scripts, vanilla);
        const said = (line: number) => hints.filter(hint => hint.startsWith(`line ${line} of GoldLeverScript.psc,`));
        expect(said(3)).toEqual(['line 3 of GoldLeverScript.psc, `MiscObjectProperty Gold001 Auto`: a property is declared with its type and the ' +
            'word Property apart — `MiscObject Property Gold001 Auto`']);
        expect(said(4)).toEqual([expect.stringMatching(/: Papyrus has no type integer — it is `Int`/)]);
        expect(said(6)).toEqual([expect.stringMatching(/: Papyrus writes no `Then`/)]);
        expect(said(7)).toEqual([expect.stringMatching(/: Papyrus has no Player — the player is `Game.GetPlayer\(\)`/)]);
        expect(said(8)).toEqual([expect.stringMatching(/: a script names no record by its FormID — /)]);
        expect(said(10)).toEqual([expect.stringMatching(/: a Papyrus comment starts with `;`/)]);
        expect(said(11)).toEqual([expect.stringMatching(/: Gold is declared nowhere in the script — .*`Int Gold = 100`$/)]);
        expect(said(12)).toEqual([expect.stringMatching(/: Papyrus has no AddGold — gold is an item/)]);
        expect(said(13)).toEqual([expect.stringMatching(/: a message on the screen is `Debug.Notification\("…"\)`/)]);
        // Answered so, and not with every function of Actor, the type of the Player it declared.
        expect(said(15)).toEqual([expect.stringMatching(/: Papyrus has no GiveItem — an item is given with AddItem, as `Game.GetPlayer\(\).AddItem\(Gold001, 100\)`/)]);
        expect(hints.filter(hint => /whose functions are/.test(hint))).toHaveLength(1);
        expect(said(16)).toEqual([expect.stringMatching(/`Player.AddItem\("Gold", 100\)`: a script names no record by its name in quotes — /)]);
        expect(hints).toContain('NewLocation is a LocationAlias, whose functions are Clear, GetLocation, ForceLocationTo, and Alias\'s, ' +
            'which it extends (LocationAlias.psc of the game\'s sources)');
        expect(hints).toContainEqual(expect.stringMatching(/^LeverScript.psc declares `ScriptName GoldLeverScript`, as GoldLeverScript.psc does/));
        expect(hints).toContainEqual(expect.stringMatching(/^line 1 of Lost.psc, `Integer Property Amount Auto`: a script's first line is its ScriptName/));
        // The record's `ScriptObjectProperty` copied: the game has a Message, and no MessageObject.
        expect(hints).toContain('line 3 of WelcomeScript.psc, `MessageObjectProperty WelcomeMessage Auto`: a property is declared with its ' +
            'type and the word Property apart — `Message Property WelcomeMessage Auto`');
    });

    it('keeps russo-2025\'s errors and summary from what it wrote, not the source lines it quotes', () => {
        // Its output for a script with three errors, cut short: stdout's timings, stderr's errors with their source lines.
        const output = 'used header dirs [\'/p/GoldLever/scripts\', \'/game/Data/Source/Scripts\']\n1 files in total\nparse files: 0.06 ms\n' +
            '/p/GoldLever/scripts/T.psc:4:30: Checker error: undefined identifier `Gold`\n' +
            '    2 | MiscObject Property Gold001 Auto\n    4 |     Game.GetPlayer().AddItem(Gold, Amount) ; failed\n' +
            '      |                              ~~~~\n' +
            '/p/GoldLever/scripts/T.psc:4:22: Checker error: cannot convert type `None` to type `Int`\n' +
            'failed to compile files, 2 errors\n' +
            // A parser error comes after its stack trace.
            '                     | 0x47701a | /tools/papyrus(papyrus__parser__parse_file+0x36a) \n' +
            'GoldLever/scripts/U.psc:4:5: Parser error: (block statement) invalid token: /, \n    4 |     // gold given\n';
        expect(compilerProblems(output)).toEqual([
            '/p/GoldLever/scripts/T.psc:4:30: Checker error: undefined identifier `Gold`',
            '/p/GoldLever/scripts/T.psc:4:22: Checker error: cannot convert type `None` to type `Int`',
            'failed to compile files, 2 errors',
            'GoldLever/scripts/U.psc:4:5: Parser error: (block statement) invalid token: /,'
        ]);
        // The Creation Kit's, as before.
        expect(compilerProblems('Starting 1 compile threads for 1 files...\nCompiling "MyQuest"...\n' +
            'C:\\p\\scripts\\MyQuest.psc(3,1): no viable alternative at input \'Then\'\nNo output generated for MyQuest, compilation failed.\n'))
            .toEqual(['C:\\p\\scripts\\MyQuest.psc(3,1): no viable alternative at input \'Then\'', 'No output generated for MyQuest, compilation failed.']);
    });

    // Eval round 158's activator runs: an `if` left open, its block in braces, 3 builds; a property typed with its record's name.
    it('names the block a line left open, and a record\'s name written as a type', () => {
        const { scripts, vanilla } = folders();
        const file = path.join(scripts, 'GoldLeverScript.psc');
        fs.writeFileSync(file, 'Scriptname GoldLeverScript extends ObjectReference\nconst GOLD_GRANT = 100\nGold001 Property Gold001 Auto\n\n' +
            'Event OnActivate(ObjectReference akActionRef)\n    if (akActionRef == Game.GetPlayer())\n    {\n' +
            '        Game.GetPlayer().AddItem(Gold001, 100) ; if only\n    }\nEndEvent\n');
        const split = path.join(scripts, 'SplitScript.psc');
        fs.writeFileSync(split, 'Scriptname SplitScript extends ObjectReference\nEvent OnActivate(ObjectReference akActionRef)\n    If akActionRef\n' +
            '    else if akActionRef == None\n    EndIf\n    While False\n    EndWhile\nEndEvent\n\nFunction Give()\n    int i = 0\n' +
            '    While i < 3\n        i += 1\nEndFunction\n\nEvent OnInit()\n');
        const hints = papyrusHints([
            `${file}:10:1: Parser error: unexpected \`endevent\`, expecting \`elseif\``,
            `${file}:2:7: Checker error: undefined type: \`const\``,
            `${file}:3:9: Checker error: undefined type: \`Gold001\``,
            `${file}:8:34: Checker error: undefined type: \`Gold001\``,
            `${split}:8:1: Parser error: unexpected \`endevent\`, expecting \`endif\``,
            `${split}:14:1: Parser error: unexpected \`endfunction\`, expecting \`endwhile\``,
            `${split}:17:1: Parser error: unexpected end of file`
        ], scripts, vanilla);
        const unclosedIf = 'line 10 of GoldLeverScript.psc, `EndEvent`: the If on line 6, `if (akActionRef == Game.GetPlayer())`, is never ' +
            'closed — Papyrus ends an If with `EndIf` on a line of its own; Papyrus has no braces `{ }`: a block ends with its End word';
        expect(hints).toEqual([
            unclosedIf,
            'line 2 of GoldLeverScript.psc, `const GOLD_GRANT = 100`: Papyrus has no `const` — a value of the script\'s own is a variable, ' +
            'its type first: `Int GOLD_GRANT = 100`',
            expect.stringMatching(/^line 3 of GoldLeverScript.psc, `Gold001 Property Gold001 Auto`: Gold001 is a record's name, no type — a record a script uses is a property, declared at its top — `MiscObject Property Gold001 Auto`/),
            'line 8 of SplitScript.psc, `EndEvent`: the If on line 3, `If akActionRef`, is never closed — Papyrus ends an If with `EndIf` ' +
            'on a line of its own; `else if` on line 4 opens a second If: Papyrus writes it as one word, `ElseIf`',
            'line 14 of SplitScript.psc, `EndFunction`: the While on line 12, `While i < 3`, is never closed — Papyrus ends a While with ' +
            '`EndWhile` on a line of its own',
            'the end of SplitScript.psc: the Event on line 16, `Event OnInit()`, is never closed — Papyrus ends an Event with ' +
            '`EndEvent` on a line of its own'
        ]);
        // The Creation Kit's words for an If left open.
        expect(papyrusHints([`GoldLeverScript.psc(10,0): mismatched input 'EndEvent' expecting ENDIF`], scripts, vanilla)).toEqual([unclosedIf]);
    });

    // Eval round 159's activator+detailed #5: `Int Property Amount` without Auto, then its event renamed a function for russo-2025's
    // "expecting `function`", and the block hint sent it to that function's EndFunction. russo-2025's words as it writes them.
    it('names a property without Auto, and one written out in full never ended', () => {
        const { scripts, vanilla } = folders();
        const write = (name: string, text: string) => { fs.writeFileSync(path.join(scripts, `${name}.psc`), `ScriptName ${name} extends ObjectReference\n\n${text}`); return path.join(scripts, `${name}.psc`); };
        const event = write('PEvent', 'ObjectReference Property GoldRef Auto\nInt Property Amount ; how much\n\nEvent OnInit()\n    Debug.Notification("x")\nEndEvent\n');
        const renamed = write('PRenamed', 'Int Property Amount\n\nFunction OnInit()\nEndEvent\n');
        const called = write('PCalled', 'Int Property Amount\n\nfunction OnActivate(ObjectReference akActionRef)\nEndFunction\n');
        const last = write('PLast', 'MiscObject Property Gold001 Hidden\n');
        const valued = write('PValued', 'Int Property Amount = 100\n\nEvent OnActivate(ObjectReference akActionRef)\nEndEvent\n');
        const full = write('PFull', 'Int Property Amount\n    Int Function Get()\n        return 5\n    EndFunction\n\nEvent OnInit()\nEndEvent\n');
        const noAuto = 'has no `Auto` — Papyrus reads a property without it as one written out in full, its own Get and Set functions to follow ' +
            'and `EndProperty` to end it, so it expects a function next. End its line with Auto: ';
        const amount = 'the Property on line 3, `Int Property Amount`, ' + noAuto + '`Int Property Amount Auto`, or `Int Property Amount = 100 Auto` to give it a value in the script';
        expect(papyrusHints([
            `${event}:6:1: Parser error: unexpected \`event\`, expecting \`function\``,
            `${renamed}:6:1: Parser error: unexpected \`endevent\`, expecting \`endfunction\``,
            `${called}:5:1: Parser error: invalid function name: onactivate, expected \`get\` or \`set\``,
            `${last}:4:1: Parser error: unexpected \`eof\`, expecting \`function\``,
            `${valued}:5:1: Parser error: unexpected \`event\`, expecting \`function\``,
            `${full}:8:1: Parser error: unexpected \`event\`, expecting \`function\``
        ], scripts, vanilla)).toEqual([
            'line 6 of PEvent.psc, `Event OnInit()`: the Property on line 4, `Int Property Amount`, ' + noAuto +
            '`Int Property Amount Auto`, or `Int Property Amount = 100 Auto` to give it a value in the script',
            'line 6 of PRenamed.psc, `EndEvent`: ' + amount + '; OnInit is an event the game sends, written `Event OnInit(…)` and ended by `EndEvent`',
            'line 5 of PCalled.psc, `function OnActivate(ObjectReference akActionRef)`: ' + amount +
            '; OnActivate is an event the game sends, written `Event OnActivate(…)` and ended by `EndEvent`',
            'the end of PLast.psc: the Property on line 3, `MiscObject Property Gold001 Hidden`, ' + noAuto + '`MiscObject Property Gold001 Auto Hidden`',
            'line 5 of PValued.psc, `Event OnActivate(ObjectReference akActionRef)`: the Property on line 3, `Int Property Amount = 100`, ' + noAuto +
            '`Int Property Amount = 100 Auto`',
            'line 8 of PFull.psc, `Event OnInit()`: the Property on line 3, `Int Property Amount`, is never closed — Papyrus ends a Property ' +
            'with `EndProperty` on a line of its own'
        ]);
        // ANTLR's words, which the Creation Kit's compiler writes.
        expect(papyrusHints([`PEvent.psc(6,0): mismatched input 'Event' expecting FUNCTION`], scripts, vanilla)).toEqual([
            expect.stringMatching(/^line 6 of PEvent.psc, `Event OnInit\(\)`: the Property on line 4, `Int Property Amount`, has no `Auto`/)
        ]);
    });

    it('adds nothing for an error the sources do not explain', () => {
        const { scripts, vanilla } = folders();
        expect(papyrusHints(['C:\\p\\scripts\\X.psc(1,0): mismatched input', 'Compilation failed'], scripts, vanilla)).toEqual([]);
        expect(declarationIn('Scriptname Quest extends Form', 'OnStoryChangeLocation')).toBeNull();
    });
});
