import path from 'node:path';
import { ownerOf } from '../modList.js';
import type { Instance, ModEntry, ModManager } from '../modManager.js';
import { findCaseInsensitive } from '../paths.js';

/**
 * Which mod gives the game a file a crash log names: a plugin, an SKSE plugin's DLL, a mesh or a texture —
 * the copy the game loads, as the mod manager layers them (the game's Data, enabled mods by priority,
 * overwrite). A mesh or texture no layer has loose is looked for in the archives (BSAs), which the game
 * loads after its own: an archive of a plugin's — `X.bsa`, `X - Textures.bsa` for X.esp — in the plugin's
 * load order, the last one holding a file winning; a loose file beats every archive (docs/adr/0012).
 */
export interface Owner {
    /** The mod's name, `overwrite`, `the game's Data folder`, or `the game folder` for a DLL beside SkyrimSE.exe. */
    mod: string;
    /** The archive the file is in, when it is in one: `CrashTestMesh.bsa`. */
    archive?: string;
}

/** The helper's bsaFiles call: for each file asked for, the last archive in the list that holds it. */
export type BsaFiles = (request: { release: string; archives: string[]; files: string[] }) => Promise<{ found: Record<string, string> }>;

export const GAME_FOLDER = 'the game folder';

export class Attributor {
    private plugins?: Map<string, string>;
    private dlls?: Map<string, string>;
    private readonly folders = new Map<string, Map<string, string>>();

    constructor(
        private readonly manager: ModManager,
        private readonly instance: Instance,
        private readonly mods: readonly ModEntry[],
        /** The active plugins in load order: the archives that load with them follow it. */
        private readonly loadOrder: readonly string[],
        /** The helper's bsaFiles: which archives hold which files. Without it, archives are not looked in. */
        private readonly bsaFiles?: BsaFiles
    ) { }

    plugin(name: string): Owner | undefined {
        this.plugins ??= this.manager.resolvePluginFiles(this.instance, this.mods);
        const file = this.plugins.get(name.toLowerCase());
        return file ? { mod: ownerOf(file, this.instance) } : undefined;
    }

    /** An SKSE plugin from Data/SKSE/Plugins, or a DLL the game folder holds beside SkyrimSE.exe (an ENB's d3d11.dll). */
    dll(name: string): Owner | undefined {
        this.dlls ??= this.manager.resolveDataFiles(this.instance, this.mods, 'SKSE/Plugins', new Set(['.dll']));
        const file = this.dlls.get(name.toLowerCase());
        if (file) return { mod: ownerOf(file, this.instance) };
        return findCaseInsensitive(this.instance.gameDir, name) ? { mod: GAME_FOLDER } : undefined;
    }

    /**
     * Meshes and textures by their path under Data — `meshes\x\y.nif` — loose first, then in the archives.
     * A path given without its top folder is taken as a mesh's, as the game's model loader names them.
     * Answers what it found; a file nowhere in the setup is left out.
     */
    async files(paths: readonly string[]): Promise<Map<string, Owner>> {
        const owners = new Map<string, Owner>();
        const looseless: { asked: string; under: string }[] = [];
        for (const asked of paths) {
            const under = dataPath(asked);
            const loose = this.loose(under);
            if (loose) owners.set(asked, { mod: ownerOf(loose, this.instance) });
            else looseless.push({ asked, under });
        }
        if (looseless.length === 0 || !this.bsaFiles) return owners;

        const archives = this.archivesInOrder();
        if (archives.length === 0) return owners;
        const answer = await this.bsaFiles({ release: this.instance.release, archives, files: looseless.map(file => file.under) });
        for (const { asked, under } of looseless) {
            const archive = answer.found[under];
            if (archive) owners.set(asked, { mod: ownerOf(archive, this.instance), archive: path.basename(archive) });
        }
        return owners;
    }

    /** `under` has backslashes, which only Windows' path functions split on: split here, the same on every system. */
    private loose(under: string): string | undefined {
        const parts = under.split('\\');
        const name = parts.pop()!;
        const folder = parts.join('/');
        let files = this.folders.get(folder.toLowerCase());
        if (!files) {
            files = this.manager.resolveDataFiles(this.instance, this.mods, folder, new Set([path.extname(name).toLowerCase()]));
            this.folders.set(folder.toLowerCase(), files);
        }
        return files.get(name.toLowerCase());
    }

    /**
     * Every archive the game loads, in its order: those of no active plugin first — the game's own, which
     * its INI lists — then each active plugin's, in load order.
     */
    private archivesInOrder(): string[] {
        const found = this.manager.resolveDataFiles(this.instance, this.mods, '', new Set(['.bsa']));
        const byPlugin = new Map<string, string[]>();
        const unclaimed: string[] = [];
        for (const [name, file] of [...found].sort(([a], [b]) => a.localeCompare(b))) {
            const owner = this.loadOrder.find(plugin => belongsTo(name, plugin));
            if (owner) byPlugin.set(owner, [...byPlugin.get(owner) ?? [], file]);
            else unclaimed.push(file);
        }
        return [...unclaimed, ...this.loadOrder.flatMap(plugin => byPlugin.get(plugin) ?? [])];
    }
}

/** A path under Data, with backslashes, its top folder added when a model loader left it out. */
export function dataPath(asked: string): string {
    const under = asked.replace(/\//g, '\\').replace(/^\\+/, '').replace(/^data\\/i, '');
    if (/^(meshes|textures|interface|sound|music|scripts|seq|strings|skse|lodsettings|grass|shadersfx)\\/i.test(under)) return under;
    return /\.nif$/i.test(under) ? `meshes\\${under}` : under;
}

/** Whether the game loads an archive with this plugin: `X.bsa` or `X - anything.bsa` for X.esp. */
export function belongsTo(archive: string, plugin: string): boolean {
    const base = plugin.replace(/\.(esp|esm|esl)$/i, '').toLowerCase();
    const name = archive.toLowerCase().replace(/\.bsa$/, '');
    return name === base || name.startsWith(`${base} - `);
}
