import fs from 'node:fs';
import path from 'node:path';
import { parseCrashLog, type CrashLog } from './crashLog.js';

/**
 * Crash Logger SSE's logs, in the SKSE folder of My Games — `crash-2026-10-30-14-02-11.log` — which every
 * setup of the game on the computer shares: a Wabbajack list's logs lie beside the test instance's. A log
 * is this setup's when its PROCESS INFO names this setup's game folder; the others are only counted.
 */
export interface CrashLogFile {
    name: string;
    file: string;
}

export interface ChosenLog {
    file: string;
    log: CrashLog;
    /** True: this setup's game wrote it; false: another's; undefined: the log stops before saying. */
    thisSetup?: boolean;
    /** Why this log, where it is not the one asked for: the minute asked for had none. */
    instead?: string;
    counts: { thisSetup: number; otherSetups: number; unknown: number };
}

export class CrashLogNotFoundError extends Error { }

const CRASH_LOG = /^crash-\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}\.log$/i;

/** The folder's crash logs, newest first — their names are their times. */
export function listCrashLogs(folder: string): CrashLogFile[] {
    let names: string[];
    try {
        names = fs.readdirSync(folder);
    } catch {
        return [];
    }
    return names.filter(name => CRASH_LOG.test(name)).sort().reverse().map(name => ({ name, file: path.join(folder, name) }));
}

/**
 * The game folder a log's game ran from — the folder of the executable on PROCESS INFO's Command Line, or
 * else its Working Directory — as the game saw it: a Windows path, `Z:\home\…` under Proton.
 */
export function gameFolderOf(log: CrashLog): string | undefined {
    const command = log.processInfo['Command Line']?.trim();
    const exe = command ? (/^"([^"]+)"/.exec(command)?.[1] ?? command.split(/\s+/)[0]) : undefined;
    if (exe && /SkyrimSE\.exe$|SkyrimVR\.exe$/i.test(exe)) return path.win32.dirname(exe.replace(/\\{2,}/g, '\\'));
    return log.processInfo['Working Directory']?.trim() || undefined;
}

/**
 * A time as a chat's message stamp writes it, `[Sat 2026-10-31 09:26 GMT+1]`, without its brackets, its weekday and its
 * time zone: a model sent its prompt's stamp, weekday first, and was refused (eval round 100's crash-mesh #3). The zone is
 * left out, not counted: a log is named in the game's local time, and so is the stamp.
 */
function bareTime(given: string): string {
    return given.replace(/^\[|\]$/g, '').trim()
        .replace(/^(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*,?\s+/i, '')
        .replace(/\s*(?:gmt|utc)(?:[+-]\d{1,2}(?::?\d{2})?)?$/i, '')
        .replace(/(?<=\d:\d{2}(?::\d{2})?)(?:z|[+-]\d{2}:?\d{2})$/i, '');
}

/**
 * Picks the log to read: the one `wanted` names — its file name, or a time it starts with, `2026-10-30`
 * or `2026-10-30 14:02` — else the newest of this setup's. Among several a time matches, this setup's
 * newest wins. A time down to the hour that no log has reads the newest before it that day: asked about a crash
 * "just now", a model sends the time of asking (eval round 90: three of ten crash runs).
 *
 * @param isThisSetup  Whether a game folder a log names, as the game saw it, is this setup's.
 */
export function chooseCrashLog(folder: string, wanted: string | undefined, isThisSetup: (gameFolder: string) => boolean): ChosenLog {
    const files = listCrashLogs(folder);
    const read = new Map<string, CrashLog>();
    const parsed = (file: CrashLogFile) => {
        let log = read.get(file.file);
        if (!log) {
            log = parseCrashLog(fs.readFileSync(file.file, 'utf8'));
            read.set(file.file, log);
        }
        return log;
    };
    const setupOf = (file: CrashLogFile): boolean | undefined => {
        const folder = gameFolderOf(parsed(file));
        return folder === undefined ? undefined : isThisSetup(folder);
    };

    const counts = { thisSetup: 0, otherSetups: 0, unknown: 0 };
    for (const file of files) {
        const setup = setupOf(file);
        if (setup === true) counts.thisSetup++;
        else if (setup === false) counts.otherSetups++;
        else counts.unknown++;
    }

    let chosen: CrashLogFile | undefined;
    let instead: string | undefined;
    if (wanted?.trim()) {
        const given = wanted.trim();
        const byName = files.find(file => file.name.toLowerCase() === given.toLowerCase() || file.name.toLowerCase() === `${given.toLowerCase()}.log`);
        if (byName) {
            chosen = byName;
        } else {
            const time = `crash-${bareTime(given).replace(/^crash-/i, '').replace(/\.log$/i, '').replace(/[\sT:_.]+/g, '-').toLowerCase()}`;
            const matching = files.filter(file => file.name.toLowerCase().startsWith(time));
            chosen = matching.find(file => setupOf(file) === true) ?? matching[0];
            const day = /^crash-\d{4}-\d{2}-\d{2}(?=-\d{2})/.exec(time)?.[0];
            if (!chosen && day) {
                // Names are times, so a name before the asked one is a log from before it.
                const before = files.filter(file => file.name.toLowerCase().startsWith(day) && file.name.toLowerCase() < time);
                chosen = before.find(file => setupOf(file) === true) ?? before[0];
                if (chosen) instead = `No crash log is dated '${given}'; this is the newest before it that day, ${chosen.name}.`;
            }
        }
        if (!chosen) {
            throw new CrashLogNotFoundError(`No crash log in ${folder} is named or dated '${given}'. ` +
                (files.length > 0 ? `The newest are ${files.slice(0, 3).map(file => file.name).join(', ')}.` : 'It holds none.'));
        }
    } else {
        chosen = files.find(file => setupOf(file) === true);
        if (!chosen) {
            throw new CrashLogNotFoundError(files.length === 0
                ? `There is no crash log in ${folder}: either the game has not crashed since Crash Logger SSE was installed, ` +
                  'or Crash Logger SSE is not installed — listMods shows whether it is.'
                : `None of the ${files.length} crash logs in ${folder} is this setup's: ${counts.otherSetups} came from another ` +
                  `game folder${counts.unknown ? `, ${counts.unknown} stop before saying which` : ''}. The newest is ${files[0].name}; ` +
                  'give its name as `log` to read it anyway.');
        }
    }
    return {
        file: chosen.file,
        log: parsed(chosen),
        ...(setupOf(chosen) !== undefined ? { thisSetup: setupOf(chosen) } : {}),
        ...(instead ? { instead } : {}),
        counts
    };
}
