import crypto from 'node:crypto';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';

/**
 * LOOT's sorting library, libloot (GPL-3.0-or-later), through its Node.js wrapper libloot-nodejs: the same sorter and
 * masterlist LOOT itself uses. The wrapper is built from libloot's source for Windows and Linux (docs/adr/0013) and published
 * as assets of the core's release `libloot-<version>`; the core downloads the one for this system the first time it sorts,
 * checks its SHA-256 against the one pinned here, and keeps it in its cache folder.
 */
export const LIBLOOT_VERSION = '0.29.7';

/** The wrapper's binary for each system the core runs on, with its SHA-256. */
export const LIBLOOT_BINARIES: Record<string, { file: string; sha256: string }> = {
    'win32-x64': { file: 'libloot-nodejs.win32-x64-msvc.node', sha256: '95b3eae678b6e277bfe4e75e6a7b375dfb5446b3792b510063ae700afa4ebfd4' },
    'linux-x64': { file: 'libloot-nodejs.linux-x64-gnu.node', sha256: '6a765c4aa2083aef3af174a15b690cc8499f8d788182a46ff0acfe01fdd4b93c' }
};

const RELEASE = `https://github.com/Ciltress/skyrim-modding-tools-core/releases/download/libloot-${LIBLOOT_VERSION}/`;

/** LOOT's masterlist and prelude for this libloot's metadata syntax: the branch named for its minor version. */
export const MASTERLIST_URL = 'https://raw.githubusercontent.com/loot/skyrimse/v0.29/masterlist.yaml';
export const PRELUDE_URL = 'https://raw.githubusercontent.com/loot/prelude/v0.29/prelude.yaml';

/** How old a downloaded masterlist may be before the next sort fetches it again. */
const MASTERLIST_MAX_AGE_MS = 24 * 60 * 60 * 1000;

/** What a sort needs and could not get — the library or the masterlist — said for the user. */
export class LootUnavailableError extends Error {
    constructor(message: string) {
        super(message);
        this.name = 'LootUnavailableError';
    }
}

/** The part of libloot-nodejs the core uses (its index.d.ts). */
export interface LiblootModule {
    GameType: { SkyrimSE: number };
    Game: new (gameType: number, gamePath: string, localPath?: string) => LootGame;
    liblootVersion?(): string;
}

export interface LootGame {
    setAdditionalDataPaths(paths: string[]): void;
    database(): LootDatabase;
    loadCurrentLoadOrderState(): void;
    loadOrder(): string[];
    loadPlugins(names: string[]): void;
    plugin(name: string): LootPlugin | null;
    sortPlugins(names: string[]): string[];
    isPluginActive(name: string): boolean;
}

export interface LootDatabase {
    loadMasterlistWithPrelude(masterlist: string, prelude: string): void;
    loadUserlist(userlist: string): void;
    generalMessages(includeUserMetadata: number, evaluateConditions: number): LootMessage[];
    pluginMetadata(plugin: string, includeUserMetadata: number, evaluateConditions: number): LootMetadata | null;
    evaluate(condition: string): boolean;
}

export interface LootMessage {
    messageType: number;
    content: { text: string; language?: string }[];
}

export interface LootMetadata {
    group: string | null;
    messages: LootMessage[];
    requirements: LootFile[];
    incompatibilities: LootFile[];
    dirtyInfo: { crc: number; itmCount: number; deletedReferenceCount: number; deletedNavmeshCount: number; cleaningUtility: string }[];
}

/** A file a plugin's metadata names: its name, as libloot's Filename, and the name to show. */
export interface LootFile {
    name: { asStr(): string };
    displayName: string | null;
}

export interface LootPlugin {
    name(): string;
    crc(): number | null;
    masters(): string[];
    isMaster(): boolean;
    isLightPlugin(): boolean;
}

/** Where the core keeps what it downloads for LOOT: the system's cache folder for programs. */
export function lootCacheDir(env: NodeJS.ProcessEnv = process.env, platform: NodeJS.Platform = process.platform): string {
    const base = platform === 'win32'
        ? env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local')
        : env.XDG_CACHE_HOME ?? path.join(os.homedir(), '.cache');
    return path.join(base, 'skyrim-modding-tools', 'loot');
}

const sha256 = (file: string) => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');

async function download(url: string, file: string): Promise<void> {
    const response = await fetch(url, { redirect: 'follow' });
    if (!response.ok) throw new Error(`${url} answered ${response.status} ${response.statusText}`);
    const partial = `${file}.part`;
    fs.writeFileSync(partial, Buffer.from(await response.arrayBuffer()));
    fs.renameSync(partial, file);
}

/** libloot's wrapper for this system: the cached binary, downloaded and checked first where it is missing. */
export async function loadLibloot(cacheDir: string, system = `${process.platform}-${process.arch}`): Promise<LiblootModule> {
    const binary = LIBLOOT_BINARIES[system];
    if (!binary) throw new LootUnavailableError(`LOOT's library has no build for this system (${system}): sorting runs on 64-bit Windows and Linux.`);
    const dir = path.join(cacheDir, `libloot-${LIBLOOT_VERSION}`);
    const file = path.join(dir, binary.file);
    if (!fs.existsSync(file) || sha256(file) !== binary.sha256) {
        fs.mkdirSync(dir, { recursive: true });
        try {
            await download(RELEASE + binary.file, file);
        } catch (error) {
            throw new LootUnavailableError(`LOOT's library could not be downloaded (${(error as Error).message}): sorting needs it once, ` +
                'from the core\'s GitHub release — try again when online.');
        }
        if (sha256(file) !== binary.sha256) {
            fs.rmSync(file, { force: true });
            throw new LootUnavailableError(`LOOT's library downloaded from ${RELEASE} is not the build the core pins: nothing was loaded.`);
        }
    }
    ownPackage(dir);
    return createRequire(import.meta.url)(file) as LiblootModule;
}

/**
 * The library's folder made a package of its own. A host that checks the native modules a plugin loads can take a module's
 * nearest package.json for its package and read all of that folder: OpenClaw found one in the user's home folder, read the
 * home and refused a link in it that leads to another drive, and no sort ran (eval round 139's loot runs).
 */
export function ownPackage(dir: string): void {
    const manifest = path.join(dir, 'package.json');
    if (fs.existsSync(manifest)) return;
    fs.writeFileSync(manifest, `${JSON.stringify({ name: 'skyrim-modding-tools-libloot', version: LIBLOOT_VERSION, private: true }, null, 2)}\n`);
}

/**
 * LOOT's masterlist and prelude in the cache, fetched again when a day old: a sort offline uses the last ones, and none at
 * all is an error.
 */
export async function masterlistFiles(cacheDir: string, now = Date.now()): Promise<{ masterlist: string; prelude: string; fetched?: true; stale?: true }> {
    const dir = path.join(cacheDir, 'skyrimse-v0.29');
    fs.mkdirSync(dir, { recursive: true });
    const masterlist = path.join(dir, 'masterlist.yaml');
    const prelude = path.join(dir, 'prelude.yaml');
    const fresh = (file: string) => fs.existsSync(file) && now - fs.statSync(file).mtimeMs < MASTERLIST_MAX_AGE_MS;
    if (fresh(masterlist) && fresh(prelude)) return { masterlist, prelude };
    try {
        await download(MASTERLIST_URL, masterlist);
        await download(PRELUDE_URL, prelude);
        return { masterlist, prelude, fetched: true };
    } catch (error) {
        if (fs.existsSync(masterlist) && fs.existsSync(prelude)) return { masterlist, prelude, stale: true };
        throw new LootUnavailableError(`LOOT's masterlist could not be downloaded (${(error as Error).message}), and none is kept from ` +
            'before: sorting needs it — try again when online.');
    }
}
