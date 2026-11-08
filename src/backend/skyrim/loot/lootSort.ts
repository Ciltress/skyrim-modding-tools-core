import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { orderChanges } from '../orderMoves.js';
import type { LiblootModule, LootGame, LootMessage } from './libloot.js';

/** libloot's MergeMode.WithUserMetadata and EvalMode.Evaluate: the user's own LOOT rules counted, conditions evaluated. */
const WITH_USER = 1;
const EVALUATE = 1;
const MESSAGE_LEVEL = ['info', 'warn', 'error'] as const;
const MAX_MESSAGES = 30;

export interface LootSortInput {
    libloot: LiblootModule;
    /** The game folder, Data in it. */
    gameDir: string;
    /** The profile's load order, every plugin with whether it is active. */
    order: { name: string; active: boolean }[];
    /** Folders the game sees as Data besides its own, the highest priority first: Mod Organizer's overwrite and mods. */
    dataPaths: string[];
    masterlist: string;
    prelude: string;
    /** LOOT's own userlist.yaml where the user has one: their rules are LOOT's too. */
    userlist?: string;
    /** An empty folder of the core's for the plugin list libloot reads. */
    workDir: string;
}

export interface LootSortReport {
    /** Active plugins sorted. */
    plugins: number;
    changed: boolean;
    /** The fewest moves from the profile's order to LOOT's: each plugin with where it goes. */
    moves: { plugin: string; from: number; to: number; after?: string; before?: string }[];
    /** LOOT's order of the active plugins, where it differs from the profile's. */
    sorted?: string[];
    /** LOOT's messages for this setup, warnings and errors first. */
    messages: { level: 'info' | 'warn' | 'error'; text: string; plugin?: string }[];
    /** Plugins LOOT knows as dirty by their CRC: identical-to-master records and deleted references to clean with SSEEdit. */
    dirty: { plugin: string; itm: number; deletedReferences: number; deletedNavmeshes: number; utility: string }[];
    /** Files a plugin needs that the setup does not have. */
    missing: { plugin: string; needs: string }[];
    /** Plugins LOOT knows as incompatible, both in the setup. */
    incompatible: { plugin: string; with: string }[];
}

const textOf = (message: LootMessage) => (message.content.find(c => !c.language || c.language === 'en') ?? message.content[0])?.text ?? '';

/**
 * LOOT's sort of the profile's load order, and what LOOT says of the setup: libloot reads the order from a plugins.txt the
 * core writes for it in asterisk form — the managers keep their own — the plugins from the game's Data and the folders a
 * manager lays over it, and the masterlist with its prelude.
 */
export function lootSort(input: LootSortInput): { report: LootSortReport; game: LootGame; current: string[]; sorted: string[] } {
    const { libloot } = input;
    fs.mkdirSync(input.workDir, { recursive: true });
    fs.writeFileSync(path.join(input.workDir, 'Plugins.txt'),
        input.order.map(plugin => `${plugin.active ? '*' : ''}${plugin.name}`).join('\r\n') + '\r\n', 'latin1');
    const game = new libloot.Game(libloot.GameType.SkyrimSE, input.gameDir, input.workDir);
    if (input.dataPaths.length) game.setAdditionalDataPaths(input.dataPaths);
    const database = game.database();
    database.loadMasterlistWithPrelude(input.masterlist, input.prelude);
    if (input.userlist && fs.existsSync(input.userlist)) database.loadUserlist(input.userlist);
    game.loadCurrentLoadOrderState();
    const current = game.loadOrder().filter(name => game.isPluginActive(name));
    game.loadPlugins(current);
    const sorted = game.sortPlugins(current);

    const messages: LootSortReport['messages'] = database.generalMessages(WITH_USER, EVALUATE)
        .map(message => ({ level: MESSAGE_LEVEL[message.messageType] ?? 'info', text: textOf(message) }));
    const dirty: LootSortReport['dirty'] = [];
    const missing: LootSortReport['missing'] = [];
    const incompatible: LootSortReport['incompatible'] = [];
    const active = new Set(current.map(name => name.toLowerCase()));
    const present = (file: string) => active.has(file.toLowerCase()) || database.evaluate(`file(${JSON.stringify(file)})`);
    for (const name of current) {
        const meta = database.pluginMetadata(name, WITH_USER, EVALUATE);
        if (!meta) continue;
        for (const message of meta.messages) messages.push({ level: MESSAGE_LEVEL[message.messageType] ?? 'info', text: textOf(message), plugin: name });
        // A dirty entry is for one version of the plugin, by its CRC: the masterlist lists the dirty ones a cleaned copy replaces.
        const crc = game.plugin(name)?.crc();
        for (const info of meta.dirtyInfo) {
            if (crc === null || crc === undefined || info.crc !== crc) continue;
            dirty.push({ plugin: name, itm: info.itmCount, deletedReferences: info.deletedReferenceCount,
                deletedNavmeshes: info.deletedNavmeshCount, utility: info.cleaningUtility.replace(/^\[([^\]]+)\]\([^)]*\)$/, '$1') });
        }
        for (const file of meta.requirements) {
            const needed = file.name.asStr();
            if (!present(needed)) missing.push({ plugin: name, needs: file.displayName || needed });
        }
        for (const file of meta.incompatibilities) {
            const other = file.name.asStr();
            if (active.has(other.toLowerCase())) incompatible.push({ plugin: name, with: file.displayName || other });
        }
    }
    const rank = { error: 0, warn: 1, info: 2 } as const;
    messages.sort((a, b) => rank[a.level] - rank[b.level]);
    const changed = sorted.some((name, at) => name.toLowerCase() !== current[at]?.toLowerCase());
    const report: LootSortReport = {
        plugins: current.length,
        changed,
        moves: changed ? orderChanges(current, sorted) : [],
        ...(changed ? { sorted } : {}),
        messages: messages.slice(0, MAX_MESSAGES),
        dirty,
        missing,
        incompatible
    };
    return { report, game, current, sorted };
}

/**
 * LOOT's own userlist for Skyrim SE, where the user has LOOT installed: the rules they set there sort here too. Windows'
 * LOOT keeps it in Local AppData, Linux's Flatpak in its app data.
 */
export function lootUserlist(system: 'windows' | 'linux', env: NodeJS.ProcessEnv = process.env): string | undefined {
    const games = system === 'windows'
        ? path.join(env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local'), 'LOOT', 'games')
        : path.join(os.homedir(), '.var', 'app', 'io.github.loot.loot', 'data', 'LOOT', 'games');
    const file = path.join(games, 'Skyrim Special Edition', 'userlist.yaml');
    return fs.existsSync(file) ? file : undefined;
}
