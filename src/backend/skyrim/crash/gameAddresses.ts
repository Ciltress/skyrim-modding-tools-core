/**
 * Places in the game's code that Phostwood's analyzer knows crashes by, under the offsets it gives them —
 * offsets into SkyrimSE.exe 1.6.1170, which every other game version moves. Each is kept here as the
 * Address Library ID of the function it is in and the distance into it, which stay put from version to
 * version, with the offset that gives in each version Address Library v13 covers (1.6.1170, 1.7.99,
 * 1.7.104; read from its versionlib-*.bin files). Crash Logger writes both: the offset on the first line
 * and in each frame, and `-> ID+0xdistance` after a frame's symbol.
 *
 * A function an update rewrote may crash at another distance into it: then the code is not recognised,
 * which errs toward silence. 132BEF's function is gone from 1.7.
 */
export interface GameAddress {
    id: number;
    delta: number;
    /** The offset into SkyrimSE.exe, by game version, without leading zeros. */
    offsets: Readonly<Record<string, string>>;
}

export const GAME_ADDRESSES = {
    '0198090': { id: 13252, delta: 0xF0, offsets: { '1.6.1170': '198090', '1.7.99': '19D620', '1.7.104': '19D620' } },
    '04D26E7': { id: 30658, delta: 0x1A7, offsets: { '1.6.1170': '4D26E7', '1.7.99': '4D9DD7', '1.7.104': '4D9DD7' } },
    '0CB748E': { id: 67750, delta: 0x9E, offsets: { '1.6.1170': 'CB748E', '1.7.99': 'CD13AE', '1.7.104': 'CD160E' } },
    '0CD9F7C': { id: 68655, delta: 0x17C, offsets: { '1.6.1170': 'CD9F7C', '1.7.99': 'CFA56C', '1.7.104': 'CFA7CC' } },
    '12F4797': { id: 94371, delta: 0x847, offsets: { '1.6.1170': '12F4797', '1.7.99': '1314657', '1.7.104': '13148B7' } },
    '12F5590': { id: 94371, delta: 0x1640, offsets: { '1.6.1170': '12F5590', '1.7.99': '1315450', '1.7.104': '13156B0' } },
    '12FDD00': { id: 448206, delta: 0xD0, offsets: { '1.6.1170': '12FDD00', '1.7.99': '131BE90', '1.7.104': '131C0F0' } },
    '132BEF': { id: 441167, delta: 0xFF, offsets: { '1.6.1170': '132BEF' } },
    '1AD5B40': { id: 256257, delta: 0x0, offsets: { '1.6.1170': '1AD5B40', '1.7.99': '1B5F6F0', '1.7.104': '1B5F710' } },
    '5999C7': { id: 442102, delta: 0x3B7, offsets: { '1.6.1170': '5999C7', '1.7.99': '5A6D27', '1.7.104': '5A6D57' } },
    '7428B1': { id: 40575, delta: 0x131, offsets: { '1.6.1170': '7428B1', '1.7.99': '7552B1', '1.7.104': '755511' } },
    '8BDA97': { id: 49800, delta: 0x57, offsets: { '1.6.1170': '8BDA97', '1.7.99': '8D2E27', '1.7.104': '8D3087' } },
    'A0D789': { id: 55503, delta: 0x39, offsets: { '1.6.1170': 'A0D789', '1.7.99': 'A25849', '1.7.104': 'A25AA9' } },
    'B02235': { id: 59972, delta: 0x25, offsets: { '1.6.1170': 'B02235', '1.7.99': 'B1C3A5', '1.7.104': 'B1C605' } },
    'C1315C': { id: 64911, delta: 0x13C, offsets: { '1.6.1170': 'C1315C', '1.7.99': 'C2D67C', '1.7.104': 'C2D8DC' } },
    'D02C2C': { id: 69666, delta: 0x26C, offsets: { '1.6.1170': 'D02C2C', '1.7.99': 'EC738C', '1.7.104': 'EC75EC' } },
    'D2B923': { id: 70631, delta: 0x23, offsets: { '1.6.1170': 'D2B923', '1.7.99': 'EF00C3', '1.7.104': 'EF0323' } },
    'D6DDDA': { id: 72119, delta: 0x1DEA, offsets: { '1.6.1170': 'D6DDDA', '1.7.99': 'F3286A', '1.7.104': 'F32ACA' } }
} as const satisfies Record<string, GameAddress>;

export type GameAddressCode = keyof typeof GAME_ADDRESSES;
