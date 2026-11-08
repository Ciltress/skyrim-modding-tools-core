/**
 * A cell in Blender and back (exportCell, importCell). The helper reads the cell's references (CellScene.cs); this module
 * holds what needs no game: the textures a mesh names, the scene's file, and what a layout written in Blender changed.
 *
 * Rotations are the game's: radians about x, y and z, the reference turned by z, then y, then x, each angle clockwise seen
 * down its axis — NiMatrix3::SetEulerAnglesXYZ, the matrix Rx(-x)·Ry(-y)·Rz(-z). Blender's script turns them into its own
 * (blender/skyrim_cell.py); two triples are the same rotation when their matrices are.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** One reference as the helper's cellScene reads it, and as cell.json holds it. */
export interface SceneReference {
    formId: string;
    editorId?: string | null;
    kind: string;
    base?: { formId: string; editorId?: string | null; type?: string | null; name?: string | null } | null;
    /** The mesh under Data — `meshes\clutter\barrel01.nif` — or null for a record the game draws no model of (an NPC, a marker). */
    model?: string | null;
    position: [number, number, number];
    rotation: [number, number, number];
    scale: number;
    persistent: boolean;
    /** Placed or changed by the project's own plugin. */
    own: boolean;
    disabled: boolean;
    light?: { radius: number; color: [number, number, number] };
}

export interface CellSceneFile {
    cell: {
        formId: string;
        editorId?: string | null;
        name?: string | null;
        interior: boolean;
        grid?: [number, number];
        worldspace?: { formId: string; editorId?: string | null };
    };
    references: SceneReference[];
    ground?: { origin: [number, number]; spacing: number; heights: number[][] };
    /** The cell's navmeshes — the project's version first — that Blender shows as wireframes. */
    navmeshes?: { formId: string; vertices: [number, number, number][]; triangles: [number, number, number][] }[];
}

/** One object of the cell as Blender's export_layout() writes it. */
export interface LayoutReference {
    /** The reference's FormID — null for a copy made in Blender, whose `copyOf` names the reference copied. */
    formId: string | null;
    copyOf?: string | null;
    base: string;
    baseEditorId?: string | null;
    kind?: string | null;
    name: string;
    position: [number, number, number];
    rotation: [number, number, number];
    scale: number;
}

export interface LayoutFile {
    cell: string;
    references: LayoutReference[];
    /** An exterior cell's ground as it stands in Blender: 33 rows of 33 heights, south to north, west to east. */
    ground?: { heights: number[][] };
}

/** Whether Blender's ground differs from the exported one by more than half a unit anywhere: it was sculpted. */
export function groundChanged(was: number[][] | undefined, now: number[][] | undefined): boolean {
    if (!was || !now || now.length !== 33) return false;
    return now.some((row, y) => row.some((h, x) => Math.abs(h - (was[y]?.[x] ?? h)) > 0.5));
}

export interface LayoutChanges {
    moved: { reference: SceneReference; position: [number, number, number]; rotation: [number, number, number]; scale: number }[];
    copies: LayoutReference[];
    removed: SceneReference[];
    unchanged: number;
}

/** The Blender script exportCell writes beside each cell, from this package. */
export const BLENDER_SCRIPT = fileURLToPath(new URL('../../../blender/skyrim_cell.py', import.meta.url));

/**
 * The textures a mesh names: each `textures\…\x.dds` among its strings — the texture sets and effect shaders of an SSE .nif
 * keep them as plain text. With their top folder, as the game looks them up under Data.
 */
export function texturesOf(nif: Buffer): string[] {
    const text = nif.toString('latin1');
    const found = new Map<string, string>();
    for (const match of text.matchAll(/textures[\\/][^\x00-\x1f"*:<>?|]{1,200}?\.dds/gi)) {
        const file = match[0].replace(/\//g, '\\');
        const normal = `textures${file.slice('textures'.length)}`;
        if (!found.has(normal.toLowerCase())) found.set(normal.toLowerCase(), normal);
    }
    return [...found.values()];
}

/** The game's rotation matrix of a reference's rotation, row by row: Rx(-x)·Ry(-y)·Rz(-z). */
export function rotationMatrix([x, y, z]: readonly number[]): number[][] {
    const [sx, cx, sy, cy, sz, cz] = [Math.sin(x), Math.cos(x), Math.sin(y), Math.cos(y), Math.sin(z), Math.cos(z)];
    return [
        [cy * cz, cy * sz, -sy],
        [sx * sy * cz - cx * sz, sx * sy * sz + cx * cz, sx * cy],
        [cx * sy * cz + sx * sz, cx * sy * sz - sx * cz, cx * cy]
    ];
}

/** Two rotations the same, whatever their angles' turns: their matrices within a hundredth of a degree's worth. */
export function sameRotation(a: readonly number[], b: readonly number[], tolerance = 2e-4): boolean {
    const [ma, mb] = [rotationMatrix(a), rotationMatrix(b)];
    return ma.every((row, i) => row.every((value, j) => Math.abs(value - mb[i][j]) <= tolerance));
}

const samePosition = (a: readonly number[], b: readonly number[]) => a.every((v, i) => Math.abs(v - b[i]) <= 0.05);

/** What a layout written in Blender changed in a cell: references moved, turned or scaled; copies made; references deleted. */
export function layoutChanges(scene: CellSceneFile, layout: LayoutFile): LayoutChanges {
    const byId = new Map(scene.references.map(reference => [reference.formId, reference]));
    const present = new Set<string>();
    const changes: LayoutChanges = { moved: [], copies: [], removed: [], unchanged: 0 };
    for (const placed of layout.references) {
        if (!placed.formId) {
            changes.copies.push(placed);
            continue;
        }
        const reference = byId.get(placed.formId);
        if (!reference) continue;
        present.add(placed.formId);
        const scale = Math.round(placed.scale * 1000) / 1000;
        if (samePosition(reference.position, placed.position) && sameRotation(reference.rotation, placed.rotation)
            && Math.abs((reference.scale || 1) - scale) < 0.002) {
            changes.unchanged++;
            continue;
        }
        changes.moved.push({ reference, position: placed.position, rotation: placed.rotation, scale });
    }
    changes.removed = scene.references.filter(reference => !present.has(reference.formId));
    return changes;
}

/** x, y, z as Spriggit writes a position or rotation: up to four decimals, no negative zero. */
export function triple(values: readonly number[]): string {
    return values.map(v => String(Math.round(v * 10000) / 10000 + 0)).join(', ');
}

/** A rotation's angles turned into [0, 2π), as the Creation Kit writes them. */
export function normalRotation(rotation: readonly number[]): [number, number, number] {
    const tau = 2 * Math.PI;
    return rotation.map(angle => {
        const turned = ((angle % tau) + tau) % tau;
        return Math.abs(turned - tau) < 1e-6 ? 0 : turned;
    }) as [number, number, number];
}

/**
 * A reference's file with its Initially Disabled flag set, as Spriggit writes a vanilla disabled one: `InitiallyDisabled` in its
 * `SkyrimMajorRecordFlags` list, and 0x800 in its `MajorRecordFlagsRaw` where the copy has that line.
 */
export function initiallyDisabled(text: string): string {
    let out = text.replace(/^MajorRecordFlagsRaw:[ \t]*(\d+)[ \t]*$/m, (_, raw: string) => `MajorRecordFlagsRaw: ${Number(raw) | 0x800}`);
    if (/^SkyrimMajorRecordFlags:[ \t]*$/m.test(out)) {
        if (!/^SkyrimMajorRecordFlags:[ \t]*\n(?:- .*\n)*- InitiallyDisabled[ \t]*$/m.test(out)) {
            out = out.replace(/^SkyrimMajorRecordFlags:[ \t]*$/m, 'SkyrimMajorRecordFlags:\n- InitiallyDisabled');
        }
    } else if (/^Base:/m.test(out)) {
        out = out.replace(/^Base:/m, 'SkyrimMajorRecordFlags:\n- InitiallyDisabled\nBase:');
    } else {
        out = `${out.replace(/\s*$/, '')}\nSkyrimMajorRecordFlags:\n- InitiallyDisabled\n`;
    }
    return out;
}

/** A new reference's file, as readRecord's `newReference` writes one. */
export function newReferenceText(reference: { kind?: string | null; editorId: string; base: string; position: readonly number[];
    rotation: readonly number[]; scale: number }): string {
    return `MutagenObjectType: ${reference.kind === 'PlacedNpc' ? 'PlacedNpc' : 'PlacedObject'}\n` +
        `EditorID: ${reference.editorId}\n` +
        `Base: ${reference.base}\n` +
        (Math.abs(reference.scale - 1) >= 0.002 ? `Scale: ${Math.round(reference.scale * 1000) / 1000}\n` : '') +
        `Placement:\n  Position: ${triple(reference.position)}\n  Rotation: ${triple(normalRotation(reference.rotation))}\n`;
}

/** A cell's folder name under the project's blender/ folder: its editor ID, or its FormID's number. */
export function sceneFolderName(cell: CellSceneFile['cell']): string {
    return (cell.editorId || cell.formId.replace(/[:.]/g, '_')).replace(/[^\w.-]/g, '_');
}

/** The Python a Blender user runs — in the Scripting tab, or sent through Blender's MCP server — to place the cell, or to write its layout. */
export function launcher(folder: string, call: 'import_cell' | 'export_layout'): string {
    const literal = JSON.stringify(folder);
    return `# ${call === 'import_cell' ? 'Places the cell in Blender' : 'Writes what was changed to layout.json'} — Skyrim Modding Tools\n` +
        `import os\nfolder = ${literal}\n` +
        `namespace = {"__name__": "skyrim_cell"}\n` +
        `exec(compile(open(os.path.join(folder, "skyrim_cell.py"), encoding="utf-8").read(), os.path.join(folder, "skyrim_cell.py"), "exec"), namespace)\n` +
        `namespace["${call}"](folder)\n`;
}

/** The file of a record in a project's plugin folder by its FormID: the file named after it, or the one whose FormKey line names it. */
export function recordFileOf(pluginDir: string, formId: string): string | undefined {
    const [id, plugin] = formId.split(':');
    const suffix = `${id}_${plugin}.yaml`.toLowerCase();
    const formKeyLine = new RegExp(`^FormKey:[ \\t]*${id}:${plugin.replace(/\./g, '\\.')}[ \\t]*$`, 'mi');
    const walk = (dir: string): string | undefined => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) {
                const hit = walk(full);
                if (hit) return hit;
            } else if (entry.name.toLowerCase().endsWith(suffix)) return full;
        }
        return undefined;
    };
    const named = fs.existsSync(pluginDir) ? walk(pluginDir) : undefined;
    if (named) return named;
    const byLine = (dir: string): string | undefined => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) {
                const hit = byLine(full);
                if (hit) return hit;
            } else if (entry.name.endsWith('.yaml') && formKeyLine.test(fs.readFileSync(full, 'utf8'))) return full;
        }
        return undefined;
    };
    return fs.existsSync(pluginDir) ? byLine(pluginDir) : undefined;
}

/** The folder of a cell's copy in a project — `Cells/…/WhiterunBreezehome - 0165A8_Skyrim.esm`, or a worldspace's — or undefined. */
export function cellFolderOf(pluginDir: string, cellFormId: string): string | undefined {
    const [id, plugin] = cellFormId.split(':');
    const ending = ` - ${id}_${plugin}`.toLowerCase();
    const walk = (dir: string): string | undefined => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            if (!entry.isDirectory()) continue;
            const full = path.join(dir, entry.name);
            if (entry.name.toLowerCase().endsWith(ending) && fs.existsSync(path.join(full, 'RecordData.yaml'))) return full;
            const hit = walk(full);
            if (hit) return hit;
        }
        return undefined;
    };
    for (const group of ['Cells', 'Worldspaces']) {
        const root = path.join(pluginDir, group);
        if (fs.existsSync(root)) {
            const hit = walk(root);
            if (hit) return hit;
        }
    }
    return undefined;
}

/** The script that starts Blender Lab's MCP server with no stdin for each Blender it runs in the background. */
export const BLENDER_MCP_NO_STDIN = fileURLToPath(new URL('../../../blender/blender-mcp-no-stdin.py', import.meta.url));

/**
 * How a client starts Blender Lab's MCP server on Windows so that its background tools answer: its own Python — where uv
 * installed it, `%APPDATA%\uv\tools\blender-mcp` — running `script` (a copy of BLENDER_MCP_NO_STDIN) before the server's
 * arguments. Started as `blender-mcp.exe`, every `execute_blender_code_for_cli` timed out after 120 s: the Blender it runs
 * inherits the server's stdin, the client's pipe, and on Windows never ends (the eval's first Blender runs, 2026-11-06).
 * The server's own Python is the one beside its launcher — a virtual environment's Scripts folder — else uv's.
 * Undefined where nothing changes: not Windows, another server, one started so already, one whose blmcp gives its
 * Blender no stdin itself (Blender Lab's pull request #48, applied), or no Python to start it with.
 */
export function blenderMcpLaunch(server: { command?: unknown; args?: unknown }, script: string,
    options: { windows: boolean; appData?: string; exists?: (file: string) => boolean; read?: (file: string) => string | undefined }):
    { command: string; args: string[] } | undefined {
    if (!options.windows || typeof server.command !== 'string') return undefined;
    const args = Array.isArray(server.args) ? server.args.map(String) : [];
    if (args.some(arg => /blender-mcp-no-stdin\.py$/i.test(arg))) return undefined;
    if (!/(?:^|[\\/])blender-mcp(?:\.exe)?$/i.test(server.command)) return undefined;
    const exists = options.exists ?? fs.existsSync;
    const read = options.read ?? (file => { try { return fs.readFileSync(file, 'utf8'); } catch { return undefined; } });
    const python = [
        path.win32.isAbsolute(server.command) ? path.win32.join(path.win32.dirname(server.command), 'python.exe') : undefined,
        options.appData ? path.win32.join(options.appData, 'uv', 'tools', 'blender-mcp', 'Scripts', 'python.exe') : undefined
    ].find((file): file is string => file !== undefined && exists(file));
    if (!python) return undefined;
    const cli = path.win32.join(path.win32.dirname(python), '..', 'Lib', 'site-packages', 'blmcp', 'tools_helpers', 'blender_cli.py');
    if (/\bstdin\s*=\s*subprocess\.DEVNULL/.test(read(cli) ?? '')) return undefined;
    return { command: python, args: [script, ...args] };
}

/**
 * Where Blender is, when SKYRIM_BLENDER names none: on PATH, then where its installer and Steam put it — on Windows in
 * `Blender Foundation\Blender <version>` of Program Files or of a drive's root, the newest version first. Undefined
 * when there is none.
 */
export function findBlender(windows: boolean, pathVariable: string | undefined, home: string,
    exists: (file: string) => boolean = fs.existsSync): string | undefined {
    const exe = windows ? 'blender.exe' : 'blender';
    const join = windows ? path.win32.join : path.posix.join;
    const onPath = (pathVariable ?? '').split(windows ? ';' : ':').filter(Boolean).map(dir => join(dir, exe));
    const versions = (foundation: string) => {
        try {
            return fs.readdirSync(foundation).filter(name => /^Blender \d/i.test(name))
                .sort((a, b) => b.localeCompare(a, undefined, { numeric: true })).map(name => join(foundation, name, exe));
        } catch {
            return [];
        }
    };
    const candidates = windows
        ? [...onPath, ...'CDEFGHIJKLMNOPQRSTUVWXYZ'.split('').flatMap(drive => [
            ...versions(`${drive}:\\Program Files\\Blender Foundation`), ...versions(`${drive}:\\Blender Foundation`),
            `${drive}:\\Program Files (x86)\\Steam\\steamapps\\common\\Blender\\blender.exe`,
            `${drive}:\\SteamLibrary\\steamapps\\common\\Blender\\blender.exe`, `${drive}:\\Steam\\steamapps\\common\\Blender\\blender.exe`])]
        : [...onPath, '/usr/bin/blender', '/usr/local/bin/blender', join(home, '.local/share/Steam/steamapps/common/Blender/blender'),
            '/var/lib/flatpak/exports/bin/org.blender.Blender', join(home, '.local/share/flatpak/exports/bin/org.blender.Blender')];
    return candidates.find(candidate => exists(candidate));
}

/**
 * The Python that saves a cell placed by import_cell as `cell.blend` beside it, for Blender started in the background:
 * a user opens it, and an agent works on it through Blender's MCP server without a Blender window (its _for_cli tools).
 * Answers import_cell's result on a line of its own.
 */
export function saveBlendCode(folder: string): string {
    const literal = JSON.stringify(folder);
    return `import bpy, json, os\nfolder = ${literal}\nns = {"__name__": "skyrim_cell"}\n` +
        `exec(compile(open(os.path.join(folder, "skyrim_cell.py"), encoding="utf-8").read(), "skyrim_cell.py", "exec"), ns)\n` +
        `for obj in list(bpy.data.objects):\n    bpy.data.objects.remove(obj, do_unlink=True)\n` +
        `answer = ns["import_cell"](folder)\n` +
        `bpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder, "cell.blend"), compress=True)\n` +
        `print("SKYRIM_CELL_BLEND " + json.dumps(answer))\n`;
}

/** An editor ID of the project's own for a copy made in Blender: the project's name, the base's and a number. */
export function copyEditorId(project: string, base: string, taken: Set<string>): string {
    const stem = `${project.replace(/[^A-Za-z0-9]/g, '')}${base.replace(/[^A-Za-z0-9]/g, '')}`.slice(0, 80) || 'BlenderCopy';
    for (let n = 1; ; n++) {
        const id = `${stem}${n}`;
        if (!taken.has(id.toLowerCase())) {
            taken.add(id.toLowerCase());
            return id;
        }
    }
}
