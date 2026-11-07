import fs from 'node:fs';
import path from 'node:path';

/** No Papyrus log where the game writes them, or none by the name asked for. The message says how to get one. */
export class PapyrusLogNotFoundError extends Error { }

const LOG = /^papyrus\.([0-3])\.log$/i;

/**
 * The Papyrus log asked for in the game's Logs\Script folder: `Papyrus.0.log`, the newest, by default; the game keeps the last
 * four sessions', 0 the newest and 3 the oldest. `log` is a file name or its number, `"1"`.
 */
export function papyrusLogFile(folder: string, log?: string): string {
    const asked = log?.trim();
    const number = asked === undefined || asked === '' ? '0' : /^[0-3]$/.test(asked) ? asked : LOG.exec(asked)?.[1];
    if (number === undefined) {
        throw new PapyrusLogNotFoundError(`'${log}' is no Papyrus log: they are Papyrus.0.log (the newest) to Papyrus.3.log (the oldest), ` +
            'or their numbers, "0" to "3".');
    }
    let names: string[] = [];
    try {
        names = fs.readdirSync(folder);
    } catch {
        // No folder: no log.
    }
    const name = names.find(entry => LOG.exec(entry)?.[1] === number);
    return path.join(folder, name ?? `Papyrus.${number}.log`);
}

/** The game's other Papyrus logs beside it, newest first. */
export function otherPapyrusLogs(folder: string, file: string): string[] {
    try {
        return fs.readdirSync(folder).filter(entry => LOG.test(entry) && path.join(folder, entry) !== file).sort();
    } catch {
        return [];
    }
}
