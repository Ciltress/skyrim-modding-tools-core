/**
 * A heightmap image as a new worldspace's ground (newWorldspace): a PNG — grey or colour, 8 or 16 bits a channel, not
 * interlaced, as World Machine, Gaea or any image editor writes one — or a RAW file of 16-bit little-endian heights (8-bit
 * when its size says so), square. Brighter is higher; the image's top is north.
 */
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

/** The image's heights, each 0 (darkest) to 1 (brightest), row by row from the top. */
export interface Heightmap {
    width: number;
    height: number;
    values: Float64Array;
}

export class HeightmapError extends Error {}

const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

export function readHeightmap(file: string): Heightmap {
    if (!fs.existsSync(file)) throw new HeightmapError(`There is no heightmap at ${file}: give the full path of a PNG or RAW file.`);
    const bytes = fs.readFileSync(file);
    if (bytes.subarray(0, 8).equals(PNG_SIGNATURE)) return decodePng(bytes);
    const extension = path.extname(file).toLowerCase();
    if (extension === '.raw' || extension === '.r16' || extension === '.r8') return decodeRaw(bytes, extension === '.r8' ? 8 : undefined);
    throw new HeightmapError(`${path.basename(file)} is neither a PNG nor a RAW heightmap (.raw, .r16).`);
}

function decodeRaw(bytes: Buffer, bits?: 8): Heightmap {
    const side16 = Math.round(Math.sqrt(bytes.length / 2));
    const side8 = Math.round(Math.sqrt(bytes.length));
    const sixteen = bits !== 8 && side16 * side16 * 2 === bytes.length;
    const side = sixteen ? side16 : side8;
    if (!sixteen && side8 * side8 !== bytes.length) {
        throw new HeightmapError(`A RAW heightmap is square: ${bytes.length} bytes are no square of 8- or 16-bit heights.`);
    }
    const values = new Float64Array(side * side);
    for (let i = 0; i < values.length; i++) values[i] = sixteen ? bytes.readUInt16LE(i * 2) / 65535 : bytes[i] / 255;
    return { width: side, height: side, values };
}

function decodePng(bytes: Buffer): Heightmap {
    let at = 8;
    let width = 0, height = 0, depth = 0, colour = 0, interlace = 0;
    const data: Buffer[] = [];
    while (at + 8 <= bytes.length) {
        const length = bytes.readUInt32BE(at);
        const type = bytes.toString('latin1', at + 4, at + 8);
        const body = bytes.subarray(at + 8, at + 8 + length);
        if (type === 'IHDR') {
            width = body.readUInt32BE(0);
            height = body.readUInt32BE(4);
            [depth, colour, interlace] = [body[8], body[9], body[12]];
        } else if (type === 'IDAT') {
            data.push(body);
        } else if (type === 'IEND') {
            break;
        }
        at += 12 + length;
    }
    const channels = ({ 0: 1, 2: 3, 4: 2, 6: 4 } as Record<number, number>)[colour];
    if (!channels) throw new HeightmapError('The PNG holds a palette: save the heightmap as greyscale (or RGB) instead.');
    if (depth !== 8 && depth !== 16) throw new HeightmapError(`The PNG has ${depth} bits a channel: save the heightmap with 8 or 16.`);
    if (interlace) throw new HeightmapError('The PNG is interlaced: save the heightmap without interlacing.');
    const pixel = channels * depth / 8;
    const stride = width * pixel;
    const raw = zlib.inflateSync(Buffer.concat(data));
    const rows = Buffer.alloc(stride * height);
    // Each row is filtered against the one above it (PNG's filters 0–4).
    for (let y = 0; y < height; y++) {
        const filter = raw[y * (stride + 1)];
        const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
        for (let x = 0; x < stride; x++) {
            const left = x >= pixel ? rows[y * stride + x - pixel] : 0;
            const up = y > 0 ? rows[(y - 1) * stride + x] : 0;
            const corner = x >= pixel && y > 0 ? rows[(y - 1) * stride + x - pixel] : 0;
            let predicted = 0;
            if (filter === 1) predicted = left;
            else if (filter === 2) predicted = up;
            else if (filter === 3) predicted = (left + up) >> 1;
            else if (filter === 4) {
                const p = left + up - corner;
                const [pa, pb, pc] = [Math.abs(p - left), Math.abs(p - up), Math.abs(p - corner)];
                predicted = pa <= pb && pa <= pc ? left : pb <= pc ? up : corner;
            }
            rows[y * stride + x] = (line[x] + predicted) & 0xff;
        }
    }
    // Grey, or the mean of red, green and blue; alpha left out.
    const colours = colour === 2 || colour === 6 ? 3 : 1;
    const top = depth === 16 ? 65535 : 255;
    const values = new Float64Array(width * height);
    for (let i = 0; i < width * height; i++) {
        let sum = 0;
        for (let c = 0; c < colours; c++) {
            const offset = i * pixel + c * depth / 8;
            sum += depth === 16 ? rows.readUInt16BE(offset) : rows[offset];
        }
        values[i] = sum / colours / top;
    }
    return { width, height, values };
}

/**
 * The heightmap stretched over a world of `span` × `span` vertices, rows south to north as the game counts them (the
 * image's bottom row first), each between `lowest` and `highest` game units: bilinear between the image's pixels.
 */
export function worldHeights(map: Heightmap, span: number, lowest: number, highest: number): number[][] {
    const at = (x: number, y: number) => map.values[Math.min(map.height - 1, y) * map.width + Math.min(map.width - 1, x)];
    const rows: number[][] = [];
    for (let row = 0; row < span; row++) {
        const fy = (1 - row / (span - 1)) * (map.height - 1);
        const y0 = Math.floor(fy), ty = fy - y0;
        const line: number[] = [];
        for (let column = 0; column < span; column++) {
            const fx = column / (span - 1) * (map.width - 1);
            const x0 = Math.floor(fx), tx = fx - x0;
            const v = (at(x0, y0) * (1 - tx) + at(x0 + 1, y0) * tx) * (1 - ty) + (at(x0, y0 + 1) * (1 - tx) + at(x0 + 1, y0 + 1) * tx) * ty;
            line.push(Math.round((lowest + v * (highest - lowest)) * 100) / 100);
        }
        rows.push(line);
    }
    return rows;
}
