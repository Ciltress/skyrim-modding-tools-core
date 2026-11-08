import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import zlib from 'node:zlib';
import { describe, expect, it } from 'vitest';
import { HeightmapError, readHeightmap, worldHeights } from '../backend/skyrim/heightmap.js';

/** A PNG as an image editor writes one: rows filtered with `filter`, one channel of grey or three of colour. */
function png(width: number, height: number, depth: 8 | 16, colour: 0 | 2, value: (x: number, y: number) => number, filter = 0): Buffer {
    const channels = colour === 2 ? 3 : 1;
    const pixel = channels * depth / 8;
    const stride = width * pixel;
    const plain = Buffer.alloc(stride * height);
    for (let y = 0; y < height; y++)
        for (let x = 0; x < width; x++)
            for (let c = 0; c < channels; c++) {
                const at = y * stride + x * pixel + c * depth / 8;
                const v = Math.round(value(x, y) * (depth === 16 ? 65535 : 255));
                if (depth === 16) plain.writeUInt16BE(v, at); else plain[at] = v;
            }
    // Filter 1 (Sub) or 2 (Up) written as the encoder would, so the decoder has to undo it.
    const raw = Buffer.alloc((stride + 1) * height);
    for (let y = 0; y < height; y++) {
        raw[y * (stride + 1)] = filter;
        for (let x = 0; x < stride; x++) {
            const left = x >= pixel ? plain[y * stride + x - pixel] : 0;
            const up = y > 0 ? plain[(y - 1) * stride + x] : 0;
            const predicted = filter === 1 ? left : filter === 2 ? up : 0;
            raw[y * (stride + 1) + 1 + x] = (plain[y * stride + x] - predicted) & 0xff;
        }
    }
    const chunk = (type: string, body: Buffer) => {
        const head = Buffer.alloc(8);
        head.writeUInt32BE(body.length, 0);
        head.write(type, 4, 'latin1');
        const crc = Buffer.alloc(4);
        crc.writeUInt32BE(zlib.crc32(Buffer.concat([head.subarray(4), body])) >>> 0, 0);
        return Buffer.concat([head, body, crc]);
    };
    const header = Buffer.alloc(13);
    header.writeUInt32BE(width, 0);
    header.writeUInt32BE(height, 4);
    [header[8], header[9]] = [depth, colour];
    return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header),
        chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

const write = (name: string, bytes: Buffer) => {
    const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'heightmap-')), name);
    fs.writeFileSync(file, bytes);
    return file;
};

describe('a heightmap for a new worldspace', () => {
    it('reads a 16-bit grey PNG and an 8-bit colour one, whatever their rows are filtered with', () => {
        const ramp = (x: number) => x / 4;
        for (const filter of [0, 1, 2]) {
            const grey = readHeightmap(write('grey.png', png(5, 3, 16, 0, ramp, filter)));
            expect([grey.width, grey.height]).toEqual([5, 3]);
            expect([...grey.values.slice(0, 5)].map(v => Math.round(v * 100) / 100)).toEqual([0, 0.25, 0.5, 0.75, 1]);
        }
        const colour = readHeightmap(write('colour.png', png(2, 2, 8, 2, (x, y) => (x + y) / 2, 1)));
        expect([...colour.values].map(v => Math.round(v * 10) / 10)).toEqual([0, 0.5, 0.5, 1]);
    });

    it('reads a square RAW of 16-bit heights', () => {
        const raw = Buffer.alloc(2 * 2 * 2);
        [0, 65535, 32768, 0].forEach((v, i) => raw.writeUInt16LE(v, i * 2));
        const map = readHeightmap(write('land.raw', raw));
        expect([map.width, map.height]).toEqual([2, 2]);
        expect(map.values[1]).toBe(1);
        expect(() => readHeightmap(write('odd.raw', Buffer.alloc(7)))).toThrow(HeightmapError);
    });

    it('lays the image over the world with its top to the north: the first row of heights is the image\'s bottom', () => {
        // Bright at the top right: north-east is highest.
        const map = readHeightmap(write('slope.png', png(3, 3, 8, 0, (x, y) => (x + (2 - y)) / 4)));
        const heights = worldHeights(map, 5, 0, 1000);
        expect(heights).toHaveLength(5);
        expect(heights[0][0]).toBe(0);
        expect(heights[4][4]).toBe(1000);
        // Eight bits: 500 is 127.5 of 255, read as 128.
        expect(heights[4][0]).toBeCloseTo(500, -1);
        expect(heights[2][2]).toBeCloseTo(500, -1);
    });

    it('says what is wrong with a file it cannot read', () => {
        expect(() => readHeightmap(path.join(os.tmpdir(), 'no-such-heightmap.png'))).toThrow(/There is no heightmap/);
        expect(() => readHeightmap(write('land.jpg', Buffer.from('not a png')))).toThrow(/neither a PNG nor a RAW/);
    });
});
