import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { holdsFiles, listDownloads } from '../backend/skyrim/downloads';
import { checkPlugins, readPluginHeader, type PluginHeaderInfo } from '../backend/skyrim/pluginCheck';
import { lz4Block, readSave } from '../backend/skyrim/saves';

const roots: string[] = [];
afterAll(() => {
    for (const root of roots) fs.rmSync(root, { recursive: true, force: true });
});

function tempDir(prefix: string): string {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
    roots.push(root);
    return root;
}

function write(file: string, data: string | Buffer = '') {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, data);
}

/** A subrecord: its type, its size in two bytes, its data. */
function subrecord(type: string, data: Buffer): Buffer {
    const head = Buffer.alloc(6);
    head.write(type, 0, 'latin1');
    head.writeUInt16LE(data.length, 4);
    return Buffer.concat([head, data]);
}

/** A plugin of a TES4 header alone: its flags and masters, as the Creation Kit writes them. */
function plugin(flags: number, masters: string[]): Buffer {
    const hedr = Buffer.alloc(12);
    hedr.writeFloatLE(1.71, 0);
    const data = Buffer.concat([
        subrecord('HEDR', hedr),
        subrecord('CNAM', Buffer.from('DEFAULT\0', 'latin1')),
        ...masters.flatMap(master => [subrecord('MAST', Buffer.from(`${master}\0`, 'latin1')), subrecord('DATA', Buffer.alloc(8))])
    ]);
    const head = Buffer.alloc(24);
    head.write('TES4', 0, 'latin1');
    head.writeUInt32LE(data.length, 4);
    head.writeUInt32LE(flags, 8);
    return Buffer.concat([head, data]);
}

describe('a plugin\'s header', () => {
    it('names its masters and its master and light flags, the extension counting as the flag', () => {
        const root = tempDir('skyrim-header-');
        write(path.join(root, 'Patch.esp'), plugin(0x200, ['Skyrim.esm', 'Base.esm']));
        write(path.join(root, 'Base.esm'), plugin(0, ['Skyrim.esm']));
        write(path.join(root, 'Small.esl'), plugin(0, []));
        write(path.join(root, 'Broken.esp'), 'not a plugin');
        expect(readPluginHeader(path.join(root, 'Patch.esp'))).toEqual({ masters: ['Skyrim.esm', 'Base.esm'], master: false, light: true });
        expect(readPluginHeader(path.join(root, 'Base.esm'))).toEqual({ masters: ['Skyrim.esm'], master: true, light: false });
        expect(readPluginHeader(path.join(root, 'Small.esl'))).toEqual({ masters: [], master: true, light: true });
        expect(() => readPluginHeader(path.join(root, 'Broken.esp'))).toThrow(/not a plugin/);
    });
});

describe('the load order checked against the masters', () => {
    const headers: Record<string, PluginHeaderInfo> = {
        'skyrim.esm': { masters: [], master: true, light: false },
        'patch.esp': { masters: ['Skyrim.esm', 'Gone.esm', 'Off.esm', 'Late.esp'], master: false, light: true },
        'late.esp': { masters: ['Skyrim.esm'], master: false, light: false },
        'needsdisabled.esp': { masters: ['InDisabled.esm'], master: false, light: false }
    };
    const order = [
        { name: 'Skyrim.esm', active: true }, { name: 'Off.esm', active: false }, { name: 'Patch.esp', active: true },
        { name: 'Late.esp', active: true }, { name: 'NeedsDisabled.esp', active: true }, { name: 'NoFile.esp', active: true }
    ];
    const files = new Map(['skyrim.esm', 'off.esm', 'patch.esp', 'late.esp', 'needsdisabled.esp'].map(name => [name, `mods/${name === 'off.esm' ? 'Off Mod' : 'Main'}/${name}`]));

    it('names each master missing, not enabled or loading later, and an enabled plugin with no file', () => {
        const check = checkPlugins({
            order, files, inDisabledMods: new Map([['indisabled.esm', 'Disabled Mod']]),
            ownerOf: file => file.split('/')[1], readHeader: file => headers[path.basename(file)]
        });
        expect(check).toMatchObject({ active: 5, full: 3, light: 1, limits: [] });
        expect(check.problems).toEqual([
            { kind: 'missingMaster', plugin: 'Patch.esp', mod: 'Main', master: 'Gone.esm' },
            { kind: 'masterNotEnabled', plugin: 'Patch.esp', mod: 'Main', master: 'Off.esm', masterMod: 'Off Mod' },
            { kind: 'masterLoadsLater', plugin: 'Patch.esp', mod: 'Main', master: 'Late.esp' },
            { kind: 'missingMaster', plugin: 'NeedsDisabled.esp', mod: 'Main', master: 'InDisabled.esm', masterMod: 'Disabled Mod' },
            { kind: 'noFile', plugin: 'NoFile.esp' }
        ]);
    });

    it('says the limit of full plugins gone past', () => {
        const many = Array.from({ length: 256 }, (_, i) => ({ name: `P${i}.esp`, active: true }));
        const check = checkPlugins({
            order: many, files: new Map(many.map(p => [p.name.toLowerCase(), p.name])), ownerOf: () => 'Mod',
            readHeader: () => ({ masters: [], master: false, light: false })
        });
        expect(check.full).toBe(256);
        expect(check.limits[0]).toMatch(/256 plugins take a slot of their own, and the game loads 254/);
    });
});

describe('the downloads checked against the mods folder', () => {
    it('calls an archive installed only where a mod named it holds files, whatever Mod Organizer marked', () => {
        const root = tempDir('skyrim-downloads-');
        const downloads = path.join(root, 'downloads');
        const mods = path.join(root, 'mods');
        const archive = (name: string, meta: string, when: number) => {
            write(path.join(downloads, name), 'x');
            write(path.join(downloads, `${name}.meta`), `[General]\r\n${meta}`);
            fs.utimesSync(path.join(downloads, name), when, when);
        };
        archive('Good-1-1-0-1700000000.7z', 'modID=1\r\nfileID=11\r\ninstalled=true\r\n', 3000);
        archive('Lost-2-1-0-1700000001.zip', 'modID=2\r\ninstalled=true\r\n', 2000);
        archive('Empty-3-1-0-1700000002.rar', 'modID=3\r\ninstalled=true\r\nremoved=true\r\n', 1000);
        archive('Renamed.7z', 'modID=4\r\nfileID=44\r\n', 500);
        write(path.join(downloads, 'Half.7z.unfinished'), 'x');
        fs.utimesSync(path.join(downloads, 'Half.7z.unfinished'), 100, 100);
        write(path.join(downloads, 'readme.txt'), 'x');
        write(path.join(mods, 'Good', 'meta.ini'), '[General]\r\ninstallationFile=Good-1-1-0-1700000000.7z\r\n');
        write(path.join(mods, 'Good', 'textures', 'a.dds'), 'x');
        write(path.join(mods, 'Empty', 'meta.ini'), '[General]\r\ninstallationFile=C:\\Downloads\\Empty-3-1-0-1700000002.rar\r\n');
        fs.mkdirSync(path.join(mods, 'Empty', 'meshes'));
        write(path.join(mods, 'Other Name', 'meta.ini'), '[General]\r\ninstallationFile=Renamed-old.7z\r\n[installedFiles]\r\n1\\modid=4\r\n1\\fileid=44\r\nsize=1\r\n');
        write(path.join(mods, 'Other Name', 'a.esp'), 'x');

        const list = listDownloads(downloads, mods, new Map([['Good', true], ['Empty', false]]), { limit: 25 });
        expect(list.counts).toEqual({ installed: 2, installedEmpty: 1, notInstalled: 1, downloading: 1 });
        const byFile = Object.fromEntries(list.downloads.map(row => [row.file, row]));
        expect(byFile['Good-1-1-0-1700000000.7z']).toMatchObject({ state: 'installed', mods: [{ name: 'Good', enabled: true, files: true }], nexus: { modId: 1, fileId: 11 } });
        expect(byFile['Good-1-1-0-1700000000.7z'].markedInstalled).toBeUndefined();
        expect(byFile['Lost-2-1-0-1700000001.zip']).toMatchObject({ state: 'notInstalled', markedInstalled: true, nexus: { modId: 2 } });
        expect(byFile['Empty-3-1-0-1700000002.rar']).toMatchObject({ state: 'installedEmpty', markedInstalled: true, hidden: true });
        expect(byFile['Renamed.7z']).toMatchObject({ state: 'installed', mods: [{ name: 'Other Name' }] });
        expect(byFile['Half.7z.unfinished']).toMatchObject({ state: 'downloading' });
        // Newest first; narrowed by state, the counts stay the folder's.
        expect(list.downloads[0].file).toBe('Good-1-1-0-1700000000.7z');
        const lost = listDownloads(downloads, mods, new Map(), { state: 'notInstalled', limit: 25 });
        expect(lost.downloads.map(row => row.file)).toEqual(['Lost-2-1-0-1700000001.zip']);
        expect(lost.counts.installed).toBe(2);
    });

    it('counts a folder of empty folders and a meta.ini as holding nothing', () => {
        const root = tempDir('skyrim-holds-');
        write(path.join(root, 'meta.ini'));
        fs.mkdirSync(path.join(root, 'a', 'b'), { recursive: true });
        expect(holdsFiles(root)).toBe(false);
        write(path.join(root, 'a', 'b', 'c.nif'));
        expect(holdsFiles(root)).toBe(true);
        expect(holdsFiles(path.join(root, 'gone'))).toBe(false);
    });

    it('says how to set a downloads folder it cannot read', () => {
        expect(() => listDownloads(path.join(os.tmpdir(), 'no-such-downloads-folder'), 'mods', new Map(), { limit: 5 }))
            .toThrow(/download_directory under \[Settings\]/);
    });
});

/** An LZ4 block of literals alone: what any LZ4 reader takes. */
function lz4Literals(data: Buffer): Buffer {
    const length = data.length;
    const extra: number[] = [];
    if (length >= 15) {
        let rest = length - 15;
        while (rest >= 255) { extra.push(255); rest -= 255; }
        extra.push(rest);
    }
    return Buffer.concat([Buffer.from([Math.min(length, 15) << 4, ...extra]), data]);
}

const wstring = (text: string) => {
    const length = Buffer.alloc(2);
    length.writeUInt16LE(text.length, 0);
    return Buffer.concat([length, Buffer.from(text, 'latin1')]);
};
const u32 = (n: number) => { const b = Buffer.alloc(4); b.writeUInt32LE(n, 0); return b; };
const u16 = (n: number) => { const b = Buffer.alloc(2); b.writeUInt16LE(n, 0); return b; };

/** A Skyrim Special Edition save: its header, a 2×1 screenshot, and its body LZ4-packed. */
function save(plugins: string[], light: string[]): Buffer {
    const header = Buffer.concat([
        u32(12), u32(7), wstring('Hero'), u32(23), wstring('Whiterun'), wstring('4E 201'), wstring('NordRace'), u16(0),
        Buffer.alloc(4), Buffer.alloc(4), Buffer.alloc(8), u32(2), u32(1), u16(2)
    ]);
    const list = Buffer.concat([Buffer.from([plugins.length]), ...plugins.map(wstring)]);
    const body = Buffer.concat([Buffer.from([78]), u32(list.length), list, u16(light.length), ...light.map(wstring), Buffer.alloc(64, 7)]);
    const packed = lz4Literals(body);
    return Buffer.concat([Buffer.from('TESV_SAVEGAME', 'latin1'), u32(header.length), header, Buffer.alloc(2 * 1 * 4), u32(body.length), u32(packed.length), packed]);
}

describe('a save', () => {
    it('gives its character and the plugins it was made with, full and light', () => {
        const root = tempDir('skyrim-save-');
        write(path.join(root, 'Save7.ess'), save(['Skyrim.esm', 'Update.esm', 'Removed Mod With A Long Name.esp'], ['Small.esl']));
        expect(readSave(path.join(root, 'Save7.ess'))).toEqual({
            character: 'Hero', level: 23, location: 'Whiterun', gameDate: '4E 201', race: 'NordRace',
            plugins: ['Skyrim.esm', 'Update.esm', 'Removed Mod With A Long Name.esp'], light: ['Small.esl']
        });
    });

    it('refuses a file that is no save', () => {
        const root = tempDir('skyrim-save-');
        write(path.join(root, 'Bad.ess'), 'TESV_SAVEGAMX and more');
        expect(() => readSave(path.join(root, 'Bad.ess'))).toThrow(/not a Skyrim save/);
    });

    it('unpacks an LZ4 match that overlaps what it copies, and stops where it is told', () => {
        // "ab", then a match of offset 2 and length 6: "abababab"; then the literals "cd".
        const block = Buffer.from([0x22, 0x61, 0x62, 0x02, 0x00, 0x20, 0x63, 0x64]);
        expect(lz4Block(block, 10).toString('latin1')).toBe('ababababcd');
        expect(lz4Block(block, 10, 2).toString('latin1')).toBe('ab');
    });
});
