import type { PapyrusEntry } from './papyrusLog.js';

/**
 * What the Papyrus log's messages mean, as the modders who know it best sort them (docs/research/papyrus-log-format.md
 * §2.7, §3): Arthmoor's notes for the Unofficial Patch, the Creation Kit wiki, Papyrus Tweaks NG's source for its
 * rewordings. Each message is
 * - `bug`: a script or install problem — minor alone, serious in volume;
 * - `history`: left in the save by a mod removed or changed mid-playthrough — orphaned scripts and their data;
 * - `noise`: harmless, or a known false error: counted, not reported one by one.
 */
export type MessageClass = 'bug' | 'history' | 'noise';

export interface MessageKind {
    id: string;
    class: MessageClass;
    match: RegExp;
    /** The script the message is about, from its text, else its stack's innermost frame. */
    script?: (match: RegExpExecArray, entry: PapyrusEntry) => string | undefined;
    title: string;
    explain: string;
    fix: string;
}

/** A form named before its FormID, `weroad08 (001065F4): …` or ` (0403A06F): …`, taken off the message's front. */
export const FORM_PREFIX = /^(?:([^\s(]*)\s*)?\(([0-9A-Fa-f]{8})\):\s*/;

const innermost = (_match: RegExpExecArray, entry: PapyrusEntry) => entry.stack[0]?.script;

export const MESSAGE_KINDS: readonly MessageKind[] = [
    // --- left in the save: a mod removed or changed mid-playthrough
    {
        id: 'missing-script', class: 'history', match: /^Cannot open store for class "([^"]*)", missing file\?/, script: m => m[1] || undefined,
        title: 'Script file missing',
        explain: 'The game could not find this script\'s compiled .pex. An orphaned script: its mod was uninstalled, or updated to a version without it, ' +
            'and a plugin or the save still names it; or the mod\'s archive is not loaded.',
        fix: 'Check that Scripts\\<name>.pex is in the setup, loose or in the mod\'s archive, and that the mod\'s plugin loads its archive. If the mod ' +
            'was removed mid-playthrough, reinstall it, or accept the line: it is generally harmless.'
    },
    {
        id: 'bind-missing', class: 'history', match: /^Script (\S+) cannot be bound to (.+?) because the script does not exist or is not currently loaded/,
        script: m => m[1],
        title: 'Script file missing',
        explain: 'Papyrus Tweaks NG\'s wording: a record names a script that is not in the setup.',
        fix: 'As for a missing script: reinstall the mod that had it, or accept the line.'
    },
    {
        id: 'save-type-missing', class: 'history', match: /^(?:Unable to get type (\S+) referenced by the save game|Could not find type (\S+) in the type table in save)/,
        script: m => m[1] ?? m[2],
        title: 'The save holds a script no longer installed',
        explain: 'The save has instances of a script type that no longer exists or failed to load: a mod was removed or changed mid-playthrough. ' +
            'Objects of this type are not loaded.',
        fix: 'Reinstall the mod, or start a new game. Cleaning the save with ReSaver (FallrimTools) removes them, but its author warns that removing ' +
            'unattached instances can break a save: back it up first.'
    },
    {
        id: 'save-variable-gone', class: 'noise', match: /^Variable (\S+) on script (\S+) loaded from save not found within the actual object/, script: m => m[2],
        title: 'A saved variable the script no longer has',
        explain: 'The script was changed since the save: the variable is skipped, and is gone from the next save. Harmless.',
        fix: 'Nothing to do.'
    },
    {
        id: 'save-function-differs', class: 'noise', match: /^Function (\w+)\.\S* in stack frame \d+ in stack \d+ (?:differs from|doesn't exist in) the in-game resource files/,
        script: m => m[1],
        title: 'A function changed since the save',
        explain: 'A function was running when the game saved and has changed since: the saved version runs until it ends. Usually harmless.',
        fix: 'Nothing to do.'
    },
    // --- script and install problems
    {
        id: 'base-type-mismatch', class: 'bug', match: /^Unable to bind script (\S+)(?: \(([^)]*)\))? to (.+?) because their base types do not match/, script: m => m[1],
        title: 'Script attached to the wrong kind of record',
        explain: 'The script extends another type than the record it is attached to. The game also writes this when the script is missing.',
        fix: 'Only the mod\'s author can fix a real mismatch; where the script is missing, as for a missing script.'
    },
    {
        id: 'property-wrong-type', class: 'bug', match: /^Property (\S+) on script (\S+) attached to (.+?) cannot be bound because (.+?) is not the right type/, script: m => m[2],
        title: 'A script property filled with the wrong record',
        explain: 'The record a plugin fills this property with is not of the property\'s type: `<NULL form>` is a record the author deleted, ' +
            'an alias of the wrong type is usually another mod overriding the quest.',
        fix: 'Check which plugin last changes the record the script is attached to (readRecord with allVersions): a compatibility patch, or the ' +
            'author fixes the property.'
    },
    {
        id: 'parent-missing', class: 'bug', match: /^Unable to link "([^"]+)" - the parent of "([^"]+)"/, script: m => m[2],
        title: 'A script whose parent script is missing',
        explain: 'The script extends a script that is not installed: a framework the mod needs, such as SkyUI\'s SKI_ConfigBase.',
        fix: 'Install the mod the missing parent script comes with: the mod\'s requirements name it.'
    },
    {
        id: 'link-types', class: 'bug', match: /^Unable to link types associated with function "([^"]+)" in state "([^"]*)" on object "([^"]+)"/, script: m => m[3],
        title: 'A script whose function names scripts that are missing',
        explain: 'The function\'s signature names script types that cannot be found: a missing or mismatched dependency, or overridden DLC scripts.',
        fix: 'Restore the missing dependency scripts; look for a mod that overrides the game\'s or a DLC\'s scripts with other versions.'
    },
    {
        id: 'native-missing', class: 'bug', match: /^Native (?:static )?function (\S+)(?: in empty state)? could find no matching (?:static )?function on linked type (\w+)/,
        script: m => m[2],
        title: 'A native function no SKSE plugin provides',
        explain: 'A script declares a native function that no loaded engine extension has: SKSE, or the SKSE plugin of that script type, is ' +
            'missing, broken or outdated.',
        fix: 'Reinstall SKSE with its script files; install or update the SKSE plugin the script type belongs to, for this game version ' +
            '(checkGameVersion).'
    },
    {
        id: 'none-call', class: 'bug', match: /^Cannot call (\w+)\(\) on a None object/, script: innermost,
        title: 'A call on nothing (None)',
        explain: 'A function was called on a variable holding nothing, and skipped: usually a script property the plugin did not fill. One is ' +
            'minor; a flood is a broken mod.',
        fix: 'The mod\'s author must fill the property; the data is in the save, so a new game is often needed after the fix.'
    },
    {
        id: 'static-missing', class: 'bug', match: /^Static function (\w+) not found on object (\w+)/, script: innermost,
        title: 'A function of a framework that is missing or outdated',
        explain: 'A script calls a global function the named script does not have: a framework it needs (PapyrusUtil, JContainers, a ' +
            'behaviour engine\'s scripts) is missing or another version.',
        fix: 'Install or update the framework the named script comes with, as the calling mod\'s requirements say.'
    },
    {
        id: 'bad-arguments', class: 'bug',
        match: /^(?:Incorrect number of arguments passed|Type mismatch for argument|Passing NONE to non-object argument|Function \S+ received (?:incompatible arguments|NONE to non-object argument))/,
        script: innermost,
        title: 'A call with the wrong arguments',
        explain: 'A script calls a function with arguments it does not take: compiled against an older version of the function, or an event ' +
            'handler with the wrong parameters.',
        fix: 'Update the mod, or the framework whose function it calls; the author recompiles.'
    },
    {
        id: 'none-array', class: 'bug', match: /^Cannot access an element of a None array|^Array index -?\d+ is out of range|^Cannot divide by zero/, script: innermost,
        title: 'A script error in its own code',
        explain: 'An array that is nothing or too short, or a division by zero: a bug in the script.',
        fix: 'Report it to the mod\'s author with the stack.'
    },
    {
        id: 'recursion', class: 'bug', match: /^StackFrameOverFlow exception|^Stack too deep/, script: innermost,
        title: 'A script calling itself without end',
        explain: 'Papyrus Tweaks NG or Recursion Monitor broke off a recursion over 1000 calls deep: the script is broken, and costs frame rate.',
        fix: 'Report it to the mod\'s author; disable the mod if it lags the game.'
    },
    {
        id: 'mcm-misuse', class: 'bug', match: /^\[(\w+) <[^>]*>\] ERROR: /, script: m => m[1],
        title: 'A mod\'s menu misusing SkyUI',
        explain: 'Written by SkyUI\'s MCM library about another mod\'s menu.',
        fix: 'Report it to that mod\'s author, not SkyUI\'s.'
    },
    // --- harmless, or known false errors
    { id: 'property-gone', class: 'noise', match: /^Property (\S+) on script (\S+) attached to (.+?) cannot be initialized because the script no longer contains that property/, script: m => m[2], title: 'A property the script no longer has', explain: 'Harmless: the plugin fills a property a recompiled script dropped.', fix: 'Nothing to do.' },
    { id: 'no-native-object', class: 'noise', match: /^Unable to call (\w+) - no native object bound to the script object/, script: innermost, title: 'A call on an object already gone', explain: 'Typical of a magic effect that ended. Ignore unless it floods.', fix: 'Nothing to do.' },
    { id: 'assign-none', class: 'noise', match: /^Assigning None to a non-object variable/, script: innermost, title: 'None assigned after a failed call', explain: 'Follows the real error just before it.', fix: 'Nothing to do.' },
    { id: 'animation-variable', class: 'noise', match: /cannot (?:fetch|find) variable named/, script: innermost, title: 'An animation variable the actor\'s behaviour lacks', explain: 'Often a false error; a flood points at the behaviour setup.', fix: 'Regenerate behaviours (Nemesis, Pandora) if it floods.' },
    { id: 'no-3d', class: 'noise', match: /has no 3d|Object reference has no 3D|no parent cell or no 3D/i, script: innermost, title: 'An object not loaded', explain: 'A false error, above all in OnLoad.', fix: 'Nothing to do.' },
    { id: 'file-not-loaded', class: 'noise', match: /^File "([^"]+)" does not exist or is not currently loaded/, script: innermost, title: 'A check for a plugin not installed', explain: 'A mod looking for an optional plugin (Game.GetFormFromFile). Not an error.', fix: 'Nothing to do.' },
    { id: 'misc-noise', class: 'noise', match: /^Script profiling is disabled|Failed to send event|attempting to start event scoped quest outside of story manager|cannot start scene because its parent quest was not running|did not have a valid Animation Graph Manager/, script: innermost, title: 'Known harmless messages', explain: 'Sometimes false alarms.', fix: 'Nothing to do unless it repeats every few seconds.' }
];

/**
 * The scripts whose errors the game writes with no mod installed: a log of the test instance — the game 1.7.104 with all its
 * Anniversary Edition content, SKSE and no mod with scripts — named these on every start (2026-11-08). The DLC's test scripts
 * and Creation Club quests' fragments that ship without their .pex, aliases and properties bound to the wrong type in the
 * game's own plugins. Their lines are counted with the noise: nothing in the user's setup can fix them, and ranked as
 * problems they put the game's own first in a log whose real problem was a mod's (the second log of that day).
 */
const GAME_OWN_SCRIPTS = new Set([
    'DLC1TestPhilAtronach', 'DLC1testPhilVortexSCRIPT', 'dlc1testPhilVortexTrigSCRIPT', 'DLC2BenthicLurkerFXSCRIPT', 'DLC2WaterScript',
    'DLCDwarvenPuzzleWinterFreezing', 'dlcdwarvenpuzzlecoolantreleaselever', 'DefaultDebugMagicEffect', 'defaultIgnoreFriendlyHits',
    'defaultcounter', 'ccBGSSSE038_AssassinEnableScript', 'ccBGSSSE067_ScourgeDremoraScript', 'ccKRTSSE001_zQuestScen_05000F40',
    'KRT_PF_ccKRTSSE001_WhiterunTe_0500081C', 'SSE_QF_ccBGSSSE051_MiscQuestA_0500083F', 'SSE_SF_ccFFBSSE001_QuestScene_02000932',
    'SSE_SF_ccFFBSSE001_QuestScene_02005EB3'
].map(script => script.toLowerCase()));

/**
 * Whether an entry of a kind, blaming a script, is one the game writes with no mod: a script of GAME_OWN_SCRIPTS, or a native
 * function of Game's for the consoles' Amiibo and NFC, which the PC's game never binds — six such lines in every log.
 */
export function gameOwn(kind: string, script: string | undefined, message: string): boolean {
    if (kind === 'native-missing') return script === 'Game' && /function \w*(?:Amiibo|NFC)\w* /.test(message);
    return !!script && GAME_OWN_SCRIPTS.has(script.toLowerCase());
}

/** An entry's kind, its message taken off a form named before it; undefined for a trace or a message no kind has. */
export function kindOf(entry: PapyrusEntry): { kind: MessageKind; match: RegExpExecArray; form?: { name?: string; formId: string } } | undefined {
    const prefix = FORM_PREFIX.exec(entry.message);
    const message = prefix ? entry.message.slice(prefix[0].length) : entry.message;
    for (const kind of MESSAGE_KINDS) {
        const match = kind.match.exec(message);
        if (match) return { kind, match, ...(prefix ? { form: { ...(prefix[1] ? { name: prefix[1] } : {}), formId: prefix[2].toUpperCase() } } : {}) };
    }
    return undefined;
}
