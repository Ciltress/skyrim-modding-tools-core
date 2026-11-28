import fs from 'node:fs';
import path from 'node:path';
import { parseIni } from './mo2.js';
import { nexusModIdOf } from './installer.js';
import { readDownloadMeta } from './nexus.js';
import { ModError } from './modList.js';

/**
 * The downloads folder's archives, each held against the mods folder: whether a mod holds its files. Mod Organizer's own
 * mark in an archive's .meta (`installed=true`) says an install was started, not that its files arrived: 540 archives of
 * one setup were marked installed, their mods' folder empty or gone, and were deleted as done (2026-11-28). An archive is
 * installed here only where a mod's meta.ini names it as the file it was installed from and the mod's folder holds files.
 */

export type DownloadState = 'installed' | 'installedEmpty' | 'notInstalled' | 'downloading';

export interface DownloadRow {
    file: string;
    bytes: number;
    modified: string;
    state: DownloadState;
    /** The mods installed from it: those whose meta.ini names it. */
    mods?: { name: string; enabled: boolean; files: boolean }[];
    /** Mod Organizer marked it installed, and no mod holds its files. */
    markedInstalled?: true;
    /** Hidden in Mod Organizer's downloads list. */
    hidden?: true;
    nexus?: { modId: number; fileId?: number; version?: string };
}

export interface DownloadList {
    folder: string;
    /** Every archive of the folder, by state. */
    counts: Record<DownloadState, number>;
    total: number;
    downloads: DownloadRow[];
}

const ARCHIVE = /\.(7z|zip|rar|fomod)$/i;

/** Mods by the archive each was installed from, lower-case file name; and by Nexus file ID, for an archive renamed since. */
interface Installed {
    byFile: Map<string, string[]>;
    byFileId: Map<number, string[]>;
}

function readInstalled(modsDir: string): Installed {
    const byFile = new Map<string, string[]>();
    const byFileId = new Map<number, string[]>();
    const add = <K>(map: Map<K, string[]>, key: K, mod: string) => {
        const list = map.get(key);
        if (list) list.push(mod);
        else map.set(key, [mod]);
    };
    let folders: fs.Dirent[] = [];
    try {
        folders = fs.readdirSync(modsDir, { withFileTypes: true });
    } catch {
        return { byFile, byFileId };
    }
    for (const folder of folders) {
        if (!folder.isDirectory()) continue;
        let ini: Map<string, Map<string, string>>;
        try {
            ini = parseIni(fs.readFileSync(path.join(modsDir, folder.name, 'meta.ini'), 'utf8'));
        } catch {
            continue;
        }
        const from = ini.get('General')?.get('installationFile');
        if (from) add(byFile, path.basename(from.replace(/\\/g, '/')).toLowerCase(), folder.name);
        // Mod Organizer keeps the Nexus files a mod was installed from as `1\fileid=…`.
        for (const [key, value] of ini.get('installedFiles') ?? []) {
            const fileId = Number(value);
            if (/\\fileid$/i.test(key) && Number.isInteger(fileId) && fileId > 0) add(byFileId, fileId, folder.name);
        }
    }
    return { byFile, byFileId };
}

/** Whether a mod's folder holds a file besides its meta.ini, at any depth: what an install that wrote nothing leaves out. */
export function holdsFiles(dir: string): boolean {
    let entries: fs.Dirent[];
    try {
        entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
        return false;
    }
    for (const entry of entries) {
        if (entry.isFile() && entry.name.toLowerCase() !== 'meta.ini') return true;
        if (entry.isDirectory() && holdsFiles(path.join(dir, entry.name))) return true;
    }
    return false;
}

export interface DownloadQuery {
    text?: string;
    state?: DownloadState;
    limit: number;
    offset?: number;
}

export function listDownloads(downloadsDir: string, modsDir: string, enabled: ReadonlyMap<string, boolean>, query: DownloadQuery): DownloadList {
    let names: string[];
    try {
        names = fs.readdirSync(downloadsDir);
    } catch {
        throw new ModError(`The downloads folder ${downloadsDir} could not be read: set download_directory under [Settings] in ModOrganizer.ini, or in Mod Organizer (Settings > Paths).`);
    }
    const installed = readInstalled(modsDir);
    const rows: DownloadRow[] = [];
    for (const name of names) {
        const unfinished = /\.unfinished$/i.test(name);
        if (!ARCHIVE.test(name.replace(/\.unfinished$/i, ''))) continue;
        const file = path.join(downloadsDir, name);
        let stat: fs.Stats;
        try {
            stat = fs.statSync(file);
        } catch {
            continue;
        }
        if (!stat.isFile()) continue;
        const meta = readDownloadMeta(file);
        const raw = rawMeta(file);
        const modId = meta.modId ?? nexusModIdOf(name);
        const modNames = unfinished ? [] : [...new Set([
            ...(installed.byFile.get(name.toLowerCase()) ?? []),
            ...(meta.fileId ? installed.byFileId.get(meta.fileId) ?? [] : [])
        ])];
        const mods = modNames.map(mod => ({ name: mod, enabled: enabled.get(mod) ?? false, files: holdsFiles(path.join(modsDir, mod)) }));
        const state: DownloadState = unfinished ? 'downloading'
            : mods.some(mod => mod.files) ? 'installed'
            : mods.length > 0 ? 'installedEmpty' : 'notInstalled';
        rows.push({
            file: name,
            bytes: stat.size,
            modified: stat.mtime.toISOString(),
            state,
            ...(mods.length > 0 ? { mods } : {}),
            ...(state !== 'installed' && raw.get('installed') === 'true' ? { markedInstalled: true as const } : {}),
            ...(raw.get('removed') === 'true' ? { hidden: true as const } : {}),
            ...(modId ? { nexus: { modId, ...(meta.fileId ? { fileId: meta.fileId } : {}), ...(meta.version ? { version: meta.version } : {}) } } : {})
        });
    }
    rows.sort((a, b) => b.modified.localeCompare(a.modified) || a.file.localeCompare(b.file));
    const counts: Record<DownloadState, number> = { installed: 0, installedEmpty: 0, notInstalled: 0, downloading: 0 };
    for (const row of rows) counts[row.state]++;
    const text = query.text?.toLowerCase();
    const matched = rows.filter(row => (!query.state || row.state === query.state) && (!text || row.file.toLowerCase().includes(text)));
    const offset = query.offset ?? 0;
    return { folder: downloadsDir, counts, total: matched.length, downloads: matched.slice(offset, offset + query.limit) };
}

/** The .meta's [General] keys, lower case, as Mod Organizer wrote them. */
function rawMeta(file: string): Map<string, string> {
    try {
        const general = parseIni(fs.readFileSync(`${file}.meta`, 'utf8')).get('General') ?? new Map<string, string>();
        return new Map([...general].map(([key, value]) => [key.toLowerCase(), value.toLowerCase()]));
    } catch {
        return new Map();
    }
}


