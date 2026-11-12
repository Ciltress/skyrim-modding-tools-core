import fs from 'node:fs';
import path from 'node:path';
import { findCaseInsensitive } from './paths.js';

/**
 * The project's records' Papyrus scripts held against their sources and the game's scripts: what builds and compiles
 * without a word, and does nothing in game. Papyrus compiles an event of any name and a script that extends nothing;
 * the game runs neither, and a property nothing fills is None. Eval rounds 125 and 126 (the quest task, 0 of 10 for each
 * model): round 126's quest #4 attached StarterGoldScript and wrote no script; round 125's quest+detailed #2 gave its gold
 * in OnQuestInit, which the game never sends; its #5 filled the property GoldCoins of a script that declared
 * `Form GoldCoins`, a variable.
 */

/** A script a record attaches, with the properties the record fills (AttachedScripts.cs). */
export interface AttachedScript {
    name: string;
    properties: { name: string; kind: string; filled: boolean }[];
}

/** A record of the project with scripts attached: its own, and a quest's aliases'. */
export interface ScriptedRecord {
    file: string;
    editorId: string;
    /** Mutagen's type name: Quest, MagicEffect, Npc, Weapon… */
    type: string;
    scripts: AttachedScript[];
    aliases?: { alias: number; scripts: AttachedScript[] }[];
}

/** A Papyrus source as the checks read it, its comments left out. */
export interface PapyrusSource {
    name: string;
    extends?: string;
    events: string[];
    properties: { name: string; type: string; auto: boolean; valued: boolean }[];
    /** The script's own variables, outside its functions and events, and whether each is given a value there. */
    variables: { name: string; type: string; valued: boolean }[];
    /** The text of its string literals: the events a script registers for by name (RegisterForModEvent). */
    strings: string[];
    /** The names it calls. */
    calls: string[];
    /** The code inside its functions and events, comments and strings left out. */
    body: string;
}

/** The events every script has, which no script declares: Papyrus sends them itself. */
const BUILT_IN_EVENTS = ['oninit', 'onbeginstate', 'onendstate'];

/** The whole first line of events a script on a placed thing most often has, to write where a name of no event was meant as one. */
const EVENT_SIGNATURES: Record<string, string> = {
    onactivate: 'OnActivate(ObjectReference akActionRef)',
    oninit: 'OnInit()',
    onequipped: 'OnEquipped(Actor akActor)',
    onunequipped: 'OnUnequipped(Actor akActor)',
    oncontainerchanged: 'OnContainerChanged(ObjectReference akNewContainer, ObjectReference akOldContainer)',
    ontriggerenter: 'OnTriggerEnter(ObjectReference akActionRef)',
    ontriggerleave: 'OnTriggerLeave(ObjectReference akActionRef)',
    onread: 'OnRead()',
    ondeath: 'OnDeath(Actor akKiller)',
    oncellattach: 'OnCellAttach()',
    onload: 'OnLoad()',
    oneffectstart: 'OnEffectStart(Actor akTarget, Actor akCaster)',
    oneffectfinish: 'OnEffectFinish(Actor akTarget, Actor akCaster)',
    onhit: 'OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, bool abPowerAttack, bool abSneakAttack, bool abBashAttack, bool abHitBlocked)'
};

/** Property types the game gives a value without a fill: 0, false, "". */
const VALUE_TYPES = new Set(['int', 'float', 'bool', 'string']);

/** What a record's script extends to be run on it, by the record's type; none where the checks do not tell. */
function expectedBase(type: string): string | undefined {
    if (type === 'Quest') return 'Quest';
    if (type === 'MagicEffect') return 'ActiveMagicEffect';
    if (type === 'Perk') return 'Perk';
    if (['Npc', 'Weapon', 'Armor', 'Ammunition', 'Book', 'Ingestible', 'Ingredient', 'MiscItem', 'Key', 'SoulGem', 'Scroll',
        'Light', 'Container', 'Door', 'Activator', 'Furniture', 'Flora', 'TalkingActivator', 'AlchemicalApparatus'].includes(type)) {
        return 'ObjectReference';
    }
    return undefined;
}

/** The game's word for a record's type in a message. */
function kindOf(type: string): string {
    return ({ Quest: 'a quest', MagicEffect: 'a magic effect', Perk: 'a perk', Npc: 'an NPC' } as Record<string, string>)[type]
        ?? `a ${type.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()}`;
}

/** Reads a Papyrus source: its name, what it extends, its events, properties, variables, strings and calls. */
export function readPapyrus(text: string): PapyrusSource {
    // Comments out first — `;/ … /;`, `{ … }` and `; …` — strings kept; then lines joined where one ends in `\`.
    const strings: string[] = [];
    const code = text.replace(/"(?:[^"\\\r\n]|\\.)*"|;\/[\s\S]*?\/;|\{[^}]*\}|;[^\r\n]*/g, match => {
        if (!match.startsWith('"')) return match.startsWith(';/') || match.startsWith('{') ? match.replace(/[^\r\n]/g, ' ') : '';
        strings.push(match.slice(1, -1));
        return '""';
    }).replace(/\\[ \t]*\r?\n/g, ' ');
    const lines = code.split(/\r?\n/);
    const head = lines.map(line => /^[ \t]*ScriptName[ \t]+(\w+)(?:[ \t]+extends[ \t]+(\w+))?/i.exec(line)).find(Boolean);
    const events: string[] = [];
    const properties: PapyrusSource['properties'] = [];
    const variables: PapyrusSource['variables'] = [];
    const calls = new Set<string>();
    const body: string[] = [];
    let depth = 0;
    for (const line of lines) {
        const event = /^[ \t]*Event[ \t]+(\w+)[ \t]*\(/i.exec(line);
        const fn = /^[ \t]*(?:\w+(?:\[\])?[ \t]+)?Function[ \t]+(\w+)[ \t]*\(/i.exec(line);
        if (event) events.push(event[1]);
        const property = /^[ \t]*(\w+(?:\[\])?)[ \t]+Property[ \t]+(\w+)(.*)$/i.exec(line);
        if (property) {
            properties.push({
                name: property[2], type: property[1],
                auto: /\bAuto(?:ReadOnly)?\b/i.test(property[3]), valued: /=/.test(property[3])
            });
        } else if (depth === 0 && !event && !fn) {
            const variable = /^[ \t]*(\w+(?:\[\])?)[ \t]+(\w+)[ \t]*(=.*)?$/.exec(line);
            if (variable && !/^(scriptname|import|state|auto|endstate|return|if|else|elseif|endif|while|endwhile|endproperty|endfunction|endevent)$/i.test(variable[1])) {
                variables.push({ name: variable[2], type: variable[1], valued: !!variable[3] });
            }
        }
        if (depth > 0) {
            body.push(line);
            for (const call of line.matchAll(/(\w+)[ \t]*\(/g)) calls.add(call[1].toLowerCase());
        }
        if ((event || fn) && !/\bnative\b/i.test(line)) depth++;
        if (/^[ \t]*End(?:Event|Function)\b/i.test(line)) depth = Math.max(0, depth - 1);
    }
    return { name: head?.[1] ?? '', extends: head?.[2], events, properties, variables, strings, calls: [...calls], body: body.join('\n') };
}

/** Where the checks read sources: the project's scripts folder first, then each folder the compiler imports. */
export class PapyrusSources {
    private readonly read = new Map<string, PapyrusSource | null>();

    constructor(private readonly folders: readonly string[]) { }

    /** A script's source by its name, from the first folder that has it; null where none does. */
    find(name: string): PapyrusSource | null {
        const key = name.toLowerCase();
        if (this.read.has(key)) return this.read.get(key)!;
        let source: PapyrusSource | null = null;
        for (const folder of this.folders) {
            const file = findCaseInsensitive(folder, `${name}.psc`);
            if (file && fs.statSync(file).isFile()) { source = readPapyrus(fs.readFileSync(file, 'utf8')); break; }
        }
        this.read.set(key, source);
        return source;
    }

    /**
     * What a source extends, in order, each with its source: up to the first whose source no folder has, which is
     * named with `null`.
     */
    chain(source: PapyrusSource): { name: string; source: PapyrusSource | null }[] {
        const chain: { name: string; source: PapyrusSource | null }[] = [];
        for (let parent = source.extends; parent && chain.length < 32 && !chain.some(c => c.name.toLowerCase() === parent!.toLowerCase());) {
            const found = this.find(parent);
            chain.push({ name: parent, source: found });
            if (!found) break;
            parent = found.extends;
        }
        return chain;
    }
}

/** Levenshtein's distance, case-blind: the event meant, for one a letter or a word off. */
function distance(a: string, b: string): number {
    const x = a.toLowerCase(), y = b.toLowerCase();
    let row = Array.from({ length: y.length + 1 }, (_, i) => i);
    for (let i = 1; i <= x.length; i++) {
        const next = [i];
        for (let j = 1; j <= y.length; j++) next[j] = Math.min(row[j] + 1, next[j - 1] + 1, row[j - 1] + (x[i - 1] === y[j - 1] ? 0 : 1));
        row = next;
    }
    return row[y.length];
}

export interface ScriptCheckContext {
    /** The project's scripts folder, where its sources are. */
    scriptsDir: string;
    sources: PapyrusSources;
    /** Whether the game has a compiled script of this name: in a mod, loose, or in an archive it loads. */
    gameHas: (name: string) => boolean;
    /** The project's record files' text: a script any of them names — a fragment's, a property's type — is in use. */
    recordTexts?: string;
}

/**
 * The warnings for the project's scripted records: a script the game does not have, a script that does not extend what
 * its record runs, an event the game never sends it, a variable the record fills as a property, and a property it declares
 * that nothing fills. A script is checked against its record only where its source is the project's. And a script of the
 * project's that nothing runs: no record attaches it — none of the project's record files names it — and no script of the
 * project's extends or calls it (round 126's quest+detailed #3 wrote one, and a quest with no script attached).
 */
export function scriptWarnings(records: readonly ScriptedRecord[], context: ScriptCheckContext): string[] {
    const { scriptsDir, sources, gameHas } = context;
    const warnings: string[] = [];
    const ownSource = (name: string) => {
        const file = findCaseInsensitive(scriptsDir, `${name}.psc`);
        return file ? readPapyrus(fs.readFileSync(file, 'utf8')) : null;
    };
    const scriptFile = (name: string) => path.join(scriptsDir, `${name}.psc`);
    const checkedScripts = new Set<string>();

    const projectScripts = fs.existsSync(scriptsDir)
        ? fs.readdirSync(scriptsDir).filter(f => f.toLowerCase().endsWith('.psc'))
            .map(f => ({ file: path.join(scriptsDir, f), text: fs.readFileSync(path.join(scriptsDir, f), 'utf8') }))
        : [];
    const word = (name: string) => new RegExp(`(^|[^\\w])${name}([^\\w]|$)`, 'i');
    for (const { file, text } of projectScripts) {
        const name = readPapyrus(text).name || path.basename(file, path.extname(file));
        const others = projectScripts.filter(s => s.file !== file);
        if (records.some(r => [...r.scripts, ...(r.aliases ?? []).flatMap(a => a.scripts)].some(s => s.name.toLowerCase() === name.toLowerCase()))) continue;
        if (word(name).test(context.recordTexts ?? '') || others.some(s => word(name).test(s.text))) continue;
        warnings.push(`${file}: ${name} is attached to no record of this project, and no script of it extends or calls ${name}, so ` +
            `the game never runs it. Attach it to the record that runs it, in that record's file — \`VirtualMachineAdapter:\` at the ` +
            `margin, \`  Scripts:\` below it, \`  - Name: ${name}\` below that, then \`    Properties:\` with each property it ` +
            'declares and the record it stands for — and build again.');
    }

    for (const record of records) {
        const attached = [
            ...record.scripts.map(script => ({ script, base: expectedBase(record.type), on: `${record.editorId}, ${kindOf(record.type)},` })),
            ...(record.aliases ?? []).flatMap(alias => alias.scripts.map(script => ({
                script, base: 'Alias', on: `${record.editorId}'s alias ${alias.alias}`
            })))
        ];
        for (const { script, base, on } of attached) {
            const own = ownSource(script.name);
            if (!own) {
                if (!gameHas(script.name)) {
                    warnings.push(`${record.file}: ${on} has the script ${script.name} attached, which neither this project nor the ` +
                        `game has — no ${scriptFile(script.name)}, and no Scripts\\${script.name}.pex in a mod or an archive the game ` +
                        `loads — so it does nothing in game. Write its source there` +
                        (base && base !== 'Alias' ? `, beginning \`ScriptName ${script.name} extends ${base}\`` : '') +
                        ', and build again: the build compiles it.');
                }
                continue;
            }
            const chain = sources.chain(own);
            const known = chain.every(c => c.source);
            const fixHead = `ScriptName ${script.name} extends ${base}`;

            if (!checkedScripts.has(script.name.toLowerCase())) {
                checkedScripts.add(script.name.toLowerCase());
                // Nothing but its ScriptName line and comments: eval round 128's quest #1 attached two such scripts, and its build
                // was a plain success.
                if (own.events.length === 0 && !own.body.trim() && own.properties.length === 0 && own.variables.length === 0) {
                    warnings.push(`${scriptFile(script.name)}: ${script.name} has no code — no event, no function, no property, nothing ` +
                        `but its ScriptName line — so it does nothing in game` +
                        (base === 'Quest'
                            ? ': a quest\'s script does its work in `Event OnInit()`, which runs when the quest starts, with the records it ' +
                              'uses as properties the quest fills — the skyrim-modding-quests-and-scripts skill writes one whole'
                            : ': write what it does in an event the game sends it') +
                        ', and build again.');
                }
                // What it extends: nothing, or a chain that never reaches what its record runs.
                if (base && known && !chain.some(c => c.name.toLowerCase() === base.toLowerCase() || (base === 'Alias' && /^(reference|location)alias$/i.test(c.name)))) {
                    warnings.push(`${scriptFile(script.name)}: ${script.name} ${own.extends ? `extends ${own.extends}, which is no ${base}` : 'extends nothing'}, ` +
                        `and the game runs a script on ${on} only if it extends ${base === 'Alias' ? 'ReferenceAlias or LocationAlias' : base}: ` +
                        `write its first line \`${base === 'Alias' ? `ScriptName ${script.name} extends ReferenceAlias` : fixHead}\`, and build again.`);
                } else if (known && own.extends) {
                    // Each event the game sends: those its parents declare, and Papyrus's own.
                    const declared = new Set([...BUILT_IN_EVENTS, ...chain.flatMap(c => c.source!.events.map(e => e.toLowerCase()))]);
                    const named = new Set(own.strings.map(s => s.toLowerCase()));
                    for (const event of own.events) {
                        const key = event.toLowerCase();
                        if (declared.has(key) || named.has(key) || own.calls.includes(key)) continue;
                        const offered = [...declared].map(e => ({ e, d: distance(e, key) })).sort((a, b) => a.d - b.d)[0];
                        // Or the declared event whose verb the name holds, a word put in front of it: OnObjectActivate is OnActivate
                        // — eval round 151's activator+detailed #3 was told only which scripts declare events, and answered it unfixed.
                        const holding = [...declared].filter(e => e.length > 4 && key.endsWith(e.slice(2)) && key !== e).sort((a, b) => b.length - a.length)[0];
                        const meant = /init/i.test(event) ? 'OnInit'
                            : offered && offered.d <= Math.max(2, Math.floor(key.length / 4)) || holding
                                ? [...chain.flatMap(c => c.source!.events), ...['OnInit', 'OnBeginState', 'OnEndState']]
                                    .find(e => e.toLowerCase() === (offered && offered.d <= Math.max(2, Math.floor(key.length / 4)) ? offered.e : holding))
                                : undefined;
                        const signature = meant ? EVENT_SIGNATURES[meant.toLowerCase()] : undefined;
                        warnings.push(`${scriptFile(script.name)}: Event ${event} is no event the game sends a script that extends ${own.extends} — ` +
                            `neither ${chain.map(c => c.name).join(' nor ')} declares it — so it never runs, and what it does never happens. ` +
                            (meant === 'OnInit' && base === 'Quest'
                                ? 'A quest\'s script begins in OnInit, which runs when the quest starts: write `Event OnInit()` in its place, and build again.'
                                : meant && signature ? `Where ${meant} was meant, write \`Event ${signature}\` in its place, and build again.`
                                : meant ? `Where ${meant} was meant, write \`Event ${meant}(\` with its parameters in its place, and build again.`
                                    : `The events it can have are those ${chain.map(c => c.name).join(' and ')} declare, and OnInit.`));
                    }
                }
            }

            // The properties: a variable the record fills as one, and each form property the script declares filled. A fill of
            // a name the script has nowhere reaches nothing and harms nothing, and is left alone.
            if (!known) continue;
            const declaredProperties = [...own.properties, ...chain.flatMap(c => c.source!.properties)];
            for (const property of script.properties) {
                if (declaredProperties.some(p => p.name.toLowerCase() === property.name.toLowerCase())) continue;
                const variable = own.variables.find(v => v.name.toLowerCase() === property.name.toLowerCase());
                if (!variable) continue;
                warnings.push(`${record.file}: ${on} fills the property ${property.name} of ${script.name}, which declares no property ` +
                    `${property.name} — \`${variable.type} ${variable.name}\` is a variable, which nothing fills, so ${variable.name} is ` +
                    `None in game. Declare it as a property, \`${variable.type} Property ${variable.name} Auto\` in its place, and build again.`);
            }
            for (const property of own.properties) {
                if (!property.auto || property.valued || property.type.endsWith('[]') || VALUE_TYPES.has(property.type.toLowerCase())) continue;
                const fill = script.properties.find(p => p.name.toLowerCase() === property.name.toLowerCase());
                if (fill?.filled) continue;
                warnings.push(`${record.file}: ${script.name} declares the property ${property.name} (${property.type}), and ${on} ` +
                    `fills it with nothing, so it is None in game and each line that uses it does nothing. Fill it in the ` +
                    `record's VirtualMachineAdapter, under the script's \`- Name: ${script.name}\`: \`Properties:\`, then ` +
                    `\`- MutagenObjectType: ScriptObjectProperty\`, \`Name: ${property.name}\` and \`Object:\` with the record it ` +
                    'stands for, its editor ID or FormID, each a line of its own; and build again.');
            }
            // A record variable the script never sets and uses: None, as an unfilled property (one the record fills is named above).
            for (const variable of own.variables) {
                if (variable.valued || variable.type.endsWith('[]') || VALUE_TYPES.has(variable.type.toLowerCase())) continue;
                if (script.properties.some(p => p.name.toLowerCase() === variable.name.toLowerCase())) continue;
                if (new RegExp(`(^|[^\\w.])${variable.name}[ \\t]*=(?!=)`, 'i').test(own.body) || !new RegExp(`(^|[^\\w])${variable.name}([^\\w]|$)`, 'i').test(own.body)) continue;
                warnings.push(`${scriptFile(script.name)}: \`${variable.type} ${variable.name}\` is a variable of ${script.name} that nothing ` +
                    `sets, so ${variable.name} is None in game and each line that uses it does nothing. Make it a property, ` +
                    `\`${variable.type} Property ${variable.name} Auto\` in its place, and fill it in ${record.file}'s VirtualMachineAdapter: ` +
                    `under \`- Name: ${script.name}\`, \`Properties:\`, then \`- MutagenObjectType: ScriptObjectProperty\`, ` +
                    `\`Name: ${variable.name}\` and \`Object:\` with the record it stands for; and build again.`);
            }
        }
    }
    return warnings;
}
