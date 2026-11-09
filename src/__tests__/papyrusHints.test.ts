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

    it('adds nothing for an error the sources do not explain', () => {
        const { scripts, vanilla } = folders();
        expect(papyrusHints(['C:\\p\\scripts\\X.psc(1,0): mismatched input', 'Compilation failed'], scripts, vanilla)).toEqual([]);
        expect(declarationIn('Scriptname Quest extends Form', 'OnStoryChangeLocation')).toBeNull();
    });
});
