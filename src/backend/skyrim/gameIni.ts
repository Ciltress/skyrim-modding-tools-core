/**
 * The game's own INIs — Skyrim.ini, SkyrimPrefs.ini, SkyrimCustom.ini — edited one setting at a time,
 * everything else left as it was: other lines, comments, their order, the file's line ends. The game
 * reads `[Section]` and `key=value` lines, sections and keys in any letter case.
 */
export interface IniEdit {
    text: string;
    /** The value before, when the setting was there. */
    was?: string;
    changed: boolean;
}

export function setIniValue(text: string, section: string, key: string, value: string | null): IniEdit {
    const eol = /\r\n/.test(text) ? '\r\n' : '\n';
    const lines = text.length > 0 ? text.split(/\r?\n/) : [];
    // A file ending in a line break splits into a last empty line, which is that break.
    if (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();

    const header = (line: string) => /^\s*\[([^\]]*)\]\s*$/.exec(line)?.[1].trim();
    const start = lines.findIndex(line => header(line)?.toLowerCase() === section.toLowerCase());
    let end = lines.length;
    if (start >= 0) {
        for (let i = start + 1; i < lines.length; i++) {
            if (header(lines[i]) !== undefined) {
                end = i;
                break;
            }
        }
    }
    const at = start < 0 ? -1 : lines.findIndex((line, i) => i > start && i < end && settingKey(line)?.toLowerCase() === key.toLowerCase());
    const was = at >= 0 ? lines[at].slice(lines[at].indexOf('=') + 1).trim() : undefined;

    if (value === null) {
        if (at < 0) return { text, changed: false };
        lines.splice(at, 1);
    } else if (at >= 0) {
        if (was === value) return { text, was, changed: false };
        lines[at] = `${settingKey(lines[at])}=${value}`;
    } else if (start >= 0) {
        // After the section's last setting, before the empty lines that part it from the next.
        let insert = end;
        while (insert > start + 1 && lines[insert - 1].trim() === '') insert--;
        lines.splice(insert, 0, `${key}=${value}`);
    } else {
        if (lines.length > 0 && lines[lines.length - 1].trim() !== '') lines.push('');
        lines.push(`[${section}]`, `${key}=${value}`);
    }
    return { text: lines.join(eol) + (lines.length > 0 ? eol : ''), ...(was !== undefined ? { was } : {}), changed: true };
}

/** A line's key as written, or undefined for a comment, a header or a line without `=`. */
function settingKey(line: string): string | undefined {
    if (/^\s*[;#[]/.test(line)) return undefined;
    const equals = line.indexOf('=');
    return equals > 0 ? line.slice(0, equals).trim() : undefined;
}
