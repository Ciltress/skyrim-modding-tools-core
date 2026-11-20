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
    const byUse = '`Message` for a message box, `MiscObject` for gold (`MiscObject Property Gold001 Auto`), `ObjectReference` for a ' +
        'thing placed in a cell, `Actor` for an NPC placed, `Quest` for a quest';

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

    // Eval round 160's message #3 ended its script with `EndScript`, told "compiler bug" three builds. russo-2025's words as it writes them.
    it('names an End word that closes nothing', () => {
        const { scripts, vanilla } = folders();
        const write = (name: string, text: string) => { fs.writeFileSync(path.join(scripts, `${name}.psc`), `ScriptName ${name} extends Quest\n\n${text}`); return path.join(scripts, `${name}.psc`); };
        const script = write('EScript', 'Message Property WelcomeMessageBox Auto\n\nEvent OnInit()\n    WelcomeMessageBox.Show()\n    Stop()\nEndEvent\nEndScript\n');
        const twice = write('ETwice', 'Event OnInit()\n    Stop()\nEndEvent\nEndEvent\n');
        const stray = write('EStray', 'Event OnInit()\n    Stop()\n    EndIf\nEndEvent\n');
        const bare = write('EBare', 'Event OnInit()\nEndEvent\nEnd\n');
        expect(papyrusHints([
            `${script}:9:1: Parser error: [Parser.parserBugCheck] compiler bug`,
            `${twice}:6:1: Parser error: (top statement) invalid token: endevent, EndEvent`,
            `${stray}:5:5: Parser error: unexpected \`endif\`, expecting \`endevent\``,
            `${bare}:5:1: Parser error: [Parser.parserBugCheck] compiler bug`
        ], scripts, vanilla)).toEqual([
            'line 9 of EScript.psc, `EndScript`: Papyrus has no `EndScript` — a script ends with its last block\'s End word, and nothing ' +
            'closes the script itself: delete this line',
            'line 6 of ETwice.psc, `EndEvent`: no Event is open here for `EndEvent` to end — the Event before it was already ended on ' +
            'line 5: delete this line',
            'line 5 of EStray.psc, `EndIf`: no If is open here for `EndIf` to end — no If comes before it: delete this line',
            'line 5 of EBare.psc, `End`: Papyrus has no `End` — a script ends with its last block\'s End word, and nothing closes the ' +
            'script itself: delete this line'
        ]);
    });

    // Eval round 161's activator+detailed #2, #4 and #5, with russo-2025's words.
    it('names None for null, the ScriptName line written with a colon, and a property declared inside an event', () => {
        const { scripts, vanilla } = folders();
        const write = (name: string, text: string) => { fs.writeFileSync(path.join(scripts, `${name}.psc`), text); return path.join(scripts, `${name}.psc`); };
        const nulled = write('NNull', 'ScriptName NNull extends ObjectReference\n\nEvent OnActivate(ObjectReference akActionRef)\n' +
            '    Actor player = Game.GetPlayer()\n    if (player != null)\n        this.Disable()\n    EndIf\nEndEvent\n');
        const colon = write('GoldLeverEffect', 'Scriptname: GoldLeverEffect\nextends Scripter\nclass GoldLeverEffect extends Scripter\n{\n}\n');
        const quest = write('NQuest', 'ScriptName: NQuest extends Quest\n');
        const inside = write('NInside', 'ScriptName NInside extends ObjectReference\n\nEvent OnInit()\n    MiscObject Property Gold001 Auto\nEndEvent\n');
        const unclosed = write('NUnclosed', 'ScriptName NUnclosed extends ObjectReference\n\nEvent OnInit()\n    Debug.Notification("x")\n' +
            'MiscObject Property Gold001 Auto\n');
        expect(papyrusHints([
            `${nulled}:5:19: Checker error: undefined identifier \`null\``,
            `${nulled}:6:9: Checker error: undefined identifier \`this\``,
            `${colon}:1:11: Scanner error: invalid character \`:\``,
            `${quest}:1:11: Scanner error: invalid character \`:\``,
            `${inside}:4:16: Parser error: (block statement) invalid token: property, Property`,
            `${unclosed}:5:12: Parser error: (block statement) invalid token: property, Property`
        ], scripts, vanilla)).toEqual([
            'line 5 of NNull.psc, `if (player != null)`: Papyrus has no null — no value, a record or an object not there, is `None`: ' +
            '`if (player != None)`',
            'line 6 of NNull.psc, `this.Disable()`: Papyrus has no this — the script\'s own object is `Self`: `Self.Disable()`',
            'line 1 of GoldLeverEffect.psc, `Scriptname: GoldLeverEffect`: a script\'s first line names it with no colon — ' +
            '`ScriptName GoldLeverEffect extends ObjectReference`, its name and what it extends on one line (a script on a thing placed in a ' +
            'cell — a lever, a door, a chest — extends ObjectReference, whose event OnActivate is; the game has no type Scripter)',
            'line 1 of NQuest.psc, `ScriptName: NQuest extends Quest`: a script\'s first line names it with no colon — ' +
            '`ScriptName NQuest extends Quest`, its name and what it extends on one line',
            'line 4 of NInside.psc, `MiscObject Property Gold001 Auto`: a property is declared at the script\'s top, outside every Event ' +
            'and Function, and this line is inside the Event on line 3, `Event OnInit()` — move it up, below the ScriptName line',
            'line 5 of NUnclosed.psc, `MiscObject Property Gold001 Auto`: the Event on line 3, `Event OnInit()`, is never closed before ' +
            'it — Papyrus ends an Event with `EndEvent` on a line of its own, and a property is declared outside it'
        ]);
    });

    // Eval round 162's activator #3 and #5, with russo-2025's words.
    it('names a comment in C\'s way, HasItem, AddMessage, and a Quest\'s Stop in an object\'s script', () => {
        const { scripts, vanilla } = folders();
        fs.writeFileSync(path.join(vanilla, 'Debug.psc'), 'Scriptname Debug Hidden\n\nFunction Notification(string asNotificationText) native global\n');
        const write = (name: string, parent: string, body: string) => {
            fs.writeFileSync(path.join(scripts, `${name}.psc`), `ScriptName ${name} extends ${parent}\n\nMiscObject Property Gold001 Auto\n\n` +
                `Event OnActivate(ObjectReference akActionRef)\n${body}EndEvent\n`);
            return path.join(scripts, `${name}.psc`);
        };
        const comment = write('CComment', 'ObjectReference', '    /* Gives the player gold when pulled */\n');
        const has = write('CHas', 'ObjectReference', '    if Game.GetPlayer().HasItem(Gold001)\n    EndIf\n');
        const message = write('CMessage', 'ObjectReference', '    Debug.AddMessage("You got gold")\n');
        const stop = write('CStop', 'ObjectReference', '    Stop()\n');
        const quest = write('CQuest', 'Quest', '    Stop()\n');
        expect(papyrusHints([
            `${comment}:6:5: Parser error: (block statement) invalid token: /,`,
            `${has}:6:25: Checker error: undefined function \`HasItem\``,
            `${message}:6:5: Checker error: undefined identifier \`Debug\``,
            `${message}:6:11: Checker error: undefined function \`AddMessage\``,
            `${stop}:6:5: Checker error: undefined function \`Stop\``,
            `${quest}:6:5: Checker error: undefined function \`Stop\``
        ], scripts, vanilla)).toEqual([
            'line 6 of CComment.psc, `/* Gives the player gold when pulled */`: a Papyrus comment starts with `;`, and one of several ' +
            'lines is written `;/ … /;` — `/* … */` is none',
            'line 6 of CHas.psc, `if Game.GetPlayer().HasItem(Gold001)`: Papyrus has no HasItem — how many of an item one holds is ' +
            'GetItemCount: `if Game.GetPlayer().GetItemCount(Gold001) > 0`',
            'line 6 of CMessage.psc, `Debug.AddMessage("You got gold")`: a message on the screen is `Debug.Notification("…")`, Debug\'s ' +
            'own function, called by its name alone — not on the player',
            'line 6 of CStop.psc, `Stop()`: Stop is a Quest\'s function, which ends a quest — this script extends ObjectReference, and ' +
            'its event ends by itself at its End line: delete this line'
        ]);
    });

    // Eval round 163's activator #3 and message+detailed #5, with russo-2025's words.
    it('names a property line with a word too many, and Object as a property\'s type', () => {
        const { scripts, vanilla } = folders();
        const write = (name: string, text: string) => { fs.writeFileSync(path.join(scripts, `${name}.psc`), text); return path.join(scripts, `${name}.psc`); };
        const crowded = write('LeverScript', 'ScriptName LeverScript extends ObjectReference\n\nMiscObjectProperty GoldItem Property Gold001 Auto\n');
        const vague = write('WelcomeMessageScript', 'ScriptName WelcomeMessageScript extends Quest\n\nObject Property WelcomeMessageBox Auto\n\n' +
            'Event OnInit()\n    WelcomeMessageBox.Show()\nEndEvent\n');
        expect(papyrusHints([
            `${crowded}:3:29: Checker error: undefined type: \`GoldItem\``,
            `${vague}:3:8: Checker error: undefined type: \`Object\``
        ], scripts, vanilla)).toEqual([
            'line 3 of LeverScript.psc, `MiscObjectProperty GoldItem Property Gold001 Auto`: a property\'s line is its type, the word Property ' +
            'and its name, then Auto — `MiscObject Property Gold001 Auto`; GoldItem stands where none of them goes',
            'line 3 of WelcomeMessageScript.psc, `Object Property WelcomeMessageBox Auto`: Papyrus has no type Object — a property is typed as ' +
            'what it holds: `Message Property WelcomeMessageBox Auto` for this one; ' + byUse
        ]);
    });

    // Eval round 163's activator #5 and activator+detailed #1, #3 and #5, with russo-2025's words.
    it('names a type the game has not, a property\'s type after its name, and what a script reads off a reference', () => {
        const { scripts, vanilla } = folders();
        const write = (name: string, body: string) => {
            fs.writeFileSync(path.join(scripts, `${name}.psc`), `ScriptName ${name} extends ObjectReference\n\n${body}`);
            return path.join(scripts, `${name}.psc`);
        };
        const glued = write('TGlued', 'ItemProperty Gold001 Auto\n');
        const item = write('TItem', 'Item Property Gold001 Auto\n');
        const after = write('TAfter', 'Property GoldItem MiscObject Auto\n');
        const reads = write('TReads', 'MiscObject Property Gold001 Auto\n\nEvent OnActivate(ObjectReference akActionRef)\n' +
            '    Game.GetPlayer().AddItem(Gold001, Gold001.Value)\n    if (akActionRef.IsPlayer())\n        akActionRef.Game.GetPlayer()\n' +
            '        Actor player = akActionRef.GetActor()\n    endif\nEndEvent\n');
        expect(papyrusHints([
            `${glued}:3:1: Checker error: undefined type: \`ItemProperty\``,
            `${item}:3:1: Checker error: undefined type: \`Item\``,
            `${after}:3:19: Parser error: unexpected name \`MiscObject\`, expecting \`function\``,
            `${reads}:6:47: Checker error: field or property \`Value\` not found`,
            `${reads}:7:21: Checker error: undefined function \`IsPlayer\``,
            `${reads}:8:25: Checker error: field or property \`Game\` not found`,
            `${reads}:9:36: Checker error: undefined function \`GetActor\``
        ], scripts, vanilla)).toEqual([
            'line 3 of TGlued.psc, `ItemProperty Gold001 Auto`: a property is declared with its type and the word Property apart, and the game ' +
            'has no type Item — a property is typed as what it holds: `MiscObject Property Gold001 Auto` for this one; ' + byUse,
            'line 3 of TItem.psc, `Item Property Gold001 Auto`: Papyrus has no type Item — a property is typed as what it holds: ' +
            '`MiscObject Property Gold001 Auto` for this one; ' + byUse,
            'line 3 of TAfter.psc, `Property GoldItem MiscObject Auto`: a property\'s line is its type first, then the word Property and its ' +
            'name — `MiscObject Property GoldItem Auto`',
            'line 6 of TReads.psc, `Game.GetPlayer().AddItem(Gold001, Gold001.Value)`: Papyrus reads no Value of Gold001 — an amount of the ' +
            'script\'s own is a number, `Game.GetPlayer().AddItem(Gold001, 100)` for 100, or an Int property of its own; what an item is ' +
            'worth is `Gold001.GetGoldValue()`',
            'line 7 of TReads.psc, `if (akActionRef.IsPlayer())`: Papyrus has no IsPlayer — whether the player did it is the reference ' +
            'compared with the player: `if (akActionRef == Game.GetPlayer())`',
            'line 8 of TReads.psc, `akActionRef.Game.GetPlayer()`: Game is a script of its own, its functions called by its name alone — ' +
            '`Game.GetPlayer()`, not on akActionRef',
            'line 9 of TReads.psc, `Actor player = akActionRef.GetActor()`: Papyrus has no GetActor on akActionRef — akActionRef is the one ' +
            'who activated it, an ObjectReference; as an Actor it is `akActionRef as Actor`, as in `Actor player = akActionRef as Actor`, and ' +
            'the player is `Game.GetPlayer()`'
        ]);
    });

    // Eval round 166's message #4: `WelcomeMessage.Show()` on a property declared an ObjectReference.
    it('names the type whose function a call on another type named', () => {
        const { scripts, vanilla } = folders();
        fs.writeFileSync(path.join(vanilla, 'ObjectReference.psc'), 'Scriptname ObjectReference extends Form Hidden\n\nFunction Disable() native\n');
        const file = path.join(scripts, 'WelcomeScript.psc');
        fs.writeFileSync(file, 'ScriptName WelcomeScript extends Quest\n\nObjectReference Property WelcomeMessage Auto\n\nEvent OnInit()\n    WelcomeMessage.Show()\nEndEvent\n');
        expect(papyrusHints([`${file}:6:20: Checker error: undefined function \`Show\``], scripts, vanilla)).toEqual([
            'Show is a Message\'s function, and WelcomeMessage is declared an ObjectReference: where it holds a Message, declare it so — ' +
            '`Message Property WelcomeMessage Auto`'
        ]);
    });

    // Eval round 167's activator #3 and activator+detailed #2, with russo-2025's words.
    it('names Game as a property\'s type, and a FormID in hex', () => {
        const { scripts, vanilla } = folders();
        const write = (name: string, body: string) => {
            fs.writeFileSync(path.join(scripts, `${name}.psc`), `ScriptName ${name} extends ObjectReference\n\n${body}`);
            return path.join(scripts, `${name}.psc`);
        };
        const glued = write('GGlued', 'GameProperty GoldReward Auto\n');
        const typed = write('GTyped', 'Game Property GoldReward Auto\n\nEvent OnActivate(ObjectReference akActionRef)\n    Game.GetPlayer().AddItem(GoldReward, 100)\nEndEvent\n');
        const hex = write('GHex', 'Event OnActivate(ObjectReference akActor)\n    akActor.AddItem(0x20001, 100)\nEndEvent\n');
        expect(papyrusHints([
            `${glued}:3:14: Checker error: undefined type: \`GameProperty\``,
            `${typed}:6:26: Checker error: cannot convert type \`Game\` to type \`Form\``,
            `${hex}:4:13: Checker error: cannot convert type \`Int\` to type \`Form\``
        ], scripts, vanilla)).toEqual([
            'line 3 of GGlued.psc, `GameProperty GoldReward Auto`: a property is declared with its type and the word Property apart, and Game ' +
            'is no type a property holds — a script of functions, called by its name — but a property is typed as what it holds: ' +
            '`MiscObject Property GoldReward Auto` for this one; ' + byUse,
            'line 6 of GTyped.psc, `Game.GetPlayer().AddItem(GoldReward, 100)`: GoldReward is declared a Game, and Game is no type a property ' +
            'holds — a script of functions, called by its name — but a property is typed as what it holds: `MiscObject Property GoldReward ' +
            'Auto` for this one; ' + byUse,
            expect.stringContaining('line 4 of GHex.psc, `akActor.AddItem(0x20001, 100)`: a script names no record by its FormID')
        ]);
    });

    // Eval round 168's activator #2, with russo-2025's words.
    it('names a property whose name is written where its type goes', () => {
        const { scripts, vanilla } = folders();
        const file = path.join(scripts, 'GoldLeverScript.psc');
        fs.writeFileSync(file, 'ScriptName GoldLeverScript extends ObjectReference\n\nItemToGive Property Gold001 Auto\n\nEvent OnActivate(ObjectReference akActionRef)\n' +
            '    if (Game.GetPlayer())\n        Game.GetPlayer().AddItem(ItemToGive, 100)\n    EndIf\nEndEvent\n');
        expect(papyrusHints([
            `${file}:3:12: Checker error: undefined type: \`ItemToGive\``,
            `${file}:7:34: Checker error: undefined identifier \`ItemToGive\``
        ], scripts, vanilla)).toEqual([
            'line 3 of GoldLeverScript.psc, `ItemToGive Property Gold001 Auto`: ItemToGive is the name the script uses, and a property\'s line is ' +
            'its type, the word Property, then that name — `MiscObject Property ItemToGive Auto`; Gold001, the record it holds, is given to it ' +
            'by the record the script is attached to, in its VirtualMachineAdapter\'s Properties: `Name: ItemToGive`, `Object: Gold001`'
        ]);
    });

    // Eval round 170's message+detailed #4 and activator+detailed #2, with russo-2025's words.
    it('names a Mutagen kind and Player written as a property\'s type', () => {
        const { scripts, vanilla } = folders();
        const write = (name: string, body: string) => {
            fs.writeFileSync(path.join(scripts, `${name}.psc`), `ScriptName ${name} extends Quest\n\n${body}`);
            return path.join(scripts, `${name}.psc`);
        };
        const glued = write('KGlued', 'ScriptObjectProperty WelcomeMessage Auto\n\nEvent OnInit()\n    WelcomeMessage.Show()\nEndEvent\n');
        const bare = write('KBare', 'ScriptObject Property WelcomeMessage Auto\n\nEvent OnInit()\n    WelcomeMessage.Show()\nEndEvent\n');
        const player = write('KPlayer', 'PlayerProperty PlayerRef Auto\n');
        expect(papyrusHints([
            `${glued}:3:22: Checker error: undefined type: \`ScriptObjectProperty\``,
            `${bare}:3:14: Checker error: undefined type: \`ScriptObject\``,
            `${player}:3:16: Checker error: undefined type: \`PlayerProperty\``
        ], scripts, vanilla)).toEqual([
            'line 3 of KGlued.psc, `ScriptObjectProperty WelcomeMessage Auto`: ScriptObjectProperty is the kind the record\'s VirtualMachineAdapter ' +
            'names the property by, no Papyrus type — in the script, a property is typed as what it holds: `Message Property WelcomeMessage Auto` ' +
            'for this one; ' + byUse,
            'line 3 of KBare.psc, `ScriptObject Property WelcomeMessage Auto`: ScriptObject is no Papyrus type — ScriptObjectProperty is the kind ' +
            'the record\'s VirtualMachineAdapter names a property by; in the script, a property is typed as what it holds: `Message Property ' +
            'WelcomeMessage Auto` for this one; ' + byUse,
            'line 3 of KPlayer.psc, `PlayerProperty PlayerRef Auto`: Papyrus has no type Player — the player is an Actor, `Game.GetPlayer()`, and ' +
            'a property for the player is `Actor Property PlayerRef Auto`, filled with the game\'s PlayerRef'
        ]);
    });

    it('adds nothing for an error the sources do not explain', () => {
        const { scripts, vanilla } = folders();
        expect(papyrusHints(['C:\\p\\scripts\\X.psc(1,0): mismatched input', 'Compilation failed'], scripts, vanilla)).toEqual([]);
        expect(declarationIn('Scriptname Quest extends Form', 'OnStoryChangeLocation')).toBeNull();
    });
});
