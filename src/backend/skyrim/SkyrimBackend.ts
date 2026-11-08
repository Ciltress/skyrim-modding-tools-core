import type { LootSortReport } from './loot/lootSort.js';
import type { Backend } from '../Backend.js';
import type { InstallerQuestions } from './installer.js';
import type { ModDisabled, ModEnabled, ModManagerName, ModManagerNotice, ProfileChange } from './modManager.js';

/**
 * The domain operations the tools call, on top of the lifecycle every backend has. FormIDs are
 * written `012EB7:Skyrim.esm` throughout — the defining plugin and the local number, never a
 * load-order slot.
 */
export interface SkyrimBackend extends Backend {
    findRecords(query: RecordQuery): Promise<RecordSearchResult>;
    readRecord(request: RecordRequest): Promise<RecordVersions>;

    newProject(request: NewProjectRequest): Promise<ProjectInfo>;
    /** Copies records into a project as overrides — creating the project first when it does not exist. */
    addPatch(request: AddPatchRequest): Promise<AddPatchResult>;
    /** Changes fields of one record file of a project by their paths, with no text to match (docs/adr/0008). */
    editRecord(request: EditRecordRequest): Promise<EditRecordResult>;
    /** Writes the fields a call sent as a text beside a copy — addPatch's `input` — into that copy, as editRecord writes them. */
    editInput(request: EditInputRequest): Promise<EditInputResult>;
    /** Moves a record file into a project's plugin folder: from a wrong folder, a wrong name, or from outside every project. */
    moveRecord(request: MoveRecordRequest): Promise<MoveRecordResult>;
    /** Deletes a record file of the projects folder: a stray copy, or a record the mod no longer needs. */
    deleteRecord(request: DeleteRecordRequest): Promise<DeleteRecordResult>;
    buildProject(project: string): Promise<BuildResult>;
    deployProject(project: string): Promise<DeployResult>;
    pullProject(project: string): Promise<PullResult>;
    /** A cell laid out for Blender in the project's blender/ folder: its references, the meshes and textures they show, the script that places them. */
    exportCell(request: ExportCellRequest): Promise<ExportCellResult>;
    /** What was changed in Blender written into the project: references moved or deleted as copies, copies made as new references. */
    importCell(request: ImportCellRequest): Promise<ImportCellResult>;

    /** Skyrim SE mods on Nexus Mods. Needs no instance, and no API key. */
    searchNexus(query: NexusSearchQuery): Promise<NexusSearchResult>;
    readNexusMod(modId: number): Promise<NexusMod>;
    /** One file of a Nexus mod into the instance's downloads folder. Needs a premium account's key. */
    downloadNexusFile(modId: number, fileId: number): Promise<NexusDownload>;

    /**
     * A downloaded file installed as a mod, enabled, its plugins active. A FOMOD installer is answered
     * first with what it asks, unless the request carries the choices.
     */
    installMod(request: InstallModRequest): Promise<InstallModResult | InstallModQuestions>;

    /** A mod disabled where it stands, its plugins out of the load order; its files stay. */
    disableMod(request: DisableModRequest): Promise<DisableModResult>;
    /** A disabled mod enabled where it stands, its plugins back in the load order. */
    enableMod(request: EnableModRequest): Promise<EnableModResult>;

    /** A plugin moved to just before or after another in the load order: after wins, its records are the game's. */
    movePlugin(request: MovePluginRequest): Promise<MovePluginResult>;
    sortPlugins(request: SortPluginsRequest): Promise<SortPluginsResult>;
    editTerrain(request: EditTerrainRequest): Promise<EditTerrainResult>;
    generateNavmesh(request: GenerateNavmeshRequest): Promise<GenerateNavmeshResult>;
    newWorldspace(request: NewWorldspaceRequest): Promise<NewWorldspaceResult>;

    /** A mod moved to just before or after another in the mod order: after wins, its files are the game's. */
    setModPriority(request: SetModPriorityRequest): Promise<SetModPriorityResult>;

    /** The selected profile's mods, lowest priority first: each one's plugins, Nexus record and SKSE plugins. */
    listMods(query: ModListQuery): Promise<ModList>;

    /** Whether SKSE, Address Library and each SKSE plugin the game loads fit its version — SKSE's log of the last start first. */
    checkGameVersion(): Promise<GameVersionCheck>;

    /** A Crash Logger SSE log of this setup read: the crash, ranked diagnoses and suspect mods — or one raw section. */
    readCrashLog(request: ReadCrashLogRequest): Promise<CrashReport | CrashLogSection>;

    /**
     * A Papyrus log of this setup read: script and install problems grouped and ranked, with the mods of their scripts and the
     * plugins of their records, what the save holds of removed mods, script lag from stack dumps — or one script's entries.
     */
    readPapyrusLog(request: ReadPapyrusLogRequest): Promise<PapyrusReport | PapyrusScriptEntries>;

    /**
     * Mods in conflict: the records whose winning version lost another plugin's changes, change by change, and the files two
     * mods give the game, with the one it takes — of one plugin, one mod, one record, or the whole setup.
     */
    findConflicts(request: FindConflictsRequest): Promise<ConflictReport>;

    /**
     * Where the game finds each file, by its path under Data (`meshes\weapons\iron\longsword.nif`; a .nif without its top
     * folder is a mesh's): the mod that has it loose, or the archive that holds it. A file the game does not have is left out.
     * No tool serves it: the build's model check reads it, and so does the eval.
     */
    findGameFiles(files: string[]): Promise<Record<string, GameFile>>;

    /** Settings of the game's Skyrim.ini, SkyrimPrefs.ini or SkyrimCustom.ini set or removed — those the game reads for this profile. */
    editIni(request: EditIniRequest): Promise<EditIniResult>;
}

export type { GameProblem, GameVersionCheck } from './sksePlugins.js';
import type { GameVersionCheck } from './sksePlugins.js';
export type { CrashLogSection, CrashReport, ReportedDiagnosis, ReportedRecord, Suspect } from './crash/crashReport.js';
export type { PapyrusLogging, PapyrusProblem, PapyrusReport } from './papyrus/papyrusReport.js';
import type { PapyrusLogging, PapyrusReport } from './papyrus/papyrusReport.js';
import type { CrashLogSection, CrashReport } from './crash/crashReport.js';
export type { FileConflictPair, FileConflicts } from './fileConflicts.js';
import type { FileConflicts } from './fileConflicts.js';
/** Where the game finds a file: the mod that has it loose, or the archive that holds it and the mod the archive is in. */
export type { Owner as GameFile } from './crash/attribution.js';
import type { Owner as GameFile } from './crash/attribution.js';
import type { SectionName } from './crash/crashLog.js';

export interface FindConflictsRequest {
    /** Only the record conflicts of this plugin: the records it changes. */
    plugin?: string;
    /** Only this mod's: the files it gives the game, and the record conflicts of its plugins. */
    mod?: string;
    /** Only this record's conflict, by FormID or editor ID. */
    record?: string;
    /** Only record conflicts, or only file conflicts. */
    kind?: 'records' | 'files';
    /** How many of each to list, 1–200; 25 by default. */
    limit?: number;
}

/** A record whose winning version lost changes another plugin made. */
export interface RecordConflict {
    formId: string;
    editorId?: string;
    type: string;
    /** The plugin whose version the game uses. */
    winner: string;
    /** Each plugin whose changes the winner does not carry, with those changes: `Keywords + WeapMaterialSilver`, a field's value. */
    lost: { plugin: string; changes: string[]; more?: number }[];
    /** A field the winner changed too, to a value of its own: which one is meant is a choice. */
    clash?: true;
}

export interface ConflictReport {
    records?: { total: number; records: RecordConflict[]; compared?: string };
    files?: FileConflicts;
    nextStep: string;
}

export interface ReadCrashLogRequest {
    /** A log by its file name, or by a time its name starts with: `2026-10-30`, `2026-10-30 14:02`. The newest of this setup's by default. */
    log?: string;
    /** Only this section of the log, as written. */
    section?: SectionName;
}

export interface ReadPapyrusLogRequest {
    /** Papyrus.0.log, the newest, by default; Papyrus.1.log to Papyrus.3.log, or their numbers "1" to "3", the older sessions'. */
    log?: string;
    /** Only this script's entries, each with its stack: the problem's detail. */
    script?: string;
}

/** One script's entries in a Papyrus log, as written. */
export interface PapyrusScriptEntries {
    log: { file: string; opened?: string };
    script: string;
    entries: { line: number; time: string; severity?: string; message: string; stack?: string[] }[];
    /** Entries past those listed. */
    more?: number;
    logging?: PapyrusLogging;
    nextStep: string;
}

export interface EnableModRequest {
    /** The mod's name as listMods shows it; a name differing only in letter case finds it too. */
    mod: string;
}

export interface EnableModResult {
    mod: string;
    /** Set when the mod was enabled already, and nothing changed. */
    note?: string;
    plugins: string[];
    /** Plugins new to the load order placed before an active plugin that needs them. */
    placedBefore?: { plugin: string; before: string }[];
    /** Its plugins needing masters no active plugin is: the game will not start until their mods are enabled too. */
    missingMasters?: { plugin: string; masters: string[] }[];
    warnings?: string[];
    profile: { name: string } & ModEnabled;
    modManager?: ModManagerNotice;
}

export interface MovePluginRequest {
    plugin: string;
    /** The plugin to load just after: the moved one's records win over it. */
    after?: string;
    /** The plugin to load just before: its records win over the moved one's. */
    before?: string;
}

export interface MovePluginResult {
    plugin: string;
    /** False where it stood there already. */
    moved: boolean;
    /** Where it loads now: its place of how many, and the plugins either side. */
    position: number;
    of: number;
    loadsAfter?: string;
    loadsBefore?: string;
    note?: string;
    modManager?: ModManagerNotice;
}

export interface EditTerrainRequest {
    project: string;
    /** The worldspace, by editor ID or FormID; Tamriel unless given. */
    worldspace?: string;
    /** The centre of the edit, in world units. */
    x: number;
    y: number;
    /** Changed fully within this radius… */
    radius: number;
    /** …and blended into the ground around over this far beyond it; half the radius unless given. */
    falloff?: number;
    action: 'flatten' | 'raise' | 'lower' | 'smooth';
    /** flatten: the height to flatten to; the ground's at the centre unless given. */
    height?: number;
    /** raise and lower: by how many units. */
    amount?: number;
}

export interface EditTerrainResult {
    worldspace: string;
    action: string;
    /** flatten: the height flattened to. */
    height?: number | null;
    /** Each cell whose ground the project now changes, by how much at most, and whether the game has a navmesh there. */
    cells: { cell: string; grid: string; file: string; raised: number; lowered: number; navmesh: boolean }[];
    /** Steps of the ground too steep for the game's heights, cut. */
    clamped: number;
    /** References the game places where the ground moved under them by 24 units or more: they float or sink now. */
    gameReferences: { reference: string; base: string; position: string; groundMoved: number }[];
}

export interface GenerateNavmeshRequest {
    project: string;
    /** The cell, by editor ID or FormID: an interior or an exterior cell of the load order, or an interior the project made. */
    cell: string;
    /**
     * importCell's: the cell's navmeshes as edited in Blender, made the cell's navmesh instead of one from the collision.
     * Not a tool argument.
     */
    surface?: SceneNavmesh[];
}

/** A navmesh as exportCell hands it to Blender and export_layout gives it back: game units, triangles by vertex index. */
export interface SceneNavmesh {
    formId: string;
    vertices: [number, number, number][];
    triangles: [number, number, number][];
}

/** A cell's navmesh made from the collision of what stands in it (helper Navmesh.cs), written into the project. */
export interface GenerateNavmeshResult {
    cell: string;
    /** The navmesh written: the cell's own overridden, or a new one. */
    navmesh: string;
    replaced: boolean;
    triangles: number;
    vertices: number;
    /** The project's files written: the navmesh's, and the map entry's (NAVI) where it is new or its doors changed. */
    files: string[];
    /** Each load door, the navmesh and the triangle before it. */
    doors: { door: string; navmesh: string; triangle: number }[];
    /** The references whose meshes have collision, those without, and meshes that could not be read. None from Blender. */
    objects?: { withCollision: number; withoutCollision: number; unreadMeshes: string[] } | null;
    /** Pieces left out: table tops, shelves, gaps behind furniture no actor reaches. None from Blender. */
    left?: { pieces: number; triangles: number } | null;
    /**
     * The surface brought back from Blender (importCell): triangles turned counter-clockwise, dropped (no area seen from above,
     * or drawn twice), left outside the cell, and the navmeshes whose object did not come back — each kept as it was.
     */
    fromBlender?: { turned: number; dropped: number; outside: number; notBack: string[] } | null;
    /** The navmesh it replaced: its size, and how much of each lies on the other (0–1). */
    before?: { triangles: number; areaM2: number; nowAreaM2: number; oldCovered: number; newOnOld: number };
    /** A cell of several navmeshes: each one's share of the new surface; one no new triangle lies on is kept as it was. */
    navmeshes?: { navmesh: string; triangles: number; kept?: boolean }[];
    /** Outdoors, the neighbouring cells' navmeshes whose links across the cell's edges were made anew. */
    neighbours?: { navmesh: string; cell: string | null }[];
    /** The project was built first: its own references are read from the built plugin. */
    builtFirst?: boolean;
}

export interface NewWorldspaceRequest {
    project: string;
    /** The worldspace's editor ID; its start cell is named after it, `<editorId>Start`. */
    editorId: string;
    /** Its in-game name. Defaults to the editor ID. */
    name?: string;
    /** Its width in cells, each 4096 units: cells × cells around grid 0, 0. Defaults to 4. */
    cells?: number;
    /** The ground's height everywhere, when no heightmap is given. Defaults to 0. */
    height?: number;
    /** A heightmap's full path: a PNG or a RAW file, brighter higher, its top north, stretched over the whole worldspace. */
    heightmap?: string;
    /** The heights the heightmap's darkest and brightest pixels stand for. Default 0 and 4096. */
    lowest?: number;
    highest?: number;
    /** The water's height; none without it. */
    water?: number;
    /** The sky and weather, a climate's editor ID. Defaults to SkyrimClimate. */
    climate?: string;
}

/** A worldspace of the project's own, its cells and ground written into the project (helper SessionWorldspace.cs). */
export interface NewWorldspaceResult {
    project: string;
    worldspace: string;
    editorId: string;
    cells: number;
    grid: { from: string; to: string };
    /** Cell 0, 0, named for `coc`, and the COC marker on its ground where coc and a door arrive. */
    start: { cell: string; formId: string; marker: string };
    /** The ground's heights, and how many steps were too steep for the game's landscape and were cut. */
    ground: { lowest: number; highest: number; clamped: number };
    water: number | null;
    climate: string;
    /** How many project files were written, and the worldspace's own. */
    files: number;
    worldspaceFile?: string | null;
}

export interface SortPluginsRequest {
    /** Write LOOT's order into the profile — only on the user's word. Without it the sort is a preview. */
    apply?: boolean;
}

/** LOOT's sort of the load order and what LOOT says of the setup (loot/lootSort.ts), and whether it was written. */
export interface SortPluginsResult extends LootSortReport {
    /** libloot's version, and whether the masterlist was fetched now or is an older one kept for offline. */
    loot: { libloot: string; masterlist: 'fetched' | 'cached' | 'stale' };
    applied: boolean;
    note?: string;
    modManager?: ModManagerNotice;
}

export interface SetModPriorityRequest {
    mod: string;
    /** The mod to come just after in the mod order: the moved mod's files win over its. */
    after?: string;
    /** The mod to come just before: its files win over the moved mod's. */
    before?: string;
}

export interface SetModPriorityResult {
    mod: string;
    /** False where it stood there already. */
    moved: boolean;
    /** The mods either side of it now: the one whose files it wins over, the one whose files win over its. */
    winsOver?: string;
    losesTo?: string;
    modManager?: ModManagerNotice;
}

export const INI_FILES = ['Skyrim.ini', 'SkyrimPrefs.ini', 'SkyrimCustom.ini'] as const;
export type IniFile = typeof INI_FILES[number];

export interface IniSetting {
    /** Skyrim.ini unless given. */
    file?: IniFile;
    /** `General`, without brackets. */
    section: string;
    key: string;
    /** The value as the INI holds it; null removes the setting. */
    value: string | null;
}

export interface EditIniRequest {
    set: IniSetting[];
}

export interface EditIniResult {
    /** Each setting as it was and is, by the file it is in. */
    changed: { file: string; section: string; key: string; was?: string; now?: string }[];
    /** Settings that already had the value asked for. */
    unchanged?: string[];
    note: string;
}

export interface ModListQuery {
    /** Only the mods whose name holds this, in any case. */
    text?: string;
    enabledOnly?: boolean;
    /** Each mod's plugins and its whole Nexus record; without, one short row a mod. */
    details?: boolean;
    /** How many mods to answer, from `offset`; all of them unless given. */
    limit?: number;
    offset?: number;
}

export interface ModList {
    modManager: ModManagerName;
    profile: string;
    /** How the list is ordered, in words. */
    order: string;
    /** The mods matched, before the page was cut. */
    total: number;
    enabled: number;
    mods: ListedMod[];
    /** How to get the rest, when the page did not hold every mod matched. */
    next?: string;
    /** What a filter left out, when it left nothing. */
    note?: string;
}

export interface ListedMod {
    name: string;
    enabled: boolean;
    /** Mod Organizer's priority, separators counted: 0 is the lowest, and of two mods with the same file the higher one's is used. */
    priority: number;
    /** The separator the mod is listed under. */
    section?: string;
    /** With details. */
    plugins?: ListedPlugin[];
    /** What the mod manager recorded when it installed the mod from Nexus Mods (meta.ini): the modId always, the rest with details. */
    nexus?: { modId: number; fileId?: number; version?: string; file?: string };
    /** Whether it carries SKSE plugins: DLLs in SKSE/Plugins. */
    skse?: true;
}

export interface ListedPlugin {
    name: string;
    /** Whether the game loads this copy: the mod enabled, the plugin active, and no higher mod's copy of it. */
    active: boolean;
    /** Where the copy the game loads comes from instead: a higher mod, overwrite, or the game's Data folder. */
    loadedFrom?: string;
}

export interface InstallModRequest {
    /** The downloaded file: its full path, or its name in the instance's downloads folder. */
    file: string;
    /** The mod's name in the mod manager. Defaults to the Nexus mod's name, else the file's. */
    name?: string;
    /** FOMOD option ids, `step.group.option`. Groups they name nothing of keep the installer's defaults. */
    choices?: string[];
    /** The folder of the file to install as the mod's Data, skipping any installer; '' is the top. */
    root?: string;
    /** Replace the files of a mod of the same name. */
    replace?: boolean;
}

/** The first answer for a FOMOD installer: what it asks. Nothing was written. */
export interface InstallModQuestions {
    status: 'chooseOptions';
    /** The name the mod will get. */
    mod: string;
    file: string;
    installer: InstallerQuestions;
    nextStep: string;
}

export interface InstallModResult {
    mod: string;
    modFolder: string;
    /** The downloaded file it came from. */
    file: string;
    /** How many files the mod got. */
    files: number;
    /** `fomod` when its FOMOD installer chose the files, `none` when a folder of the file is the mod. */
    installer: 'fomod' | 'none';
    /** The folder of the file installed as Data; '' is its top. */
    root?: string;
    /** What the FOMOD installer installed, step by step — defaults included. */
    chosen?: { step: string; group: string; options: string[] }[];
    /** Choices in steps the other choices left hidden, which installed nothing. */
    ignoredChoices?: string[];
    /** The mod's plugins, in the order they now load. */
    plugins: string[];
    /** Plugins put before an active plugin that needs them as a master, instead of at the end of the load order. */
    placedBefore?: { plugin: string; before: string }[];
    /** Plugins needing masters the load order lacks: the game will not start until those are installed. */
    missingMasters?: { plugin: string; masters: string[] }[];
    warnings?: string[];
    /** Set when a mod of the same name was replaced. */
    replaced?: true;
    /** The Nexus mod and file, from the download's record. */
    nexus?: { modId: number; fileId?: number; version?: string };
    profile: { name: string } & ModEnabled;
    modManager: ModManagerNotice;
}

export interface DisableModRequest {
    /** The mod's name as listMods shows it; a name differing only in letter case finds it too. */
    mod: string;
}

export interface DisableModResult {
    /** The mod's name as the mod manager has it. */
    mod: string;
    /** Set when the mod was disabled already, and nothing changed. */
    note?: string;
    /** Its plugins the game still loads, because another enabled mod, overwrite or the game has the same file. */
    keptActive?: { plugin: string; loadedFrom: string }[];
    /** Active plugins needing a plugin taken out: the game will not start until their mods are disabled too. */
    missingMasters?: { plugin: string; mod: string; masters: string[] }[];
    warnings?: string[];
    profile: { name: string } & ModDisabled;
    /** What the mod manager was told; absent when nothing changed. */
    modManager?: ModManagerNotice;
}

export interface NewProjectRequest {
    /** Folder name under the projects folder, and the mod's name in Mod Organizer. */
    project: string;
    /** Plugin file name. Defaults to `<project>.esp`. */
    plugin?: string;
    /** Light (ESL-flagged). Defaults to true. */
    light?: boolean;
}

export interface ProjectInfo {
    project: string;
    plugin: string;
    light: boolean;
    /** The project's folder on disk. */
    path: string;
    /** Where its records live, one YAML file each. */
    pluginFolder: string;
    /** Where a record's file goes, in full paths, and the group folders' names. */
    writeRecordsTo: string;
    created: boolean;
}

export interface AddPatchRequest {
    project: string;
    /** FormIDs or editor IDs of the records to override. */
    records: string[];
    /** Copy that plugin's version instead of the winning one. */
    from?: string;
    /** Put back into each copy the changes other plugins made that its winning version lost: a conflict patch. */
    merge?: boolean;
    /** Plugin file name when the project is created here. Defaults to `<project>.esp`. */
    plugin?: string;
}

/** What a merged copy was given back: each plugin's lost changes, and the fields two plugins changed each its own way. */
export interface MergedChanges {
    plugins: string[];
    changes: string[];
    clashes?: string[];
    /** Set where the changes could not be written into the copy: they are named for editRecord. */
    notWritten?: string;
}

/** A file in a project: the full path a file tool opens as it stands, and what it holds. */
export interface ProjectFile {
    path: string;
    /** Left out once an answer carries enough text — `textLeftOut` then says so. */
    text?: string;
}

export interface EditRecordRequest {
    project: string;
    /** The record file: its path in the plugin folder (`Weapons/FlameBlade.yaml`), its file name, or the record's EditorID. */
    record: string;
    /** Fields by path — `Data.Damage`, `Name`, `Effects[0].Data.Magnitude` — and the value each takes. */
    set?: Record<string, unknown>;
    /** Paths of fields, or of a list's items, to delete. */
    remove?: string[];
}

export interface EditRecordResult {
    /** The full path of the file changed. */
    file: string;
    /** Each change, as `set Data.Damage: 7 → 12`. */
    changed: string[];
    /** The file's text after the change. */
    text: string;
}

export interface EditInputRequest {
    project: string;
    /** The copy's file, as EditRecordRequest's `record`. */
    record: string;
    /** The text sent: YAML fields — `Entries:` and the items to add — maybe as a patch's lines or fenced. */
    input: string;
}

export interface EditInputResult {
    /** The full path of the copy's file. */
    file: string;
    /** Each change made, as editRecord's. */
    changed: string[];
    /** The items left out, the copy holding them already. */
    kept: string[];
    /** The text read as no fields: nothing was written. */
    unread: boolean;
}

export interface MoveRecordRequest {
    project: string;
    /**
     * The file to move: its path in the project's plugin folder (`Notes/MavenWarning.yaml`), the record's EditorID, or a
     * full path anywhere in the projects folder, for a file written outside the plugin folder.
     */
    from: string;
    /** Where it goes: its path in the project's plugin folder, `Books/MavenWarning.yaml`. */
    to: string;
}

export interface MoveRecordResult {
    /** Both full paths. */
    moved: { from: string; to: string };
}

export interface DeleteRecordRequest {
    project: string;
    /** As MoveRecordRequest's `from`. */
    record: string;
}

export interface DeleteRecordResult {
    /** The full path of the file deleted. */
    deleted: string;
}

export interface AddPatchResult {
    project: ProjectInfo;
    added: { formId: string; editorId: string | null; type: string; from: string; files: ProjectFile[]; merged?: MergedChanges }[];
    /** Records the project holds already, not copied again; with `merge`, the other plugins' lost changes written into the copy. */
    alreadyInProject: { formId: string; files: ProjectFile[]; merged?: MergedChanges; from?: string; editorId?: string | null }[];
    /** Set when some files come without their text. */
    textLeftOut?: string;
}

export interface BuildResult {
    project: string;
    plugin: string;
    output: string;
    /**
     * What the build wrote into the files where a fix had one right answer — escapes copied out of a JSON answer, a
     * FormKey copied into a new record from the record read, past a light plugin's last FormID or shared by two files no
     * build gave it to, a line written twice the same, an EditorID taken from the file's name or written with its letters alone, a field moved to the one place its record has it or taken out
     * where that place says the same, a magic effect's Data moved into the one Effects item that names it or taken out where that item has the same
     * — each with its file's full path and what was changed.
     */
    corrected?: string[];
    records: number;
    masters: string[];
    headerVersion: number;
    /** New records that got their FormID in this build, each with the full path of its file. */
    assignedFormIds: { formId: string; editorId: string | null; file: string }[];
    /**
     * Links written by editor ID and the FormID each names — `from` as written, `to` the FormID — each with the full
     * path of its file. The files keep the editor IDs; links.lock.json beside the plugin folder keeps what each link to
     * the load order named, and the next build keeps to it.
     */
    resolvedLinks: { file: string; field: string; from: string; to: string }[];
    /**
     * Each record the plugin holds, read back from it: type, editor ID, name, new or an override, and what a player sees —
     * a weapon's damage, weight, value, enchantment and charge, an enchantment's or spell's effects, an NPC's race, sex
     * and level. The first 20, then how many more.
     */
    built: string[];
    scripts: { compiled: number };
    /** What did not stop the build but was not built: record files outside the plugin folder. */
    warnings?: string[];
    /** For each file two or more warnings give a fix on, the one call that makes them all and builds again. */
    combined?: { file: string; count: number; call: string }[];
}

export interface DeployResult {
    project: string;
    plugin: string;
    modFolder: string;
    files: number;
    /** Copies of the plugin that outranked the mod's — in overwrite — already pulled into the project, and removed so they no longer hide it. */
    removedCopies?: string[];
    /** What happened in the selected profile. */
    profile: { name: string } & ProfileChange;
    /** What the mod manager was told. */
    modManager: ModManagerNotice;
}

export interface PullResult {
    project: string;
    plugin: string;
    source: string;
    /** Full paths of the project's files, as for addPatch. */
    added: string[];
    changed: string[];
    removed: string[];
    /** Set when the pull could not check git, so nothing guarded uncommitted work. */
    warning?: string;
}

export interface ExportCellRequest {
    project: string;
    /** The cell: an interior's editor ID like WhiterunBreezehome, an exterior cell's like Riverwood, or its FormID. */
    cell: string;
    /** Copy the textures the meshes name too. Defaults to true; without them Blender shows the meshes grey. */
    textures?: boolean;
}

export interface ExportCellResult {
    project: string;
    cell: string;
    /** The folder the cell was written into: cell.json, meshes/, textures/ and the scripts. */
    folder: string;
    references: number;
    meshes: number;
    textures: number;
    /** Meshes and textures no mod and no archive of the setup has. */
    missing: string[];
    /** The Python that places the cell in Blender, and the one that writes its layout. */
    importScript: string;
    exportScript: string;
    /** The cell placed and saved by Blender in the background — open it, or work on it through Blender's MCP server's _for_cli tools. */
    blendFile?: string;
    /** Why there is no cell.blend: no Blender found, or no PyNifly in it. */
    blendFileNote?: string;
}

export interface ImportCellRequest {
    project: string;
    /** The cell exported, by the name of its folder under the project's blender/ folder (its editor ID). Defaults to the only one. */
    cell?: string;
}

export interface ImportCellResult {
    project: string;
    folder: string;
    /** Full paths of the reference files written: moved, copies made, deleted ones disabled or removed. */
    moved: string[];
    added: string[];
    removed: string[];
    unchanged: number;
    /** The ground sculpted in Blender, written into the cell's copy and its neighbours' edges (as editTerrain answers it). */
    ground?: Omit<EditTerrainResult, 'action' | 'height'>;
    /** The navmesh edited in Blender, made the cell's (as generateNavmesh answers it). */
    navmesh?: GenerateNavmeshResult;
}

export interface RecordQuery {
    /** Case-insensitive text in the editor ID or the name. */
    text?: string;
    /** Exact lookups: FormIDs or editor IDs. Takes precedence over `text`. */
    records?: string[];
    /** Signatures (`WEAP`) or Mutagen type names (`Weapon`). */
    types?: string[];
    /** Only records this plugin defines or overrides. */
    plugin?: string;
    limit: number;
}

export interface RecordSummary {
    formId: string;
    editorId: string | null;
    name: string | null;
    signature: string;
    type: string;
    /** The plugin that first defined the record. */
    definedIn: string;
    /** The plugin whose version the game uses. */
    winner: string;
    /** How many plugins override it after the one that defined it. */
    overrides: number;
}

export interface RecordSearchResult {
    /** Matches before the limit. */
    total: number;
    records: RecordSummary[];
    /** Exact lookups that matched nothing. */
    notFound?: string[];
    /** With `notFound`: how to search what was not found — by `text`, which also reads in-game names. */
    nextStep?: string;
    /** A type of `types` named by the game's word for what it holds — NOTE — and the record type it was read as: BOOK. */
    typesRead?: string;
    /** A `text` that found nothing, and the singular it found these by: "no record is named 'Steel Arrows': these are 'Steel Arrow'". */
    searchedAs?: string;
}

export interface RecordRequest {
    /** A FormID or an editor ID. */
    record: string;
    /** That plugin's version, instead of the winning one. */
    plugin?: string;
    /** Every plugin's version, winning first. */
    allVersions?: boolean;
    /** For a placeable record: its places only in the cells whose editor ID or name holds this text (`placedIn`). */
    in?: string;
}

export interface RecordFile {
    /**
     * Where the file would sit in a project's plugin folder, e.g. `Weapons/IronSword - 012EB7_Skyrim.esm.yaml`.
     * Not called `path`: that name is kept for full paths a file tool opens (ProjectFile).
     */
    pathInPluginFolder: string;
    text: string;
}

export interface RecordVersion {
    plugin: string;
    winning: boolean;
    files: RecordFile[];
}

/**
 * The Nexus account behind NEXUS_API_KEY: none set; checked, with its name and standing; refused by
 * Nexus (`problem`); or not checked, because Nexus could not be asked (`unchecked`). Never the key.
 */
export type NexusAccount =
    | { apiKey: false }
    | { apiKey: true; name: string; premium: boolean; supporter: boolean }
    | { apiKey: true; problem: string }
    | { apiKey: true; unchecked: string };

export type NexusSort = 'endorsements' | 'downloads' | 'updated';

export interface NexusSearchQuery {
    /** Words in the mod's name, matched in any order. Without it, the catalogue in `sort` order. */
    text?: string;
    /** Most endorsed first unless set. */
    sort?: NexusSort;
    limit: number;
}

export interface NexusModSummary {
    modId: number;
    name: string;
    version: string;
    author: string;
    category: string;
    summary: string;
    endorsements: number;
    downloads: number;
    /** `YYYY-MM-DD`. */
    updated: string;
    /** The mod's page on Nexus Mods. */
    url: string;
    /** Set when Nexus marks the mod as adult content. */
    adult?: true;
}

export interface NexusSearchResult {
    /** Matches before the limit. */
    total: number;
    mods: NexusModSummary[];
}

export interface NexusRequirement {
    /** Set for a requirement on Nexus Mods. */
    modId?: number;
    name: string;
    notes?: string;
    url?: string;
}

/** One download a Nexus mod offers now. */
export interface NexusFileInfo {
    fileId: number;
    name: string;
    version: string;
    /** `main`, `update`, `optional` or `miscellaneous`. */
    category: string;
    /** The file's name as it downloads; Nexus does not say it for newer uploads. */
    fileName?: string;
    sizeBytes: number;
    /** `YYYY-MM-DD`. */
    uploaded: string;
    description?: string;
}

export interface NexusMod extends NexusModSummary {
    uploader: string;
    /** The mod page's description as plain text. */
    description: string;
    /** Set when the description was cut, saying where the rest is. */
    descriptionCut?: string;
    requirements: NexusRequirement[];
    /** DLCs the mod needs, by name. */
    dlcRequirements: string[];
    files: NexusFileInfo[];
    /** Files the page keeps as old versions, archived or removed, left out of `files`. */
    olderFiles: number;
}

/** A Nexus file in the instance's downloads folder. */
export interface NexusDownload {
    /** The downloaded file's full path. */
    path: string;
    modId: number;
    fileId: number;
    modName: string;
    /** The file's name on the mod page. */
    name: string;
    version: string;
    /** `main`, `update`, `optional`, `miscellaneous`… */
    category: string;
    sizeBytes: number;
    /** Set when the file was there already, with the right size, and was not downloaded again. */
    alreadyThere?: true;
}

export interface RecordVersions {
    formId: string;
    editorId: string | null;
    type: string;
    signature: string;
    definedIn: string;
    winner: string;
    /** Every plugin that carries the record, in load order — the last one wins. */
    plugins: string[];
    versions: RecordVersion[];
    /** A cell's: what the load order places in it, and the lines a new reference is written with (CellContents.cs). */
    placed?: CellPlaced;
    /** A dialogue topic's: what is said in it, each response a record of its own in the topic's Responses/ (DialogContents.cs). */
    responses?: TopicResponses;
}

/** A topic's responses, gathered from every plugin's version of it, each as its winning version says it. */
export interface TopicResponses {
    count: number;
    /** At most 30: each response's lines, and its conditions as the Creation Kit lists them — `GetIsID(Hulda) == 1`. */
    responses: { formId: string; editorId?: string; says: string[]; conditions?: string[]; winner?: string }[];
    note: string;
}

/** What is placed in a cell, gathered from every plugin's version of it. */
export interface CellPlaced {
    count: number;
    /** The references by the type of record each places. */
    byKind: Record<string, number>;
    /** Those one places a new object beside — containers, furniture, doors, activators, NPCs, items — at most 40. */
    references: { formId: string; editorId: string | null; base: string; position: string; rotation: string; persistent: boolean }[];
    newReference: { pathInPluginFolder: string; text: string; note: string };
}
