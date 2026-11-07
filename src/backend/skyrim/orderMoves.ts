/**
 * Moving a plugin in the load order, or a mod in the mod order — the fix for a conflict the user decides by order. The
 * lines are each manager's own files (plugins.txt, loadorder.txt, modlist.txt); these are the rules both keep:
 *
 * - **Later wins.** A plugin `after` another loads after it, and its version of a record they share is the game's; a mod
 *   `after` another — below it in Mod Organizer's list, above it in modlist.txt — gives the game its files.
 * - **Masters first.** A plugin loads after every master it names, and before every plugin that names it as a master.
 * - **Masters before plain plugins.** The game loads every plugin flagged master — an .esm, an .esl, an ESM-flagged .esp
 *   — before every plain one, whatever the files say: a move across that line would change nothing in game.
 * - **The game's own plugins** — Skyrim.esm, its DLCs and the Creation Club plugins Skyrim.ccc names — load first, in
 *   their own order, and are not moved.
 */

export type Where = 'before' | 'after';

/** A plugin's header as the move needs it: its masters, and whether it is flagged master. */
export interface PluginKind {
    masters: string[];
    master: boolean;
}

/**
 * The line of `isIt` moved to just before or after the line of `isTarget`, in place. False where either is missing, or
 * the line stands there already.
 */
export function moveLine(lines: string[], isIt: (line: string) => boolean, isTarget: (line: string) => boolean, where: Where): boolean {
    const from = lines.findIndex(isIt);
    if (from < 0 || lines.findIndex(isTarget) < 0) return false;
    const [line] = lines.splice(from, 1);
    const target = lines.findIndex(isTarget);
    const at = where === 'before' ? target : target + 1;
    lines.splice(at, 0, line);
    return at !== from;
}

/** The load order with one plugin moved, by name: in any letter case. */
export function movedOrder(order: readonly string[], plugin: string, where: Where, target: string): string[] {
    const lines = [...order];
    const same = (name: string) => (line: string) => line.toLowerCase() === name.toLowerCase();
    moveLine(lines, same(plugin), same(target), where);
    return lines;
}

/**
 * Why a plugin cannot be moved so — the rules above — or undefined where it can. `order` is the load order, every plugin in
 * it; `kinds` each active plugin's header by its lower-case name; `primary` the game's own plugins.
 */
export function moveRefusal(order: readonly string[], plugin: string, where: Where, target: string,
    kinds: ReadonlyMap<string, PluginKind>, primary: ReadonlySet<string>): string | undefined {
    const find = (name: string) => order.find(line => line.toLowerCase() === name.toLowerCase());
    const listed = find(plugin);
    if (!listed) return `${plugin} is in no load order of this profile: listMods names the plugins there are.`;
    const other = find(target);
    if (!other) return `${target} is in no load order of this profile: listMods names the plugins there are.`;
    if (listed.toLowerCase() === other.toLowerCase()) return `${plugin} is the plugin to move: name another to move it ${where}.`;
    if (primary.has(listed.toLowerCase()))
        return `${listed} is one of the game's own plugins, which load first in their own order: move the other plugin instead.`;
    if (primary.has(other.toLowerCase())) {
        return where === 'before'
            ? `${other} is one of the game's own plugins, which load before every other: nothing loads before it.`
            : undefined;
    }
    const kindOf = (name: string) => kinds.get(name.toLowerCase())?.master ?? /\.(esm|esl)$/i.test(name);
    if (kindOf(listed) !== kindOf(other)) {
        const [master, plain] = kindOf(listed) ? [listed, other] : [other, listed];
        return `${master} is flagged master and ${plain} is not, and the game loads every master before every plain plugin, ` +
            'whatever the order: the move would change nothing in game.';
    }
    const moved = movedOrder(order, listed, where, other);
    const position = new Map(moved.map((name, at) => [name.toLowerCase(), at]));
    const at = position.get(listed.toLowerCase())!;
    for (const master of kinds.get(listed.toLowerCase())?.masters ?? []) {
        const masterAt = position.get(master.toLowerCase());
        if (masterAt !== undefined && masterAt > at)
            return `${listed} names ${master} as a master, and a plugin loads after its masters: moved ${where} ${other}, it would load before it.`;
    }
    for (const [name, kind] of kinds) {
        if (!kind.masters.some(master => master.toLowerCase() === listed.toLowerCase())) continue;
        const dependentAt = position.get(name);
        if (dependentAt !== undefined && dependentAt < at) {
            const dependent = moved[dependentAt];
            return `${dependent} names ${listed} as a master, and a plugin loads after its masters: moved ${where} ${other}, ` +
                `${listed} would load after it.`;
        }
    }
    return undefined;
}
