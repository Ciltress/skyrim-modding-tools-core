import fs from 'node:fs';
import path from 'node:path';
import { belongsTo } from './crash/attribution.js';
import { ownerOf } from './modList.js';
import type { Instance, ModEntry } from './modManager.js';

/**
 * File conflicts: two mods giving the game the same file — a mesh, a texture, a script, a plugin — and which one the game
 * takes. Loose files layer by the mod order: the enabled mods from the lowest priority up, then overwrite, the last one
 * winning. A file in an archive (BSA) is taken only where no mod has it loose — a loose file beats every archive, whatever
 * the mod order — and among archives the one of the plugin that loads last wins (docs/adr/0012). The game's own files are no
 * conflict: a mod that replaces one is made to.
 */

/** Two mods that give the game the same files: the one whose copies the game takes, the one whose it does not. */
export interface FileConflictPair {
    winner: string;
    loser: string;
    /** How the winner wins: `mod order` (both loose), `loose over archive`, or `load order` (both in archives). */
    how: 'mod order' | 'loose over archive' | 'load order';
    files: number;
    /** A few of the files, by their path under Data. */
    examples: string[];
}

export interface FileConflicts {
    /** The pairs, the most files first. */
    pairs: FileConflictPair[];
    /** How many pairs there are, before the limit. */
    total: number;
    /** Archives that could not be read, each with why. */
    unreadArchives?: { archive: string; error: string }[];
}

/** The helper's archiveFiles call: every file of each archive, as the game compares paths. */
export type ArchiveLists = (request: { release: string; archives: string[] }) =>
    Promise<{ files: Record<string, string[]>; errors?: { archive: string; error: string }[] }>;

/** A path as the game compares it: any case, backslashes, no leading separator. */
const key = (relative: string) => relative.replace(/\//g, '\\').replace(/^\\+/, '').toLowerCase();

/** The files a mod's folder gives the game, by their path under Data — every file but the mod manager's own meta.ini. */
export function looseFiles(dir: string): string[] {
    const files: string[] = [];
    const walk = (folder: string, prefix: string) => {
        let entries: fs.Dirent[];
        try {
            entries = fs.readdirSync(folder, { withFileTypes: true });
        } catch {
            return;
        }
        for (const entry of entries) {
            const relative = prefix ? `${prefix}\\${entry.name}` : entry.name;
            if (entry.isDirectory()) walk(path.join(folder, entry.name), relative);
            else if (entry.isFile() && !(prefix === '' && entry.name.toLowerCase() === 'meta.ini')) files.push(key(relative));
        }
    };
    walk(dir, '');
    return files;
}

/**
 * The setup's file conflicts between mods — of one mod, where `mod` names it — loose and in archives. `loadOrder` is the
 * active plugins in load order, which the archives load with.
 */
export async function findFileConflicts(instance: Instance, mods: readonly ModEntry[], loadOrder: readonly string[],
    archiveLists: ArchiveLists | undefined, options: { mod?: string; limit: number }): Promise<FileConflicts> {
    // The layers, lowest first: the enabled mods by priority (the list is highest first), then overwrite.
    const layers = [...mods].reverse().filter(mod => mod.enabled && mod.managed)
        .map(mod => ({ name: mod.name, dir: path.join(instance.modsDir, mod.name) }))
        .concat([{ name: 'overwrite', dir: instance.overwriteDir }]);
    const loose = new Map<string, string[]>();
    for (const layer of layers)
        for (const file of looseFiles(layer.dir)) loose.set(file, [...loose.get(file) ?? [], layer.name]);

    const pairs = new Map<string, FileConflictPair>();
    const count = (winner: string, loser: string, how: FileConflictPair['how'], file: string) => {
        if (winner === loser) return;
        const id = `${winner}\u0000${loser}\u0000${how}`;
        const pair = pairs.get(id) ?? { winner, loser, how, files: 0, examples: [] };
        pair.files++;
        if (pair.examples.length < 3) pair.examples.push(file);
        pairs.set(id, pair);
    };
    for (const [file, owners] of loose)
        for (const loser of owners.slice(0, -1)) count(owners[owners.length - 1], loser, 'mod order', file);

    // The mods' archives the game loads: each of an active plugin's, in load order.
    let unreadArchives: FileConflicts['unreadArchives'];
    if (archiveLists) {
        const archives: { file: string; mod: string }[] = [];
        for (const plugin of loadOrder) {
            for (const layer of layers) {
                let names: string[];
                try {
                    names = fs.readdirSync(layer.dir).filter(name => /\.bsa$/i.test(name) && belongsTo(name, plugin));
                } catch {
                    continue;
                }
                for (const name of names.sort()) archives.push({ file: path.join(layer.dir, name), mod: ownerOf(path.join(layer.dir, name), instance) });
            }
        }
        if (archives.length > 0) {
            const listed = await archiveLists({ release: instance.release, archives: archives.map(a => a.file) });
            if (listed.errors?.length) unreadArchives = listed.errors;
            const inArchives = new Map<string, string[]>();
            for (const archive of archives)
                for (const file of listed.files[archive.file] ?? []) inArchives.set(file, [...inArchives.get(file) ?? [], archive.mod]);
            for (const [file, owners] of inArchives) {
                const looseOwners = loose.get(file);
                if (looseOwners) for (const loser of new Set(owners)) count(looseOwners[looseOwners.length - 1], loser, 'loose over archive', file);
                else for (const loser of owners.slice(0, -1)) count(owners[owners.length - 1], loser, 'load order', file);
            }
        }
    }

    const wanted = options.mod?.toLowerCase();
    const all = [...pairs.values()]
        .filter(pair => !wanted || pair.winner.toLowerCase() === wanted || pair.loser.toLowerCase() === wanted)
        .sort((a, b) => b.files - a.files || a.winner.localeCompare(b.winner));
    return { pairs: all.slice(0, options.limit), total: all.length, ...(unreadArchives ? { unreadArchives } : {}) };
}
