/**
 * Skyrim's Papyrus log, `My Games\<game>\Logs\Script\Papyrus.0.log` (the newest; 1–3 older), read into entries. The
 * grammar, the same from 2012's game to 2026's (docs/research/papyrus-log-format.md §2):
 * - an entry starts with a 26-character timestamp, `[MM/DD/YYYY - hh:mm:ssAM] `, then its message;
 * - lines with no timestamp belong to the entry before them: an error's `stack:` and its tab-indented frames;
 * - a stack dump is a block of timestamped lines after `Suspended stack count is over our warning threshold, dumping
 *   stacks:`, a `Dumping stack N:` for each stack, its fields one tab in, its frames two;
 * - Papyrus Tweaks NG adds a summary before a dump, `Event: <Script>.<Event>, Frequency: <n>` between two banners.
 */

/** One frame of a stack: `[<self>].<Script>.<Function>() - "<File>.psc" Line <n>`. */
export interface PapyrusFrame {
    /** What the script runs on, as written: `_WetQuest (34000D63)`, ` (00000014)`, `alias X on quest Y (…)`, `None`. */
    self: string;
    /** The FormID inside `self`, eight hex digits, when it has one. */
    formId?: string;
    script: string;
    fn: string;
    /** The source file and line, `"Script.psc" Line 12`, `"<native>" Line ?`, or `(requested call)`. */
    at: string;
}

export interface PapyrusEntry {
    /** The log's line it starts on, from 1. */
    line: number;
    /** As the log writes it: `09/03/2022 - 10:20:52AM`. */
    time: string;
    severity?: 'error' | 'warning' | 'info';
    /** The message, its severity prefix taken off. */
    message: string;
    /** The stack trace written under it, innermost first. */
    stack: PapyrusFrame[];
}

export interface DumpedStack {
    id: string;
    state?: string;
    type?: string;
    frames: PapyrusFrame[];
}

export interface StackDump {
    line: number;
    time: string;
    stacks: DumpedStack[];
    /** Papyrus Tweaks NG's summary before it: each event and how many dumped stacks run it. */
    summary: { event: string; frequency: number }[];
}

export interface PapyrusLog {
    /** `Papyrus log opened (PC)`'s time; a Fallout 4 log writes `(PC-64)`. */
    opened?: string;
    platform?: string;
    /** `Log closed`: written at a normal exit. A log without it ended otherwise, or the game still runs. */
    closed: boolean;
    /** The game's [Papyrus] settings as the header writes them. */
    header: { updateBudgetMs?: number; extraTaskletMs?: number; loadScreenMs?: number; memoryPage?: { min: number; max: number; total: number } };
    /** Saves loaded and made in this session, by the VM's lines. */
    loads: number;
    saves: number;
    entries: PapyrusEntry[];
    dumps: StackDump[];
    lines: number;
}

const STAMP = /^\[(\d{2}\/\d{2}\/\d{4} - \d{2}:\d{2}:\d{2}(?:AM|PM))\] ?(.*)$/;
const SEVERITY = /^(error|warning|info):\s?(.*)$/i;
const FRAME = /^\t*(?:\[(.*)\]|(<unknown self>))\.([\w:]+)\.([\w:]+)\(\) - (.+)$/;
const FORM_ID = /\(([0-9A-Fa-f]{8})\)/;
const DUMP_START = /^(?:Suspended stack count is over our warning threshold|Count of \w+ stacks is over our warning threshold), dumping stacks:/;
const SUMMARY_LINE = /^Event: (.+), Frequency: (\d+)\s*$/;
const BANNER = /^#{5,}Stack Dump Summary (Start|End)#{5,}\s*$/;

export function parseFrame(text: string): PapyrusFrame | undefined {
    const match = FRAME.exec(text.replace(/\s+$/, ''));
    if (!match) return undefined;
    const self = match[1] ?? match[2];
    const formId = FORM_ID.exec(self)?.[1]?.toUpperCase();
    return { self, ...(formId ? { formId } : {}), script: match[3], fn: match[4], at: match[5] };
}

export function parsePapyrusLog(text: string): PapyrusLog {
    const log: PapyrusLog = { closed: false, header: {}, loads: 0, saves: 0, entries: [], dumps: [], lines: 0 };
    const lines = text.split(/\r?\n/);
    log.lines = lines.length;
    let last: PapyrusEntry | undefined;
    let dump: StackDump | undefined;
    let stack: DumpedStack | undefined;
    let summary: { event: string; frequency: number }[] | undefined;
    for (let i = 0; i < lines.length; i++) {
        const raw = lines[i];
        const stamped = STAMP.exec(raw);
        if (!stamped) {
            if (summary && (BANNER.test(raw.trim()) || SUMMARY_LINE.test(raw.trim()))) {
                const item = SUMMARY_LINE.exec(raw.trim());
                if (item) summary.push({ event: item[1], frequency: Number(item[2]) });
                continue;
            }
            // An error's stack: `stack:` and its frames, belonging to the entry before.
            const frame = parseFrame(raw);
            if (frame && last) last.stack.push(frame);
            continue;
        }
        const [, time, rest] = stamped;
        const message = rest;
        // Papyrus Tweaks NG's summary comes before the dump it sums up.
        if (BANNER.test(message.trim())) {
            if (/Start/.test(message)) summary = [];
            continue;
        }
        if (summary && SUMMARY_LINE.test(message.trim())) {
            const item = SUMMARY_LINE.exec(message.trim())!;
            summary.push({ event: item[1], frequency: Number(item[2]) });
            continue;
        }
        if (DUMP_START.test(message)) {
            dump = { line: i + 1, time, stacks: [], summary: summary ?? [] };
            summary = undefined;
            log.dumps.push(dump);
            stack = undefined;
            last = undefined;
            continue;
        }
        if (dump) {
            const dumped = /^Dumping stack (\d+):/.exec(message);
            if (dumped) {
                stack = { id: dumped[1], frames: [] };
                dump.stacks.push(stack);
                continue;
            }
            if (/^\s/.test(message) || (stack && FRAME.test(message))) {
                const field = message.trim();
                const state = /^State: (.+)$/.exec(field);
                const type = /^Type: (.+)$/.exec(field);
                if (stack && state) stack.state = state[1];
                else if (stack && type) stack.type = type[1];
                else if (stack) {
                    const frame = parseFrame(message);
                    if (frame) stack.frames.push(frame);
                }
                continue;
            }
            // The VM freezing for a save can fall inside a dump; anything else ends it.
            if (!/^VM is (freezing|frozen)/.test(message)) {
                dump = undefined;
                stack = undefined;
            }
        }
        const opened = /^Papyrus log opened \(([^)]+)\)/.exec(message);
        if (opened) {
            log.opened = time;
            log.platform = opened[1];
            continue;
        }
        if (/^Log closed\s*$/.test(message)) {
            log.closed = true;
            continue;
        }
        const budget = /^Update budget: ([\d.]+)ms \(Extra tasklet budget: ([\d.]+)ms, Load screen budget: ([\d.]+)ms\)/.exec(message);
        if (budget) {
            log.header.updateBudgetMs = Number(budget[1]);
            log.header.extraTaskletMs = Number(budget[2]);
            log.header.loadScreenMs = Number(budget[3]);
            continue;
        }
        const memory = /^Memory page: (\d+) \(min\) (\d+) \(max\) (\d+) \(max total\)/.exec(message);
        if (memory) {
            log.header.memoryPage = { min: Number(memory[1]), max: Number(memory[2]), total: Number(memory[3]) };
            continue;
        }
        // The VM's own lines around a save or a load: counted, no entries.
        if (/^Loading game\.\.\./.test(message)) log.loads++;
        if (/^Saving game\.\.\./.test(message)) log.saves++;
        if (/^(?:VM is (?:freezing|frozen|thawing)|Reverting game|Loading game|Saving game)\.*\s*$/.test(message)) continue;
        const severity = SEVERITY.exec(message);
        last = {
            line: i + 1, time,
            ...(severity ? { severity: severity[1].toLowerCase() as PapyrusEntry['severity'] } : {}),
            message: (severity ? severity[2] : message).trim(),
            stack: []
        };
        log.entries.push(last);
    }
    return log;
}
