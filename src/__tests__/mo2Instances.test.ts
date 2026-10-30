import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { checkMo2Instance } from '../backend/index';
import { mo2InstancesDir, skyrimInstances } from '../backend/skyrim/mo2Instances';

/** Finding the user's Mod Organizer instance, as the products' setups do. */

const roots: string[] = [];
afterEach(() => { for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true }); });

function tempDir(): string {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'skyrim-instances-'));
    roots.push(root);
    return root;
}

function write(file: string, text = ''): void {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text);
}

/** Mod Organizer writes a path in its ini with its backslashes doubled. */
const qt = (value: string) => value.replace(/\\/g, '\\\\');

describe('Mod Organizer\'s instances', () => {
    it('offers the global instances that manage Skyrim Special Edition, and no others', () => {
        const root = tempDir();
        write(path.join(root, 'Skyrim SE', 'ModOrganizer.ini'), '[General]\ngameName=Skyrim Special Edition\n');
        write(path.join(root, 'Fallout', 'ModOrganizer.ini'), '[General]\ngameName=Fallout 4\n');
        write(path.join(root, 'Broken', 'notes.txt'), 'no ini');
        write(path.join(root, 'LoreRim', 'ModOrganizer.ini'), '[General]\r\ngameName=Skyrim Special Edition\r\n');
        expect(skyrimInstances(root)).toEqual([path.join(root, 'LoreRim'), path.join(root, 'Skyrim SE')]);
        expect(skyrimInstances(path.join(root, 'nowhere'))).toEqual([]);
    });

    it('looks for them in the local app data folder on Windows only', () => {
        expect(mo2InstancesDir({ LOCALAPPDATA: 'C:\\Users\\me\\AppData\\Local' }, 'win32')).toBe(path.join('C:\\Users\\me\\AppData\\Local', 'ModOrganizer'));
        expect(mo2InstancesDir({ LOCALAPPDATA: '/somewhere' }, 'linux')).toBeUndefined();
        expect(mo2InstancesDir({}, 'win32')).toBeUndefined();
    });
});

describe('checkMo2Instance', () => {
    it('takes a pasted folder in quotes, read as the tools read it', () => {
        const root = tempDir();
        const game = path.join(root, 'game');
        const instance = path.join(root, 'mo2');
        write(path.join(game, 'SkyrimSE.exe'));
        write(path.join(instance, 'ModOrganizer.ini'),
            ['[General]', 'gameName=Skyrim Special Edition', `gamePath=@ByteArray(${qt(game)})`, 'selected_profile=@ByteArray(Default)', ''].join('\r\n'));
        write(path.join(instance, 'profiles', 'Default', 'modlist.txt'));
        expect(checkMo2Instance(`"${instance}"`, {}, { root })).toEqual({ instance });
    });

    it('says why a folder cannot be used', () => {
        const root = tempDir();
        expect(checkMo2Instance('  ', {}, { root })).toEqual({ error: 'No folder given.' });
        const result = checkMo2Instance(path.join(root, 'nothing here'), {}, { root });
        expect('error' in result && result.error).toMatch(/ModOrganizer\.ini/);
    });
});
