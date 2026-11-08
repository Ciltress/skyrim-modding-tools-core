import { createHash, timingSafeEqual } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { PermanentBackendError, SessionExpiredError } from '../Backend.js';
import type { HealthReport } from '../Backend.js';
import { compileScripts, hasUncommittedChanges, scriptSourceFolders } from './externalTools.js';
import { PapyrusSources, scriptWarnings, type ScriptedRecord } from './scriptChecks.js';
import { HelperError, HelperUnavailableError } from './helperClient.js';
import {
    FomodInstaller, InstallError, checkRoot, defaultModName, findDataRoot, folderName, modMetaIni, placeInLoadOrder, plainLayout, pluginOrder,
    topFolders, type ArchiveListing, type InstallEnvironment, type PluginHeader, type UnpackedFile
} from './installer.js';
import type { Instance, ModEntry, ModManagerNotice } from './modManager.js';
import { LIBLOOT_VERSION } from './loot/libloot.js';
import { lootSort, lootUserlist } from './loot/lootSort.js';
import { ModError, findMod, listedMods, nexusRecord, ownerOf, pluginFiles } from './modList.js';
import { findFileConflicts } from './fileConflicts.js';
import { moveRefusal, type PluginKind, type Where } from './orderMoves.js';
import { primaryPlugins } from './mo2.js';
import { checkGameVersion, myGamesDir } from './gameVersion.js';
import { Attributor, dataPath } from './crash/attribution.js';
import {
    BLENDER_SCRIPT, cellFolderOf, copyEditorId, findBlender, groundChanged, initiallyDisabled, layoutChanges, launcher, newReferenceText, normalRotation, recordFileOf,
    saveBlendCode, sceneFolderName, texturesOf,
    triple, type CellSceneFile, type LayoutFile
} from './cellScene.js';
import { CrashLogNotFoundError, chooseCrashLog } from './crash/crashLogFiles.js';
import { buildCrashReport, crashLogSection, type CrashLogSection, type CrashReport } from './crash/crashReport.js';
import { CRASH_RULES } from './crash/rules/index.js';
import { BASE_MASTERS } from './crash/suspects.js';
import type { CrashLog } from './crash/crashLog.js';
import { iniValue, setIniValue } from './gameIni.js';
import { parsePapyrusLog } from './papyrus/papyrusLog.js';
import { PapyrusLogNotFoundError, papyrusLogFile } from './papyrus/papyrusLogFiles.js';
import { buildPapyrusReport, type PapyrusLogging, type PapyrusReport, type PluginOf } from './papyrus/papyrusReport.js';
import { findCaseInsensitive } from './paths.js';
import {
    ProjectError, assignFormIds, belongsAt, combinedFixes, orderedWarnings, correctCopiedEscapes, createProject, type CopiedEscape, deleteCall, deleteRecordFile, editCall, editedRecord, fencedRecordFiles, fileHash,
    keepRecordTexts, misnamedRecordFiles, looseRecordFiles, moveCall, moveRecordFile, nearRecordFile, newestSourceTime, outsideRecordCalls, placeCall,
    projectExists, projectInfo, projectPaths, readDeployState, readProjectHeader, recordFileList, recordFiles, strayRecordFiles, syncFolder, syncYaml,
    whereRecordsGo, writeDeployState, type EditFix, type ProjectPaths
} from './projects.js';
import type { SkyrimEngine } from './engine.js';
import { NexusCatalogue, markDownloadInstalled, readDownloadMeta } from './nexus.js';
import type {
    AddPatchRequest, AddPatchResult, BuildResult, DeleteRecordRequest, DeleteRecordResult, DeployResult, DisableModRequest, DisableModResult,
    EditIniRequest, EditIniResult, EditRecordRequest, GameFile, EditRecordResult, EditInputRequest, EditInputResult, EnableModRequest, EnableModResult, InstallModQuestions, InstallModRequest, ReadCrashLogRequest, ReadPapyrusLogRequest, PapyrusScriptEntries,
    ConflictReport, FindConflictsRequest, MergedChanges, RecordConflict, MovePluginRequest, MovePluginResult, SetModPriorityRequest,
    SetModPriorityResult, SortPluginsRequest, SortPluginsResult, EditTerrainRequest, EditTerrainResult,
    InstallModResult, GameVersionCheck, ModList, ModListQuery, MoveRecordRequest, MoveRecordResult,
    NewProjectRequest, NexusAccount, NexusDownload, NexusMod, NexusSearchQuery, NexusSearchResult, ProjectFile, ProjectInfo,
    PullResult, ExportCellRequest, ExportCellResult, ImportCellRequest, ImportCellResult, RecordQuery, RecordRequest, RecordSearchResult, RecordVersion, RecordVersions, SkyrimBackend
} from './SkyrimBackend.js';
import { CORE_INFO } from '../../lib/product.js';
import { exampleWeapons } from '../../lib/skills.js';

/**
 * What the helper's build answers of a record on a model no record of its type in the load order is on (ModelFiles.cs): its file
 * in the plugin folder, the model below Meshes, and the models of the load order's records most like it, the most like first.
 */
interface UnknownModel {
    file: string;
    editorId: string;
    model: string;
    /** The field the model is in: `Model.File`, or an armor's ground model, `WorldModel.Male.Model.File`. */
    field?: string;
    like: { editorId: string; model: string }[];
}

/** What the helper's addOverrides answers: files relative to the project's plugin folder. */
interface CopiedRecords {
    added: { formId: string; editorId: string | null; type: string; from: string; files: string[]; merged?: MergedChanges }[];
    alreadyInProject: { formId: string; file: string }[];
}

/**
 * An editRecord call refused for want of its file: the record files its project had then, relative to the plugin folder, when it
 * was refused, and in how many builds it has been said.
 */
interface RefusedEdit {
    request: EditRecordRequest;
    before: string[];
    at: number;
    said: number;
}

export interface SkyrimInstanceBackendOptions {
    engine: SkyrimEngine;
    /** The caller's bearer token, in HTTP mode. */
    credential?: string;
    /** Tokens HTTP callers may present. Undefined in stdio mode, where nothing is checked. */
    acceptedCredentials?: readonly string[];
    /** The Nexus Mods catalogue: createBackend's, told the product's version. Replaced in tests; by default the real one. */
    nexus?: NexusCatalogue;
}

/**
 * The backend one server talks to: a thin layer over the shared SkyrimEngine, which holds the
 * instance — through its mod manager — and the plugin helper. Per session it only adds the
 * credential check. Nothing here knows which mod manager it is (docs/adr/0007).
 *
 * `connect()` is the credential check and nothing else — it starts the engine in the background but
 * does not wait for it. An instance that cannot be read, or a helper that is not built, is not a
 * refused credential: it surfaces on the first tool call and in healthcheck with its own cause, and
 * is checked again on every call rather than latched.
 */
export class SkyrimInstanceBackend implements SkyrimBackend {
    private accepted = false;
    private readonly nexus: NexusCatalogue;
    /** editRecord calls refused for want of their file, by plugin folder: a build says what a file written since lacks of each. */
    private readonly refusedEdits = new Map<string, RefusedEdit[]>();

    constructor(private readonly options: SkyrimInstanceBackendOptions) {
        this.nexus = options.nexus ?? new NexusCatalogue({ appVersion: CORE_INFO.version });
    }

    private get engine(): SkyrimEngine {
        return this.options.engine;
    }

    describe(): string {
        return this.engine.modManager.describe();
    }

    get connected(): boolean {
        return this.accepted;
    }

    async connect(): Promise<void> {
        const { acceptedCredentials, credential } = this.options;
        if (acceptedCredentials !== undefined && !matchesAny(credential, acceptedCredentials)) {
            throw new PermanentBackendError(
                'The bearer token was refused. Use one of the tokens in SKYRIM_ACCEPTED_TOKENS.');
        }
        this.accepted = true;
        if (!this.engine.ready) this.engine.warmUp();
    }

    isSessionExpired(error: unknown): boolean {
        return error instanceof SessionExpiredError;
    }

    /**
     * `instance` always, and `nexus-premium` when Nexus confirms the key in NEXUS_API_KEY belongs to a
     * premium account. A key Nexus could not check, or did not in time, is not confirmed premium: the
     * download is not offered then, and a restart asks again. `instance` keeps the list from ever being
     * empty, which would gate nothing (capabilityGate.ts) and list the download after all.
     */
    async capabilities(): Promise<readonly string[] | undefined> {
        const account = await this.nexusAccount(CAPABILITIES_WAIT_MS);
        return [
            'instance',
            ...('premium' in account && account.premium ? ['nexus-premium'] : [])
        ];
    }

    /** The Nexus account, waiting no longer than healthcheck can afford; a slow check finishes in the background. */
    private async nexusAccount(waitMs = 3000): Promise<NexusAccount> {
        let timer: NodeJS.Timeout | undefined;
        const late = new Promise<NexusAccount>(resolve => {
            timer = setTimeout(() => resolve({
                apiKey: true, unchecked: `Nexus Mods did not answer within ${waitMs / 1000} s; the key is checked again next time.`
            }), waitMs);
            timer.unref();
        });
        try {
            return await Promise.race([this.nexus.account(), late]);
        } finally {
            clearTimeout(timer);
        }
    }

    async health(): Promise<HealthReport> {
        // Nexus is optional: an account problem is reported, never counted against the instance's health.
        const nexus = await this.nexusAccount();
        let instance;
        try {
            instance = this.engine.modManager.readInstance();
        } catch (error) {
            return { ok: false, summary: (error as Error).message, nexus };
        }
        const engine = this.engine;
        const helper = engine.ready
            ? { running: true, version: engine.helperVersion }
            : { running: false };
        const missing = engine.missingPlugins.length;
        const failed = !engine.ready && engine.startError !== undefined;
        const gameNote = await this.gameVersionNote();
        const notes = [...engine.modManager.notes(instance), ...(gameNote ? [gameNote] : [])];
        return {
            // Still starting is not a fault: the instance is readable, and the helper is on its way.
            ok: !failed && missing === 0,
            summary: failed
                ? `The plugin helper could not start: ${engine.startError}`
                : !engine.ready
                ? `The instance is readable; the plugin helper is starting, or starts with the first tool call.`
                : missing > 0
                    ? `${missing} active plugin(s) have no file in the game or any enabled mod: ${engine.missingPlugins.slice(0, 5).join(', ')}.`
                    : `Profile '${instance.profile}': ${engine.activePlugins} active plugins, game ${engine.gameVersion}.`,
            modManager: engine.modManager.name,
            instance: engine.modManager.location,
            profile: instance.profile,
            ...(notes.length > 0 ? { notes } : {}),
            game: { dir: instance.gameDir, edition: instance.edition, version: engine.gameVersion ?? null },
            plugins: { active: engine.activePlugins, missing: engine.missingPlugins },
            helper,
            nexus
        };
    }

    async findRecords(query: RecordQuery): Promise<RecordSearchResult> {
        await this.engine.ensureFresh();
        return this.engine.helper.call('findRecords', { ...query });
    }

    async readRecord(request: RecordRequest): Promise<RecordVersions> {
        await this.engine.ensureFresh();
        // The helper says `path`, relative to a plugin folder; here that name is kept for full paths.
        type HelperVersions = Omit<RecordVersions, 'versions'> & {
            versions: (Omit<RecordVersion, 'files'> & { files: { path: string; text: string }[] })[];
        };
        const read = await this.engine.helper.call<HelperVersions>('readRecord', { ...request });
        return {
            ...read,
            versions: read.versions.map(version => ({
                ...version,
                files: version.files.map(file => ({ pathInPluginFolder: file.path, text: file.text }))
            }))
        };
    }

    // ---------------------------------------------------------------- projects

    /** An existing project is returned as it is (`created: false`) — unless it was asked for under another plugin name. */
    async newProject(request: NewProjectRequest): Promise<ProjectInfo> {
        const paths = projectPaths(this.engine.projectsDir, request.project);
        if (projectExists(paths)) {
            const existing = projectInfo(request.project, paths, false);
            if (request.plugin && request.plugin.toLowerCase() !== existing.plugin.toLowerCase()) {
                throw new ProjectError(
                    `The project '${request.project}' already exists, with the plugin ${existing.plugin}. Choose another project name.`);
            }
            return existing;
        }
        const plugin = request.plugin ?? `${request.project}.esp`;
        this.refuseTakenPlugin(plugin);
        prepareProjectsFolder(this.engine.projectsDir);
        createProject(paths, plugin, request.light ?? true);
        return projectInfo(request.project, paths, true);
    }

    /**
     * A new project's plugin must not share its name with a plugin the load order has: deployed, it
     * would hide that one from the game — a project's Skyrim.esm would take the game's own master's place.
     */
    private refuseTakenPlugin(plugin: string): void {
        const modManager = this.engine.modManager;
        const instance = modManager.readInstance();
        const taken = modManager.resolvePluginFiles(instance, modManager.readModList(instance)).get(plugin.toLowerCase());
        if (!taken) return;
        const name = path.basename(taken);
        throw new ProjectError(
            `${name} is already a plugin in the load order; a project with that plugin would hide it from the game. ` +
            `Leave \`plugin\` out — the project gets <project>.esp — or name a new file. To copy records from ${name}, ` +
            `pass it to addPatch as \`from\`.`);
    }

    async editRecord(request: EditRecordRequest): Promise<EditRecordResult> {
        await this.engine.ensureFresh();
        const paths = projectPaths(this.engine.projectsDir, request.project);
        readProjectHeader(paths); // names a project that is not there
        const edited = await this.engine.helper.call<EditRecordResult>('editRecord', {
            projectPluginDir: paths.plugin,
            record: editedRecord(this.engine.projectsDir, paths, request.record),
            ...(request.set ? { set: request.set } : {}),
            ...(request.remove ? { remove: request.remove } : {})
        }).catch((error: unknown) => {
            // Refused for a file written outside the project — "none yet; write one first" — the model wrote it outside again
            // (eval round 67's npc+detailed #3): the files outside the plugin folder first, each with its call.
            if (!(error instanceof HelperError) || error.code !== 'notFound') throw error;
            const outside = outsideRecordCalls(this.engine.projectsDir, paths);
            if (outside) {
                throw new HelperError(error.code, `No record file '${request.record}' in the project's plugin folder, where ` +
                    `editRecord changes records.${outside} The project's record files: ${recordFileList(paths)}.`);
            }
            // A file not written yet: the call's changes, given back. Refused for a sword not written yet, a model wrote it from
            // the Iron Sword with its value 25, and told the user the value of 150 that only the refused call had (eval round 90).
            // A bare name comes back too in a project with no record file yet, where it cannot be a typo of one: two calls named
            // their record so, "SteelSword" and "FlameBlade", before its file was written (eval round 91). In a project with
            // record files, a name that is no file — an EditorID mistyped — is only refused.
            if (!/\.ya?ml$/i.test(request.record.trim()) && recordFiles(paths.plugin).length > 0) throw error;
            // A path a letter or two off the project's file: the same call to that file.
            const near = nearRecordFile(paths, request.record);
            if (near) {
                throw new HelperError(error.code, `No record file '${request.record}' in the project — it has ${near}. Nothing was changed: ` +
                    `the same call to ${near} — ${editCall(paths, { ...request, record: near })}`);
            }
            this.keepRefusedEdit(paths, request);
            throw new HelperError(error.code, `${error.message} Nothing was changed: once the file is written, send this ` +
                `call again for its changes — ${editCall(paths, request)}`);
        });
        return { ...edited, file: path.resolve(paths.plugin, edited.file) };
    }

    async editInput(request: EditInputRequest): Promise<EditInputResult> {
        await this.engine.ensureFresh();
        const paths = projectPaths(this.engine.projectsDir, request.project);
        readProjectHeader(paths);
        const edited = await this.engine.helper.call<{ file: string; changed?: string[]; kept?: string[]; unread?: boolean }>('editInput', {
            projectPluginDir: paths.plugin,
            record: editedRecord(this.engine.projectsDir, paths, request.record),
            input: request.input
        });
        return { file: path.resolve(paths.plugin, edited.file), changed: edited.changed ?? [], kept: edited.kept ?? [], unread: edited.unread ?? false };
    }

    /** A call refused for want of its file, kept with the record files the project had then; the same call sent again replaces it. */
    private keepRefusedEdit(paths: ProjectPaths, request: EditRecordRequest): void {
        const key = path.resolve(paths.plugin);
        const same = (kept: RefusedEdit) => JSON.stringify(kept.request) === JSON.stringify(request);
        const kept = (this.refusedEdits.get(key) ?? []).filter(edit => !same(edit) && Date.now() - edit.at < REFUSED_EDIT_KEPT_MS);
        const before = recordFiles(paths.plugin).map(file => path.relative(paths.plugin, file).split(path.sep).join('/'));
        this.refusedEdits.set(key, [...kept, { request, before, at: Date.now(), said: 0 }]);
    }

    /**
     * What an editRecord call refused for want of its file still lacks in the file written since, as a warning with the one call
     * that writes it: told "once the file is written, send this call again", a model wrote its NPC from an atronach, never sent
     * the call — her race, her voice — again, and answered that it had made her a Nord (eval round 114's npc #2). The helper
     * finds the file and tries each change on it without writing; a call is said in two builds at most, and dropped once
     * its file holds it.
     */
    private async refusedEditWarnings(paths: ProjectPaths): Promise<string[]> {
        const key = path.resolve(paths.plugin);
        const kept = (this.refusedEdits.get(key) ?? []).filter(edit => Date.now() - edit.at < REFUSED_EDIT_KEPT_MS);
        if (kept.length === 0) {
            this.refusedEdits.delete(key);
            return [];
        }
        const now = recordFiles(paths.plugin).map(file => path.relative(paths.plugin, file).split(path.sep).join('/'));
        const warnings: string[] = [];
        const left: RefusedEdit[] = [];
        for (const edit of kept) {
            if (edit.said >= 2) continue;
            const { request } = edit;
            const pending = await this.engine.helper.call<{ file: string | null; differs?: { path: string; has: string | null; removes: boolean }[] }>(
                'pendingEdit', {
                    projectPluginDir: paths.plugin,
                    record: editedRecord(this.engine.projectsDir, paths, request.record),
                    ...(request.set ? { set: request.set } : {}),
                    ...(request.remove ? { remove: request.remove } : {}),
                    among: now.filter(file => !edit.before.includes(file))
                }).catch(() => ({ file: null }));
            // No file of it yet, or no one file: kept for a later build.
            if (!pending.file) {
                left.push(edit);
                continue;
            }
            const differs = pending.differs ?? [];
            if (differs.length === 0) continue;
            edit.said++;
            left.push(edit);
            const sets = differs.filter(d => !d.removes);
            const removes = differs.filter(d => d.removes);
            const said = [
                ...sets.map(d => `${d.path} is ${d.has ?? 'not in it'}, where that call set ${shownValue(request.set?.[d.path])}`),
                ...removes.map(d => `${d.path} is in it still, ${d.has}, where that call removed it`)
            ];
            warnings.push(`${pending.file}: an editRecord call was refused before this file was written — '${request.record}' was no ` +
                `record file of the project yet — and was not sent again, so its changes are not in it: ${said.join('; ')}. One call ` +
                `makes them and builds again: ${editCall(paths, {
                    record: pending.file,
                    ...(sets.length > 0 ? { set: Object.fromEntries(sets.map(d => [d.path, request.set?.[d.path]])) } : {}),
                    ...(removes.length > 0 ? { remove: removes.map(d => d.path) } : {})
                })}`);
        }
        if (left.length > 0) this.refusedEdits.set(key, left);
        else this.refusedEdits.delete(key);
        return warnings;
    }

    async moveRecord(request: MoveRecordRequest): Promise<MoveRecordResult> {
        const paths = projectPaths(this.engine.projectsDir, request.project);
        readProjectHeader(paths); // names a project that is not there
        return { moved: moveRecordFile(this.engine.projectsDir, paths, request.from, request.to) };
    }

    async deleteRecord(request: DeleteRecordRequest): Promise<DeleteRecordResult> {
        const paths = projectPaths(this.engine.projectsDir, request.project);
        readProjectHeader(paths);
        return { deleted: deleteRecordFile(this.engine.projectsDir, paths, request.record) };
    }

    async addPatch(request: AddPatchRequest): Promise<AddPatchResult> {
        await this.engine.ensureFresh();
        const paths = projectPaths(this.engine.projectsDir, request.project);
        let created = false;
        if (!projectExists(paths)) {
            const plugin = request.plugin ?? `${request.project}.esp`;
            this.refuseTakenPlugin(plugin);
            prepareProjectsFolder(this.engine.projectsDir);
            createProject(paths, plugin, true);
            created = true;
        }
        const { plugin } = readProjectHeader(paths);
        const copied = await afterLocalWrites(this.engine.helper.call<CopiedRecords>('addOverrides', {
            plugin,
            projectPluginDir: paths.plugin,
            records: request.records,
            from: request.from,
            ...(request.merge ? { merge: true } : {})
        }));
        // Full paths, and the text to edit: given paths relative to the plugin folder, models joined
        // them to the project folder; given no text, they spent a call reading it. The text stops
        // short of what the response budget would withhold — a patch of fifty leveled lists is fine.
        let textBytes = 0;
        let leftOut = 0;
        const projectFile = (file: string): ProjectFile => {
            const full = path.resolve(paths.plugin, file);
            const text = fs.readFileSync(full, 'utf8');
            textBytes += Buffer.byteLength(text);
            if (textBytes > PATCH_TEXT_BYTES) {
                leftOut++;
                return { path: full };
            }
            return { path: full, text };
        };
        const result: AddPatchResult = {
            project: projectInfo(request.project, paths, created),
            added: copied.added.map(record => ({ ...record, files: record.files.map(projectFile) })),
            alreadyInProject: copied.alreadyInProject.map(record => ({ formId: record.formId, files: [projectFile(record.file)] }))
        };
        if (leftOut > 0) {
            result.textLeftOut = `The text of ${leftOut} file${leftOut === 1 ? '' : 's'} is left out to keep this answer ` +
                `small: read ${leftOut === 1 ? 'it at its path' : 'each at its path'} before editing.`;
        }
        return result;
    }

    async buildProject(project: string): Promise<BuildResult> {
        await this.engine.ensureFresh();
        const paths = projectPaths(this.engine.projectsDir, project);
        const { plugin, light } = readProjectHeader(paths);

        // A build of nothing succeeded, and small models took it for done (eval round 9): name what is
        // missing, and the record files no build would read.
        const strays = strayRecordFiles(paths);
        // Told where each belonged, the model wrote it there again and left the first copy behind (eval round 66): one call moves it,
        // or deletes it where the plugin folder has its file already.
        const where = (file: string) => placeCall(this.engine.projectsDir, paths, file);
        const files = recordFiles(paths.plugin).length;
        if (files === 0) {
            const loose = looseRecordFiles(this.engine.projectsDir);
            // The way out first: placed after the general text, it went unread, three builds in a row (eval probe 15).
            const misnamed = misnamedRecordFiles(paths.plugin);
            throw new ProjectError(
                `'${project}' has no records to build.` +
                (misnamed.length > 0 ? ` Files the build does not read for their ending — move each to its .yaml name: ` +
                    `${misnamed.map(({ file, as }) => moveCall(paths, file, as)).join('; ')}.` : '') +
                (strays.length > 0 ? ` Its record files outside its plugin folder, where no build reads them — move each where it ` +
                    `belongs: ${strays.map(where).join('; ')}.` : '') +
                (loose.length > 0 ? ` Record files that belong to no project — move each of this project's where it belongs: ` +
                    `${loose.map(where).join('; ')}.` : '') +
                ` ${whereRecordsGo(paths)} Mirror a vanilla record of the same type (readRecord), leaving FormKey out.`);
        }
        // Copied with the fence an answer shows it in, a record broke the YAML at line 1, and the reader's
        // message sent the model to fix its indentation (eval round 15's probes): named first, plainly.
        const fenced = fencedRecordFiles(paths.plugin);
        if (fenced.length > 0) {
            const lines = (numbers: number[]) => numbers.length === 1
                ? `line ${numbers[0]}`
                : `lines ${numbers.slice(0, -1).join(', ')} and ${numbers[numbers.length - 1]}`;
            throw new ProjectError(
                `${fenced.map(({ file, lines: at }) => `${file}, ${lines(at)}`).join('; ')}: a markdown fence — a line of ` +
                'backticks, the way an answer shows a file. A record file holds only the record\'s YAML: delete the fence ' +
                'lines and keep what is between them.');
        }
        // Escapes copied out of a JSON answer as they read built without a word, into a path the game cannot
        // find (eval rounds 12–15); refused with the lines as they were to be, three of the seven runs that met the
        // refusal failed after it (rounds 24–31). The build writes those lines itself, and says so.
        const corrected = correctCopiedEscapes(paths.plugin).map(escapeCorrected);
        // A refusal after a correction names it: the model knows the lines as it wrote them, and an edit of one
        // the build has rewritten finds nothing.
        // The helper names its own corrections so too: one sentence holds both.
        const withCorrections = (error: unknown) => {
            if (corrected.length === 0 || !(error instanceof HelperError || error instanceof ProjectError)) return error;
            const said = 'Corrected in the files already: ';
            const message = error.message.includes(said)
                ? error.message.replace(/\.?\s*$/, `; ${corrected.join('; ')}.`)
                : `${error.message} ${said}${corrected.join('; ')}.`;
            return error instanceof HelperError ? new HelperError(error.code, message) : new ProjectError(message);
        };
        // With records to build, files elsewhere are named but stop nothing: a refusal only deleting a
        // file could satisfy trapped a model whose file tools could not delete (eval round 10). Each with its call.
        const warnings: string[] = [];
        if (strays.length > 0) {
            warnings.push(`${strays.length} record file(s) outside the plugin folder were not built — no build reads them. Move each ` +
                `record meant for this mod into the plugin folder, and delete each copy of a file already there: ${strays.map(where).join('; ')}.`);
        }
        // Written again in its project after a refusal, a record's first copy stayed outside every project, and the eval found a
        // copied escape in it (eval round 66's sword+detailed #3).
        const copiedOutside = looseRecordFiles(this.engine.projectsDir).filter(file => {
            const target = belongsAt(file, paths, this.engine.projectsDir);
            return target !== null && fs.existsSync(target);
        });
        if (copiedOutside.length > 0) {
            warnings.push(`${copiedOutside.length} record file(s) outside every project have the name of one in this project's plugin ` +
                `folder — no build reads them, and the project's file is the one built. Delete each: ` +
                `${copiedOutside.map(file => deleteCall(paths, file)).join('; ')}.`);
        }
        // Fields a record type does not have, folders that are no group, copied FormKeys, links that name no
        // record: named before any record is numbered. A file in a folder that is no group, whose record its
        // group's folder holds too, is left out, untouched — the same trap (eval probe 10 after round 11).
        // The helper corrects a FormKey copied into a new record from the record it read, and moves a field written
        // where its record has none to the one place it goes, as it says in `corrected`.
        const checked = await this.engine.helper.call<{
            records: number; leftOut?: { file: string; copy: string }[]; notes?: string[]; corrected?: { file: string; change: string }[];
            fixes?: EditFix[];
        }>('checkRecords', { projectPluginDir: paths.plugin }).catch((error: unknown) => { throw withCorrections(error); });
        corrected.push(...(checked.corrected ?? []).map(({ file, change }) => `${path.join(paths.plugin, file)}: ${change}`));
        // What builds but likely not as meant — a woman's voice on a man, an enchantment nothing carries (eval probe 14),
        // a magic effect nothing casts (eval round 34).
        warnings.push(...checked.notes ?? []);
        const leftOut = (checked.leftOut ?? []).map(({ file, copy }) => ({ file: path.join(paths.plugin, file), copy: copy ? path.join(paths.plugin, copy) : '' }));
        const copies = leftOut.filter(({ copy }) => copy);
        if (copies.length > 0) {
            warnings.push(`${copies.length} record file(s) the build does not read were not built: ` +
                `${copies.map(({ file, copy }) => `${file}, whose record ${copy} holds`).join('; ')}. The build reads a record ` +
                // The model took this for a note on its FormKey line and answered with the copy in Notes/ (eval round 66's note #5).
                `only from a .yaml file directly in its group's folder — delete each copy: ` +
                `${copies.map(({ file }) => deleteCall(paths, file)).join('; ')}.`);
        }
        // A file written empty holds no record: the way to drop one before a model had a tool to delete it (eval probe 16).
        const emptied = leftOut.filter(({ copy }) => !copy);
        if (emptied.length > 0) {
            warnings.push(`${emptied.length} record file(s) are empty, so they hold no record and were left out — delete each: ` +
                `${emptied.map(({ file }) => deleteCall(paths, file)).join('; ')}.`);
        }
        const skip = new Set(leftOut.map(({ file }) => file));

        const assignedFormIds = assignFormIds(paths.plugin, plugin, light, skip);
        // Every build starts from nothing, so a script deleted from the project leaves no .pex behind.
        // The plugin is written last: a build that fails on the way leaves no plugin, and deploy refuses.
        fs.rmSync(paths.build, { recursive: true, force: true });

        const instance = this.engine.modManager.readInstance();
        const mods = this.engine.modManager.readModList(instance);
        const compiled = await compileScripts({
            platform: this.engine.platform,
            compiler: this.engine.papyrusCompiler,
            searched: this.engine.papyrusCompilerCandidates,
            creationKitDir: this.engine.creationKitDir,
            scriptsDir: paths.scripts,
            outputDir: path.join(paths.build, 'Scripts'),
            instance,
            mods
        });

        const output = path.join(paths.build, plugin);
        // A build that fails leaves the record files as they were before it. The helper writes the FormIDs that links
        // named by editor ID stand for only into its own copy of them (eval round 47).
        const giveBack = keepRecordTexts(paths.plugin);
        let built: {
            records: number; masters: string[]; headerVersion: number; resolvedLinks: BuildResult['resolvedLinks']; built?: string[]; notes?: string[];
            models?: UnknownModel[]; scripts?: ScriptedRecord[]; fixes?: EditFix[];
        };
        try {
            built = await afterLocalWrites(this.engine.helper.call<typeof built>('build', {
                projectPluginDir: paths.plugin,
                outputPath: output,
                headerVersion: this.engine.headerVersion,
                // A new weapon that keeps part of a skill example's numbers is noted, as one that keeps its model's record's.
                examples: exampleWeapons()
            }, BUILD_TIMEOUT_MS));
            // Whatever the files looked like, a plugin without records is never what was asked (eval round 10).
            if (built.records === 0) {
                fs.rmSync(output, { force: true });
                // Empty files alone: blamed on folder nesting, the one empty file was no fault named (refusal probes of 2026-10-31, G10).
                const empty = recordFiles(paths.plugin).filter(file => fs.readFileSync(file, 'utf8').trim() === '');
                if (empty.length > 0 && empty.length === recordFiles(paths.plugin).length) {
                    throw new ProjectError(`${empty.map(file => path.relative(paths.plugin, file).split(path.sep).join('/')).join(', ')} ` +
                        `${empty.length === 1 ? 'is' : 'are'} empty, and the build read no record and wrote no plugin — write the record ` +
                        'into it, the way readRecord shows a record of its type.');
                }
                throw new ProjectError(
                    `The build read no record from ${project}'s ${files} record file(s), and wrote no plugin. A record file sits ` +
                    `directly in its group's folder — ${path.join(paths.plugin, 'Weapons', 'MyWeapon.yaml')}, not in a folder ` +
                    `below it — and is written the way readRecord shows a record of its type.`);
            }
        } catch (error) {
            giveBack();
            throw withCorrections(error);
        }
        // An override that changes nothing, said once the plugin is written: told apart only from the record itself.
        warnings.push(...built.notes ?? []);
        const models = await this.missingModels(paths, built.models ?? []);
        warnings.push(...models.map(model => model.warning));
        warnings.push(...await this.scriptWarnings(paths, built.scripts ?? [], instance, mods));
        warnings.push(...await this.refusedEditWarnings(paths));

        // A file keeps the name its author gave it, the build's FormID written in: renamed the way Spriggit names
        // a file, Hilda's went on being edited under its old name, then written anew — a second Hilda (eval probe
        // 23, round 30). A pull writes Spriggit's names.
        const fullPath = (file: string) => path.resolve(paths.plugin, file);
        // Each correction said as the build's own work: a model took "moved `Level`, `SpeedMultiplier` under `Configuration`"
        // for warnings to fix, wrote its NPC again from memory with the lines among her Flags, and answered unbuilt (eval
        // round 113's npc #3). The file's name ends at the first ": " — a drive's colon has none after it.
        const done = corrected.map(entry => entry.replace(': ', ', done by the build: '));
        // In the order they are to be fixed, a copy's own fixes waiting while the recipe that makes it is warned.
        const order = orderedWarnings(warnings);
        warnings.splice(0, warnings.length, ...order.warnings);
        const waits = (fix: EditFix) => order.waiting.some(copy => copy.toLowerCase() === fix.file.replace(/\\/g, '/').toLowerCase());
        const combined = combinedFixes(paths, [...checked.fixes ?? [], ...built.fixes ?? [], ...models.flatMap(model => model.fix ? [model.fix] : [])]
            .filter(fix => !waits(fix)));
        return {
            project, plugin, output,
            ...(done.length > 0 ? { corrected: done } : {}),
            records: built.records,
            masters: built.masters,
            headerVersion: built.headerVersion,
            assignedFormIds: assignedFormIds.map(record => ({ ...record, file: fullPath(record.file) })),
            resolvedLinks: (built.resolvedLinks ?? []).map(link => ({ ...link, file: fullPath(link.file) })),
            built: built.built ?? [],
            scripts: { compiled },
            ...(warnings.length > 0 ? { warnings } : {}),
            ...(combined.length > 0 ? { combined } : {})
        };
    }

    async deployProject(project: string): Promise<DeployResult> {
        const modManager = this.engine.modManager;
        const instance = modManager.readInstance();
        const paths = projectPaths(this.engine.projectsDir, project);
        const { plugin } = readProjectHeader(paths);
        const output = path.join(paths.build, plugin);

        if (!fs.existsSync(output)) {
            throw new ProjectError(`'${project}' has not been built yet: run buildProject first.`);
        }
        if (newestSourceTime(paths) > fs.statSync(output).mtimeMs) {
            throw new ProjectError(`'${project}' changed since its last build: run buildProject, then deploy.`);
        }

        const modFolder = path.join(instance.modsDir, project);
        const deployed = path.join(modFolder, plugin);
        // A copy that outranks the mod — Mod Organizer's overwrite — is the one the game loads, and the one to guard.
        const shadowing = modManager.shadowingCopies(instance, project, plugin).filter(file => fs.existsSync(file));
        const live = shadowing[0] ?? deployed;
        const state = readDeployState(paths);
        if (!state && fs.existsSync(modFolder) && fs.readdirSync(modFolder).some(f => f.toLowerCase() !== 'meta.ini')) {
            throw new ProjectError(
                `A mod named '${project}' already exists in the instance and was not deployed from this project. ` +
                `Rename the project, or remove that mod in the mod manager first.`);
        }
        if (!state && shadowing.length > 0) {
            throw new ProjectError(
                `A ${plugin} at '${shadowing[0]}' would hide this deploy from the game. If it is this project's work, ` +
                `run pullProject first; otherwise move it away in the mod manager.`);
        }
        if (state && fs.existsSync(live) && fileHash(live) !== state.pluginHash) {
            throw new ProjectError(
                `The deployed ${plugin}${live === deployed ? '' : ` (the copy at '${live}')`} changed since the last ` +
                `deploy — saved in the Creation Kit or xEdit? Run pullProject to bring those changes into the project ` +
                `first; deploying now would overwrite them.`);
        }
        const refusal = await modManager.writeRefusal(instance, 'add');
        if (refusal) throw new ProjectError(refusal);

        // The mod folder becomes the build plus the assets, so files removed from the project go too —
        // all but the mod manager's own meta.ini.
        const files = syncFolder([paths.assets, paths.build], modFolder, name => name.toLowerCase() === 'meta.ini');
        // Guarded above: a shadowing copy that got this far was pulled, so the project holds its work.
        for (const copy of shadowing) fs.rmSync(copy);

        const enabled = modManager.enable(instance, project, [plugin]);
        const notice = await modManager.afterDeploy(instance);
        writeDeployState(paths, { modFolder, plugin, pluginHash: fileHash(deployed), at: new Date().toISOString() });

        return {
            project, plugin, modFolder, files,
            ...(shadowing.length > 0 ? { removedCopies: shadowing } : {}),
            profile: {
                name: instance.profile,
                modEnabled: enabled.modEnabled,
                pluginActive: enabled.activated.length > 0,
                addedToLoadOrder: enabled.addedToLoadOrder.length > 0
            },
            modManager: notice
        };
    }

    async pullProject(project: string): Promise<PullResult> {
        await this.engine.ensureFresh();
        const instance = this.engine.modManager.readInstance();
        const paths = projectPaths(this.engine.projectsDir, project);
        const { plugin } = readProjectHeader(paths);

        // The Creation Kit writes a plugin it edits back to its mod, but one it saves anew can land
        // elsewhere — in Mod Organizer, in overwrite. The newest copy is the latest work; of copies with
        // the same time — a coarse file clock gives them that — the one that outranks the mod is what the
        // game loads. Highest first, so the stable sort keeps that order among equals.
        const deployed = path.join(instance.modsDir, project, plugin);
        const candidates = [...this.engine.modManager.shadowingCopies(instance, project, plugin), deployed].filter(file => fs.existsSync(file));
        if (candidates.length === 0) {
            throw new ProjectError(`There is no deployed ${plugin} to pull from: deploy the project first.`);
        }
        const source = candidates.sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs)[0];

        const dirty = await hasUncommittedChanges(paths.root);
        if (dirty) {
            throw new ProjectError(
                `'${project}' has uncommitted changes. Commit them first, so what the pull changes shows up on its own in ` +
                `git diff and can be undone.`);
        }

        const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'skyrim-pull-'));
        try {
            await this.engine.helper.call('serialize', { pluginPath: source, outputDir: scratch }, BUILD_TIMEOUT_MS);
            const changes = syncYaml(scratch, paths.plugin);
            writeDeployState(paths, {
                modFolder: path.join(instance.modsDir, project),
                plugin,
                pluginHash: fileHash(source),
                at: new Date().toISOString()
            });
            const inPlugin = (files: string[]) => files.map(file => path.resolve(paths.plugin, file));
            return {
                project, plugin, source,
                added: inPlugin(changes.added),
                changed: inPlugin(changes.changed),
                removed: inPlugin(changes.removed),
                ...(dirty === undefined
                    ? { warning: 'git could not check the project folder for uncommitted changes, so nothing guarded them.' }
                    : {})
            };
        } finally {
            fs.rmSync(scratch, { recursive: true, force: true });
        }
    }

    async exportCell({ project, cell, textures = true }: ExportCellRequest): Promise<ExportCellResult> {
        await this.engine.ensureFresh();
        const instance = this.engine.modManager.readInstance();
        const paths = projectPaths(this.engine.projectsDir, project);
        if (!projectExists(paths)) await this.newProject({ project });
        const { plugin } = readProjectHeader(paths);
        const built = path.join(paths.build, plugin);
        const scene = await this.engine.helper.call<CellSceneFile>('cellScene',
            { cell, ...(fs.existsSync(built) ? { projectPlugin: built } : {}) }, BUILD_TIMEOUT_MS);

        // Into the project's blender/ folder, which git leaves out: the game's meshes and textures are no part of the mod.
        const blender = path.join(paths.root, 'blender');
        const folder = path.join(blender, sceneFolderName(scene.cell));
        fs.mkdirSync(folder, { recursive: true });
        fs.writeFileSync(path.join(blender, '.gitignore'), '*\n');
        fs.rmSync(path.join(folder, 'layout.json'), { force: true });

        const attributor = this.attributor(instance);
        const copyOut = async (files: string[]) => {
            const sources = await attributor.sources(files);
            const extract: { archive: string; from: string; to: string }[] = [];
            for (const [asked, source] of sources) {
                const target = path.join(folder, ...dataPath(asked).split('\\'));
                if ('file' in source) {
                    fs.mkdirSync(path.dirname(target), { recursive: true });
                    fs.copyFileSync(source.file, target);
                } else extract.push({ archive: source.archive, from: source.under, to: target });
            }
            if (extract.length > 0) await this.engine.helper.call('extractFiles', { files: extract }, BUILD_TIMEOUT_MS * 4);
            return files.filter(file => !sources.has(file));
        };
        const models = [...new Set(scene.references.map(r => r.model).filter((m): m is string => !!m))];
        const missing = await copyOut(models);
        const found = models.filter(model => !missing.includes(model));
        let textureCount = 0;
        if (textures) {
            const named = new Map<string, string>();
            for (const model of found) {
                const file = path.join(folder, ...dataPath(model).split('\\'));
                if (fs.existsSync(file)) for (const texture of texturesOf(fs.readFileSync(file))) named.set(texture.toLowerCase(), texture);
            }
            const all = [...named.values()];
            const lacking = await copyOut(all);
            missing.push(...lacking);
            textureCount = all.length - lacking.length;
        }

        fs.writeFileSync(path.join(folder, 'cell.json'), JSON.stringify(scene, null, 1));
        fs.copyFileSync(BLENDER_SCRIPT, path.join(folder, 'skyrim_cell.py'));
        const importScript = path.join(folder, 'import_cell.py');
        const exportScript = path.join(folder, 'export_layout.py');
        fs.writeFileSync(importScript, launcher(folder, 'import_cell'));
        fs.writeFileSync(exportScript, launcher(folder, 'export_layout'));
        const blend = await this.saveBlend(folder);
        return {
            project, cell: scene.cell.editorId ?? scene.cell.formId, folder,
            references: scene.references.length, meshes: found.length, textures: textureCount, missing, importScript, exportScript, ...blend
        };
    }

    /**
     * The cell placed and saved as cell.blend by Blender in the background, where SKYRIM_BLENDER or a search finds one with PyNifly:
     * a user opens the file, and an agent works on it through Blender's MCP server with no Blender window. Without Blender, why not.
     */
    private async saveBlend(folder: string): Promise<{ blendFile: string } | { blendFileNote: string }> {
        const windows = this.engine.platform.name === 'windows';
        const blender = this.engine.blender ?? findBlender(windows, process.env.PATH, os.homedir());
        const blendFile = path.join(folder, 'cell.blend');
        fs.rmSync(blendFile, { force: true });
        if (!blender) {
            return { blendFileNote: 'No Blender found — set SKYRIM_BLENDER to blender.exe for a ready cell.blend; the importScript places the cell in a running Blender as well.' };
        }
        const { execFile } = await import('node:child_process');
        const output = await new Promise<string>(resolve => {
            execFile(blender, ['--background', '--python-expr', saveBlendCode(folder)], { timeout: 15 * 60_000, maxBuffer: 64 * 1024 * 1024, windowsHide: true },
                (error, stdout, stderr) => resolve(`${stdout}\n${stderr}${error ? `\n${error.message}` : ''}`));
        });
        if (fs.existsSync(blendFile)) return { blendFile };
        const why = /PyNifly add-on is not enabled/.test(output) ? 'its PyNifly add-on is not enabled'
            : output.split('\n').filter(line => /Error|error:/.test(line)).slice(-2).join(' ').slice(0, 300) || 'it saved none';
        return { blendFileNote: `${blender} made no cell.blend: ${why}. The importScript places the cell in a running Blender.` };
    }

    async importCell({ project, cell }: ImportCellRequest): Promise<ImportCellResult> {
        await this.engine.ensureFresh();
        const paths = projectPaths(this.engine.projectsDir, project);
        const { plugin } = readProjectHeader(paths);
        const blender = path.join(paths.root, 'blender');
        const exported = fs.existsSync(blender)
            ? fs.readdirSync(blender, { withFileTypes: true }).filter(e => e.isDirectory() && fs.existsSync(path.join(blender, e.name, 'cell.json'))).map(e => e.name)
            : [];
        const name = cell ? exported.find(e => e.toLowerCase() === cell.toLowerCase().replace(/[^\w.-]/g, '_')) : exported.length === 1 ? exported[0] : undefined;
        if (!name) {
            throw new ProjectError(exported.length === 0
                ? `'${project}' has no cell exported for Blender: exportCell writes one first.`
                : `Name the cell to bring back, one of ${exported.join(', ')}.`);
        }
        const folder = path.join(blender, name);
        const layoutFile = path.join(folder, 'layout.json');
        if (!fs.existsSync(layoutFile)) {
            throw new ProjectError(`${folder} holds no layout.json yet: in Blender, run ${path.join(folder, 'export_layout.py')} — ` +
                `the Scripting tab's Run Script, or its text sent through Blender's MCP server — then call importCell again.`);
        }
        const scene = JSON.parse(fs.readFileSync(path.join(folder, 'cell.json'), 'utf8')) as CellSceneFile;
        const changes = layoutChanges(scene, JSON.parse(fs.readFileSync(layoutFile, 'utf8')) as LayoutFile);
        const result: ImportCellResult = { project, folder, moved: [], added: [], removed: [], unchanged: changes.unchanged };
        const projectPlugin = plugin.toLowerCase();
        const isNew = (formId: string) => formId.split(':')[1]?.toLowerCase() === projectPlugin;

        // The other plugins' references moved or deleted are changed as copies; a copy made in Blender needs the cell's copy.
        const toCopy = [...changes.moved.map(m => m.reference), ...changes.removed]
            .filter(r => !isNew(r.formId) && !recordFileOf(paths.plugin, r.formId)).map(r => r.formId);
        if (!cellFolderOf(paths.plugin, scene.cell.formId)) toCopy.unshift(scene.cell.formId);
        if (toCopy.length > 0) await this.addPatch({ project, records: [...new Set(toCopy)] });
        const cellFolder = cellFolderOf(paths.plugin, scene.cell.formId);
        if (!cellFolder) throw new ProjectError(`The copy of ${scene.cell.editorId ?? scene.cell.formId} is not in '${project}'.`);

        for (const { reference, position, rotation, scale } of changes.moved) {
            const file = recordFileOf(paths.plugin, reference.formId);
            if (!file) continue;
            const set: Record<string, unknown> = {
                'Placement.Position': triple(position),
                'Placement.Rotation': triple(normalRotation(rotation))
            };
            if (Math.abs(scale - (reference.scale || 1)) >= 0.002) set.Scale = Math.round(scale * 1000) / 1000;
            await this.editRecord({ project, record: path.relative(paths.plugin, file).replace(/\\/g, '/'), set });
            result.moved.push(file);
        }
        for (const reference of changes.removed) {
            const file = recordFileOf(paths.plugin, reference.formId);
            if (!file) continue;
            if (isNew(reference.formId)) {
                result.removed.push((await this.deleteRecord({ project, record: path.relative(paths.plugin, file).replace(/\\/g, '/') })).deleted);
                continue;
            }
            // Another plugin's reference is never deleted — what names it would break: it starts disabled, as xEdit's
            // "Undelete and Disable" leaves one.
            fs.writeFileSync(file, initiallyDisabled(fs.readFileSync(file, 'utf8')));
            result.removed.push(file);
        }
        const taken = new Set(recordFiles(paths.plugin).map(file => path.basename(file, '.yaml').toLowerCase()));
        for (const copy of changes.copies) {
            const editorId = copyEditorId(project, copy.baseEditorId || copy.base.split(':')[0], taken);
            const file = path.join(cellFolder, 'Temporary', `${editorId}.yaml`);
            fs.mkdirSync(path.dirname(file), { recursive: true });
            fs.writeFileSync(file, newReferenceText({ kind: copy.kind, editorId, base: copy.base, position: copy.position, rotation: copy.rotation, scale: copy.scale }));
            result.added.push(file);
        }
        // The ground, sculpted: the cell's heights set, its neighbours' edges moved with it (helper SetTerrain).
        const layout = JSON.parse(fs.readFileSync(layoutFile, 'utf8')) as LayoutFile;
        if (scene.ground && scene.cell.grid && groundChanged(scene.ground.heights, layout.ground?.heights)) {
            result.ground = await this.engine.helper.call<NonNullable<ImportCellResult['ground']>>('setTerrain', {
                plugin, projectPluginDir: paths.plugin, grid: `${scene.cell.grid[0]}, ${scene.cell.grid[1]}`, heights: layout.ground!.heights,
                ...(scene.cell.worldspace ? { worldspace: scene.cell.worldspace.formId } : {})
            }, BUILD_TIMEOUT_MS);
        }
        return result;
    }

    // Nexus needs neither the instance nor the helper: these answer whatever state the engine is in.
    async searchNexus(query: NexusSearchQuery): Promise<NexusSearchResult> {
        return this.nexus.searchMods(query);
    }

    async readNexusMod(modId: number): Promise<NexusMod> {
        return this.nexus.readMod(modId);
    }

    async downloadNexusFile(modId: number, fileId: number): Promise<NexusDownload> {
        return this.nexus.downloadFile(modId, fileId, this.engine.modManager.readInstance().downloadsDir);
    }

    // ---------------------------------------------------------------- installing

    /**
     * A downloaded file into the instance as a mod, the way deployProject puts a project there: the mod
     * manager may refuse first, the mod is enabled and its plugins activated, and the mod manager is
     * told. Whatever can be refused is refused before anything is written; a FOMOD installer's first
     * answer writes nothing at all.
     */
    async listMods(query: ModListQuery): Promise<ModList> {
        const manager = this.engine.modManager;
        return listedMods(manager, manager.readInstance(), query);
    }

    /** The last game check's note, for one load order: computed once the helper is up, never waited for while it starts. */
    private gameCheck?: { key: string; note?: string };

    /**
     * One line for healthcheck when SKSE or a plugin does not fit the game — so a modder learns it
     * without knowing to ask (the user's decision, 2026-10-17). Undefined when all fits, or nothing is known yet.
     */
    private async gameVersionNote(): Promise<string | undefined> {
        const engine = this.engine;
        if (!engine.ready) return undefined;
        const manager = engine.modManager;
        const key = `${manager.fingerprint()}|${engine.gameVersion}`;
        if (this.gameCheck?.key === key) return this.gameCheck.note;
        let note: string | undefined;
        try {
            const check = await checkGameVersion(manager, engine.platform, engine.helper, manager.readInstance(), engine.gameVersion);
            const count = (verdicts: string[]) => check.problems.filter(p => verdicts.includes(p.verdict)).reduce((n, p) => n + p.plugins.length, 0);
            const broken = count(['refusedAtLastStart', 'willNotLoad']);
            const maybe = count(['mayNotWork']);
            const game = check.game.runtime;
            note = !check.skse.installed ? `SKSE is not installed in the game folder — checkGameVersion says what to do.`
                : check.skse.forThisRuntime === false ? `The SKSE in the game folder is not built for game ${game} — checkGameVersion says what to do.`
                : broken + maybe > 0
                    ? `${broken + maybe} SKSE plugin${broken + maybe === 1 ? ' does' : 's do'} not fit game ${game}` +
                      `${maybe > 0 ? `, ${maybe} of them may still work` : ''} — checkGameVersion names them, and what to do.`
                    : undefined;
        } catch {
            note = undefined;
        }
        this.gameCheck = { key, note };
        return note;
    }

    async checkGameVersion(): Promise<GameVersionCheck> {
        // The helper reads the DLLs, and the game's version on connecting.
        await this.engine.ensureFresh();
        const manager = this.engine.modManager;
        return checkGameVersion(manager, this.engine.platform, this.engine.helper, manager.readInstance(), this.engine.gameVersion);
    }

    async installMod(request: InstallModRequest): Promise<InstallModResult | InstallModQuestions> {
        const modManager = this.engine.modManager;
        const instance = modManager.readInstance();
        const file = findDownload(instance.downloadsDir, request.file);
        const fileName = path.basename(file);
        const meta = readDownloadMeta(file);
        const mod = request.name !== undefined ? checkModName(request.name) : defaultModName(meta, fileName);
        const modFolder = path.join(instance.modsDir, mod);

        if (isProjectMod(this.engine.projectsDir, mod)) {
            throw new InstallError(
                `'${mod}' is the mod of the project '${mod}', which deployProject puts into the instance. Give \`name\` to ` +
                `install this file as another mod.`);
        }
        const exists = fs.existsSync(modFolder) && fs.readdirSync(modFolder).length > 0;
        if (exists && !request.replace) {
            throw new InstallError(
                `A mod named '${mod}' is already in the instance. Pass replace: true to replace its files with this file's, ` +
                `or give \`name\` to install it as another mod.`);
        }

        await this.engine.ensureFresh();
        const listing = await this.engine.helper.call<ArchiveListing>('readArchive', { archivePath: file }, BUILD_TIMEOUT_MS);
        const warnings: string[] = [];
        let plan: Pick<InstallModResult, 'installer' | 'root' | 'chosen' | 'ignoredChoices'> & { files: UnpackedFile[] };
        if (listing.fomod && request.root === undefined) {
            const fomod = new FomodInstaller(listing, this.installEnvironment(instance));
            if (request.choices === undefined && fomod.asksQuestions) {
                return { status: 'chooseOptions', mod, file, installer: fomod.describe(), nextStep: CHOOSE_OPTIONS };
            }
            const planned = fomod.plan(request.choices ?? []);
            warnings.push(...planned.warnings);
            plan = {
                installer: 'fomod', files: planned.files, chosen: planned.chosen,
                ...(planned.ignoredChoices.length > 0 ? { ignoredChoices: planned.ignoredChoices } : {})
            };
        } else {
            const root = request.root !== undefined ? checkRoot(listing.files, request.root) : findDataRoot(listing.files);
            if (root === undefined) {
                throw new InstallError(
                    `Which folder of ${fileName} is the mod is not clear: it has ${topFolders(listing.files).join(', ')} at the ` +
                    `top, likely options to choose from. Call installMod again with \`root\` set to the one the user wants — ` +
                    `the mod page (readNexusMod) says what each is.`);
            }
            if (request.choices !== undefined) warnings.push(`${fileName} was installed without its FOMOD installer, so \`choices\` were not used.`);
            plan = { installer: 'none', root, files: plainLayout(listing.files, root) };
        }
        if (plan.files.length === 0) {
            throw new InstallError(`Nothing of ${fileName} would be installed. Choose other options, or another \`root\`.`);
        }
        const refusal = await modManager.writeRefusal(instance, 'add');
        if (refusal) throw new InstallError(refusal);

        // Written from here on. A half-unpacked mod is worse than none: the file is still there to install again.
        if (exists) fs.rmSync(modFolder, { recursive: true, force: true });
        fs.mkdirSync(modFolder, { recursive: true });
        try {
            const unpacking = this.engine.helper.call('extractArchive', { archivePath: file, targetDir: modFolder, files: plan.files }, BUILD_TIMEOUT_MS);
            await (exists ? afterLocalWrites(unpacking) : unpacking);
        } catch (error) {
            fs.rmSync(modFolder, { recursive: true, force: true });
            throw error;
        }
        fs.writeFileSync(path.join(modFolder, 'meta.ini'), modMetaIni(meta, fileName), 'utf8');
        markDownloadInstalled(file);

        const plugins = await this.pluginsOf(modFolder, warnings);
        const order = pluginOrder(plugins);
        const loaded = new Set([
            ...modManager.readLoadOrder(instance).filter(plugin => plugin.active).map(plugin => plugin.name.toLowerCase()),
            ...order.map(name => name.toLowerCase())
        ]);
        const missingMasters = order
            .map(name => ({ plugin: name, masters: plugins.find(p => p.name === name)!.masters.filter(m => !loaded.has(m.toLowerCase())) }))
            .filter(entry => entry.masters.length > 0);
        if (missingMasters.length > 0) {
            const masters = [...new Set(missingMasters.flatMap(entry => entry.masters))];
            warnings.push(
                `The game will not start while ${masters.join(', ')} ${masters.length === 1 ? 'is' : 'are'} missing from the load ` +
                `order: install the mods ${masters.length === 1 ? 'it comes' : 'they come'} from — readNexusMod lists a mod's requirements.`);
        }

        // A plugin new to the load order that an active plugin needs goes before it, not to the end.
        const loadOrder = modManager.readLoadOrder(instance);
        const listed = new Set(loadOrder.map(plugin => plugin.name.toLowerCase()));
        const joining = order.map(name => plugins.find(plugin => plugin.name === name)!);
        let placed = { before: new Map<string, string>(), warnings: [] as string[] };
        if (joining.some(plugin => !listed.has(plugin.name.toLowerCase()))) {
            const files = modManager.resolvePluginFiles(instance, modManager.readModList(instance));
            const read = await afterLocalWrites(this.activeMasters(instance, files));
            placed = placeInLoadOrder(loadOrder.map(plugin => plugin.name), joining,
                new Map(read.map(plugin => [path.basename(plugin.path).toLowerCase(), plugin.masters])));
            warnings.push(...placed.warnings);
        }

        const enabled = modManager.enable(instance, mod, order, placed.before);
        const notice = await modManager.afterDeploy(instance);
        const { files: unpacked, ...how } = plan;
        return {
            mod, modFolder, file,
            files: unpacked.length,
            ...how,
            plugins: order,
            ...(placed.before.size > 0 ? {
                placedBefore: order.filter(name => placed.before.has(name.toLowerCase()))
                    .map(name => ({ plugin: name, before: placed.before.get(name.toLowerCase())! }))
            } : {}),
            ...(missingMasters.length > 0 ? { missingMasters } : {}),
            ...(warnings.length > 0 ? { warnings } : {}),
            ...(exists ? { replaced: true as const } : {}),
            ...(meta.modId ? {
                nexus: { modId: meta.modId, ...(meta.fileId ? { fileId: meta.fileId } : {}), ...(meta.version ? { version: meta.version } : {}) }
            } : {}),
            profile: { name: instance.profile, ...enabled },
            modManager: notice
        };
    }

    /**
     * A mod disabled where it stands, on the user's word: its plugins leave the load order unless another
     * enabled mod, overwrite or the game has the same file, as the mod manager does with a disabled mod's.
     * Everything that can be refused is refused before anything is written; its files stay.
     */
    async disableMod(request: DisableModRequest): Promise<DisableModResult> {
        const modManager = this.engine.modManager;
        const instance = modManager.readInstance();
        const mods = modManager.readModList(instance);
        const entry = findMod(mods, request.mod, instance.profile);
        const mod = entry.name;
        if (!entry.managed) {
            throw new ModError(`'${mod}' is part of the game, which Mod Organizer lists but does not manage: it cannot be disabled.`);
        }
        if (entry.locked) {
            throw new ModError(`Amethyst has '${mod}' locked, and a locked mod cannot be disabled. Unlock it in Amethyst first, then try again.`);
        }
        if (isProjectMod(this.engine.projectsDir, mod)) {
            throw new ModError(
                `'${mod}' is the mod of the project '${mod}', which deployProject keeps enabled. If the user wants it out of ` +
                `the game, they disable it in the mod manager.`);
        }
        const refusal = await modManager.writeRefusal(instance, 'change');
        if (refusal) throw new ModError(refusal);

        // What the game loads once the mod is disabled: a plugin of its that another layer also has stays.
        const files = modManager.resolvePluginFiles(instance, mods.map(m => m === entry ? { ...m, enabled: false } : m));
        const loadOrder = modManager.readLoadOrder(instance);
        const active = new Set(loadOrder.filter(plugin => plugin.active).map(plugin => plugin.name.toLowerCase()));
        const own = pluginFiles(path.join(instance.modsDir, mod));
        const leaving = own.filter(plugin => !files.has(plugin.toLowerCase()));
        const keptActive = entry.enabled
            ? own.filter(plugin => files.has(plugin.toLowerCase()) && active.has(plugin.toLowerCase()))
                .map(plugin => ({ plugin, loadedFrom: ownerOf(files.get(plugin.toLowerCase())!, instance) }))
            : [];

        // Plugins the game still loads that need one taken out. Their headers are read only when one is.
        const gone = new Set(leaving.filter(plugin => active.has(plugin.toLowerCase())).map(plugin => plugin.toLowerCase()));
        const missingMasters: NonNullable<DisableModResult['missingMasters']> = [];
        if (gone.size > 0) {
            for (const plugin of await this.activeMasters(instance, files, gone)) {
                const masters = plugin.masters.filter(master => gone.has(master.toLowerCase()));
                if (masters.length > 0) missingMasters.push({ plugin: path.basename(plugin.path), mod: ownerOf(plugin.path, instance), masters });
            }
        }

        const disabled = modManager.disable(instance, mod, leaving);
        const changed = disabled.modDisabled || disabled.deactivated.length > 0;
        const notice = changed ? await modManager.afterDeploy(instance) : undefined;
        return {
            mod,
            ...(changed ? {} : { note: `'${mod}' was disabled already: nothing changed.` }),
            ...(keptActive.length > 0 ? { keptActive } : {}),
            ...(missingMasters.length > 0 ? { missingMasters, warnings: [needsMastersWarning(missingMasters, mod)] } : {}),
            profile: { name: instance.profile, ...disabled },
            ...(notice ? { modManager: notice } : {})
        };
    }

    /**
     * A disabled mod enabled where it stands, on the user's word — disableMod's mirror: its plugins join the
     * load order as installMod places them, at the end or before an active plugin that needs one.
     */
    async enableMod(request: EnableModRequest): Promise<EnableModResult> {
        const modManager = this.engine.modManager;
        const instance = modManager.readInstance();
        const mods = modManager.readModList(instance);
        const entry = findMod(mods, request.mod, instance.profile);
        const mod = entry.name;
        if (!entry.managed) throw new ModError(`'${mod}' is part of the game, which Mod Organizer lists but does not manage: it is always enabled.`);
        const loadOrder = modManager.readLoadOrder(instance);
        const active = new Set(loadOrder.filter(plugin => plugin.active).map(plugin => plugin.name.toLowerCase()));
        const modFolder = path.join(instance.modsDir, mod);
        const own = pluginFiles(modFolder);
        if (entry.enabled && own.every(plugin => active.has(plugin.toLowerCase()))) {
            return {
                mod, note: `'${mod}' was enabled already: nothing changed.`, plugins: own,
                profile: { name: instance.profile, modEnabled: false, activated: [], addedToLoadOrder: [] }
            };
        }
        const refusal = await modManager.writeRefusal(instance, 'change');
        if (refusal) throw new ModError(refusal);

        const warnings: string[] = [];
        const plugins = await this.pluginsOf(modFolder, warnings);
        const order = pluginOrder(plugins);
        const loaded = new Set([...active, ...order.map(name => name.toLowerCase())]);
        const missingMasters = order
            .map(name => ({ plugin: name, masters: plugins.find(p => p.name === name)!.masters.filter(m => !loaded.has(m.toLowerCase())) }))
            .filter(found => found.masters.length > 0);
        if (missingMasters.length > 0) {
            const masters = [...new Set(missingMasters.flatMap(found => found.masters))];
            warnings.push(`The game will not start while ${masters.join(', ')} ${masters.length === 1 ? 'is' : 'are'} missing from the load ` +
                `order: enable or install the mods ${masters.length === 1 ? 'it comes' : 'they come'} from.`);
        }
        const listed = new Set(loadOrder.map(plugin => plugin.name.toLowerCase()));
        const joining = order.map(name => plugins.find(plugin => plugin.name === name)!);
        let placed = { before: new Map<string, string>(), warnings: [] as string[] };
        if (joining.some(plugin => !listed.has(plugin.name.toLowerCase()))) {
            const files = modManager.resolvePluginFiles(instance, mods);
            const read = await this.activeMasters(instance, files);
            placed = placeInLoadOrder(loadOrder.map(plugin => plugin.name), joining,
                new Map(read.map(plugin => [path.basename(plugin.path).toLowerCase(), plugin.masters])));
            warnings.push(...placed.warnings);
        }

        const enabled = modManager.enable(instance, mod, order, placed.before);
        const notice = await modManager.afterDeploy(instance);
        return {
            mod,
            plugins: order,
            ...(placed.before.size > 0 ? {
                placedBefore: order.filter(name => placed.before.has(name.toLowerCase()))
                    .map(name => ({ plugin: name, before: placed.before.get(name.toLowerCase())! }))
            } : {}),
            ...(missingMasters.length > 0 ? { missingMasters } : {}),
            ...(warnings.length > 0 ? { warnings } : {}),
            profile: { name: instance.profile, ...enabled },
            modManager: notice
        };
    }

    /**
     * A Crash Logger SSE log read for this setup (docs/adr/0011): the newest of this setup's by default —
     * the folder is shared by every setup of the game on the computer — or the one asked for.
     */
    async readCrashLog(request: ReadCrashLogRequest): Promise<CrashReport | CrashLogSection> {
        const { modManager, platform } = this.engine;
        const instance = modManager.readInstance();
        const folder = this.engine.crashLogDir ?? (await myGamesDir(modManager, platform, instance).then(dir => dir && path.join(dir, 'SKSE')));
        if (!folder) {
            throw new CrashLogNotFoundError('The Documents folder, where the game keeps My Games and Crash Logger its logs, could not be ' +
                'found. Set SKYRIM_CRASH_LOG_DIR to the folder with the crash-*.log files.');
        }
        const toLocal = platform.windowsPaths(modManager.winePrefix(instance));
        const gameDir = path.resolve(instance.gameDir).toLowerCase();
        const chosen = chooseCrashLog(folder, request.log, seen => {
            const local = toLocal(seen);
            return !!local && path.resolve(local).toLowerCase() === gameDir;
        });
        if (request.section) return crashLogSection(chosen, request.section);

        if (chosen.thisSetup) await this.changedModels(chosen.log, instance);
        return buildCrashReport(chosen, CRASH_RULES, this.attributor(instance), mod => nexusRecord(path.join(instance.modsDir, mod))?.modId);
    }

    /**
     * The mesh each record of a crash log is drawn with where a mod's plugin changed it to one of its own, set on the record as
     * `model`: the log names the record and the plugin that changed it last, not the mesh. Read from that plugin's file in this
     * setup, enabled or not in the load order. The inn crash of 2026-11-07: RusticTrophies.esp gave the game's
     * WallMountedMudcrab01 a broken mesh, and readCrashLog named the plugin but not the mesh. Left out where the helper fails.
     */
    private async changedModels(log: CrashLog, instance: Instance): Promise<void> {
        const changed = log.records.filter(record => record.lastChangedBy && record.definedBy
            && record.lastChangedBy.toLowerCase() !== record.definedBy.toLowerCase() && !BASE_MASTERS.has(record.lastChangedBy.toLowerCase()));
        if (changed.length === 0) return;
        const files = this.engine.modManager.resolvePluginFiles(instance, this.engine.modManager.readModList(instance));
        const asked = changed.flatMap(record => {
            const file = files.get(record.lastChangedBy!.toLowerCase());
            const id = Number.parseInt(record.formId, 16);
            if (!file || Number.isNaN(id)) return [];
            // A light plugin's FormID is FE, its index, then 12 bits of its own; a full plugin's, 24 bits after its index.
            const own = id >>> 24 === 0xfe ? id & 0xfff : id & 0xffffff;
            return [{ record, formKey: `${own.toString(16).toUpperCase().padStart(6, '0')}:${record.definedBy}`, path: file }];
        });
        if (asked.length === 0) return;
        try {
            await this.engine.ensureFresh();
            const { models } = await this.engine.helper.call<{ models: { formKey: string; model: string; was?: string | null }[] }>(
                'recordModels', { records: asked.map(({ formKey, path: file }) => ({ formKey, path: file })) }, BUILD_TIMEOUT_MS);
            for (const found of models) {
                const { record } = asked.find(item => item.formKey.toLowerCase() === found.formKey.toLowerCase()) ?? {};
                if (!record) continue;
                record.model = `meshes\\${found.model}`;
                if (found.was) record.modelWas = `meshes\\${found.was}`;
            }
        } catch {
            // The log is read without them.
        }
    }

    async readPapyrusLog(request: ReadPapyrusLogRequest): Promise<PapyrusReport | PapyrusScriptEntries> {
        const { modManager, platform } = this.engine;
        const instance = modManager.readInstance();
        const myGames = await myGamesDir(modManager, platform, instance);
        const folder = this.engine.papyrusLogDir ?? (myGames && path.join(myGames, 'Logs', 'Script'));
        if (!folder) {
            throw new PapyrusLogNotFoundError('The Documents folder, where the game keeps My Games and its Papyrus logs, could not be found.');
        }
        const logging = this.papyrusLogging(instance, myGames);
        const file = papyrusLogFile(folder, request.log);
        const turnOn = `editIni {"set": [${['bEnableLogging', 'bEnableTrace', 'bLoadDebugInformation']
            .map(key => `{"section": "Papyrus", "key": "${key}", "value": "1"}`).join(', ')}]}`;
        if (!fs.existsSync(file)) {
            // Under Mod Organizer with the profile's own INIs, the test instance's game wrote no log with the three lines in the
            // profile's Skyrim.ini, and wrote one once My Games' Skyrim.ini had them too (2026-11-08).
            const myGamesIni = myGames && logging && path.resolve(path.dirname(logging.file)) !== path.resolve(myGames)
                ? path.join(myGames, 'Skyrim.ini') : undefined;
            const elsewhere = myGamesIni ? ` Where the game still writes none, it reads its Papyrus settings from ${myGamesIni}: ` +
                'the same three lines go into its [Papyrus] section, by hand and on the user\'s word.' : '';
            throw new PapyrusLogNotFoundError(`There is no ${path.basename(file)} in ${folder}` +
                (logging && !logging.enabled ? `: the game writes none, Papyrus logging is off in ${logging.file}. ` : '. ') +
                (logging?.enabled ? `Papyrus logging is on in ${logging.file}.${elsewhere} ` : '') +
                (logging?.enabled ? '' : `Turning it on is a change to the user's setup — on their word: ${turnOn}. `) +
                'Then the game writes one each time it starts; play until the problem shows, quit the game, and read it again. ' +
                'Logging costs some script time: turn it off after, with value "0".' + (logging?.enabled ? '' : elsewhere));
        }
        const log = parsePapyrusLog(fs.readFileSync(file, 'utf8'));
        if (log.platform && log.platform !== 'PC') {
            throw new PapyrusLogNotFoundError(`${path.basename(file)} is another game's (its log opened "(${log.platform})"), not Skyrim's.`);
        }
        const off = logging && !logging.enabled
            ? `Papyrus logging is off now in ${logging.file}: this log, opened ${log.opened ?? 'at an unknown time'}, is from before. ` : '';
        if (request.script) {
            const wanted = request.script.toLowerCase();
            const entries = log.entries.filter(entry => entry.stack.some(frame => frame.script.toLowerCase() === wanted)
                || entry.message.toLowerCase().includes(wanted));
            const listed = entries.slice(0, 20).map(entry => ({
                line: entry.line, time: entry.time, ...(entry.severity ? { severity: entry.severity } : {}), message: entry.message,
                ...(entry.stack.length ? { stack: entry.stack.map(frame => `[${frame.self}].${frame.script}.${frame.fn}() - ${frame.at}`) } : {})
            }));
            return {
                log: { file, ...(log.opened ? { opened: log.opened } : {}) },
                script: request.script,
                entries: listed,
                ...(entries.length > listed.length ? { more: entries.length - listed.length } : {}),
                ...(logging ? { logging } : {}),
                nextStep: off + (entries.length === 0 ? `No entry names ${request.script}.`
                    : 'Each entry\'s stack runs innermost first: the first frame is where it happened, the last the event the game called.')
            };
        }
        const attributor = this.attributor(instance);
        const report = await buildPapyrusReport(file, log, async scripts => {
            const owners = await attributor.files(scripts.map(script => `scripts\\${script}.pex`));
            const found = new Map<string, { mod: string; file: string }>();
            scripts.forEach(script => {
                const owner = owners.get(`scripts\\${script}.pex`);
                if (owner) found.set(script.toLowerCase(), { mod: owner.mod, file: owner.archive ? `in ${owner.archive}` : 'loose' });
            });
            return found;
        }, await this.formIdPlugins(instance), logging);
        if (off) report.nextStep = off + report.nextStep;
        if (!logging?.debugInfo && log.entries.some(entry => entry.stack.some(frame => / Line \?$/.test(frame.at)))) {
            report.nextStep += ' Its stacks have no line numbers: bLoadDebugInformation=1 in [Papyrus] gives them, on the user\'s word.';
        }
        return report;
    }

    /** Whether the game writes its Papyrus log, by the INIs it reads: the profile's under Mod Organizer, else My Games'. */
    private papyrusLogging(instance: Instance, myGames: string | undefined): PapyrusLogging | undefined {
        const folder = this.engine.modManager.profileIniDir(instance) ?? myGames;
        if (!folder) return undefined;
        const text = (name: string) => {
            const file = findCaseInsensitive(folder, name);
            return file ? fs.readFileSync(file, 'utf8') : '';
        };
        const [skyrim, custom] = [text('Skyrim.ini'), text('SkyrimCustom.ini')];
        // SkyrimCustom.ini's values win over Skyrim.ini's.
        const on = (key: string) => /^\s*(1|true)\s*$/i.test(iniValue(custom, 'Papyrus', key) ?? iniValue(skyrim, 'Papyrus', key) ?? '0');
        return {
            enabled: on('bEnableLogging'), trace: on('bEnableTrace'), debugInfo: on('bLoadDebugInformation'),
            file: findCaseInsensitive(folder, 'Skyrim.ini') ?? path.join(folder, 'Skyrim.ini')
        };
    }

    /**
     * The plugin a runtime FormID's first digits stand for, in the load order now: a full plugin's index, or FE and a light
     * plugin's — an .esl, or a plugin its header flags light. A Papyrus log names no plugins: one written before the load order
     * changed maps to the plugins of now.
     */
    private async formIdPlugins(instance: Instance): Promise<PluginOf> {
        const { modManager } = this.engine;
        const order = modManager.readLoadOrder(instance).filter(plugin => plugin.active).map(plugin => plugin.name);
        const light = new Set(order.filter(name => /\.esl$/i.test(name)).map(name => name.toLowerCase()));
        try {
            const files = modManager.resolvePluginFiles(instance, modManager.readModList(instance));
            const paths = order.filter(name => !light.has(name.toLowerCase())).map(name => files.get(name.toLowerCase()))
                .filter((file): file is string => file !== undefined);
            if (paths.length > 0) {
                await this.engine.ensureFresh();
                const read = await this.engine.helper.call<{ plugins: { path: string; light?: boolean }[] }>('pluginHeaders', { paths }, BUILD_TIMEOUT_MS);
                for (const header of read.plugins) if (header.light) light.add(path.basename(header.path).toLowerCase());
            }
        } catch {
            // Light plugins by their extension alone.
        }
        const full = order.filter(name => !light.has(name.toLowerCase()));
        const small = order.filter(name => light.has(name.toLowerCase()));
        return formId => {
            const index = Number.parseInt(formId.slice(0, 2), 16);
            if (index === 0xfe) return small[Number.parseInt(formId.slice(2, 5), 16)];
            return index === 0xff ? undefined : full[index];
        };
    }

    async movePlugin(request: MovePluginRequest): Promise<MovePluginResult> {
        const { modManager } = this.engine;
        const instance = modManager.readInstance();
        const [where, target] = placeOf(request, 'plugin');
        const order = modManager.readLoadOrder(instance).map(plugin => plugin.name);
        const primary = new Set(primaryPlugins(instance.gameDir).map(name => name.toLowerCase()));
        const same = (a: string) => (b: string) => a.toLowerCase() === b.toLowerCase();
        const placed = (name: string, note?: string): MovePluginResult => {
            const now = modManager.readLoadOrder(instance).map(plugin => plugin.name);
            const at = now.findIndex(same(name));
            return {
                plugin: now[at] ?? name, moved: false, position: at + 1, of: now.length,
                ...(at > 0 ? { loadsAfter: now[at - 1] } : {}), ...(at >= 0 && at < now.length - 1 ? { loadsBefore: now[at + 1] } : {}),
                ...(note ? { note } : {})
            };
        };
        const plugin = order.find(same(request.plugin));
        // After one of the game's own plugins, every other plugin loads already.
        if (plugin && where === 'after' && primary.has(target.toLowerCase()) && !primary.has(plugin.toLowerCase()))
            return placed(plugin, `${plugin} loads after the game's own plugins already, ${target} among them: nothing to move.`);
        // Each plugin's masters and master flag, for the rules a move keeps.
        const files = modManager.resolvePluginFiles(instance, modManager.readModList(instance));
        const paths = order.map(name => files.get(name.toLowerCase())).filter((file): file is string => file !== undefined);
        await this.engine.ensureFresh();
        const read = paths.length === 0 ? { plugins: [] } : await this.engine.helper.call<{ plugins: { path: string; masters?: string[]; master?: boolean }[] }>(
            'pluginHeaders', { paths }, BUILD_TIMEOUT_MS);
        const kinds = new Map<string, PluginKind>(read.plugins.map(header => [path.basename(header.path).toLowerCase(), {
            masters: header.masters ?? [], master: (header.master ?? false) || /\.(esm|esl)$/i.test(header.path)
        }]));
        const refusal = moveRefusal(order, request.plugin, where, target, kinds, primary);
        if (refusal) throw new ModError(refusal);
        const writeRefusal = await modManager.writeRefusal(instance, 'change');
        if (writeRefusal) throw new ModError(writeRefusal);
        const moved = modManager.movePlugin(instance, plugin!, where, order.find(same(target))!);
        const notice = moved ? await modManager.afterDeploy(instance) : undefined;
        return { ...placed(plugin!), moved, ...(notice ? { modManager: notice } : {}) };
    }

    /**
     * LOOT's sort of the profile's load order (loot/lootSort.ts): libloot with LOOT's masterlist and the user's own LOOT
     * rules, over the game's Data and the folders the mod manager lays over it — Mod Organizer's overwrite and enabled mods,
     * the highest priority first. A preview unless `apply`: then the order is written as the manager writes a sort, and the
     * manager told.
     */
    async sortPlugins(request: SortPluginsRequest): Promise<SortPluginsResult> {
        const { modManager } = this.engine;
        const instance = modManager.readInstance();
        const mods = modManager.readModList(instance);
        const order = modManager.readLoadOrder(instance).map(plugin => ({ name: plugin.name, active: plugin.active }));
        const dataPaths = [instance.overwriteDir, ...mods.filter(mod => mod.enabled && mod.managed).map(mod => path.join(instance.modsDir, mod.name))]
            .filter(dir => fs.existsSync(dir));
        const libloot = await this.engine.loot.library(this.engine.lootCacheDir);
        const lists = await this.engine.loot.masterlist(this.engine.lootCacheDir);
        const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'skyrim-loot-'));
        try {
            let sorted: ReturnType<typeof lootSort>;
            try {
                sorted = lootSort({
                    libloot, gameDir: instance.gameDir, order, dataPaths, masterlist: lists.masterlist, prelude: lists.prelude,
                    userlist: lootUserlist(this.engine.platform.name), workDir
                });
            } catch (error) {
                // libloot's own refusal — a cycle among the rules, a plugin it cannot read — said as LOOT says it.
                throw new ModError(`LOOT could not sort this load order: ${(error as Error).message}`);
            }
            const { report } = sorted;
            let applied = false;
            let notice: ModManagerNotice | undefined;
            if (request.apply && report.changed) {
                const refusal = await modManager.writeRefusal(instance, 'change');
                if (refusal) throw new ModError(refusal);
                applied = modManager.setLoadOrder(instance, sorted.sorted);
                if (applied) notice = await modManager.afterDeploy(instance);
            }
            return {
                ...report,
                loot: { libloot: libloot.liblootVersion?.() ?? LIBLOOT_VERSION, masterlist: lists.fetched ? 'fetched' : lists.stale ? 'stale' : 'cached' },
                applied,
                ...(request.apply && !report.changed ? { note: 'LOOT\'s order is the profile\'s already: nothing was written.' } : {}),
                ...(notice ? { modManager: notice } : {})
            };
        } finally {
            fs.rmSync(workDir, { recursive: true, force: true });
        }
    }

    /**
     * The ground of a worldspace changed in a circle (helper Terrain.cs): the project's copies of the cells it touches,
     * each with its Landscape, their heights and normals written anew. The build writes the plugin from them as from any
     * record file.
     */
    async editTerrain(request: EditTerrainRequest): Promise<EditTerrainResult> {
        await this.engine.ensureFresh();
        const paths = projectPaths(this.engine.projectsDir, request.project);
        const { plugin } = readProjectHeader(paths);
        return this.engine.helper.call<EditTerrainResult>('editTerrain', {
            plugin, projectPluginDir: paths.plugin, x: request.x, y: request.y, radius: request.radius, action: request.action,
            ...(request.worldspace ? { worldspace: request.worldspace } : {}),
            ...(request.falloff !== undefined ? { falloff: request.falloff } : {}),
            ...(request.height !== undefined ? { height: request.height } : {}),
            ...(request.amount !== undefined ? { amount: request.amount } : {})
        }, BUILD_TIMEOUT_MS);
    }

    async setModPriority(request: SetModPriorityRequest): Promise<SetModPriorityResult> {
        const { modManager } = this.engine;
        const instance = modManager.readInstance();
        const [where, target] = placeOf(request, 'mod');
        const mods = modManager.readModList(instance);
        const entry = findMod(mods, request.mod, instance.profile);
        const other = findMod(mods, target, instance.profile);
        if (entry.name === other.name) throw new ModError(`${entry.name} is the mod to move: name another to move it ${where}.`);
        for (const mod of [entry, other])
            if (!mod.managed) throw new ModError(`'${mod.name}' is part of the game, which the mod manager lists but does not manage: its place is fixed.`);
        const refusal = await modManager.writeRefusal(instance, 'change');
        if (refusal) throw new ModError(refusal);
        const moved = modManager.moveMod(instance, entry.name, where, other.name);
        const notice = moved ? await modManager.afterDeploy(instance) : undefined;
        // The list is the highest priority first: the mod above it in the list wins over it.
        const now = modManager.readModList(instance).filter(mod => !/_separator$/i.test(mod.name));
        const at = now.findIndex(mod => mod.name === entry.name);
        return {
            mod: entry.name, moved,
            ...(at < now.length - 1 ? { winsOver: now[at + 1].name } : {}),
            ...(at > 0 ? { losesTo: now[at - 1].name } : {}),
            ...(notice ? { modManager: notice } : {})
        };
    }

    async findConflicts(request: FindConflictsRequest): Promise<ConflictReport> {
        await this.engine.ensureFresh();
        const { modManager } = this.engine;
        const instance = modManager.readInstance();
        const mods = modManager.readModList(instance);
        const limit = Math.min(200, Math.max(1, Math.floor(request.limit ?? 25)));
        const mod = request.mod ? findMod(mods, request.mod, instance.profile) : undefined;
        const report: Omit<ConflictReport, 'nextStep'> = {};

        if (request.kind !== 'files') {
            // A mod's record conflicts are its plugins'; a mod without one has none.
            const plugins: (string | undefined)[] = mod ? pluginFiles(path.join(instance.modsDir, mod.name)) : [request.plugin];
            const rows = new Map<string, RecordConflict>();
            let total = 0;
            let compared: string | undefined;
            for (const plugin of plugins) {
                const found = await this.engine.helper.call<{ total: number; records: RecordConflict[]; compared?: string }>('findConflicts', {
                    ...(plugin ? { plugin } : {}), ...(request.record ? { record: request.record } : {}), limit
                }, BUILD_TIMEOUT_MS * 4);
                total += found.total;
                compared ??= found.compared;
                for (const row of found.records) if (rows.size < limit) rows.set(row.formId, row);
            }
            report.records = { total, records: [...rows.values()], ...(compared ? { compared } : {}) };
        }
        if (request.kind !== 'records' && !request.plugin && !request.record) {
            const loadOrder = modManager.readLoadOrder(instance).filter(plugin => plugin.active).map(plugin => plugin.name);
            report.files = await findFileConflicts(instance, mods, loadOrder, async lists => {
                await this.engine.ensureFresh();
                return this.engine.helper.call('archiveFiles', lists, BUILD_TIMEOUT_MS * 4);
            }, { mod: mod?.name, limit });
        }
        return { ...report, nextStep: conflictsNextStep(report) };
    }

    async findGameFiles(files: string[]): Promise<Record<string, GameFile>> {
        return Object.fromEntries(await this.attributor(this.engine.modManager.readInstance()).files(files));
    }

    /** Where the game finds files: loose as the mod manager layers the mods, then in the load order's archives (docs/adr/0012). */
    private attributor(instance: Instance): Attributor {
        const { modManager } = this.engine;
        const mods = modManager.readModList(instance);
        const loadOrder = modManager.readLoadOrder(instance).filter(plugin => plugin.active).map(plugin => plugin.name);
        return new Attributor(modManager, instance, mods, loadOrder, async files => {
            await this.engine.ensureFresh();
            return this.engine.helper.call('bsaFiles', files, BUILD_TIMEOUT_MS);
        });
    }

    /**
     * The project's records on a mesh the game does not have — in no archive it loads, no mod and not the project's assets — each
     * as a warning with the one call that gives it the model of the record most like it the game has. The helper names only the
     * models no record of their type in the load order is on (ModelFiles.cs): eval round 105's sword #1, #3 and #5 built a Flame
     * Blade on a mesh path of their own making, which the game draws nothing for, and each build answered success. A model of
     * "" is a record with none at all (round 93's note #1, round 94's sword #5).
     */
    /**
     * The project's scripted records held against the scripts' sources and the game's scripts (scriptChecks.ts): what builds
     * and does nothing in game. A script the project has no source of is looked up where the game finds it, as a mesh is.
     */
    private async scriptWarnings(paths: ProjectPaths, records: ScriptedRecord[], instance: Instance, mods: ModEntry[]): Promise<string[]> {
        const hasSources = fs.existsSync(paths.scripts) && fs.readdirSync(paths.scripts).some(f => f.toLowerCase().endsWith('.psc'));
        if (records.length === 0 && !hasSources) return [];
        const names = [...new Set(records.flatMap(r => [...r.scripts, ...(r.aliases ?? []).flatMap(a => a.scripts)].map(s => s.name)))];
        const unwritten = names.filter(name => !findCaseInsensitive(paths.scripts, `${name}.psc`));
        const found = unwritten.length > 0 ? await this.findGameFiles(unwritten.map(name => `scripts\\${name}.pex`)) : {};
        return scriptWarnings(records, {
            scriptsDir: paths.scripts,
            sources: new PapyrusSources(scriptSourceFolders(paths.scripts, this.engine.creationKitDir, instance, mods)),
            gameHas: name => !!found[`scripts\\${name}.pex`],
            recordTexts: hasSources ? recordFiles(paths.plugin).map(file => fs.readFileSync(file, 'utf8')).join('\n') : ''
        });
    }

    private async missingModels(paths: ProjectPaths, models: UnknownModel[]): Promise<{ warning: string; fix?: EditFix }[]> {
        if (models.length === 0) return [];
        const found = await this.findGameFiles([...new Set(models.flatMap(m => [m.model, ...m.like.map(l => l.model)]).filter(Boolean))]);
        const has = (model: string) => model !== '' && (!!found[model] || !!findCaseInsensitive(paths.assets, dataPath(model)));
        return models.filter(m => !has(m.model)).map(m => {
            const like = m.like.find(l => has(l.model));
            const field = m.field ?? 'Model.File';
            // An armor's ground model draws it where it lies; worn, its armor addons do (eval round 130's armor #3).
            const ground = field.startsWith('WorldModel.');
            const warning = (m.model
                ? `${m.file}: ${m.editorId}'s ${ground ? `ground model, ${field.replace(/\.Model\.File$/, '')}'s` : 'model'}, ${m.model}, is a mesh the ` +
                  `game does not have — in no archive it loads, no mod and not this project's assets folder — so the game draws nothing for ${m.editorId}`
                : `${m.file}: ${m.editorId} has no Model, so the game draws nothing for it`) +
                (ground ? ' where it lies: dropped, on a shelf or in the inventory (worn, it is drawn by its Armature\'s armor addons). '
                    : ': not in the world, in a hand or in the inventory. ') +
                (like
                    ? `${like.editorId} is the record of the load order most like it: one call gives it ${like.editorId}'s model, ` +
                      `${like.model}, and builds again: ${editCall(paths, { record: m.file, set: { [field]: like.model } })}`
                    : 'Give it the Model lines of a record of the load order like it, as readRecord shows them, and build again') +
                // Split here: Linux's path functions keep a backslash as part of a name.
                (m.model ? `; a mesh of its own goes into the project's assets folder, as ${path.join(paths.assets, ...dataPath(m.model).split('\\'))}.` : '.');
            return { warning, ...(like ? { fix: { file: m.file, set: [{ path: field, value: like.model }], remove: [] } } : {}) };
        });
    }

    /**
     * Settings of the game's own INIs set or removed, on the user's word: the profile's copies when the mod manager
     * keeps the INIs per profile, else those in My Games. Only Skyrim.ini, SkyrimPrefs.ini and SkyrimCustom.ini.
     */
    async editIni(request: EditIniRequest): Promise<EditIniResult> {
        const { modManager, platform } = this.engine;
        const instance = modManager.readInstance();
        const profileDir = modManager.profileIniDir(instance);
        const folder = profileDir ?? await myGamesDir(modManager, platform, instance);
        if (!folder) throw new ModError('The Documents folder, where the game keeps its INIs in My Games, could not be found.');

        const changed: EditIniResult['changed'] = [];
        const unchanged: string[] = [];
        const texts = new Map<string, string>();
        for (const setting of request.set) {
            const name = setting.file ?? 'Skyrim.ini';
            // Mod Organizer names a profile's copies in lower case; the game's own are as the game writes them.
            const file = findCaseInsensitive(folder, name) ?? path.join(folder, profileDir ? name.toLowerCase() : name);
            const text = texts.get(file) ?? (fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '');
            const edit = setIniValue(text, setting.section, setting.key, setting.value);
            texts.set(file, edit.text);
            if (edit.changed) {
                changed.push({
                    file, section: setting.section, key: setting.key,
                    ...(edit.was !== undefined ? { was: edit.was } : {}),
                    ...(setting.value !== null ? { now: setting.value } : {})
                });
            } else {
                unchanged.push(`${name} [${setting.section}] ${setting.key}`);
            }
        }
        for (const file of new Set(changed.map(entry => entry.file))) fs.writeFileSync(file, texts.get(file)!, 'utf8');
        return {
            changed,
            ...(unchanged.length > 0 ? { unchanged } : {}),
            note: (profileDir ? `These are the INIs of the profile '${instance.profile}', which the game reads when started from the mod manager. ` : '') +
                'The game reads them when it starts: a game running now keeps its settings until it is started again.'
        };
    }

    /**
     * The masters of every active plugin but those leaving, read from the copy the game loads: one header
     * each, which on a big list takes a while the first time — 23 s for LoreRim's 3,450 on a hard disk.
     */
    private async activeMasters(
        instance: Instance, files: ReadonlyMap<string, string>, leaving: ReadonlySet<string> = new Set()
    ): Promise<{ path: string; masters: string[] }[]> {
        const paths = this.engine.modManager.readLoadOrder(instance)
            .filter(plugin => plugin.active && !leaving.has(plugin.name.toLowerCase()))
            .map(plugin => files.get(plugin.name.toLowerCase()))
            .filter((file): file is string => file !== undefined);
        if (paths.length === 0) return [];
        await this.engine.ensureFresh();
        const read = await this.engine.helper.call<{ plugins: { path: string; masters?: string[] }[] }>('pluginHeaders', { paths }, BUILD_TIMEOUT_MS);
        return read.plugins.map(plugin => ({ path: plugin.path, masters: plugin.masters ?? [] }));
    }

    /** The plugins at the top of a mod folder, with their masters; a file that is not a plugin is named in `warnings`. */
    private async pluginsOf(modFolder: string, warnings: string[]): Promise<PluginHeader[]> {
        const names = fs.readdirSync(modFolder, { withFileTypes: true })
            .filter(entry => entry.isFile() && /\.(esp|esm|esl)$/i.test(entry.name))
            .map(entry => entry.name);
        if (names.length === 0) return [];
        const read = await afterLocalWrites(this.engine.helper.call<{ plugins: { path: string; masters?: string[]; master?: boolean; error?: string }[] }>(
            'pluginHeaders', { paths: names.map(name => path.join(modFolder, name)) }));
        return read.plugins.map(plugin => {
            const name = path.basename(plugin.path);
            if (plugin.error) warnings.push(`${name} could not be read as a plugin (${plugin.error}); the game may not load it.`);
            return { name, masters: plugin.masters ?? [], master: plugin.master ?? false };
        });
    }

    /** What a FOMOD installer's conditions can ask: a file's state in the instance, the game's version, SKSE. */
    private installEnvironment(instance: Instance): InstallEnvironment {
        const modManager = this.engine.modManager;
        const mods = modManager.readModList(instance);
        const present = modManager.resolvePluginFiles(instance, mods);
        const active = new Set(modManager.readLoadOrder(instance).filter(p => p.active).map(p => p.name.toLowerCase()));
        const folders = [
            instance.overwriteDir,
            ...mods.filter(m => m.enabled && m.managed).map(m => path.join(instance.modsDir, m.name)),
            path.join(instance.gameDir, 'Data')
        ];
        // Any other file is looked for in every enabled mod — thousands, in a big list — so once per file.
        const looked = new Map<string, boolean>();
        return {
            fileState: name => {
                const file = name.replace(/\\/g, '/');
                const key = file.toLowerCase();
                if (/\.(esp|esm|esl)$/i.test(file) && !file.includes('/')) {
                    return present.has(key) ? (active.has(key) ? 'Active' : 'Inactive') : 'Missing';
                }
                if (!looked.has(key)) looked.set(key, folders.some(folder => findCaseInsensitive(folder, file)));
                return looked.get(key) ? 'Active' : 'Missing';
            },
            gameVersion: this.engine.gameVersion,
            scriptExtender: findCaseInsensitive(instance.gameDir, 'skse64_loader.exe') !== undefined
        };
    }
}

/** What the first answer for a FOMOD installer asks the caller to do. */
const CHOOSE_OPTIONS =
    'Choose the options the user wants — ask them where it matters — then call installMod again with the same file ' +
    'and `choices`: the ids of the options to install, e.g. ["1.1.2", "2.1.1"]. A group you choose nothing in keeps ' +
    'its default; `choices: []` installs every default. A step with `shownWhen` is installed only when that holds.';

/** The downloaded file a request names: a full path, or a name in the downloads folder in any letter case. */
function findDownload(downloadsDir: string, file: string): string {
    const given = file.trim();
    const found = path.isAbsolute(given) ? (fs.existsSync(given) ? given : undefined) : findCaseInsensitive(downloadsDir, given);
    if (found && fs.statSync(found).isFile()) return found;
    throw new InstallError(path.isAbsolute(given)
        ? `There is no file at '${given}'. Give the full path of a downloaded .7z, .zip or .rar, or its name in the downloads folder ${downloadsDir}.`
        : `No file '${given}' in the downloads folder ${downloadsDir}. Download it with downloadNexusFile, or give the full ` +
          `path of a file downloaded by hand.`);
}

/** A name the caller gave, as it is — or refused, when it cannot be a folder. */
function checkModName(name: string): string {
    const given = name.trim();
    if (!given || folderName(given) !== given) {
        throw new InstallError(`'${name}' cannot be a mod's name: it becomes a folder, so leave out < > : " / \\ | ? * and a trailing dot.`);
    }
    return given;
}

/**
 * The refusal of copied escapes: each line as written and as it is to be. A line that becomes several is
 * described line by line on one line of text — an answer is JSON, so a line break in it would reach the
 * model as `\n`, the very escape it copied.
 */
/** A line correctCopiedEscapes wrote, as the build's answer names it: the file, the line as it was, and as it is. */
function escapeCorrected({ file, line, kind, text, fixed }: CopiedEscape): string {
    const clip = (part: string) => part.length > 80 ? `${part.slice(0, 80)}…` : part;
    return `${file}, line ${line} (${kind}): ` + (fixed.length === 1
        ? `\`${text}\` → ${fixed[0] ? `\`${fixed[0]}\`` : 'an empty line'}`
        : `\`${clip(text)}\` → ${fixed.length} lines: ${fixed.map((next, k) => `${k + 1} ${next.trim() ? `\`${next}\`` : 'empty'}`).join(', ')}`);
}

/** What disableMod says when plugins the game still loads need one it took out — the game will not start. */
function needsMastersWarning(missing: NonNullable<DisableModResult['missingMasters']>, mod: string): string {
    const plugins = missing.map(entry => entry.plugin);
    const masters = [...new Set(missing.flatMap(entry => entry.masters))];
    const mods = [...new Set(missing.map(entry => entry.mod))];
    const shown = plugins.length > 5 ? `${plugins.slice(0, 5).join(', ')} and ${plugins.length - 5} more` : plugins.join(', ');
    return `The game will not start while ${shown} ${plugins.length === 1 ? 'is' : 'are'} active without ${masters.join(', ')}: ` +
        `disable ${mods.length === 1 ? 'its mod' : 'their mods'} too (${mods.join(', ')}), or enable '${mod}' again in the mod manager.`;
}

/**
 * What to do about the conflicts found: a merged patch for the records — each winning version with the other plugins' lost
 * changes put back — and the mod order for the files, both on the user's word where they change the setup.
 */
function conflictsNextStep(report: Omit<ConflictReport, 'nextStep'>): string {
    const steps: string[] = [];
    const records = report.records?.records ?? [];
    if (records.length > 0) {
        const named = records.slice(0, 5).map(row => JSON.stringify(row.editorId ?? row.formId)).join(', ');
        steps.push(`Each record conflict is settled by a patch that loads last and carries every plugin's changes: addPatch ` +
            `{"project": "ConflictPatch", "records": [${named}], "merge": true} copies each winning version with the lost changes ` +
            `put back, then buildProject. Where a record is marked clash, two plugins changed a field each its own way: the patch ` +
            `keeps the later plugin's, its answer names the field, and the user says which is meant. Deploy the patch on the user's word.`);
    }
    if ((report.files?.pairs.length ?? 0) > 0) {
        steps.push('Each file conflict is settled by the order: two mods\' loose files by the mod order, the mod lower in the ' +
            'list winning; archives by the load order of their plugins; and a loose file beats every archive. Where the losing ' +
            'mod\'s files are the ones meant, it goes below the other with setModPriority — only on the user\'s word; a mod meant to ' +
            'replace another\'s files, as a texture or patch mod is, is in its place already.');
    }
    return steps.length > 0 ? steps.join(' ')
        : 'No conflicts: no plugin lost a change to another, and no mod\'s file is hidden by another\'s.';
}

/** A move's place: just after or just before the plugin or mod named — one of the two. */
function placeOf(request: { after?: string; before?: string }, kind: 'plugin' | 'mod'): [Where, string] {
    const after = request.after?.trim();
    const before = request.before?.trim();
    if (after && before) throw new ModError(`Give \`after\` or \`before\`, not both: the ${kind} it moves next to.`);
    if (after) return ['after', after];
    if (before) return ['before', before];
    throw new ModError(`Give \`after\` — the ${kind} it is to win over — or \`before\` — the ${kind} that is to win over it.`);
}

/** Whether a mod of this name is a project's, deployed by deployProject. */
function isProjectMod(projectsDir: string, mod: string): boolean {
    try {
        return projectExists(projectPaths(projectsDir, mod));
    } catch {
        return false; // not a name a project can have
    }
}

/**
 * How long capabilities() waits for Nexus's key check: under the server's 8 s for the whole gate, so the
 * answer arrives in time — a late one would leave the gate empty and list the download after all.
 */
const CAPABILITIES_WAIT_MS = 5_000;

/** Builds and pulls of big plugins take a while; the helper's default timeout is for lookups. */
const BUILD_TIMEOUT_MS = 600_000;

/**
 * How much copied text one addPatch answer carries: about ten records, and well inside the smallest
 * response budget (responseBudget.ts), which would otherwise withhold the whole answer.
 */
const PATCH_TEXT_BYTES = 16_000;

/** How long a refused editRecord call is kept for its file to be written: a task's turns, not a later one's. */
const REFUSED_EDIT_KEPT_MS = 3_600_000;

/** A value of a call's `set` as a warning names it: a word as it is, anything else as JSON, cut at 60 characters. */
function shownValue(value: unknown): string {
    const said = typeof value === 'string' ? value : JSON.stringify(value) ?? 'nothing';
    return said.length > 60 ? `${said.slice(0, 57)}…` : said;
}

/**
 * A helper call made after this tool call already wrote files — a created project, assigned FormIDs.
 * "The helper was not running" normally means nothing happened, and the server retries; here something
 * did, and a retry would report it wrongly, so the failure is reported as it is instead.
 */
function afterLocalWrites<T>(call: Promise<T>): Promise<T> {
    return call.catch(error => {
        if (!(error instanceof HelperUnavailableError)) throw error;
        throw new HelperError('helperUnavailable',
            `${error.message} This call had already written to the project; check it before repeating the call.`);
    });
}

/**
 * The projects folder's own git rules, written once: build output and deploy state stay out of git,
 * binary assets go to Git LFS (docs/adr/0004) — where it is installed; git ignores a filter it has no
 * driver for, so without it they are ordinary files.
 */
function prepareProjectsFolder(projectsDir: string): void {
    fs.mkdirSync(projectsDir, { recursive: true });
    const ignore = path.join(projectsDir, '.gitignore');
    if (!fs.existsSync(ignore)) fs.writeFileSync(ignore, '# What a build produces, and what was deployed last.\n*/build/\n*/.deploy.json\n');
    const attributes = path.join(projectsDir, '.gitattributes');
    if (!fs.existsSync(attributes)) {
        const binary = ['nif', 'dds', 'tga', 'png', 'hkx', 'wav', 'xwm', 'fuz', 'lip', 'bsa', 'swf', 'pex', 'esp', 'esm', 'esl'];
        fs.writeFileSync(attributes,
            '# Binary assets go to Git LFS where it is installed (git lfs install); without it, git keeps them as ordinary files.\n' +
            binary.map(ext => `*.${ext} filter=lfs diff=lfs merge=lfs -text`).join('\n') + '\n');
    }
}

function matchesAny(credential: string | undefined, accepted: readonly string[]): boolean {
    if (!credential) return false;
    const digest = (value: string) => createHash('sha256').update(value).digest();
    const given = digest(credential);
    return accepted.some(candidate => timingSafeEqual(given, digest(candidate)));
}
