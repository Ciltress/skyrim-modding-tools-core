import fs from 'node:fs';
import path from 'node:path';
import { parseIni } from './mo2.js';

/**
 * Where Mod Organizer keeps its global instances: on Windows, ModOrganizer in the local app data folder. Linux has
 * none to offer (Mod Organizer under Proton keeps them inside its prefix), so there a setup asks for the folder.
 */
export function mo2InstancesDir(env: NodeJS.ProcessEnv, platform: NodeJS.Platform = process.platform): string | undefined {
    return platform === 'win32' && env.LOCALAPPDATA ? path.join(env.LOCALAPPDATA, 'ModOrganizer') : undefined;
}

/**
 * Mod Organizer's global instances that manage Skyrim Special Edition: each a folder of its own under MO2's
 * instances folder with a ModOrganizer.ini naming the game. A portable instance can be anywhere, so a setup
 * offers these and takes a pasted folder besides.
 */
export function skyrimInstances(instancesDir: string): string[] {
    let entries: fs.Dirent[];
    try {
        entries = fs.readdirSync(instancesDir, { withFileTypes: true });
    } catch {
        return [];
    }
    return entries
        .filter(entry => entry.isDirectory())
        .map(entry => path.join(instancesDir, entry.name))
        .filter(dir => {
            try {
                return parseIni(fs.readFileSync(path.join(dir, 'ModOrganizer.ini'), 'utf8')).get('General')?.get('gameName') === 'Skyrim Special Edition';
            } catch {
                return false;
            }
        })
        .sort((a, b) => a.localeCompare(b));
}
