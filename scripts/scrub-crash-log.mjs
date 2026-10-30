#!/usr/bin/env node
/**
 * Copies Crash Logger SSE logs into the test fixtures, scrubbed of what says whose PC made them:
 * the Windows user's name in paths, the player character's name, and the hardware in SYSTEM SPECS
 * (the GPU's maker stays — rules tell an Nvidia driver crash from an AMD one by it — and so do the
 * memory figures, which the memory rules read).
 *
 *   node scripts/scrub-crash-log.mjs <log>... [--player <name>] [--out <folder>]
 *
 * The default folder is src/__tests__/fixtures/crash-logs; a file keeps its name.
 */
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const option = name => {
    const at = args.indexOf(name);
    if (at < 0) return undefined;
    const [, value] = args.splice(at, 2);
    return value;
};
const player = option('--player');
const out = option('--out') ?? path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'src', '__tests__', 'fixtures', 'crash-logs');
if (args.length === 0) {
    console.error('Usage: node scripts/scrub-crash-log.mjs <log>... [--player <name>] [--out <folder>]');
    process.exit(2);
}

const user = os.userInfo().username;
const escape = text => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

export function scrub(text) {
    let lines = text.split(/\r?\n/);
    let inSpecs = false;
    lines = lines.map(line => {
        if (/^SYSTEM SPECS:/.test(line)) {
            inSpecs = true;
            return line;
        }
        if (inSpecs && !line.startsWith('\t')) inSpecs = false;
        if (!inSpecs) return line;
        const [, key, value] = /^\t([^:]+):\s*(.*)$/.exec(line) ?? [];
        if (!key) return line;
        if (key === 'OS') return `\tOS: ${value.replace(/\s+v[\d.]+$/, '').replace(/ (Home|Pro|Education|Enterprise)$/, '')}`;
        if (key === 'CPU') return `\tCPU: ${value.split(' ')[0]}`;
        if (/^GPU #\d+$/.test(key)) return `\t${key}: ${value.split(' ')[0]}`;
        return line;
    });
    let scrubbed = lines.join('\n');
    scrubbed = scrubbed.replace(new RegExp(`([\\\\/]Users[\\\\/])${escape(user)}(?=[\\\\/])`, 'gi'), '$1user');
    if (player) scrubbed = scrubbed.replace(new RegExp(`"${escape(player)}"`, 'g'), '"Player"');
    return scrubbed;
}

fs.mkdirSync(out, { recursive: true });
for (const file of args) {
    const target = path.join(out, path.basename(file));
    fs.writeFileSync(target, scrub(fs.readFileSync(file, 'utf8')));
    console.log(`${file} -> ${target}`);
}
