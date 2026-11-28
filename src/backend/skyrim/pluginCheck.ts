import fs from 'node:fs';
import path from 'node:path';

/**
 * The load order's plugins held against each other, read from their headers alone: what stops the game at its main menu
 * or before it — a master missing or not enabled, a master loading after the plugin that needs it — and the plugin
 * limits. Read here, not by the helper: a header is the first few hundred bytes of a file, and 1,408 of them are read in
 * well under a second, with no wait for the record index.
 */

/** What a plugin's TES4 header says of it. */
export interface PluginHeaderInfo {
    masters: string[];
    /** Flagged master, or an .esm or .esl: the game loads it with the masters. */
    master: boolean;
    /** Flagged light, or an .esl: it takes no slot of the 254, and shares the FE slot. */
    light: boolean;
}

const MASTER_FLAG = 0x1;
const LIGHT_FLAG = 0x200;

/** The header of a Skyrim Special Edition plugin; throws for a file that is none. */
export function readPluginHeader(file: string): PluginHeaderInfo {
    const fd = fs.openSync(file, 'r');
    try {
        const head = Buffer.alloc(24);
        if (fs.readSync(fd, head, 0, 24, 0) < 24 || head.toString('latin1', 0, 4) !== 'TES4') {
            throw new Error(`${path.basename(file)} is not a plugin: it does not start with a TES4 header`);
        }
        const size = head.readUInt32LE(4);
        const flags = head.readUInt32LE(8);
        // A header holds its masters and a few small fields; an ONAM of many overrides makes it larger, never this large.
        if (size > 16 * 1024 * 1024) throw new Error(`${path.basename(file)}'s header says it is ${size} bytes long`);
        const data = Buffer.alloc(size);
        const read = fs.readSync(fd, data, 0, size, 24);
        const masters: string[] = [];
        let at = 0;
        let big: number | undefined;
        while (at + 6 <= read) {
            const type = data.toString('latin1', at, at + 4);
            let length = data.readUInt16LE(at + 4);
            at += 6;
            if (big !== undefined) { length = big; big = undefined; }
            if (type === 'XXXX') { big = data.readUInt32LE(at); at += length; continue; }
            if (type === 'MAST') {
                const end = data.indexOf(0, at);
                masters.push(data.toString('latin1', at, end >= 0 && end < at + length ? end : at + length));
            }
            at += length;
        }
        const extension = path.extname(file).toLowerCase();
        return {
            masters,
            master: (flags & MASTER_FLAG) !== 0 || extension === '.esm' || extension === '.esl',
            light: (flags & LIGHT_FLAG) !== 0 || extension === '.esl'
        };
    } finally {
        fs.closeSync(fd);
    }
}

export type PluginProblemKind = 'noFile' | 'unreadable' | 'missingMaster' | 'masterNotEnabled' | 'masterLoadsLater';

export interface PluginProblem {
    kind: PluginProblemKind;
    plugin: string;
    /** The mod the plugin's copy comes from. */
    mod?: string;
    master?: string;
    /** Where the master is: the mod of a master not enabled, or a disabled mod that has a missing one's file. */
    masterMod?: string;
    error?: string;
}

export interface PluginCheck {
    active: number;
    /** Plugins that take a slot of their own: at most 254 load. */
    full: number;
    /** Light plugins, sharing the FE slot: at most 4,096 load. */
    light: number;
    problems: PluginProblem[];
    /** The limits gone past, said whole. */
    limits: string[];
}

export interface PluginCheckInput {
    /** The load order: every plugin it names, whether it is enabled. */
    order: readonly { name: string; active: boolean }[];
    /** Each plugin the game would load, by its lower-case name: the file of the copy that wins. */
    files: ReadonlyMap<string, string>;
    /** Each plugin a disabled mod has, by its lower-case name: that mod, to say where a missing master is. */
    inDisabledMods?: ReadonlyMap<string, string>;
    /** The mod a file is in. */
    ownerOf: (file: string) => string;
    /** Read a plugin's header; readPluginHeader unless a test gives its own. */
    readHeader?: (file: string) => PluginHeaderInfo;
}

export const FULL_LIMIT = 254;
export const LIGHT_LIMIT = 4096;

export function checkPlugins(input: PluginCheckInput): PluginCheck {
    const read = input.readHeader ?? readPluginHeader;
    const position = new Map(input.order.map((plugin, i) => [plugin.name.toLowerCase(), i]));
    const enabled = new Map(input.order.map(plugin => [plugin.name.toLowerCase(), plugin.active]));
    const problems: PluginProblem[] = [];
    let full = 0;
    let light = 0;
    const active = input.order.filter(plugin => plugin.active);
    for (const plugin of active) {
        const key = plugin.name.toLowerCase();
        const file = input.files.get(key);
        if (!file) {
            problems.push({ kind: 'noFile', plugin: plugin.name });
            continue;
        }
        const mod = input.ownerOf(file);
        let header: PluginHeaderInfo;
        try {
            header = read(file);
        } catch (error) {
            problems.push({ kind: 'unreadable', plugin: plugin.name, mod, error: error instanceof Error ? error.message : String(error) });
            continue;
        }
        if (header.light) light++;
        else full++;
        for (const master of header.masters) {
            const masterKey = master.toLowerCase();
            const masterFile = input.files.get(masterKey);
            if (!enabled.has(masterKey) && !masterFile) {
                const elsewhere = input.inDisabledMods?.get(masterKey);
                problems.push({ kind: 'missingMaster', plugin: plugin.name, mod, master, ...(elsewhere ? { masterMod: elsewhere } : {}) });
            } else if (!enabled.get(masterKey)) {
                const where = masterFile ? input.ownerOf(masterFile) : input.inDisabledMods?.get(masterKey);
                problems.push({ kind: 'masterNotEnabled', plugin: plugin.name, mod, master, ...(where ? { masterMod: where } : {}) });
            } else if ((position.get(masterKey) ?? -1) > (position.get(key) ?? -1)) {
                problems.push({ kind: 'masterLoadsLater', plugin: plugin.name, mod, master });
            }
        }
    }
    const limits: string[] = [];
    if (full > FULL_LIMIT) {
        limits.push(`${full} plugins take a slot of their own, and the game loads ${FULL_LIMIT}: those past it are not loaded. ` +
            'Flag plugins light (ESL) where their FormIDs allow it, or disable some');
    }
    if (light > LIGHT_LIMIT) limits.push(`${light} light plugins are enabled, and the game loads ${LIGHT_LIMIT}`);
    return { active: active.length, full, light, problems, limits };
}
