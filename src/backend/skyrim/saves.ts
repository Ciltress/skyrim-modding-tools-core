import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

/**
 * The game's saves, read from their own header: the character, where and when, and the plugins the save was made with.
 * A save made with a plugin the load order no longer enables loads with that plugin's records gone — its quests, its
 * items, its placed objects — which is what a save that crashes or breaks after a mod was removed comes from.
 */

export interface SaveHeader {
    character: string;
    level: number;
    location: string;
    gameDate: string;
    race: string;
    /** The plugins it was made with, full ones then light ones, in their load order then. */
    plugins: string[];
    light: string[];
}

/** A string of a save: its length in two bytes, then that many bytes. */
function wstring(buffer: Buffer, at: number): [string, number] {
    const length = buffer.readUInt16LE(at);
    return [buffer.toString('latin1', at + 2, at + 2 + length), at + 2 + length];
}

/**
 * An LZ4 block, unpacked as far as `want` bytes: the plugin list a save starts with needs a few kilobytes of the tens of
 * megabytes the rest of it holds.
 */
export function lz4Block(input: Buffer, size: number, want = size): Buffer {
    const out = Buffer.alloc(Math.min(size, want + 65_536 + 270));
    let at = 0;
    let written = 0;
    const limit = Math.min(size, out.length);
    while (at < input.length && written < want && written < limit) {
        const token = input[at++];
        let literals = token >> 4;
        if (literals === 15) {
            let more: number;
            do { more = input[at++]; literals += more; } while (more === 255 && at < input.length);
        }
        const copy = Math.min(literals, limit - written, input.length - at);
        input.copy(out, written, at, at + copy);
        written += copy;
        at += literals;
        if (at >= input.length || written >= limit || written >= want) break;
        const offset = input[at] | (input[at + 1] << 8);
        at += 2;
        if (offset === 0) throw new Error('the save\'s data is damaged: an LZ4 match with no offset');
        let match = token & 15;
        if (match === 15) {
            let more: number;
            do { more = input[at++]; match += more; } while (more === 255 && at < input.length);
        }
        match += 4;
        // Byte by byte: a match may overlap what it copies.
        for (let i = 0; i < match && written < limit; i++, written++) out[written] = out[written - offset];
    }
    return out.subarray(0, written);
}

export class SaveFormatError extends Error { }

/** The header and plugin list of a Skyrim Special Edition save (.ess). */
export function readSave(file: string): SaveHeader {
    const fd = fs.openSync(file, 'r');
    try {
        const read = (at: number, length: number) => {
            const buffer = Buffer.alloc(length);
            return buffer.subarray(0, fs.readSync(fd, buffer, 0, length, at));
        };
        const start = read(0, 17);
        if (start.toString('latin1', 0, 13) !== 'TESV_SAVEGAME') throw new SaveFormatError(`${path.basename(file)} is not a Skyrim save`);
        const headerSize = start.readUInt32LE(13);
        const header = read(17, headerSize);
        const version = header.readUInt32LE(0);
        let at = 8;
        let character: string, location: string, gameDate: string, race: string;
        [character, at] = wstring(header, at);
        const level = header.readUInt32LE(at);
        at += 4;
        [location, at] = wstring(header, at);
        [gameDate, at] = wstring(header, at);
        [race, at] = wstring(header, at);
        at += 2 + 4 + 4 + 8;
        const width = header.readUInt32LE(at);
        const height = header.readUInt32LE(at + 4);
        const compression = version >= 12 ? header.readUInt16LE(at + 8) : 0;
        let offset = 17 + headerSize + width * height * (version >= 12 ? 4 : 3);

        let body: (want: number) => Buffer;
        if (version >= 12 && compression !== 0) {
            const sizes = read(offset, 8);
            const size = sizes.readUInt32LE(0);
            const packed = sizes.readUInt32LE(4);
            offset += 8;
            body = want => {
                // LZ4 and zlib never pack bytes into fewer than this: what `want` bytes need is read, not the whole save.
                const input = read(offset, Math.min(packed, want + (want >> 6) + 4096));
                if (compression === 2) return lz4Block(input, size, want);
                if (compression === 1) return zlib.inflateSync(input, { finishFlush: zlib.constants.Z_SYNC_FLUSH });
                throw new SaveFormatError(`${path.basename(file)} is packed in a way this server does not read (compression ${compression})`);
            };
        } else {
            body = want => read(offset, want);
        }

        const first = body(5);
        if (first.length < 5) throw new SaveFormatError(`${path.basename(file)} ends before its plugin list`);
        const formVersion = first[0];
        const infoSize = first.readUInt32LE(1);
        // The full plugins' list is infoSize bytes long; the light plugins' follows it: at most 4,096 names of 260.
        const data = body(5 + infoSize + 2 + (formVersion >= 78 ? 4096 * 262 : 0));
        let p = 5;
        const plugins: string[] = [];
        const count = data[p++];
        for (let i = 0; i < count; i++) {
            let name: string;
            [name, p] = wstring(data, p);
            plugins.push(name);
        }
        const light: string[] = [];
        if (formVersion >= 78 && p + 2 <= data.length) {
            const lightCount = data.readUInt16LE(p);
            p += 2;
            for (let i = 0; i < lightCount && p + 2 <= data.length; i++) {
                let name: string;
                [name, p] = wstring(data, p);
                light.push(name);
            }
        }
        return { character, level, location, gameDate, race, plugins, light };
    } finally {
        fs.closeSync(fd);
    }
}
