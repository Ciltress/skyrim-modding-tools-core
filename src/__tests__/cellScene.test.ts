import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
    BLENDER_MCP_NO_STDIN, BLENDER_SCRIPT, blenderMcpLaunch, cellFolderOf, copyEditorId, findBlender, initiallyDisabled, launcher, layoutChanges, newReferenceText, normalRotation,
    saveBlendCode,
    recordFileOf, rotationMatrix, sameRotation, texturesOf, triple, type CellSceneFile, type LayoutFile
} from '../backend/skyrim/cellScene.js';

const chest = {
    formId: '10EB84:Skyrim.esm', editorId: null, kind: 'PlacedObject',
    base: { formId: '0F3923:Skyrim.esm', editorId: 'PlayerHouseChest', type: 'Container', name: 'Chest' },
    model: 'meshes\\clutter\\upperclass\\upperchest01.nif', position: [-766.1362, 539.9182, 186.6452] as [number, number, number],
    rotation: [0, 0, 3.2432] as [number, number, number], scale: 0.65, persistent: true, own: false, disabled: false
};
const barrel = { ...chest, formId: '03EE74:Skyrim.esm', base: { formId: '10C0E3:Skyrim.esm', editorId: 'Barrel02Static' },
    position: [-581.483, 219.6936, 63] as [number, number, number], rotation: [0, 0, 4.712389] as [number, number, number], scale: 1, persistent: false };
const scene: CellSceneFile = { cell: { formId: '0165A8:Skyrim.esm', editorId: 'WhiterunBreezehome', interior: true }, references: [chest, barrel] };

describe('a cell in Blender and back', () => {
    it('reads the textures a mesh names, with their top folder, each once', () => {
        const nif = Buffer.concat([Buffer.from([0x22, 0, 0, 0]), Buffer.from('textures\\clutter\\Barrel01.dds', 'latin1'), Buffer.from([0x1c, 0, 0, 0]),
            Buffer.from('Data\\Textures\\clutter\\barrel01_n.dds', 'latin1'), Buffer.from([0, 0x10]), Buffer.from('textures/clutter/barrel01.dds', 'latin1')]);
        expect(texturesOf(nif)).toEqual(['textures\\clutter\\Barrel01.dds', 'textures\\clutter\\barrel01_n.dds']);
    });

    it('builds the game\'s rotation matrix: a positive z turns clockwise seen from above, and turns map to the same rotation', () => {
        const quarter = rotationMatrix([0, 0, Math.PI / 2]);
        // The object's front, +y, turns to +x: east of north, clockwise.
        const front = [0, 1, 0].map((_, i) => quarter[i][1]);
        expect(front[0]).toBeCloseTo(1);
        expect(front[1]).toBeCloseTo(0);
        expect(sameRotation([0, 0, 5.2582], [0, 0, 5.2582 - 2 * Math.PI])).toBe(true);
        expect(sameRotation([0.1, 0.2, 0.3], [0.1, 0.2, 0.31])).toBe(false);
        expect(normalRotation([-0.5, 2 * Math.PI, 7])).toEqual([2 * Math.PI - 0.5, 0, 7 - 2 * Math.PI]);
    });

    it('tells moved, copied and deleted references apart from those left where they were', () => {
        const layout: LayoutFile = {
            cell: '0165A8:Skyrim.esm',
            references: [
                { formId: chest.formId, base: chest.base.formId, name: 'PlayerHouseChest [10EB84]', position: [-716.1362, 539.9182, 186.6452],
                    rotation: [0, 0, 2.7432], scale: 0.65 },
                { formId: null, copyOf: chest.formId, base: chest.base.formId, baseEditorId: 'PlayerHouseChest', kind: 'PlacedObject',
                    name: 'PlayerHouseChest [10EB84].001', position: [-716.1362, 619.9182, 186.6452], rotation: [0, 0, 2.7432], scale: 0.65 }
            ]
        };
        const changes = layoutChanges(scene, layout);
        expect(changes.moved.map(m => m.reference.formId)).toEqual([chest.formId]);
        expect(changes.copies.map(c => c.name)).toEqual(['PlayerHouseChest [10EB84].001']);
        expect(changes.removed.map(r => r.formId)).toEqual([barrel.formId]);

        // Blender's export rounds, and writes an angle a turn apart: the same placement, unchanged.
        const still = layoutChanges(scene, { cell: scene.cell.formId, references: [
            { formId: chest.formId, base: '', name: 'a', position: [-766.13, 539.92, 186.65], rotation: [0, 0, 3.2432 - 2 * Math.PI], scale: 0.6501 },
            { formId: barrel.formId, base: '', name: 'b', position: barrel.position, rotation: barrel.rotation, scale: 1 }] });
        expect(still).toMatchObject({ moved: [], copies: [], removed: [], unchanged: 2 });
    });

    it('writes a copy made in Blender as a new reference, and a deleted one of another plugin as initially disabled', () => {
        expect(newReferenceText({ kind: 'PlacedObject', editorId: 'CampChest1', base: '0F3923:Skyrim.esm', position: [-716.13621, 619.9, -0],
            rotation: [0, 0, -0.5], scale: 0.65 })).toBe(
            'MutagenObjectType: PlacedObject\nEditorID: CampChest1\nBase: 0F3923:Skyrim.esm\nScale: 0.65\nPlacement:\n' +
            `  Position: -716.1362, 619.9, 0\n  Rotation: 0, 0, ${triple([2 * Math.PI - 0.5])}\n`);
        expect(initiallyDisabled('MutagenObjectType: PlacedObject\nFormKey: 03EE74:Skyrim.esm\nBase: 10C0E3:Skyrim.esm\n')).toBe(
            'MutagenObjectType: PlacedObject\nFormKey: 03EE74:Skyrim.esm\nSkyrimMajorRecordFlags:\n- InitiallyDisabled\nBase: 10C0E3:Skyrim.esm\n');
        expect(initiallyDisabled('MajorRecordFlagsRaw: 1024\nSkyrimMajorRecordFlags:\n- 0x400\nBase: x\n')).toBe(
            'MajorRecordFlagsRaw: 3072\nSkyrimMajorRecordFlags:\n- InitiallyDisabled\n- 0x400\nBase: x\n');
        const twice = initiallyDisabled(initiallyDisabled('SkyrimMajorRecordFlags:\n- 0x400\nBase: x\n'));
        expect(twice.match(/InitiallyDisabled/g)).toHaveLength(1);
        const taken = new Set(['campbarrel02static1']);
        expect(copyEditorId('Camp', 'Barrel02Static', taken)).toBe('CampBarrel02Static2');
    });

    it('finds a reference\'s file and the cell\'s copy in a project, indoors and outdoors', () => {
        const plugin = fs.mkdtempSync(path.join(os.tmpdir(), 'cell-scene-'));
        try {
            const outdoors = path.join(plugin, 'Worldspaces', 'Tamriel - 00003C_Skyrim.esm', '0, -1', '0, -2', 'Riverwood - 009732_Skyrim.esm');
            fs.mkdirSync(path.join(outdoors, 'Temporary'), { recursive: true });
            fs.writeFileSync(path.join(outdoors, 'RecordData.yaml'), 'FormKey: 009732:Skyrim.esm\n');
            fs.writeFileSync(path.join(outdoors, 'Temporary', '1054A1_Skyrim.esm.yaml'), 'FormKey: 1054A1:Skyrim.esm\n');
            fs.writeFileSync(path.join(outdoors, 'Temporary', 'CampChest.yaml'), 'MutagenObjectType: PlacedObject\nFormKey: 000800:Camp.esp\n');
            expect(cellFolderOf(plugin, '009732:Skyrim.esm')).toBe(outdoors);
            expect(recordFileOf(plugin, '1054A1:Skyrim.esm')).toBe(path.join(outdoors, 'Temporary', '1054A1_Skyrim.esm.yaml'));
            expect(recordFileOf(plugin, '000800:Camp.esp')).toBe(path.join(outdoors, 'Temporary', 'CampChest.yaml'));
            expect(cellFolderOf(plugin, '0165A8:Skyrim.esm')).toBeUndefined();
        } finally {
            fs.rmSync(plugin, { recursive: true, force: true });
        }
    });

    it('finds Blender on PATH first, then where its installer or Steam put it, the newest version first', () => {
        const have = new Set(['C:\\Tools\\blender.exe', 'D:\\SteamLibrary\\steamapps\\common\\Blender\\blender.exe']);
        expect(findBlender(true, 'C:\\Windows;C:\\Tools', 'C:\\Users\\me', file => have.has(file))).toBe('C:\\Tools\\blender.exe');
        expect(findBlender(true, 'C:\\Windows', 'C:\\Users\\me', file => have.has(file))).toBe('D:\\SteamLibrary\\steamapps\\common\\Blender\\blender.exe');
        expect(findBlender(false, '/usr/sbin', '/home/me', file => file === '/home/me/.local/share/flatpak/exports/bin/org.blender.Blender'))
            .toBe('/home/me/.local/share/flatpak/exports/bin/org.blender.Blender');
        expect(findBlender(false, '', '/home/me', () => false)).toBeUndefined();
        const code = saveBlendCode('C:\\Mods\\Camp\\blender\\Riverwood');
        expect(code).toContain('ns["import_cell"](folder)');
        expect(code).toContain('save_as_mainfile(filepath=os.path.join(folder, "cell.blend")');
    });

    it('ships the Blender script, and launches it from the folder it sits in', () => {
        const script = fs.readFileSync(BLENDER_SCRIPT, 'utf8');
        expect(script).toContain('def import_cell(folder');
        expect(script).toContain('def export_layout(folder');
        expect(script).toContain('Euler((-x, -y, -z), "ZYX")');
        const text = launcher('C:\\Mods\\Camp\\blender\\Riverwood', 'export_layout');
        expect(text).toContain('folder = "C:\\\\Mods\\\\Camp\\\\blender\\\\Riverwood"');
        expect(text).toContain('namespace["export_layout"](folder)');
    });
});

describe('blenderMcpLaunch', () => {
    const appData = 'C:\\Users\\me\\AppData\\Roaming';
    const python = 'C:\\Users\\me\\AppData\\Roaming\\uv\\tools\\blender-mcp\\Scripts\\python.exe';
    const script = 'C:\\Users\\me\\.openclaw\\blender-mcp-no-stdin.py';
    const has = (file: string) => file === python;

    it('starts blender-mcp on Windows through its own Python and the script, its arguments after it', () => {
        expect(blenderMcpLaunch({ command: 'C:\\Users\\me\\.local\\bin\\blender-mcp.exe', args: ['--verbose'] }, script, { windows: true, appData, exists: has }))
            .toEqual({ command: python, args: [script, '--verbose'] });
        expect(blenderMcpLaunch({ command: 'blender-mcp' }, script, { windows: true, appData, exists: has }))
            .toEqual({ command: python, args: [script] });
    });

    it('changes nothing elsewhere: not Windows, another server, started so already, no Python of uv', () => {
        expect(blenderMcpLaunch({ command: 'blender-mcp' }, script, { windows: false, appData, exists: has })).toBeUndefined();
        expect(blenderMcpLaunch({ command: 'npx', args: ['blender-mcp'] }, script, { windows: true, appData, exists: has })).toBeUndefined();
        expect(blenderMcpLaunch({ command: python, args: [script] }, script, { windows: true, appData, exists: has })).toBeUndefined();
        expect(blenderMcpLaunch({ command: 'blender-mcp.exe' }, script, { windows: true, appData, exists: () => false })).toBeUndefined();
    });

    it('takes the Python beside a virtual environment\'s launcher, and leaves a blmcp alone that gives Blender no stdin itself', () => {
        const venv = 'C:\\Users\\me\\.local\\share\\blender-mcp-pr48';
        const launcher = `${venv}\\Scripts\\blender-mcp.exe`;
        const own = `${venv}\\Scripts\\python.exe`;
        const cli = path.win32.join(venv, 'Lib', 'site-packages', 'blmcp', 'tools_helpers', 'blender_cli.py');
        const there = (file: string) => file === own || file === python;
        const unfixed = 'proc = subprocess.run(\n    [blender, "--background"],\n    capture_output=True,\n)';
        const fixed = 'proc = subprocess.run(\n    [blender, "--background"],\n    stdin=subprocess.DEVNULL,\n    capture_output=True,\n)';
        expect(blenderMcpLaunch({ command: launcher }, script, { windows: true, appData, exists: there, read: file => (file === cli ? unfixed : undefined) }))
            .toEqual({ command: own, args: [script] });
        expect(blenderMcpLaunch({ command: launcher }, script, { windows: true, appData, exists: there, read: file => (file === cli ? fixed : undefined) }))
            .toBeUndefined();
    });

    it('ships the script beside the cell bridge', () => {
        expect(fs.existsSync(BLENDER_MCP_NO_STDIN)).toBe(true);
        expect(path.dirname(BLENDER_MCP_NO_STDIN)).toBe(path.dirname(BLENDER_SCRIPT));
        expect(fs.readFileSync(BLENDER_MCP_NO_STDIN, 'utf8')).toContain('subprocess.DEVNULL');
    });
});
