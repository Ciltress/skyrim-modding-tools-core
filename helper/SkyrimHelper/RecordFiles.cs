using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// A project's record files, checked before they are built, so that a mistake is named rather than
/// built: a file outside a group folder or below one, a folder that is no group, a field the record's
/// type does not have, a FormKey that cannot be one — or that names another plugin's record the file
/// does not mean to change. The YAML reader skips what it does not know — an invented <c>damage:</c>
/// gives a weapon without damage and a build that succeeds — and fails on some of it with an exception
/// that names the file but not the field.
/// </summary>
/// <remarks>
/// Fields are a record type's public properties, the names Spriggit writes them under; only the top
/// level of a file is checked. A group's records are files directly in its folder; the records cells,
/// worldspaces and dialogue keep in folders of their own are left to the reader — no other group
/// nests, as five serialized Creation Club plugins show.
/// </remarks>
public static class RecordFiles
{
    /// <summary>The groups whose records sit in folders below the group's own.</summary>
    private static readonly HashSet<string> Nested = new(StringComparer.Ordinal) { "Cells", "Worldspaces", "DialogTopics" };

    /// <summary>A plugin's group folders by name — <c>Weapons</c> → Weapon — and null for a group of nested blocks.</summary>
    private static readonly Dictionary<string, Type?> Groups = typeof(SkyrimMod)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType.IsGenericType && p.PropertyType.Name.Contains("Group", StringComparison.Ordinal))
        .ToDictionary(
            p => p.Name,
            p => p.PropertyType.GetGenericArguments()[0] is var record && typeof(IMajorRecord).IsAssignableFrom(record) ? record : null,
            StringComparer.Ordinal);

    /// <summary>
    /// A vanilla record of each everyday type, to read before writing one: a small model that wrote from
    /// memory invented fields build after build (eval round 11: Gender, Sex, OneHanded, Signature).
    /// </summary>
    private static readonly Dictionary<string, string> Examples = new(StringComparer.Ordinal)
    {
        ["Weapon"] = "IronSword", ["Armor"] = "ArmorIronCuirass", ["Spell"] = "Firebolt",
        ["MagicEffect"] = "FireDamageFFAimed", ["ObjectEffect"] = "EnchWeaponFireDamage01", ["Npc"] = "HousecarlWhiterun",
        // The load order's first of these were a tempering recipe, whose perk condition is NotEqualTo, and a dragon's shout of one
        // word, L_DragonFrostIceStormShout: eval round 128's recipe and shout runs copied both.
        ["ConstructibleObject"] = "RecipeWeaponSteelSword", ["Shout"] = "FrostBreathShout", ["WordOfPower"] = "WordFo"
    };

    /// <summary>The skill whose tables hold a record type's lines — the server's FAMILY_SKILLS — or null.</summary>
    private static string? SkillOf(string type) => type switch
    {
        "Weapon" or "Armor" or "ArmorAddon" or "Ammunition" or "Ingestible" or "Ingredient" or "Book" or "MiscItem"
            or "ConstructibleObject" or "ObjectEffect" => "items-and-crafting",
        "Spell" or "MagicEffect" or "Scroll" or "Shout" => "magic",
        "Npc" => "npcs",
        "LeveledItem" or "LeveledNpc" or "LeveledSpell" => "leveled-lists",
        "Perk" => "perks",
        "Quest" => "quests-and-scripts",
        "Cell" or "PlacedObject" or "PlacedNpc" or "Container" => "world",
        _ => null
    };

    /// <summary>The everyday vanilla record of a type, by editor ID, or null — for messages written without the load order.</summary>
    internal static string? ExampleName(string type) => Examples.GetValueOrDefault(type);

    /// <summary>The editor ID of a vanilla record of the type — the everyday one, else the first the load order has.</summary>
    private static string? ExampleOf(Type type, RecordIndex index) =>
        Examples.TryGetValue(type.Name, out var example) && index.ByEditorId.TryGetValue(example, out var key)
            && index.ByFormKey.TryGetValue(key, out var entry) && entry.Type == type.Name
            ? example
            : index.ByFormKey.Values.FirstOrDefault(e => e.Type == type.Name && e.EditorId is not null)?.EditorId;

    /// <summary>The group folders a new record of an everyday kind goes into, for messages.</summary>
    // "ObjectEffects (enchantments)" was taken for a folder's name, then "ObjectEffectsEnchantments" (eval probe 19).
    public const string CommonGroups = "Weapons, Armors, Spells, MagicEffects, Npcs, LeveledItems, Quests, MiscItems for gold and " +
        "other misc items, and ObjectEffects for an enchantment";

    /// <summary>
    /// The group folder another word names — its record type's own name, or the game's word for it, one
    /// or many: Enchantments is ObjectEffects, Notes is Books, Weapon is Weapons. Null for none.
    /// </summary>
    private static string? GroupCalled(string folder)
    {
        string[] words = [folder, folder.EndsWith("es", StringComparison.OrdinalIgnoreCase) ? folder[..^2] : folder, folder.TrimEnd('s', 'S')];
        foreach (var word in words.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var typeName = RecordIndex.EditorNames.TryGetValue(word, out var named) ? named : word;
            if (Groups.FirstOrDefault(g => g.Value is not null && g.Value.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).Key is { } group)
                return group;
        }
        return null;
    }

    private static readonly ConcurrentDictionary<Type, HashSet<string>> Fields = new();

    private static readonly Regex TopLevelKey = new(@"^([A-Za-z_]\w*)[ \t]*:", RegexOptions.Multiline);
    // A comment after the value is no part of it: `FormKey: 000123:Skyrim.esm # Placeholder` passed for a new record (round 14).
    // A value of words is one too: `FormKey: [Will be assigned]` matched nothing, and the reader's list was refused as
    // "FormKey takes fields of its own" (eval round 51's sword+detailed #1) — taken out now, as any line that is no FormKey.
    private static readonly Regex FormKeyLine = new(@"^FormKey:[ \t]*([^#\r\n]*?)[ \t]*(?:#.*?)?\r?$", RegexOptions.Multiline);

    private static readonly Regex ObjectTypeLine = new(@"^MutagenObjectType:[ \t]*(\w+)", RegexOptions.Multiline);
    private static readonly Regex EditorIdLine = new(@"^EditorID:[ \t]*([^\s#]+)[ \t]*(?:#.*?)?\r?$", RegexOptions.Multiline);
    private static readonly Regex AnyEditorIdLine = new(@"^EditorID[ \t]*:", RegexOptions.Multiline);
    private static readonly Regex WrittenEditorIdLine = new(@"^EditorID[ \t]*:[ \t]*(?<value>[^\r\n]*?)[ \t]*(?:[ \t]#[^\r\n]*)?(?=\r?$)", RegexOptions.Multiline);
    private static readonly Regex EditorIdName = new(@"^[A-Za-z_][A-Za-z0-9_]*$");
    // The lines Spriggit writes above a record's EditorID.
    private static readonly Regex HeadLine = new(@"^(?:FormKey|MutagenObjectType|VersionControl)[ \t]*:");
    private static readonly Regex ModKeyLine = new(@"^ModKey:[ \t]*(\S+?)[ \t]*\r?$", RegexOptions.Multiline);

    /// <summary>The file's FormKey and editor ID, as its top-level lines give them.</summary>
    public static (FormKey? FormKey, string? EditorId) Identity(string text) => (
        FormKeyLine.Match(text) is { Success: true } key && FormKey.TryFactory(key.Groups[1].Value, out var formKey) ? formKey : null,
        EditorIdLine.Match(text) is { Success: true } id ? id.Groups[1].Value : null);

    /// <summary>The plugin a project's folder builds, from its RecordData.yaml.</summary>
    public static ModKey? OwnPlugin(string pluginDir)
    {
        var header = Path.Combine(pluginDir, "RecordData.yaml");
        if (!File.Exists(header) || ModKeyLine.Match(File.ReadAllText(header)) is not { Success: true } line) return null;
        return ModKey.TryFromFileName(line.Groups[1].Value, out var key) ? key : (ModKey?)null;
    }

    private static readonly Regex SmallFlag = new(@"^\s*-\s*Small\s*$|^\s*Flags:\s*\[[^\]]*\bSmall\b", RegexOptions.Multiline);

    /// <summary>Whether a project's folder builds a light plugin: its RecordData.yaml flags it Small, as newProject writes it.</summary>
    public static bool LightPlugin(string pluginDir)
    {
        var header = Path.Combine(pluginDir, "RecordData.yaml");
        return File.Exists(header) && SmallFlag.IsMatch(File.ReadAllText(header));
    }

    /// <summary>The highest FormID a light plugin's records can have.</summary>
    private const uint LightMax = 0xFFF;

    /// <summary>
    /// Whether a record file holds no record: nothing, or only blank lines and comments. "Emptied" with a comment,
    /// a magic effect built as a record of no fields and no EditorID (eval round 25).
    /// </summary>
    public static bool Empty(string text) =>
        text.Split('\n').All(line => line.Trim().Length == 0 || line.TrimStart().StartsWith('#'));

    /// <summary>A record's type as a reader says it — a Weapon, an ObjectEffect.</summary>
    public static string Article(string typeName) => $"{("AEIOU".Contains(typeName[0]) ? "an" : "a")} {typeName}";

    /// <summary>
    /// The record files of the plugin folder that hold one record each: directly in a group's folder,
    /// with that group's record type — or the kind the file names, where a group has several.
    /// </summary>
    public static IEnumerable<(string File, string Relative, Type Type)> FlatRecords(string pluginDir)
    {
        if (!Directory.Exists(pluginDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(pluginDir, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(pluginDir, file).Replace('\\', '/');
            var parts = relative.Split('/');
            if (parts.Length != 2 || !Groups.TryGetValue(parts[0], out var type) || type is null) continue;
            yield return (file, relative, KindIn(type, File.ReadAllText(file)) ?? type);
        }
    }

    /// <summary>A cell's folder as Spriggit names it: `WhiterunBreezehome - 0165A8_Skyrim.esm`, its editor ID, FormID and plugin.</summary>
    private static readonly Regex CellFolder = new(@"^.+ - (?<id>[0-9A-Fa-f]{6})_(?<plugin>.+\.(?:esm|esp|esl))$", RegexOptions.IgnoreCase);

    /// <summary>The folders of a cell's references: Spriggit writes each placed reference of a cell as a file in one of them.</summary>
    internal static readonly HashSet<string> PlacedFolders = new(StringComparer.Ordinal) { "Temporary", "Persistent" };

    /// <summary>
    /// How many folders of a project file's path name its cell: an interior cell's <c>Cells/&lt;block&gt;/&lt;sub-block&gt;/&lt;cell&gt;/</c>
    /// (4), an exterior cell's <c>Worldspaces/&lt;worldspace&gt;/&lt;block x, y&gt;/&lt;sub-block x, y&gt;/&lt;cell&gt;/</c> (5), a worldspace's
    /// persistent cell <c>Worldspaces/&lt;worldspace&gt;/TopCell/</c> (3) — as Spriggit writes them — or 0 for a file in no cell's folder.
    /// </summary>
    internal static int CellDepth(string[] parts) => parts[0] switch
    {
        "Cells" when parts.Length >= 5 => 4,
        "Worldspaces" when parts.Length >= 4 && parts[2] == "TopCell" => 3,
        "Worldspaces" when parts.Length >= 6 => 5,
        _ => 0
    };

    /// <summary>A cell's own file: RecordData.yaml in its folder.</summary>
    internal static bool IsCellFile(string[] parts) => CellDepth(parts) is > 0 and var depth && parts.Length == depth + 1 && parts[depth] == "RecordData.yaml";

    /// <summary>A reference placed in a cell: a file in its folder's Temporary/ or Persistent/.</summary>
    internal static bool IsPlacedFile(string[] parts) => CellDepth(parts) is > 0 and var depth && parts.Length == depth + 2 && PlacedFolders.Contains(parts[depth]);

    /// <summary>An exterior cell's folder — in a worldspace's block, by its grid — rather than an interior cell's or the persistent one's.</summary>
    internal static bool IsExterior(string[] parts) => parts[0] == "Worldspaces" && CellDepth(parts) == 5;

    /// <summary>
    /// The records a project keeps in a cell's folder — an interior cell's, an exterior cell's in its worldspace, a worldspace's
    /// persistent cell's (<see cref="CellDepth"/>): the cell's own RecordData.yaml, and each reference placed in it, a file in its
    /// Temporary or Persistent folder of the kind its MutagenObjectType names — PlacedObject, PlacedNpc… — or null where it names
    /// no placed kind. Read apart from the groups' records (FlatRecords): eval round 125's world runs wrote a chest into a cell's
    /// file under fields of their own, and placed by its editor ID it was refused by the reader as a malformed FormKey — outdoors
    /// as well, where a chest placed beside Riverwood's cooking pot by its editor ID was refused so (2026-11-06). The worldspace's
    /// own file and its blocks' are left to the reader.
    /// </summary>
    public static IEnumerable<(string File, string Relative, Type? Type)> CellRecords(string pluginDir)
    {
        foreach (var group in new[] { "Cells", "Worldspaces" })
        {
            var folder = Path.Combine(pluginDir, group);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(pluginDir, file).Replace('\\', '/');
                var parts = relative.Split('/');
                if (IsCellFile(parts)) yield return (file, relative, typeof(Cell));
                else if (IsPlacedFile(parts)) yield return (file, relative, PlacedKind(File.ReadAllText(file)));
            }
        }
    }

    /// <summary>
    /// What the reader skips in an interior cell's files without a word: fields a cell or a placed reference has not, and a
    /// reference's file that names no placed kind. Eval round 125's world #4 wrote a chest into Breezehome's file as `Items`,
    /// then a kind and a Base of no record, and each build answered success; four more world runs wrote such fields.
    /// </summary>
    private static string? CellProblem(string relative, string text, string[] parts, string pluginDir)
    {
        var depth = CellDepth(parts);
        var placed = IsPlacedFile(parts);
        Type? type = IsCellFile(parts) ? typeof(Cell) : placed ? PlacedKind(text) : null;
        var cellFolder = string.Join('/', parts.Take(depth));
        // A reference in the folder of a cell the project has no copy of: the reader answered "RecordData.yaml cannot be read as
        // a record: Could not find file …", and the run never copied the cell (the mini-round after eval round 128,
        // world+detailed #1).
        if (placed && !File.Exists(Path.Combine(pluginDir, cellFolder, "RecordData.yaml")))
        {
            var cell = CellFolder.Match(parts[depth - 1]);
            var copy = cell.Success
                ? $"one call copies it in, and this file stays where it is: addPatch {{\"project\": {ToolCalls.Quoted(ToolCalls.ProjectOf(pluginDir))}, " +
                  $"\"records\": [\"{cell.Groups["id"].Value.ToUpperInvariant()}:{cell.Groups["plugin"].Value}\"]}} — where its answer names " +
                  "another folder for the cell, move this file into that folder's Temporary/ with moveRecord; then build again"
                : "copy the cell in with addPatch, whose answer names its folder, and move this file into that folder's Temporary/ with moveRecord";
            return $"{relative}: {cellFolder}/ holds no RecordData.yaml — its cell is no record of the project yet, and the build reads a " +
                $"reference only beside the copy of the cell it is placed in: {copy}";
        }
        const string written = "`MutagenObjectType: PlacedObject` for an object — a container, a door, furniture, a static — or `PlacedNpc` for " +
            "an NPC; `EditorID:` with a name of its own; `Base:` with the record placed, by its editor ID or FormID; and `Placement:` with " +
            "`Position: x, y, z` and `Rotation: x, y, z` below it, two spaces in";
        if (placed && type is null)
            return $"{relative}: a reference placed in a cell names its kind on its first line, and the build reads none here — write {written}";
        if (type is null || Empty(text)) return null;
        var known = FieldsOf(type);
        var unknown = TopLevelKey.Matches(text).Select(m => m.Groups[1].Value).Where(key => !known.Contains(key)).Distinct().ToList();
        if (unknown.Count == 0) return null;
        var fields = unknown.Count == 1 ? unknown[0] : $"{string.Join(", ", unknown.Take(unknown.Count - 1))} and {unknown[^1]}";
        var them = unknown.Count == 1 ? "it" : "them";
        return type == typeof(Cell)
            ? $"{relative}: a Cell has no field {fields} — delete {them}. What is placed in a cell is no field of its file: each reference " +
              $"is a file of its own in {cellFolder}/Temporary/, as readRecord of the cell lists them — write {written}"
            : $"{relative}: {Article(type.Name)} has no field {fields} — delete {them}; a placed reference is written with {written}";
    }

    /// <summary>
    /// A placed reference's file outside a cell's folder, which the reader takes for no record: the move into the project's
    /// cell copy, or the cell's addPatch first. Eval round 128's world runs wrote a chest's reference as plugin/Temporary/,
    /// then MiscItems/, References/ and World/, each refused as no group folder or as a line of no MiscItem, and never built.
    /// </summary>
    private static string PlacedOutside(string relative, Type kind, string pluginDir)
    {
        var name = relative.Split('/')[^1];
        var cells = CellRecords(pluginDir).Where(c => c.Type == typeof(Cell)).Select(c => c.Relative[..c.Relative.LastIndexOf('/')]).ToList();
        var said = $"{relative} is {Article(kind.Name)}, a reference placed in a cell: the build reads one only in the folder of the cell " +
            "it is placed in, beside the cell's RecordData.yaml, in its Temporary/ folder — ";
        return said + (cells.Count == 1
            ? $"one call moves it there: {ToolCalls.Move(pluginDir, relative, $"{cells[0]}/Temporary/{name}")}"
            : cells.Count > 1
                ? $"move it into the Temporary/ folder of the cell meant, one of {string.Join(", ", cells.Select(c => c + "/Temporary/"))}, with moveRecord"
                : "copy the cell in first with addPatch, whose answer names the cell's folder, then move this file into that folder's Temporary/ with moveRecord");
    }

    /// <summary>The placed kind a reference's file names — PlacedObject, PlacedNpc… — or null.</summary>
    internal static Type? PlacedKind(string text) =>
        ObjectTypeLine.Match(text) is { Success: true } named
            && typeof(Cell).Assembly.GetType($"Mutagen.Bethesda.Skyrim.{named.Groups[1].Value}") is { } kind
            && typeof(IPlaced).IsAssignableFrom(kind) && !kind.IsInterface ? kind : null;

    /// <summary>The kind a file of a group of several kinds names — GlobalFloat in Globals — or null.</summary>
    private static Type? KindIn(Type group, string text) =>
        ObjectTypeLine.Match(text) is { Success: true } objectType ? KindOf(group, objectType.Groups[1].Value) : null;

    /// <summary>A record type's fields, and MutagenObjectType, which names the kind where a group has several.</summary>
    internal static HashSet<string> FieldsOf(Type type) =>
        Fields.GetOrAdd(type, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
            .Append("MutagenObjectType").ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// Fields written under another name, by the record types they were written on — the names a small
    /// model guessed after reading the vanilla record a refusal named (eval round 11's probes). Checked
    /// before a field of that name further down: a weapon's <c>Effect</c> is its enchantment, not the
    /// critical hit's Effect.
    /// </summary>
    private static readonly (string[] Types, string[] Keys, string? Field, string Hint)[] Synonyms =
    [
        (["Weapon", "Armor"], ["Enchantment", "Enchantments", "Enchant", "Effect", "Effects", "MagicEffect", "BaseEffect"], "ObjectEffect",
            "an enchantment is ObjectEffect, naming an ObjectEffect record"),
        (["MagicEffect"], ["Magnitude", "Damage", "DamageAmount", "Area", "Duration"], null, EffectStrength),
        (["MagicEffect"], ["DamageType", "Element", "Resist"], null,
            "fire, frost and shock damage is ResistValue — ResistFire, ResistFrost, ResistShock — with Archetype's ActorValue Health"),
        (["Spell", "ObjectEffect", "Scroll", "Ingestible", "Ingredient"], ["Damage", "DamageAmount", "Strength"], null,
            "damage is Magnitude, under Data, in each item of Effects"),
        (["EffectData"], ["Damage", "DamageAmount", "Strength"], "Magnitude", "damage is Magnitude"),
        // An item of Effects names its magic effect as BaseEffect; told "has no field Effect", a model kept it (eval probe 10).
        (["Effect"], ["Effect", "MagicEffect", "EffectID", "MGEF", "Base", "Spell", "EffectType", "Type"], "BaseEffect", "it is BaseEffect, naming the MagicEffect"),
        // An enchantment's charge belongs to what carries it; told "delete it", the model wrote it elsewhere in the enchantment (eval round 19).
        (["ObjectEffect", "Effect", "EffectData"], ["Charge", "Charges", "ChargeAmount", "EnchantmentCharge"], null,
            "an enchantment's charge is the weapon's or armor's EnchantmentAmount, beside its ObjectEffect line"),
        // Told only "delete it", a model dropped the damage of its sword's enchantment (eval round 17).
        (["Effect"], ["Damage", "DamageAmount", "Strength"], null, "damage is Magnitude, under Data — beside Area and Duration"),
        (["Spell", "ObjectEffect", "Scroll"], ["Delivery"], "TargetType", "it is TargetType: Self, Touch, Aimed, TargetActor or TargetLocation"),
        (["Spell"], ["Cost", "MagickaCost"], "BaseCost", "it is BaseCost"),
        // Told only where a school is, the model took MagicSkill out of its magic effect and kept it in the spell (eval round 23).
        (["Spell"], ["School", "MagicSkill", "Skill"], null, SpellSchool),
        (["Weapon"], ["Type", "WeaponType"], null, "a weapon's type is AnimationType, under Data — OneHandSword, TwoHandAxe, Bow…"),
        (["Npc"], ["Gender", "Sex", "Female"], null, "a female NPC has Female among the Flags under Configuration"),
        // Told "delete it: a Npc has it nowhere", a model deleted its NPC's `Outfit:` line and answered her dressed as asked,
        // in no outfit at all (eval round 113's npc+detailed #4).
        (["Npc"], ["Outfit", "Outfits", "Clothes", "Clothing"], "DefaultOutfit",
            "an NPC's outfit is DefaultOutfit, naming an Outfit record — SleepingOutfit is the one worn in bed"),
        (["Npc", "NpcConfiguration", "PcLevelMult", "NpcLevel"], ["Level", "LevelMult", "CalcMinLevel", "CalcMaxLevel"], null, NpcLevel),
        // Told "delete it" for a `Mult: 1` below a level that follows the player's, a model deleted it: the level was 0 times the
        // player's, held at its CalcMinLevel, and the model answered it as made (eval round 114's npc #4).
        (["PcLevelMult"], ["Mult", "Multiplier", "LevelMultiplier", "PlayerLevelMult", "PcMult", "PcLevelMult"], "LevelMult",
            "the multiple of the player's level is LevelMult — 1 is the player's own level, and without it the level stays at CalcMinLevel"),
        // Told "delete it" for `RequiredPerk: SteelSmithing`, all five of eval round 128's recipe+detailed runs deleted it, and three
        // built their recipe with no perk at all.
        (["ConstructibleObject"], ["RequiredPerk", "RequiredPerks", "RequiresPerk", "PerkRequired", "PerkCondition", "Perk", "Perks", "SkillPerk"], null,
            "a perk a recipe needs is a condition, under Conditions: `- MutagenObjectType: ConditionFloat`, then, two spaces in, `Data:` with " +
            "`MutagenObjectType: HasPerkConditionData` and `Perk: SteelSmithing` below it, and `ComparisonValue: 1` beside Data — as the game's " +
            "RecipeWeaponSteelSword has it, readRecord {\"record\": \"RecipeWeaponSteelSword\"}; with no CompareOperator line it is EqualTo, " +
            "the player has the perk"),
        // An armor's `Model:` was answered "move it in each item of Stages, under Destructible", the one field of that name an Armor
        // has, and eval round 129's armor+detailed #4 wrote its helmet's mesh there; #3 and #4 ended unbuilt.
        (["Armor"], ["Model", "Models", "Mesh", "Meshes", "GroundModel", "WornModel", "ModelFile", "Nif"], null, ArmorModels),
        (["Armor"], ["ArmorValue", "Defense", "Rating"], "ArmorRating", "it is ArmorRating"),
        // Its cell found by editRecord, a model would set `Placed[0]` on it (eval round 128's world+detailed #4).
        (["Cell"], ["Placed", "References", "Refs", "Objects", "Items", "Containers", "Contents", "Chest", "Chests", "PlacedObjects"], null,
            "what is placed in a cell is no field of its file: each reference is a file of its own in the cell's Temporary/ folder, beside " +
            "its RecordData.yaml, written with your file tools — `MutagenObjectType: PlacedObject`, `EditorID:`, `Base:` and `Placement:` — " +
            "as the skyrim-modding-world skill shows")
    ];

    /// <summary>A Projectile's own fields, which a model copied from one into an arrow it wrote as a Weapon.</summary>
    private static readonly HashSet<string> ProjectileFields = new(["Gravity", "Speed", "Range", "CollisionRadius", "FadeDuration",
        "ImpactForce", "RelaunchInterval", "LifeTime", "TracerChance", "ConeSpread", "MuzzleFlash", "MuzzleFlashDuration"], StringComparer.Ordinal);

    /// <summary>Where a spell's school is: in its magic effects, not in the spell — a field to delete there.</summary>
    private const string SpellSchool = "delete it here, and keep the magic effect's: a spell's school is the MagicSkill of the magic effects its Effects name";

    /// <summary>Where the strength of a magic effect is written: in what names it, not in the effect.</summary>
    internal const string EffectStrength ="a magic effect has no strength of its own: Magnitude, Area and Duration go under Data, " +
        "in the Effects item of the spell or ObjectEffect that names it as BaseEffect";

    /// <summary>Where an armor's meshes are: its WorldModel on the ground, its Armature's armor addons worn.</summary>
    internal const string ArmorModels = "an armor's look is two things, as the game's IronHelmet has them, readRecord {\"record\": \"IronHelmet\"}: " +
        "WorldModel, the mesh on the ground — `WorldModel:`, then `Male:`, `Model:` and `File:`, each two spaces further in, the path " +
        "editRecord sets as WorldModel.Male.Model.File — and Armature, the ArmorAddon records it is worn as, `Armature:` with `- IronHelmetAA` " +
        "below it; delete it here";

    /// <summary>A field's name as YAML writes it, at a line's start or in flow style — not the 012EB7 of a FormID.</summary>
    private static readonly Regex KeyIn = new(@"(?<![\w.])([A-Za-z_]\w*)[ \t]*:(?=[ \t]|$)", RegexOptions.Multiline);

    /// <summary>
    /// A magic effect's Data, answered by what it holds. Found by name, a Data was a condition's: "move it in each
    /// item of Conditions" sent the model away (eval round 24) — and again for a `Data: {}` holding nothing (round 51's
    /// spell+detailed #3). A strength — Magnitude, Area, Duration — or nothing goes in the Effects item of the spell;
    /// the magic effect's own fields, which xEdit shows under DATA (Flags, BaseCost…), at the margin, with the edit
    /// that puts them there.
    /// </summary>
    private static string? DataHint(Type type, string key, string text)
    {
        if (type.Name != "MagicEffect" || key != "Data" || BlockOf(text, key) is not { Length: > 0 } block) return null;
        var keys = KeyIn.Matches(Regex.Replace(block[(key.Length + 1)..], @"[ \t]#.*$", "", RegexOptions.Multiline))
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        var strength = keys.Any(k => k is "Magnitude" or "Area" or "Duration");
        var own = keys.Where(k => k != "MutagenObjectType" && FieldsOf(type).Contains(k)).ToList();
        if (own.Count == 0) return EffectStrength;
        var theirs = $"{string.Join(", ", own)} {(own.Count == 1 ? "is" : "are")} the magic effect's own, at the margin";
        if (strength) return $"{EffectStrength}; {theirs}";
        var flow = Regex.Replace(block.Split('\n')[0][(key.Length + 1)..], "#.*$", "").Trim().Length > 0;
        return theirs + (flow ? ": write each there on a line of its own, and delete the `Data:` line"
            : ": delete the `Data:` line and take two spaces out in front of each line below it");
    }

    /// <summary>
    /// The value a magic effect changes, written as a field of its own — `CarryWeight: 100`, `FortifyCarryWeight`, or a
    /// `Data:` holding `CarryWeightIncrease` — answered with where it goes: the ActorValue under Archetype, the amount in the
    /// spell. Told only "delete it", 5 of eval round 128's 10 effect runs wrote it elsewhere, field by field, until the turns
    /// ran out or they took a game effect in its place.
    /// </summary>
    private static string? ActorValueHint(Type type, string key, string text)
    {
        if (type.Name != "MagicEffect") return null;
        var named = new[] { key }.Concat(key == "Data" && BlockOf(text, key) is { Length: > 0 } block
            ? KeyIn.Matches(block[(key.Length + 1)..]).Select(m => m.Groups[1].Value) : []);
        if (named.Select(ActorValueIn).FirstOrDefault(v => v is not null) is not { } value) return null;
        return $"the value an effect changes is the ActorValue under Archetype: `Archetype:` at the margin, and below it, two spaces in, " +
            $"`MutagenObjectType: MagicEffectPeakValueModArchetype` and `ActorValue: {value}` — as the game's Fortify effects have it " +
            "(readRecord AlchFortifyCarryWeight); how much and how long are Magnitude and Duration under Data, in the Effects item of the " +
            "spell that names this effect";
    }

    /// <summary>The actor value a field's name names, with a word around it — FortifyCarryWeight, CarryWeightIncrease — or null.</summary>
    private static string? ActorValueIn(string field)
    {
        var bare = Regex.Replace(field, @"^(Fortify|Increase|Raise|Boost|Restore|Damage)|(Increase|Increased|Bonus|Boost|Amount|Mod|Modifier|Change|Value)$", "",
            RegexOptions.IgnoreCase);
        return bare.Length > 2 && !int.TryParse(bare, out _) && Enum.TryParse<ActorValue>(bare, true, out var value)
            && value != ActorValue.None && Enum.IsDefined(value) ? value.ToString() : null;
    }

    /// <summary>A top-level field's lines — its own, and those indented or listed below it; empty where the file has none.</summary>
    private static string BlockOf(string text, string key)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var at = Array.FindIndex(lines, line => line.StartsWith(key + ":", StringComparison.Ordinal));
        return at < 0 ? "" : string.Join('\n', lines.Skip(at).Take(1)
            .Concat(lines.Skip(at + 1).TakeWhile(line => line.Length == 0 || line[0] is ' ' or '\t' or '-')));
    }

    /// <summary>
    /// An NPC's level, whole, in the flow style the build reads. Told to move one of its fields at a time,
    /// a model moved CalcMinLevel into Level, Level to the top and LevelMult under Configuration, back and
    /// forth — nine builds for one NPC (eval round 12).
    /// </summary>
    // "Beside it" left a model nesting CalcMinLevel inside Level, or moving it to the top (eval probe 17): the
    // three lines, each as deep as Configuration's `Flags:` line. "Indented as its Flags line is" read as its items' lines
    // to 9 NPC runs of eval rounds 94–106, which wrote `- Level: {…}` among the flags.
    public const string NpcLevel = "an NPC's level is three lines under Configuration, each as deep as its `Flags:` line, with no dash: " +
        "`Level: {MutagenObjectType: PcLevelMult, LevelMult: 1}`, `CalcMinLevel: 1`, `CalcMaxLevel: 25` — the level follows " +
        "the player's between those two; or, for a fixed level, the one line `Level: {MutagenObjectType: NpcLevel, Level: 10}`";

    /// <summary>The whole level, where a field of an NPC's level is written in the wrong place; else null.</summary>
    public static string? LevelHint(Type type, string key) =>
        type.Name is "Npc" or "NpcConfiguration" or "PcLevelMult" or "NpcLevel"
        && key is "Level" or "LevelMult" or "CalcMinLevel" or "CalcMaxLevel" ? NpcLevel : null;

    /// <summary>
    /// What an unknown field is likely meant to be: a field spelled otherwise (<c>name</c> → Name,
    /// <c>Effect</c> → Effects), another name for one (a weapon's <c>Enchantment</c> → ObjectEffect), or
    /// one that belongs inside another (a weapon's <c>Weight</c> → under BasicStats, a spell's
    /// <c>Magnitude</c> → under Data, in each item of Effects). With the value written on the field's
    /// line, a field of one value to rename is answered as the line to write: told "an enchantment is
    /// ObjectEffect", a model wrote <c>EnchantmentID</c> (eval round 11's probes).
    /// </summary>
    private static string? Hint(Type type, HashSet<string> known, string key, string? value = null, IReadOnlyList<string>? values = null,
        IReadOnlyList<string>? fields = null)
    {
        string Rename(string field, string otherwise) =>
            value is { Length: > 0 } && OneValue(type, field) ? $"write it as `{field}: {value}`" : otherwise;
        if (known.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } spelled) return Rename(spelled, $"it is spelled {spelled}");
        foreach (var (types, keys, field, hint) in Synonyms)
            if (types.Contains(type.Name) && keys.Contains(key, StringComparer.OrdinalIgnoreCase)) return field is null ? hint : Rename(field, hint);
        if (FlagHint(type, key) is { } flag) return flag;
        if (known.FirstOrDefault(k => k.Equals(key + "s", StringComparison.OrdinalIgnoreCase) || k.Equals(key + "es", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith('s') && k.Equals(key[..^1], StringComparison.OrdinalIgnoreCase)) is { } number)
            return Rename(number, $"it is spelled {number}");
        // "Move", not "it goes": told Value and Weight go under BasicStats, a model added BasicStats and kept
        // them at the top level, build after build (eval probe 7 after round 11).
        if (Under(type, key, values, fields) is { } path) return $"move it {path}";
        // One letter off one field of the type: HalflCostPerk, told "delete it", was deleted, written back and deleted
        // again until the turns ran out (eval round 37's spell+detailed #3).
        return known.Where(k => OneEditApart(k, key)).ToList() is [var near] ? Rename(near, $"it is spelled {near}") : null;
    }

    /// <summary>
    /// Where a field named as a flag is one: "it is one of the Flags under Configuration: delete it here and list Unique there" — the
    /// record's Flags, or those of one of its parts. An NPC's `Unique: 1` was "delete it: a Npc has it nowhere", and the
    /// NPC lost its flag (eval round 69's npc+detailed #3). Null for a name no Flags of the record has.
    /// </summary>
    private static string? FlagHint(Type type, string key) => FlagPlace(type, key) switch
    {
        (null, { } own) => $"it is one of the Flags: delete it here and list {own} there",
        ({ } part, { } flag) => $"it is one of the Flags under {part}: delete it here and list {flag} there",
        _ => null
    };

    /// <summary>
    /// Where a name is a flag: the part whose Flags it is one of — null for the type's own Flags — and the flag as its Flags
    /// spell it; no flag where no Flags of the type, or of one part of it alone, has that name.
    /// </summary>
    internal static (string? Part, string? Flag) FlagPlace(Type type, string key)
    {
        if (FlagNamed(FlagsOf(type), key) is { } own) return (null, own);
        var parts = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p.Name, Flag: FlagNamed(FlagsOf(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType), key)))
            .Where(p => p.Flag is not null).ToList();
        return parts is [var (part, flag)] ? (part, flag) : (null, null);
    }

    /// <summary>The flags a part's Flags field lists, where it has one of flags; else null.</summary>
    internal static Type? FlagsOf(Type part) => part.GetProperty("Flags", BindingFlags.Public | BindingFlags.Instance)?.PropertyType is { } flags
        && (Nullable.GetUnderlyingType(flags) ?? flags) is { IsEnum: true } named && named.IsDefined(typeof(FlagsAttribute), false) ? named : null;

    /// <summary>The flag of <paramref name="flags"/> that <paramref name="key"/> names, in any case; null for none.</summary>
    internal static string? FlagNamed(Type? flags, string key) =>
        flags is null ? null : Enum.GetNames(flags).FirstOrDefault(n => n.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether two names differ, case aside, by one letter added, left out, changed, or two swapped.</summary>
    private static bool OneEditApart(string a, string b)
    {
        (a, b) = (a.ToLowerInvariant(), b.ToLowerInvariant());
        if (a == b || Math.Abs(a.Length - b.Length) > 1) return false;
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        if (a.Length > b.Length) return a[(i + 1)..] == b[i..];
        if (b.Length > a.Length) return a[i..] == b[(i + 1)..];
        return a[(i + 1)..] == b[(i + 1)..]
            || i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a[(i + 2)..] == b[(i + 2)..];
    }

    /// <summary>The kinds a part of several kinds can be: the concrete classes Mutagen makes for Skyrim.</summary>
    private static readonly Lazy<Type[]> Parts = new(() => typeof(Weapon).Assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == typeof(Weapon).Namespace).ToArray());

    /// <summary>Whether a field is written as one value on its own line — a number, a name, a link — not as fields, a list or a text.</summary>
    private static bool OneValue(Type type, string field) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p => p.Name == field) is { } property
        && (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) is var t
        && (t.IsPrimitive || t.IsEnum || t == typeof(string) || typeof(IFormLinkGetter).IsAssignableFrom(t));

    /// <summary>
    /// Where a field of the record's parts is, two levels deep at most, the nearest first, and at one
    /// depth a part before a list's items: "under BasicStats", "under Data, in each item of Effects". A
    /// magic effect's <c>Type</c> is its Archetype's, not a sound's. Only the parts Mutagen makes for
    /// Skyrim are followed — a link's FormKey, a text's Value are no field a record is written with. Of two parts at one
    /// depth with the field, the one whose field takes the <paramref name="values"/> written: a weapon's Flags are
    /// Data's and Critical's, and OnDeath is only Critical's — sent to Data, the model ran out of turns (eval round 40's sword #2).
    /// A field written with <paramref name="fields"/> below it goes only where most of them are its: an enchantment written
    /// after the Iron Sword was told to move the sword's Data — AnimationType, Speed, Reach — into each item of Effects
    /// (eval round 58's sword+detailed #5).
    /// </summary>
    private static string? Under(Type type, string key, IReadOnlyList<string>? values = null, IReadOnlyList<string>? fields = null)
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
        var level = new List<(Type Part, string Path, bool Listed)> { (type, "", false) };
        for (var depth = 0; depth < 2 && level.Count > 0; depth++)
        {
            var next = new List<(Type Part, string Path, bool Listed)>();
            foreach (var (part, path, listed) in level)
                foreach (var property in part.GetProperties(Public))
                {
                    var inner = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                    var list = inner != typeof(string) && inner.IsGenericType && typeof(System.Collections.IList).IsAssignableFrom(inner);
                    if (list) inner = inner.GetGenericArguments()[0];
                    if (inner.Namespace != typeof(Weapon).Namespace || !inner.IsClass || typeof(IMajorRecordGetter).IsAssignableFrom(inner)) continue;
                    var at = (list ? $"in each item of {property.Name}" : $"under {property.Name}") + (path.Length > 0 ? $", {path}" : "");
                    next.Add((inner, at, listed || list));
                }
            // By name, not GetProperty: that throws where a class redeclares a property of its base. A part
            // of several kinds has the fields of each: Archetype's Type is MagicEffectArchetype's.
            var hits = next.Where(n => KindsOf(n.Part)
                .Any(kind => kind.GetProperties(Public).Any(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (fields is { Count: > 0 }) hits = hits.Where(h => Fits(h.Part, key, fields)).ToList();
            if (hits.Count > 1 && values is { Count: > 0 } && hits.Where(h => Takes(h.Part, key, values)).ToList() is [var fits]) return fits.Path;
            if (hits.Count > 0) return hits.OrderBy(h => h.Listed).First().Path;
            level = next;
        }
        return null;
    }

    /// <summary>A part's kinds: its own type, or the concrete ones of a part of several kinds.</summary>
    private static IEnumerable<Type> KindsOf(Type part) => part.IsAbstract ? Parts.Value.Where(part.IsAssignableFrom) : [part];

    /// <summary>Whether most of the fields written below a part's field are fields of what that field holds, of any of its kinds.</summary>
    private static bool Fits(Type part, string key, IReadOnlyList<string> fields)
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
        foreach (var kind in KindsOf(part))
        {
            if (kind.GetProperties(Public).FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is not { } property) continue;
            var held = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (held != typeof(string) && held.IsGenericType && typeof(System.Collections.IList).IsAssignableFrom(held)) held = held.GetGenericArguments()[0];
            var theirs = KindsOf(held).ToList();
            if (fields.Count(f => theirs.Any(t => t.GetProperties(Public).Any(p => p.Name == f))) * 2 > fields.Count) return true;
        }
        return false;
    }

    /// <summary>
    /// The fields written one level below a top-level field written with fields below it — AnimationType, Speed… of a
    /// weapon's Data; none for a field of one value, a list, or a flow.
    /// </summary>
    private static List<string> FieldsBelow(string text, string key)
    {
        var below = BlockOf(text, key).Split('\n').Skip(1).Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#')).ToList();
        if (below.Count == 0 || below[0].TrimStart().StartsWith('-')) return [];
        var indent = below[0].Length - below[0].TrimStart().Length;
        return below.Where(line => line.Length - line.TrimStart().Length == indent)
            .Select(line => Regex.Match(line, @"^[ \t]+([A-Za-z_]\w*)[ \t]*:")).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    /// <summary>Whether a part's field of names takes each of the values: a flag or a kind its enum has.</summary>
    private static bool Takes(Type part, string key, IReadOnlyList<string> values)
    {
        if (part.GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p => p.Name == key) is not { } property) return false;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) type = type.GetGenericArguments()[0];
        return type.IsEnum && values.All(v => Enum.GetNames(type).Contains(v, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The values a top-level field is written with: its one value, a list in brackets, or the items listed below it.</summary>
    private static List<string> ValuesOf(string text, string key)
    {
        var block = BlockOf(text, key);
        if (block.Length == 0) return [];
        var first = Regex.Replace(block.Split('\n')[0][(key.Length + 1)..], @"[ \t]#.*$", "").Trim();
        if (first.Length > 0) return first.Trim('[', ']').Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        return block.Split('\n').Skip(1).Select(line => Regex.Match(line, @"^[ \t]*-[ \t]*([^\s#]+)")).Where(m => m.Success)
            .Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>Where a record type has a field: "at the top level", "under Data", or null for nowhere.</summary>
    public static string? WhereIn(Type record, string key) =>
        record.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.Name == key) ? "at the top level" : Under(record, key);

    /// <summary>
    /// What a field a type does not have is likely meant to be, as words to append to its name — " (it
    /// is spelled Name)" — or nothing.
    /// </summary>
    public static string HintFor(Type type, string key) =>
        Hint(type, FieldsOf(type), key) is { } hint ? $" ({hint})" : "";

    /// <summary>The field another name of one stands for — a weapon's Enchantments, its ObjectEffect — or null.</summary>
    internal static string? SynonymFor(Type type, string key) =>
        Synonyms.FirstOrDefault(s => s.Field is not null && s.Types.Contains(type.Name) && s.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)).Field;

    /// <summary>
    /// The part of the record a top-level field it lacks goes under, where the refusal names one place: an NPC's
    /// level, under Configuration; another, as "move it under BasicStats" says. Null for any other — a field to
    /// rename or delete, or one "in each item of Effects", which is not one place where a list holds several.
    /// </summary>
    internal static string? PartFor(Type type, string key, string text)
    {
        if (type.Name == "Npc" && key is "Level" or "CalcMinLevel" or "CalcMaxLevel") return "Configuration";
        var hint = HintIn(type, FieldsOf(type), key, text);
        return hint is not null && Regex.Match(hint, @"^move it under (?<part>\w+)$") is { Success: true } move ? move.Groups["part"].Value : null;
    }

    /// <summary>
    /// The one other record type whose fields a file's are, all of them, where its own type has one of them nowhere: a copy of
    /// the Steel Arrow — Projectile, Flags, Damage, Value, Weight at the margin — written in Weapons/. Its fields are not moved
    /// into a weapon's parts: the refusal sends the file to that type's folder. Eval round 130's arrow+detailed #4 had its
    /// Damage, Value and Weight moved under BasicStats and its Flags under Data, Projectile alone refused, and built a weapon.
    /// </summary>
    internal static Type? WrittenAsOther(Type type, string text)
    {
        var keys = TopLevelKey.Matches(text).Select(m => m.Groups[1].Value)
            .Where(k => k is not ("MutagenObjectType" or "FormKey" or "EditorID" or "VersionControl" or "Version2")).Distinct().ToList();
        var known = FieldsOf(type);
        if (!keys.Any(k => !known.Contains(k) && HintIn(type, known, k, text) is null)) return null;
        var owners = Groups.Values.OfType<Type>().Distinct().Where(o => !o.IsAbstract && o != type && keys.All(FieldsOf(o).Contains)).ToList();
        return owners is [var only] ? only : null;
    }

    /// <summary>
    /// What to say of a file listing Items as a record whose type holds none: what holds items in the world, a chest, is a
    /// Container. Eval round 131's world+detailed #3 took HearthFires' "Chest", a MiscItem, for its pattern and wrote its chest in
    /// MISC/ with Items and Respawns: sent to MiscItems/ by the folder's name, then told to delete both, it built a misc item lying
    /// in the house. Null where the type holds items or the file lists none.
    /// </summary>
    private static string? HoldsItems(Type type, string text, string pluginDir, string relative) =>
        FieldsOf(type).Contains("Items") || !TopLevelKey.Matches(text).Any(m => m.Groups[1].Value == "Items") ? null
            : $"{Article(type.Name)} holds no Items, and this file lists them: what holds items in the world — a chest, a barrel, a sack — " +
              $"is a Container, in Containers/: if this is one, move the file there, {ToolCalls.Move(pluginDir, relative, $"Containers/{Path.GetFileName(relative)}")}, " +
              "and build again — the build then names what of it a Container has not";

    /// <summary>What a top-level field its type lacks is likely meant to be, as the file writes it — the value on its line, the fields below it — or null.</summary>
    private static string? HintIn(Type type, HashSet<string> known, string key, string text) =>
        ActorValueHint(type, key, text) ?? DataHint(type, key, text) ?? TextHint(type, key, text) ?? Hint(type, known, key, ValueOf(text, key), ValuesOf(text, key), FieldsBelow(text, key));

    /// <summary>
    /// A field at the margin written with a text, where the one of its name, a part down, takes a number: `Value: Flame Blade`
    /// at a weapon's margin, BasicStats' Value its price. Told "move it under BasicStats", the model wrote a price there and kept
    /// the text at the margin, build after build, and once BasicStats had no Value, the build moved the text there itself and
    /// refused it as no number — 22 turns (eval round 83's sword+detailed #3). Neither moved nor sent there: deleted, with
    /// the text of the record that has the line already, a Name's. Null for any other.
    /// </summary>
    private static string? TextHint(Type type, string key, string text)
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
        if (ValueOf(text, key)?.Trim('\'', '"') is not { Length: > 0 } value || IsNumber(value)
            || Under(type, key) is not { } path || Regex.Match(path, @"^under (?<part>\w+)$") is not { Success: true } under
            || type.GetProperties(Public).FirstOrDefault(p => p.Name == under.Groups["part"].Value) is not { } part
            || (Nullable.GetUnderlyingType(part.PropertyType) ?? part.PropertyType).GetProperties(Public).FirstOrDefault(p => p.Name == key) is not { } field
            || (Nullable.GetUnderlyingType(field.PropertyType) ?? field.PropertyType) is not { IsPrimitive: true } held || held == typeof(bool) || held == typeof(char))
            return null;
        var number = held == typeof(float) || held == typeof(double) ? "a number" : "a whole number";
        var had = type.GetProperties(Public)
            .Where(p => typeof(Mutagen.Bethesda.Strings.ITranslatedStringGetter).IsAssignableFrom(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
            .FirstOrDefault(p => Regex.IsMatch(BlockOf(text, p.Name), $@"^[ \t]+{Regex.Escape(key)}:[ \t]*['""]?{Regex.Escape(value)}['""]?[ \t]*$", RegexOptions.Multiline));
        return $"delete it: {Article(type.Name)}'s {key} is {path}, {number}, and `{value}` is a text" +
            (had is null ? "" : $" — {had.Name} has `{key}: {value}` already");
    }

    /// <summary>A number as a file writes one: in digits, a fraction, or hex.</summary>
    private static bool IsNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || Regex.IsMatch(value, "^0[xX][0-9A-Fa-f]+$");

    /// <summary>
    /// The unknown fields of a record file, named for a message: those with one hint together — "Damage,
    /// Weight (move them under BasicStats)" — and those without any last, set apart, so that "Type, Level
    /// (move it under Configuration)" does not send Type along (eval round 11's probes). Those the type has
    /// nowhere are to be deleted, and said so first: "has no field Description" alone was read as one
    /// missing, and a model wrote it back build after build (eval probe 9 after round 11). With the call that deletes them
    /// and builds: told "an ObjectEffect has no field Description (delete it: an ObjectEffect has it nowhere)", the model
    /// wrote its enchantment anew five times, Description back in four of them — "the build is suggesting that a
    /// Description field might be missing" — until its turns ran out (eval round 101's sword+detailed #4).
    /// </summary>
    private static string Unknown(Type type, HashSet<string> known, IEnumerable<string> keys, string text, string pluginDir, string relative,
        IEnumerable<(string Relative, string Text)> others)
    {
        // A block of the record's own fields: an armor's Value, Weight and ArmorRating written under a weapon's BasicStats, told
        // only to delete it, built as 0, 0 and 0 (refusal probes of 2026-10-31, A05) — written at the margin with one call.
        var hinted = keys.Select(key => (Key: key, Hint: Lifted(type, known, key, text, pluginDir, relative) ?? HintIn(type, known, key, text))).ToList();
        // A file whose fields are all another type's: the move alone, not a hint for each field — "Model (move it in each item of
        // Stages, under Destructible)" pointed a sword written in Armors/ the wrong way (refusal probes, G06).
        var whole = TopLevelKey.Matches(text).Select(m => m.Groups[1].Value).Where(k => k != "MutagenObjectType").Distinct().ToList();
        if (Groups.Values.OfType<Type>().Distinct().Count(other => !other.IsAbstract && other != type && whole.All(FieldsOf(other).Contains)) == 1)
            hinted = hinted.Select(f => (f.Key, Hint: f.Hint?.StartsWith("its lines are") == true ? f.Hint : null)).ToList();
        // A text where a number goes is deleted with the call that does it (TextHint).
        string Removal(string key) => TextHint(type, key, text) is null ? ""
            : "; one call deletes it and builds again: " + ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, "", ToolCalls.Quoted(key), build: true);
        var named = hinted.Where(f => f.Hint is not null).GroupBy(f => f.Hint!).Select(group => group.Count() == 1
            ? $"{group.First().Key} ({group.Key}{Moved(text, group.Key, [group.First().Key])}{Removal(group.First().Key)})"
            : $"{string.Join(", ", group.Select(f => f.Key))} ({Regex.Replace(group.Key, @"^(move it|it is)\b", m => m.Groups[1].Value == "move it" ? "move them" : "they are")}" +
                $"{Moved(text, group.Key, group.Select(f => f.Key).ToList())})");
        var bare = hinted.Where(f => f.Hint is null).Select(f => f.Key).ToList();
        var them = bare.Count == 1 ? "it" : "them";
        // A file of one other record type's fields alone: an enchantment written in MagicEffects/, told to delete its
        // EnchantType, Effects and EnchantmentAmount, deleted them, and ran out of turns (eval round 38's sword+detailed
        // #3). Every field of the file, not only those its type lacks: a spell with a magic effect's fields beside its
        // own Effects was sent to MagicEffects/ (round 39's spell+detailed #4).
        var fields = TopLevelKey.Matches(text).Select(m => m.Groups[1].Value).Where(k => k != "MutagenObjectType").Distinct().ToList();
        var owner = bare.Count == 0 ? [] : Groups.Where(g => g.Value is { IsAbstract: false } other && other != type && fields.All(FieldsOf(other).Contains))
            .DistinctBy(g => g.Value).ToList();
        // The project's own record of that type, where it has one: an enchantment written as a copy of the Iron Sword, in
        // MagicEffects/ beside the project's sword, was moved into Weapons/ as this said, a second sword, and two runs failed
        // (eval rounds 96 and 104); the move is offered only where the project has none.
        var theirs = owner is [var (ownFolder, _)]
            ? others.Select(o => o.Relative.Replace('\\', '/'))
                .FirstOrDefault(o => o != relative.Replace('\\', '/') && o.StartsWith($"{ownFolder}/", StringComparison.OrdinalIgnoreCase))
            : null;
        var example = ExampleName(type.Name) is { } vanilla ? $" (readRecord {{\"record\": \"{vanilla}\"}})" : "";
        var holds = owner.Count == 0 && bare.Contains("Items") ? HoldsItems(type, text, pluginDir, relative) : null;
        var deleted = holds is not null
            ? $"{holds}; if {Article(type.Name)} was meant, delete {them}: one call deletes {them} and builds again: " +
                ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, "", string.Join(", ", bare.Select(ToolCalls.Quoted)), build: true)
            : owner is [var (folder, of)]
            ? NamedAs(type, of!, relative, text, others) is { } namer
                ? $"{(bare.Count == 1 ? "it is" : "they are")} {Article(of!.Name)}'s, which {Article(type.Name)} has nowhere, and {namer}: " +
                    $"write this file anew as {Article(type.Name)}, with {Article(type.Name)}'s fields alone"
                : theirs is not null
                    ? $"{(bare.Count == 1 ? "it is" : "they are")} {Article(of!.Name)}'s, which {Article(type.Name)} has nowhere, and the project " +
                      $"has its {of.Name} already, {theirs}: this file is a copy of {Article(of.Name)} written in {Path.GetDirectoryName(relative)?.Replace('\\', '/')}/. " +
                      $"If {Article(type.Name)} was meant, write this file anew with {Article(type.Name)}'s fields alone{example}; if not, delete it — " +
                      ToolCalls.Delete(pluginDir, relative)
                    : $"{(bare.Count == 1 ? "it is" : "they are")} {Article(of!.Name)}'s, which {Article(type.Name)} has nowhere: if this is " +
                      $"{Article(of.Name)}, move the file into {folder}/ — {ToolCalls.Move(pluginDir, relative, $"{folder}/{Path.GetFileName(relative)}")}"
            : $"delete {them}: {Article(type.Name)} has {them} nowhere; one call deletes {them} and builds again: " +
                ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, "", string.Join(", ", bare.Select(ToolCalls.Quoted)), build: true);
        return string.Join(", ", named.Append(bare.Count == 0 ? "" : $"{string.Join(", ", bare)} ({deleted})")
            .Where(s => s.Length > 0));
    }

    /// <summary>
    /// A top-level block whose lines are all the record's own fields, written at the margin nowhere yet, as the hint that writes
    /// them there and deletes the block with one call; null for any other.
    /// </summary>
    private static string? Lifted(Type type, HashSet<string> known, string key, string text, string pluginDir, string relative)
    {
        var lines = Regex.Match(text.Replace("\r\n", "\n"), $@"^{Regex.Escape(key)}:[ \t]*\n(?<block>(?:[ \t]+\S.*\n?)+)", RegexOptions.Multiline);
        if (!lines.Success) return null;
        var fields = Regex.Matches(lines.Groups["block"].Value, @"^[ \t]+(?<key>[A-Za-z][A-Za-z0-9_]*):[ \t]*(?<value>[^\n#]*?)[ \t]*$", RegexOptions.Multiline)
            .Select(m => (Key: m.Groups["key"].Value, Value: m.Groups["value"].Value)).ToList();
        // A field under another name, as its own: an armor's BasicStats with `ArmorValue: 20` was answered "delete them", and eval
        // round 129's armor+detailed #4 lost its rating, weight and value with it.
        fields = fields.Select(f => known.Contains(f.Key) ? f : (Key: SynonymFor(type, f.Key) ?? f.Key, f.Value)).ToList();
        var margin = TopLevelKey.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        if (fields.Count == 0 || fields.Any(f => !known.Contains(f.Key) || margin.Contains(f.Key) || f.Value.Length == 0)) return null;
        var set = string.Join(", ", fields.Select(f => $"{{\"path\": \"{f.Key}\", \"value\": " +
            (double.TryParse(f.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? f.Value : ToolCalls.Quoted(f.Value.Trim('\'', '"'))) + "}"));
        return $"its lines are {Article(type.Name)}'s own fields, written at the margin: one call writes {string.Join(", ", fields.Select(f => f.Key))} " +
            $"there, deletes {key} and builds again — " + ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, set, ToolCalls.Quoted(key), build: true);
    }

    /// <summary>
    /// Another record of the project that names a file's record in a field taking the type of the file's folder, as words —
    /// "Weapons/FlameBlade.yaml names EnchFlameBlade as its ObjectEffect, which takes an ObjectEffect" — or null. Such a file
    /// is meant as its folder's type, whatever fields it was written with: an enchantment written as a copy of the Iron
    /// Sword, its sword naming it, was moved into Weapons/ as the refusal said, and the run ended unbuilt, a second sword
    /// in the project (eval round 96's sword #5). A field that takes the fields' type as well tells nothing: an NPC's Items
    /// take a weapon and an armor alike.
    /// </summary>
    private static string? NamedAs(Type type, Type fields, string relative, string text, IEnumerable<(string Relative, string Text)> others)
    {
        if (Identity(text).EditorId is not { } id) return null;
        foreach (var (other, otherText) in others)
        {
            if (other == relative || other.Split('/') is not [var group, _] || Groups.GetValueOrDefault(group) is not { } otherType) continue;
            if (RecordLinks.LinksTo(otherText, otherType).FirstOrDefault(l => l.Value.Equals(id, StringComparison.OrdinalIgnoreCase)
                    && l.Takes.IsAssignableFrom(type) && !l.Takes.IsAssignableFrom(fields)) is { Path: { } path })
                return $"{other} names {id} as its {path}, which takes {Article(type.Name)}";
        }
        return null;
    }

    /// <summary>
    /// The edit a "move it under X" is, X a part of the record, as words after it: told to move an NPC's Health
    /// and Stamina under PlayerSkills, two models indented them where they stood, under the field above them
    /// (eval round 22, probe 23). Empty for any other hint.
    /// </summary>
    private static string Moved(string text, string hint, IReadOnlyList<string> keys)
    {
        if (Regex.Match(hint, @"^move it under (?<part>\w+)$") is not { Success: true } move) return "";
        var part = move.Groups["part"].Value;
        var them = keys.Count > 1 ? "them" : "it";
        var at = Regex.Match(text, $@"^{Regex.Escape(part)}:[ \t]*\r?$", RegexOptions.Multiline);
        // The part written as one value: `BodyTemplate: IronHelmetAA`, an armor addon's name, which eval round 129's armor+detailed #3
        // set as BodyTemplate's File, then as Armature, then as File again, until its turns ran out.
        if (!at.Success && Regex.Match(text, $@"^{Regex.Escape(part)}:[ \t]*(?<one>[^\s#][^\r\n#]*?)[ \t]*(?:#.*)?\r?$", RegexOptions.Multiline) is { Success: true } written)
        {
            var one = written.Groups["one"].Value;
            return $": `{part}:` is written as one value, `{one}`, and takes fields of its own — write `{part}:` alone, with {them} below it, " +
                "two spaces in" + (part == "BodyTemplate"
                    ? "; BodyTemplate holds the slots the armor fills and its weight class — `FirstPersonFlags:` with `- Hair` below it for a " +
                      "helmet, and `ArmorType:` — and no mesh: the ArmorAddon records the armor is worn as go under `Armature:`, " +
                      (one.EndsWith("AA", StringComparison.Ordinal) ? $"as `- {one}`" : "as `- IronHelmetAA` for the game's Iron Helmet")
                    : "");
        }
        if (!at.Success) return $": delete {them} at the margin, and write `{part}:` with {them} below, two spaces in";
        // The part has it already, with another value (the same is taken out by the build): moved, it would be there
        // twice — the model was told to move BasicStats' Value there, twice, and answered unbuilt (eval round 37).
        var block = BlockOf(text, part);
        var had = keys.Select(k => Regex.Match(block, $@"^[ \t]+{Regex.Escape(k)}[ \t]*:.*$", RegexOptions.Multiline))
            .Where(m => m.Success).Select(m => $"`{m.Value.Trim()}`").ToList();
        return had.Count > 0
            ? $": `{part}:` has {string.Join(" and ", had)} already — write the value meant there, and delete {them} at the margin"
            : $": delete {them} at the margin and write {them} again below `{part}:` (line {text[..at.Index].Count(c => c == '\n') + 1}), " +
                "as deep as its other fields";
    }

    /// <summary>The project's record files that are no one YAML record: one written as JSON, or holding records apart by `---`.</summary>
    internal static List<string> NotOneRecord(string pluginDir)
    {
        var unfit = new List<string>();
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            var start = text.TrimStart();
            if (start.StartsWith('{') || start.StartsWith('['))
            {
                unfit.Add($"{relative} is written as JSON — a record file is YAML, a `Field: value` line for each field and a part's fields " +
                    $"indented below it, the way readRecord shows {Article(type.Name)}");
                continue;
            }
            // A `---` at the very top starts the one record; one after a field starts another.
            var lines = text.Split('\n');
            var first = Array.FindIndex(lines, line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'));
            var apart = lines.Select((line, i) => (line, i)).Where(l => l.i > first && Regex.IsMatch(l.line, @"^---[ \t]*$")).Select(l => l.i).ToList();
            if (apart.Count == 0) continue;
            var ids = Regex.Matches(text, @"^EditorID:[ \t]*(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
            unfit.Add($"{relative} holds {apart.Count + 1} records, apart by `---` (line {string.Join(", ", apart.Select(i => i + 1))}) — a record " +
                $"file holds one: write {(ids.Count > 1 ? string.Join(", ", ids.Skip(1)) : "each after the first")} in a file of its own in " +
                $"{Path.GetDirectoryName(relative)?.Replace('\\', '/')}/, and delete {(apart.Count == 1 ? "it" : "them")} and the `---` from this one");
        }
        return unfit;
    }

    /// <summary>A record's in-game name as its file writes it — `Value:` below `Name:`, or `Name:` as one value — or null.</summary>
    internal static string? NameOf(string text) =>
        Regex.Match(text.Replace("\r\n", "\n"), @"^Name:[ \t]*\n(?:[ \t]+\S.*\n)*?[ \t]+Value:[ \t]*(?<name>[^\n#]*?)[ \t]*$", RegexOptions.Multiline) is { Success: true } block
            ? block.Groups["name"].Value.Trim('\'', '"')
            : Regex.Match(text, @"^Name:[ \t]+(?<name>[^\r\n#]+?)[ \t]*\r?$", RegexOptions.Multiline) is { Success: true } one ? one.Groups["name"].Value.Trim('\'', '"') : null;

    /// <summary>
    /// The value written on a top-level field's own line — the FlameBladeEnch of <c>Enchantment: FlameBladeEnch</c> —
    /// or the EditorID written in its block, where it names a record as one (<c>Enchantment:</c> above
    /// <c>EditorID: FireEnchantment</c>, eval probe 7 after round 11); else null.
    /// </summary>
    private static string? ValueOf(string text, string key)
    {
        // A comment after the value too: readRecord writes a link's editor ID beside its FormID (`012EB7:Skyrim.esm  # IronSword`).
        if (Regex.Match(text, $@"^{Regex.Escape(key)}:[ \t]*([^\s#|>{{\[][^#\r\n]*?)[ \t]*(?:#.*?)?\r?$", RegexOptions.Multiline) is { Success: true } m)
            return m.Groups[1].Value;
        // Each line of the block matches one way only — its indent, a letter, the rest without the \r — or as a
        // blank line. Written `[ \t]+.*\r?\n`, a block of a dozen lines with no EditorID in it was tried every
        // way its indents and line ends could be split, and checkRecords hung past its 120 s (eval round 17).
        return Regex.Match(text, $@"^{Regex.Escape(key)}:[ \t]*\r?\n(?:[ \t]+\S[^\r\n]*\r?\n|[ \t]*\r?\n)*?[ \t]+EditorID:[ \t]*(\w+)[ \t]*\r?$", RegexOptions.Multiline)
            is { Success: true } block ? block.Groups[1].Value : null;
    }

    /// <summary>
    /// What a weapon or armor of the project lacks that every official one carries itself: a weapon its
    /// Data (3,823 of 3,823 in the 80 official plugins); one with a Template, or an armor with a
    /// TemplateArmor, the template's own lines (3,258 of 3,258 weapons, 2,679 of 2,679 armors) — the
    /// Creation Kit copies them in. A model mirrored the enchanted EnchIronSwordFire01 by its Template, name,
    /// enchantment and damage alone, and built a sword without an animation (eval round 12). Checked with
    /// the links (RecordLinks), on a file that parses: first, it hid the refusal a file was written for.
    /// </summary>
    internal static string? Uncarried(Type type, string text, RecordIndex? index)
    {
        var (templateField, carried) = type.Name switch
        {
            "Weapon" => ("Template", new[] { "Data", "Keywords", "Model", "EquipmentType" }),
            "Armor" => ("TemplateArmor", new[] { "Armature", "Keywords", "BodyTemplate" }),
            _ => ((string?)null, Array.Empty<string>())
        };
        if (templateField is null) return null;
        var keys = TopLevelKey.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var missing = carried.Where(field => !keys.Contains(field)).ToList();
        if (keys.Contains(templateField) && missing.Count > 0)
        {
            var written = ValueOf(text, templateField) ?? "its template";
            var template = FormKey.TryFactory(written, out var key) && index?.ByFormKey.TryGetValue(key, out var entry) == true
                && entry!.EditorId is { } id ? id : written;
            var lines = missing.Count == 1 ? missing[0] : $"{string.Join(", ", missing.Take(missing.Count - 1))} and {missing[^1]}";
            return $"{Article(type.Name)} with a {templateField} carries the template's lines itself, as every official one does — " +
                $"copy {lines} from {template} (readRecord {{\"record\": \"{template}\"}})";
        }
        if (type.Name == "Weapon" && !keys.Contains("Data"))
        {
            var example = index is not null ? ExampleOf(type, index) ?? "IronSword" : "IronSword";
            return $"a Weapon has its Data — AnimationType, Speed, Reach, Skill — as every official one does: copy it from {example} " +
                $"(readRecord {{\"record\": \"{example}\"}}) and change what differs";
        }
        return null;
    }

    /// <summary>A weapon's or armor's enchantment line that names a record, not Null.</summary>
    private static readonly Regex ObjectEffectLine = new(@"^ObjectEffect:[ \t]*(?!(?:Null|null|~)[ \t]*(?:#|\r?$))[^\s#]", RegexOptions.Multiline);
    private static readonly Regex ConstantCast = new(@"^CastType:[ \t]*ConstantEffect\b", RegexOptions.Multiline);

    /// <summary>
    /// A weapon or armor of the project that carries no enchantment; EditorId is its file's, Overrides the load order's
    /// record a copy changes.
    /// </summary>
    internal sealed record Carrier(string Relative, string Type, string Name, string? EditorId, string? Overrides);

    /// <summary>
    /// The project's new enchantments no other record names — by editor ID, or by the FormID a build wrote in, an
    /// override's too — each with the weapons and armors that carry none (Carriers). The model's Flame Blade carried its
    /// enchantment on a copy of IronSword (round 23).
    /// </summary>
    internal static List<(string Relative, string EditorId, List<Carrier> Carriers)> Unworn(
        IReadOnlyList<(string Relative, Type Type, string Text)> records, ModKey? own)
    {
        var unworn = new List<(string, string, List<Carrier>)>();
        foreach (var (relative, type, text) in records)
        {
            if (type != typeof(ObjectEffect) || !IsNew(text, own) || Identity(text) is not { EditorId: { } enchantment }) continue;
            if (NamedElsewhere(records, relative, text)) continue;
            unworn.Add((relative, enchantment, Carriers(records, own, text)));
        }
        return unworn;
    }

    private static bool IsNew(string text, ModKey? own) => Identity(text).FormKey is not { } key || key.ModKey == own;

    /// <summary>A weapon's or armor's enchantment as its line names it: the editor ID beside a FormID, or what is written.</summary>
    private static readonly Regex CarriedLine = new(@"^ObjectEffect:[ \t]*(?<value>[^\s#]+)(?:[ \t]*#[ \t]*(?<named>\S+))?", RegexOptions.Multiline);

    /// <summary>
    /// The project's weapons and armors that carry an enchantment already, each with the one it names: the new ones, else
    /// the copies of the load order's.
    /// </summary>
    internal static List<(string Relative, string Name, string Carried)> Enchanted(IReadOnlyList<(string Relative, Type Type, string Text)> records, ModKey? own)
    {
        var carrying = records.Where(r => (r.Type == typeof(Weapon) || r.Type == typeof(Armor)) && ObjectEffectLine.IsMatch(r.Text)).ToList();
        var fresh = carrying.Where(r => IsNew(r.Text, own)).ToList();
        return (fresh.Count > 0 ? fresh : carrying)
            .Select(r => (r.Relative, Identity(r.Text).EditorId ?? r.Relative,
                CarriedLine.Match(r.Text) is var line && line.Groups["named"].Success ? line.Groups["named"].Value : line.Groups["value"].Value))
            .ToList();
    }

    /// <summary>
    /// The project's weapons and armors that carry no enchantment, for one to carry this enchantment: the new ones, else
    /// the copies of the load order's; armors first for an enchantment cast ConstantEffect, weapons first for any other.
    /// </summary>
    internal static List<Carrier> Carriers(IReadOnlyList<(string Relative, Type Type, string Text)> records, ModKey? own, string enchantment)
    {
        var bare = records.Where(r => (r.Type == typeof(Weapon) || r.Type == typeof(Armor)) && !ObjectEffectLine.IsMatch(r.Text)).ToList();
        var fresh = bare.Where(r => IsNew(r.Text, own)).ToList();
        var armorFirst = ConstantCast.IsMatch(enchantment);
        return (fresh.Count > 0 ? fresh : bare)
            .OrderBy(r => r.Type == typeof(Armor) == armorFirst ? 0 : 1)
            .Select(r =>
            {
                var (key, id) = Identity(r.Text);
                if (key is not { } copied || copied.ModKey == own) return new Carrier(r.Relative, r.Type.Name, id ?? r.Relative, id, null);
                var name = id ?? Session.FormKeyText(copied);
                return new Carrier(r.Relative, r.Type.Name, name, id, $"{name} of {copied.ModKey.FileName}");
            })
            .ToList();
    }

    /// <summary>Whether another record of the project names this one — by editor ID, or by the FormID a build wrote in.</summary>
    internal static bool NamedElsewhere(IReadOnlyList<(string Relative, Type Type, string Text)> records, string relative, string text)
    {
        var (key, editorId) = Identity(text);
        var formId = key is { } numbered ? Session.FormKeyText(numbered) : null;
        return records.Any(f => f.Relative != relative && (editorId is not null && Regex.IsMatch(f.Text, $@"\b{Regex.Escape(editorId)}\b")
            || formId is not null && f.Text.Contains(formId, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A new enchantment no record names while the project has a weapon or armor that carries none — the one meant
    /// to, most likely — refused with the lines to write there: built with a note saying so, the model told the user the
    /// mod was done, twice (eval round 27). With no such weapon or armor it is a note (ProjectNotes): an enchantment
    /// for another mod.
    /// </summary>
    internal static string UnwornProblem(string relative, string enchantment, List<Carrier> carriers)
    {
        // None of the official plugins' 3,007 enchanted armors has an EnchantmentAmount; the skill's weapon has 500 (2026-10-21).
        string Lines(string type) => type == "Weapon" ? $"`ObjectEffect: {enchantment}` and `EnchantmentAmount: 500`" : $"`ObjectEffect: {enchantment}`";
        var shown = carriers.Take(4).ToList();
        var copies = shown[0].Overrides is not null;
        var kinds = shown.Select(c => c.Type).Distinct().ToList();
        var names = shown.Select(c => c.Name).ToList();
        var who = shown.Count == 1
            ? copies ? $"this copy of {shown[0].Overrides} carries" : $"{shown[0].Name} carries"
            : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}{(carriers.Count > shown.Count ? " and more" : "")} carry";
        var into = shown.Count == 1 ? copies ? "it" : shown[0].Name : "the one meant to carry it";
        var write = kinds.Count == 1 ? $"write {Lines(kinds[0])} in {into}" : $"write in {into} {Lines("Weapon")} for a weapon, {Lines("Armor")} for an armor";
        var kind = kinds.Count == 1 ? kinds[0].ToLowerInvariant() : "one";
        // Where: told only "at the margin", the model wrote them in place of Data's last line, `Stagger: 0.75`, inside Data and
        // over the line (eval round 28). The EditorID line is at the margin and one of a kind, and the lines build there.
        var where = shown.Count == 1
            ? shown[0].EditorId is { } id ? $"on the lines right below `EditorID: {id}`, at the margin" : "at the margin"
            : "on the lines right below its `EditorID:` line, at the margin";
        return $"{string.Join(", ", shown.Select(c => c.Relative))}: {who} no enchantment, and no record of the project names the new " +
            $"{enchantment} ({relative}), so the enchantment does nothing — {write}, {where}" +
            (!copies ? ""
                : shown.Count == 1
                    ? $". The copy changes {shown[0].Name} itself, everywhere in the game: for a new {kind}, also delete its FormKey line and give it an EditorID of its own"
                    : $". Each copy changes its record itself, everywhere in the game: for a new {kind}, also delete the FormKey line of the one you write it in and give it an EditorID of its own");
    }

    /// <summary>A top-level field with no value on its line, and a field indented below it.</summary>
    private static readonly Regex BlockFields = new(@"^([A-Za-z_]\w*):[ \t]*(?:#.*)?\r?\n[ \t]+[A-Za-z_]\w*[ \t]*:", RegexOptions.Multiline);

    /// <summary>
    /// What a field of one value written with fields below it takes instead. The reader fails on the first
    /// of them with "Expected 'MappingEnd', got 'Scalar'", which sent a model through five builds with an
    /// enchantment written inside the weapon, under ObjectEffect (eval probe 10 after round 11).
    /// </summary>
    private static string Blocked(Type type, string key, string text)
    {
        var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).First(p => p.Name == key);
        var named = ValueOf(text, key);
        var example = named is null ? "" : $": `{key}: {named}`";
        if (RecordLinks.LinkTarget(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) is not { } target)
            return $"{key} is written with fields below it — it takes one value on its own line{example}";
        var kind = RecordLinks.Kind(target);
        var folder = Groups.FirstOrDefault(g => g.Value?.Name == kind).Key;
        return $"{key} is written with fields below it — it takes one value on its own line, the editor ID or FormID of " +
            $"{Article(kind)}{example}" + (folder is null ? "" : $"; a new {kind} is a file of its own in {folder}/, named here by its EditorID");
    }

    /// <summary>The kind of a group's records a file names — GlobalFloat for a Global — or null; the same for any polymorphic field.</summary>
    public static Type? KindOf(Type group, string name) =>
        group.Assembly.GetTypes().FirstOrDefault(t => t.Name == name && !t.IsInterface && group.IsAssignableFrom(t));

    /// <summary>
    /// How many record files the plugin folder holds, what is wrong with any of them, and the files left
    /// out: in a folder that is no group, or one below a group's folder, whose record a group's folder holds
    /// too — by file name or editor ID — and a .yml file beside its .yaml twin. The reader skips such a folder; refused, the file trapped a model whose file tools could not
    /// delete it, after it had written the record again in the right folder (eval probe 10 after round 11).
    /// With the load order's index, a FormKey of another plugin is checked against the record it names.
    /// </summary>
    public static (int Records, List<string> Problems, List<(string File, string Copy)> LeftOut) Check(string pluginDir, RecordIndex? index = null, Dictionary<string, string>? unneeded = null)
    {
        var problems = new List<string>();
        var leftOut = new List<(string File, string Copy)>();
        var texts = new Lazy<List<(string Relative, string Text)>>(() => FlatRecords(pluginDir).Select(f => (f.Relative, File.ReadAllText(f.File))).ToList());
        var records = 0;
        if (!Directory.Exists(pluginDir)) return (0, problems, leftOut);
        var own = OwnPlugin(pluginDir);
        var formKeys = new Dictionary<FormKey, List<string>>();
        var editorIds = new Dictionary<string, List<(string Relative, FormKey? Key, Type Type)>>(StringComparer.OrdinalIgnoreCase);
        var grouped = new Lazy<List<(string Relative, string Name, string? EditorId)>>(() => FlatRecords(pluginDir)
            .Select(f => (f.Relative, Path.GetFileName(f.File), Identity(File.ReadAllText(f.File)).EditorId)).ToList());
        foreach (var file in Directory.EnumerateFiles(pluginDir, "*.*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(pluginDir, file).Replace('\\', '/');
            if (relative == "RecordData.yaml") continue;
            if (file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                // Written again as .yaml, as the refusal said: left out — refused, the .yml file failed every build
                // of a model whose file tools could not delete it, until it made a second project (eval round 15).
                var twin = Path.ChangeExtension(file, ".yaml");
                if (File.Exists(twin))
                    leftOut.Add((relative, Path.GetRelativePath(pluginDir, twin).Replace('\\', '/')));
                else
                    problems.Add($"{relative} ends in .yml, which the build does not read: name it .yaml — " +
                        ToolCalls.Move(pluginDir, relative, Path.ChangeExtension(relative, ".yaml")));
                continue;
            }
            // Any other ending in a group's folder too: an enchantment saved as FlameBladeBurn.nhs went unread,
            // and the link to it was refused as naming no record (eval probe 16).
            if (!file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                var folder = relative.Split('/')[0];
                if (relative.Contains('/') && Groups.ContainsKey(folder))
                    problems.Add($"{relative} ends in {Path.GetExtension(file)}, which the build does not read: name it .yaml — " +
                        ToolCalls.Move(pluginDir, relative, Path.ChangeExtension(relative, ".yaml")));
                continue;
            }
            // An empty record file holds no record, and is left out: the way to drop a record without deleting
            // its file — a broken one no longer needed trapped a model whose file tools could not delete (probe 16).
            if (Empty(File.ReadAllText(file)))
            {
                leftOut.Add((relative, ""));
                continue;
            }
            records++;

            var parts = relative.Split('/');
            // In Cells/ too, where it is no cell's Temporary/ or Persistent/ file: eval round 131's world #1 wrote its chest's
            // reference as Cells/HuntersChestRef.yaml, which the reader took for nothing, and the build answered success without it.
            // Outdoors the same: a reference anywhere in Worldspaces/ but an exterior cell's or the persistent cell's folder.
            var inCell = IsPlacedFile(parts);
            if (!inCell && PlacedKind(File.ReadAllText(file)) is { } placedKind)
            {
                problems.Add(PlacedOutside(relative, placedKind, pluginDir));
                continue;
            }
            if (parts.Length == 1)
            {
                problems.Add($"{relative} is in no group folder: move it into its type's, as {ToolCalls.Move(pluginDir, relative, $"Weapons/{relative}")} does for a weapon");
                continue;
            }
            if (!Groups.TryGetValue(parts[0], out var type))
            {
                var editorId = parts.Length == 2 ? Identity(File.ReadAllText(file)).EditorId : null;
                if (parts.Length == 2 && grouped.Value.FirstOrDefault(g => g.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase)
                        || editorId is not null && editorId.Equals(g.EditorId, StringComparison.OrdinalIgnoreCase)) is { Relative: { } copy })
                {
                    leftOut.Add((relative, copy));
                    records--;
                    continue;
                }
                var spelled = Groups.Keys.FirstOrDefault(g => g.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
                var called = spelled is null ? GroupCalled(parts[0]) : null;
                problems.Add(spelled is not null ? $"{relative}: the folder is spelled {spelled}, not {parts[0]}"
                    : called is not null && Groups[called] is { } calledType && HoldsItems(calledType, File.ReadAllText(file), pluginDir, relative) is { } holds
                        ? $"{relative}: '{parts[0]}' is no group folder, and by its name it would be {called} — but {holds}; if {Article(calledType.Name)} " +
                          $"was meant, move it into {called}/ and leave its Items out — {ToolCalls.Move(pluginDir, relative, $"{called}/{parts[^1]}")}"
                    : called is { } group
                        ? $"{relative}: '{parts[0]}' is no group folder — the folder for them is {group}: move the file there — {ToolCalls.Move(pluginDir, relative, $"{group}/{parts[^1]}")}"
                        : $"{relative}: '{parts[0]}' is no group folder — use the one readRecord's pathInPluginFolder names for the type, such as {CommonGroups}");
                continue;
            }

            // Below a group of single records the reader takes nothing for a record, but the file still
            // got a FormID, and the build succeeded empty (eval round 10: Spells/Spells/…).
            if (parts.Length > 2 && !Nested.Contains(parts[0]))
            {
                var into = Groups.ContainsKey(parts[1]) ? string.Join('/', parts[1..]) : $"{parts[0]}/{parts[^1]}";
                // Written again where the refusal said, its record is in the group's folder: left out, like a file
                // in a folder that is no group — refused, the first copy failed every build of a model whose file
                // tools could not delete it (ObjectEffects/Enchantments/, eval round 15's probes).
                var nestedId = Identity(File.ReadAllText(file)).EditorId;
                if (grouped.Value.FirstOrDefault(g => g.Relative.Equals(into, StringComparison.OrdinalIgnoreCase)
                        || nestedId is not null && nestedId.Equals(g.EditorId, StringComparison.OrdinalIgnoreCase)) is { Relative: { } copy })
                {
                    leftOut.Add((relative, copy));
                    records--;
                    continue;
                }
                problems.Add($"{relative} is in a folder below {parts[0]}/, where the build reads no record: move it directly into its group's folder — {ToolCalls.Move(pluginDir, relative, into)}");
                continue;
            }

            var text = File.ReadAllText(file);
            var line = FormKeyLine.Match(text);
            if (line.Success)
            {
                if (!FormKey.TryFactory(line.Groups[1].Value, out var formKey))
                    problems.Add($"{relative}: '{line.Groups[1].Value}' is no FormKey — leave FormKey out of a new record, the build assigns one");
                else if (formKey.ID == 0)
                    problems.Add($"{relative}: {Session.FormKeyText(formKey)} is no record's FormID — leave FormKey out of a new record, the build assigns one");
                else
                    (formKeys.TryGetValue(formKey, out var holders) ? holders : formKeys[formKey] = []).Add(relative);
            }
            if (CellDepth(parts) > 0 && CellProblem(relative, text, parts, pluginDir) is { } cellProblem) problems.Add(cellProblem);
            if (type is null || parts.Length != 2) continue;
            if (Identity(text) is { EditorId: { } recordId } identity)
                (editorIds.TryGetValue(recordId, out var sharing) ? sharing : editorIds[recordId] = []).Add((relative, identity.FormKey, type));
            // The reader takes a record's first line for its FormKey — after the kind, where a group has
            // several (eval round 11: EditorID written above it).
            if (line.Success && TopLevelKey.Matches(text).Select(m => m.Groups[1].Value).FirstOrDefault(k => k != "MutagenObjectType") is { } first
                && first != "FormKey")
                problems.Add($"{relative}: FormKey is the first line of a record file — move it above {first}");
            if (index is not null && Foreign(relative, text, own, index) is { } foreign) problems.Add(foreign);

            // A group of several kinds — Globals: GlobalFloat, GlobalShort… — names the kind in the file.
            var kind = KindIn(type, text);
            var known = FieldsOf(kind ?? type);
            // A field written twice breaks the reader a check later: told only of the fields the type lacks, the model moved
            // those, was then told of DefaultOutfit written twice, and wrote the file anew each time, ten builds (eval probe 23).
            if (Twice(text, known) is { } twice) problems.Add($"{relative}: {twice}");
            // A record type named at the margin that is not the folder's: the reader answered "Malformed FormKey string:
            // MiscItem" for a note's file (refusal probes of 2026-10-31, B14).
            if (Regex.Match(text, @"^MutagenObjectType:[ \t]*(?<kind>\w+)", RegexOptions.Multiline) is { Success: true } named && kind is null
                && named.Groups["kind"].Value is var said && !said.Equals(type.Name, StringComparison.OrdinalIgnoreCase))
                // An archetype's kind written at the margin, told "delete it", was deleted with the actor value the model meant
                // to write below it (eval round 128's effect+detailed #5).
                problems.Add(type.Name == "MagicEffect" && said.EndsWith("Archetype", StringComparison.OrdinalIgnoreCase)
                    ? $"{relative}: `MutagenObjectType: {said}` names the kind of the effect's Archetype: move it under `Archetype:`, two spaces in, " +
                      "with the `ActorValue:` the effect changes beside it"
                    : $"{relative}: `MutagenObjectType: {said}` is no line of {Article(type.Name)}'s file, whose type is its folder's — delete it" +
                    (Groups.FirstOrDefault(g => g.Value?.Name.Equals(said, StringComparison.OrdinalIgnoreCase) == true).Key is { } folder
                        ? $"; {Article(said)} is written in {folder}/" : ""));
            foreach (var key in BlockFields.Matches(text).Select(m => m.Groups[1].Value).Where(known.Contains).Distinct())
                if (OneValue(kind ?? type, key)) problems.Add($"{relative}: {Blocked(kind ?? type, key, text)}");
            // An arrow written as a new weapon with a weapon's fields alone: eval round 131's arrow #3 wrote Hunter's Arrow in Weapons/
            // with `Data.Skill: Archery` and built a weapon, its missing mesh offered a staff's. No weapon of the game is named an
            // arrow — the word whole: Harrowing is a weapon's. A sword the user named so has its kind written, and is left alone.
            if (type.Name == "Weapon" && IsNew(text, own) && NameOf(text) is { } arrowName && Regex.IsMatch(arrowName, @"\b[Aa]rrows?\b")
                && Regex.Match(text, @"^[ \t]+AnimationType:[ \t]*(?<kind>\w+)", RegexOptions.Multiline) is var animation
                && (!animation.Success || animation.Groups["kind"].Value is "Bow" or "Crossbow"))
            {
                problems.Add($"{relative}: {Identity(text).EditorId ?? Path.GetFileNameWithoutExtension(relative)} is named '{arrowName}', and an " +
                    "arrow is no Weapon — a weapon is a sword, a bow, a staff…: an arrow is an Ammunition, in Ammunitions/, whose `Projectile:` " +
                    "names the projectile it flies as (the game's SteelArrow names ArrowSteelProjectile). One call moves this file there: " +
                    ToolCalls.Move(pluginDir, relative, $"Ammunitions/{parts[^1]}") +
                    $"; then write it as readRecord {{\"record\": \"SteelArrow\"}} shows an arrow — its Model, Projectile, Flags with NonBolt, " +
                    "Damage, Value and Weight at the margin — with a weapon's fields left out");
                continue;
            }
            var unknown = TopLevelKey.Matches(text).Select(m => m.Groups[1].Value).Where(key => !known.Contains(key)).Distinct().ToList();
            if (unknown.Count == 0) continue;
            if (index is not null && EffectItem(type, unknown, text, relative, texts.Value, pluginDir, index) is { } item)
            {
                problems.Add($"{relative}: {item}");
                continue;
            }
            // A projectile's flight written into a weapon: an arrow is an Ammunition. Told to delete Gravity, Range, CollisionRadius…,
            // eval round 127's arrow+detailed #5 did, and built a hand-to-hand weapon named Hunter's Arrow.
            if (type.Name == "Weapon" && unknown.Where(ProjectileFields.Contains).ToList() is { Count: >= 2 } flight)
            {
                problems.Add($"{relative}: {string.Join(", ", flight)} are a Projectile's fields — how an arrow or a bolt flies — and an " +
                    "arrow is no Weapon: it is an Ammunition, in Ammunitions/, whose `Projectile:` names the projectile it flies as (the game's " +
                    "SteelArrow names ArrowSteelProjectile). One call moves this file there: " +
                    ToolCalls.Move(pluginDir, relative, $"Ammunitions/{parts[^1]}") +
                    $"; then write it as readRecord {{\"record\": \"SteelArrow\"}} shows an arrow — its Model, Projectile, Flags with NonBolt, " +
                    "Damage, Value and Weight — with these fields deleted");
                continue;
            }
            // The family's skill first: its tables have the lines of each kind — told only to read Firebolt, a
            // model built its shock spell on Firebolt's fire effect (eval probe 15).
            var skill = SkillOf((kind ?? type).Name) is { } family ? $"the {family} skill's tables, readSkill {{\"skill\": \"{family}\"}}, or " : "";
            var read = index is not null && ExampleOf(kind ?? type, index) is { } example
                ? $" — read {skill}a vanilla one, readRecord {{\"record\": \"{example}\"}}, and write yours with its field names and nesting"
                : "";
            problems.Add($"{relative}: {Droppable(kind ?? type, relative, text, texts.Value, pluginDir)}" +
                $"{Article(type.Name)} has no field {Unknown(kind ?? type, known, unknown, text, pluginDir, relative, texts.Value)}{read}" +
                (index is not null && GameEnchantmentEffect(type, text, relative, texts.Value, pluginDir, index) is { } instead ? $"; or {instead}" : ""));
        }
        // Two files of one record: the reader names the FormKey but neither file (eval round 11: 000000 in each new record).
        foreach (var (formKey, holders) in formKeys.Where(pair => pair.Value.Count > 1))
            problems.Add($"{string.Join(" and ", holders)} have the same FormKey {Session.FormKeyText(formKey)} — one file per record; " +
                "a new record has no FormKey line, the build assigns one");
        // One editor ID, two records: told of a warning in a file the build had just numbered and renamed, the model wrote
        // the file again under its old name, and Hilda was built twice, a man and a woman (eval probe 23).
        // A numbered file written whole again lost the FormKey line the build gave it, but keeps the name the build gave it:
        // told to empty the other, the model rewrote the numbered one without the line, and was then told only to pick (eval round 28).
        // A build no longer renames a file (eval round 30); a pull, and builds before that, name it the way Spriggit does.
        bool Numbered((string Relative, FormKey? Key, Type Type) file) => file.Key is { } key ? key.ModKey == own
            : own is { } plugin && Regex.IsMatch(Path.GetFileName(file.Relative), $@" - [0-9A-Fa-f]{{6}}_{Regex.Escape(plugin.FileName)}\.yaml$", RegexOptions.IgnoreCase);
        foreach (var (editorId, files) in editorIds.Where(pair => pair.Value.Count > 1 && pair.Value.Any(f => f.Key is not { } key || key.ModKey == own)))
        {
            // Records of different types are records of their own, not one written twice: told to delete one, the model deleted
            // its spell beside the magic effect it casts (eval round 84). The build renames the one a link of the other names
            // (RenameSharedEditorIds); with no such link, which gets a new name is the model's to say.
            if (files.Select(f => f.Type).Distinct().Count() == files.Count)
            {
                problems.Add($"{string.Join(" and ", files.Select(f => f.Relative))} have the same EditorID {editorId} — " +
                    $"{string.Join(" and ", files.Select(f => Article(f.Type.Name)))}, records of their own, and an editor ID names one " +
                    "record: give one an EditorID of its own, and the links meant for it the same — " +
                    string.Join(" or ", files.Select(f => ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), f.Relative,
                        $"{{\"path\": \"EditorID\", \"value\": {ToolCalls.Quoted(editorId + TypeWord(f.Type))}}}"))));
                continue;
            }
            var numbered = files.Where(Numbered).ToList();
            var fresh = files.Where(f => f.Key is null && !Numbered(f)).ToList();
            problems.Add($"{string.Join(" and ", files.Select(f => f.Relative))} have the same EditorID {editorId} — " +
                (numbered.Count == 1 && fresh.Count == files.Count - 1
                    ? $"one record written twice: a build numbered it in {numbered[0].Relative}. Make the changes " +
                        $"there, and delete {string.Join(" and ", fresh.Select(f => f.Relative))} — {string.Join("; ", fresh.Select(f => ToolCalls.Delete(pluginDir, f.Relative)))}"
                    : "an editor ID names one record: give each new one an EditorID of its own, or delete the file of the one not needed — " +
                        string.Join(" or ", files.Select(f => ToolCalls.Delete(pluginDir, f.Relative)))));
        }
        // A file of a copy nothing names, its delete first, its faults after it, and those files first (RenameTakenEditorIds).
        if (unneeded is { Count: > 0 })
        {
            string? Copy(string problem) => unneeded.Keys.FirstOrDefault(k => problem.StartsWith($"{k}:", StringComparison.OrdinalIgnoreCase));
            problems = problems.Select(p => Copy(p) is { } k ? $"{k}: {unneeded[k]}{p[(k.Length + 1)..].TrimStart()}" : p)
                .OrderByDescending(p => Copy(p) is not null).ToList();
        }
        return (records, problems, leftOut);
    }

    /// <summary>
    /// A top-level field of the record written twice, which the reader refuses as a duplicate key — said with both
    /// lines, as the YAML check says one — or null. Only the type's own fields: a line of a text written at the
    /// margin is the YAML check's to name, as the text it fell out of.
    /// </summary>
    private static string? Twice(string text, HashSet<string> known)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            if (TopLevelKey.Match(lines[i]) is not { Success: true } key || !known.Contains(key.Groups[1].Value)) continue;
            var name = key.Groups[1].Value;
            if (seen.TryAdd(name, i)) continue;
            var (first, second) = (lines[seen[name]].Trim(), lines[i].Trim());
            return $"{name} is written twice, on lines {seen[name] + 1} and {i + 1}, and a field is written once — " +
                (first.EndsWith(':') ? $"keep one `{name}:` with the lines below it, and delete the other with its lines"
                    : first == second ? "delete one of the two lines, they are the same"
                    : $"keep the one meant, `{first}` or `{second}`, and delete the other");
        }
        return null;
    }

    /// <summary>
    /// What is wrong with a file that names another plugin's record: a FormKey copied from the record a
    /// model read makes the build change that record — the Iron Sword "renamed" Flame Blade (eval round
    /// 10) — and one that names no record makes a record the plugin does not have.
    /// </summary>
    private static string? Foreign(string relative, string text, ModKey? own, RecordIndex index)
    {
        var (formKey, editorId) = Identity(text);
        if (formKey is not { } key || key.ModKey == own) return null;
        const string fresh = "a new record has no FormKey line: remove it, and the build assigns one";
        if (!index.ByFormKey.TryGetValue(key, out var entry))
            return $"{relative}: FormKey {Session.FormKeyText(key)} names no record in the load order — {fresh}";
        if (editorId is not null && entry.EditorId is { } theirs && !editorId.Equals(theirs, StringComparison.OrdinalIgnoreCase))
            // Only the way to a new record: offered "keep its editor ID" as well, a model restored it and
            // built an override of a vanilla weapon as its new sword (eval round 11's probes).
            return $"{relative}: FormKey {Session.FormKeyText(key)} is {theirs} of {key.ModKey.FileName}, so this file would change " +
                $"that record instead of making {editorId} — {fresh}";
        return null;
    }

    /// <summary>
    /// A magic effect file that holds an effect item — `BaseEffect:` at its margin, naming a magic effect of the load order, as
    /// an item of an enchantment's or a spell's Effects does — said as what it is, with the calls that put it right: the file
    /// deleted, then each Effects item of the project naming it set to the effect it names, the last call building. Told "a
    /// MagicEffect has no field BaseEffect (delete it …)", the model deleted the line, and the game's effect with it, and made
    /// a magic effect of the name alone through four more refusals, until its turns ran out (eval round 82's sword+detailed
    /// #5). So is one whose `Effects` list has one item, an enchantment's whole list: told to delete `Effects`, `Data` and
    /// `Category` field by field, the model wrote them back by turns until its turns ran out — and every sword run of eval
    /// rounds 87–115 that wrote a magic effect file failed (round 115's sword+detailed #3). An effect named by an editor ID
    /// no record has is the game's of the name closest to it — FireDamageFFContact the game's EnchFireDamageFFContact — where
    /// one alone is closest. Null for any other file.
    /// </summary>
    private static string? EffectItem(Type type, List<string> unknown, string text, string relative,
        IReadOnlyList<(string Relative, string Text)> texts, string pluginDir, RecordIndex index)
    {
        if (type.Name != "MagicEffect") return null;
        var named = unknown.Contains("BaseEffect") ? ValueOf(text, "BaseEffect") : null;
        var listed = false;
        if (named is not { Length: > 0 } && unknown.Contains("Effects") && OneItemsBaseEffect(text) is { } item) (named, listed) = (item, true);
        if (named is not { Length: > 0 }) return null;
        var key = FormKey.TryFactory(named, out var formKey) ? formKey : index.ByEditorId.TryGetValue(named, out var byId) ? byId : (FormKey?)null;
        string game;
        var closest = false;
        if (key is { } effect && index.ByFormKey.TryGetValue(effect, out var entry) && entry.Signature == "MGEF") game = entry.EditorId ?? named;
        else if (key is null && ClosestEffect(named, index) is { } near) (game, closest) = (near, true);
        else return null;
        var id = Identity(text).EditorId;
        var project = ToolCalls.ProjectOf(pluginDir);
        var namers = id is null ? [] : texts.Where(t => t.Relative != relative && Groups.GetValueOrDefault(t.Relative.Split('/')[0]) is not null)
            .Select(t => (t.Relative, Name: Identity(t.Text).EditorId ?? t.Relative,
                Paths: RecordLinks.LinksIn(t.Text, Groups[t.Relative.Split('/')[0]]!)
                    .Where(l => l.Path.EndsWith("BaseEffect", StringComparison.Ordinal) && l.Value.Equals(id, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Path).ToList()))
            .Where(n => n.Paths.Count > 0).ToList();
        var delete = ToolCalls.Delete(pluginDir, relative);
        var what = (listed
                ? $"it holds an Effects list, as an enchantment or a spell does — its one item, `BaseEffect: {named}` with its Data — not a "
                : $"it holds an effect item, as an enchantment's or a spell's Effects do — `BaseEffect: {named}` with its Data — not a ") +
            (closest
                ? $"magic effect: no record is {named}, and {game} is the game's magic effect closest to it by name"
                : $"magic effect: {game} is the game's magic effect");
        if (namers.Count == 0)
            return $"{what}; delete this file — {delete} — and name {game} as `BaseEffect:` in the Effects item meant to cast it";
        var sets = namers.Select((n, at) => ToolCalls.Edit(project, n.Relative,
            string.Join(", ", n.Paths.Select(p => $"{{\"path\": \"{p}\", \"value\": {ToolCalls.Quoted(game)}}}")), build: at == namers.Count - 1));
        return $"{what}, and {string.Join(" and ", namers.Select(n => n.Name))} name{(namers.Count == 1 ? "s" : "")} this file's {id} in " +
            $"its place. {(namers.Count == 1 ? "Two" : $"{namers.Count + 1}")} calls do it: {delete}; then {string.Join("; then ", sets)}, " +
            $"which names {game} there and builds again";
    }

    /// <summary>The `BaseEffect` of a file's `Effects` list where the list has one item, an effect item's mapping; else null.</summary>
    private static string? OneItemsBaseEffect(string text) =>
        MisplacedFields.Root(text) is { } root
        && root.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("Effects"), out var effects)
        && effects is YamlDotNet.RepresentationModel.YamlSequenceNode { Children.Count: 1 } list
        && list.Children[0] is YamlDotNet.RepresentationModel.YamlMappingNode item
        && item.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("BaseEffect"), out var named)
        && named is YamlDotNet.RepresentationModel.YamlScalarNode { Value: { } value } && value.Trim() is { Length: > 0 } trimmed
            ? trimmed : null;

    /// <summary>
    /// The load order's magic effect an editor ID no record has was meant for: of those whose editor ID holds it or is held in
    /// it, at least half as long, the shortest — EnchFireDamageFFContact for FireDamageFFContact — where it alone is that
    /// short; else null, and the effect is not guessed.
    /// </summary>
    /// <summary>
    /// For a magic effect of the project, refused, that an enchantment of the project names: the game's enchantment effect most
    /// like it — by the words of its editor ID found in the file, Ench… effects alone — named in the enchantment in its place,
    /// and the file deleted, as calls; null where none is clearly most like it. Eval round 128's armor+detailed #3 wrote
    /// a frost resistance of its own, `Type: Resistance`, refused field by field until its turns ran out, where the game's
    /// enchantments of frost resistance name EnchResistFrostConstantSelf.
    /// </summary>
    private static string? GameEnchantmentEffect(Type type, string text, string relative, IReadOnlyList<(string Relative, string Text)> texts,
        string pluginDir, RecordIndex index)
    {
        if (type.Name != "MagicEffect" || Identity(text).EditorId is not { } id) return null;
        var namers = texts.Where(t => t.Relative.StartsWith("ObjectEffects/", StringComparison.OrdinalIgnoreCase))
            .Select(t => (t.Relative, Paths: RecordLinks.LinksIn(t.Text, typeof(ObjectEffect))
                .Where(l => l.Path.EndsWith("BaseEffect", StringComparison.Ordinal) && l.Value.Equals(id, StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Path).ToList()))
            .Where(n => n.Paths.Count > 0).ToList();
        if (namers.Count != 1) return null;
        var said = text.ToUpperInvariant();
        // Most of its words in the file, then fewest not: EnchRobesResistFrostConstantSelf has a word more than EnchResistFrostConstantSelf.
        var scored = index.ByFormKey.Values
            .Where(e => e.Signature == "MGEF" && e.EditorId is { } effect && effect.StartsWith("Ench", StringComparison.Ordinal) && e.FormKey.ModKey != Identity(text).FormKey?.ModKey)
            .Select(e => RecordLinks.WordsOf(e.EditorId!).Where(w => w != "ENCH" && w.Length >= 4).ToList() is var words
                ? (Id: e.EditorId!, Score: words.Count(said.Contains), Missing: words.Count(w => !said.Contains(w))) : default)
            .Where(c => c.Score >= 3).GroupBy(c => (c.Score, -c.Missing)).OrderByDescending(g => g.Key).FirstOrDefault()?
            .Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (scored is not [var game]) return null;
        var (namer, paths) = namers[0];
        return $"name the game's {game}, the magic effect the game's enchantments name for this, in {namer}'s Effects in its place, and delete " +
            "this file — two calls and a build: " +
            ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), namer, string.Join(", ", paths.Select(p => $"{{\"path\": \"{p}\", \"value\": {ToolCalls.Quoted(game)}}}"))) +
            $", then {ToolCalls.Delete(pluginDir, relative)}, then buildProject";
    }

    private static string? ClosestEffect(string named, RecordIndex index)
    {
        var near = index.ByFormKey.Values
            .Where(e => e.Signature == "MGEF" && e.EditorId is { } id && 2 * Math.Min(id.Length, named.Length) >= Math.Max(id.Length, named.Length)
                && (id.Contains(named, StringComparison.OrdinalIgnoreCase) || named.Contains(id, StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.EditorId!).Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(id => Math.Abs(id.Length - named.Length)).OrderBy(g => g.Key).FirstOrDefault()?.ToList();
        return near is [var one] ? one : null;
    }

    /// <summary>
    /// For a new record of the project that no other record names, how to drop it — the deleteRecord call — as
    /// words before what is wrong with it; else nothing. A model
    /// wrote a magic effect of its own, then named a vanilla one in its spell, and the broken file it no
    /// longer needed failed every build (probe 16). Said after the fields, the model went back to the file it had
    /// fixed instead (eval round 24).
    /// </summary>
    internal static string Droppable(Type type, string relative, string text, IEnumerable<(string Relative, string Text)> others, string pluginDir)
    {
        // Only a part another record names — an effect, an enchantment, an addon — and beside other records:
        // offered on an NPC that was the whole mod, emptying it would have left nothing to build.
        if (type.Name is not ("MagicEffect" or "ObjectEffect" or "ArmorAddon")) return "";
        var rest = others.Where(o => o.Relative != relative).ToList();
        return rest.Count > 0 && Identity(text) is { FormKey: null, EditorId: { } id }
            && !rest.Any(o => Regex.IsMatch(o.Text, $@"\b{Regex.Escape(id)}\b"))
            ? $"no other record names {id}: if it is not needed, delete it — {ToolCalls.Delete(pluginDir, relative)}. If it is needed: "
            : "";
    }

    /// <summary>
    /// Takes out of a new record's file the FormKey line copied from the record it was written after — another
    /// plugin's record of another editor ID — or one that names nothing: no record of the load order, 000000, no
    /// FormKey at all. Answers each file and what was taken out. A new record has no FormKey line, and the refusal
    /// offered only that way (eval round 11's probes); refused, it was the refusal runs met most — 51 of 312 in eval
    /// rounds 24–31, seven failed after it. addPatch's copy given another editor ID too: refused as an override
    /// renamed, it was met in three runs of eval rounds 32–33 and ended sword #4 of round 33 on an empty turn — a
    /// copy is renamed for a new record. One of addPatch's copies that names no record is left to Foreign. And the
    /// plugin's own FormKey past a light plugin's last, 000FFF: Mutagen refused it as "out of bounds for the given
    /// record compaction settings", naming no file, and npc+detailed #4 of eval round 34 gave up after three builds.
    /// A link to it in another file of the project names its editor ID instead, which the build resolves.
    /// </summary>
    public static List<(string File, string Change)> DropCopiedFormKeys(string pluginDir, RecordIndex index)
    {
        var changes = new List<(string File, string Change)>();
        var own = OwnPlugin(pluginDir);
        var light = LightPlugin(pluginDir);
        foreach (var (file, relative, _) in FlatRecords(pluginDir))
        {
            var text = File.ReadAllText(file);
            if (Empty(text) || FormKeyLine.Match(text) is not { Success: true } line) continue;
            var written = line.Groups[1].Value;
            bool Copied(FormKey key) => Path.GetFileName(file).EndsWith($"{key.ID:X6}_{key.ModKey.FileName}.yaml", StringComparison.OrdinalIgnoreCase);
            string why;
            var relinked = new List<(string File, string Change)>();
            if (!FormKey.TryFactory(written, out var key)) why = $"'{written}' is no FormKey";
            else if (key.ID == 0) why = $"{Session.FormKeyText(key)} is no record's FormID";
            else if (key.ModKey == own)
            {
                if (!light || key.ID <= LightMax) continue;
                why = $"{Session.FormKeyText(key)} is past the last FormID a light plugin has, 000FFF";
                if (Identity(text).EditorId is { } named) relinked = Relink(pluginDir, file, key, named);
            }
            else if (!index.ByFormKey.TryGetValue(key, out var entry))
            {
                if (Copied(key)) continue;
                why = $"{Session.FormKeyText(key)} names no record in the load order";
            }
            else if (Identity(text).EditorId is { } editorId && entry.EditorId is { } theirs && !editorId.Equals(theirs, StringComparison.OrdinalIgnoreCase))
                why = Copied(key)
                    ? $"{Session.FormKeyText(key)} is {theirs} of {key.ModKey.FileName}, and addPatch's copy of it was given the EditorID " +
                        $"{editorId}, which makes it a new record and leaves {theirs} as it is"
                    : $"{Session.FormKeyText(key)} is {theirs} of {key.ModKey.FileName}, and this file makes {editorId}";
            else continue;
            // The line with its line break: the lines below it move up one.
            var end = line.Index + line.Length;
            if (end < text.Length && text[end] == '\n') end++;
            File.WriteAllText(file, text[..line.Index] + text[end..]);
            changes.Add((relative, $"removed `{line.Value.TrimEnd('\r')}` — {why}; a new record has no FormKey line, and the build numbers it"));
            changes.AddRange(relinked);
        }
        return changes;
    }

    /// <summary>
    /// Writes the editor ID in place of a FormKey in the links of the plugin folder's other record files, the FormKey
    /// lines left as they are; answers each file changed.
    /// </summary>
    private static List<(string File, string Change)> Relink(string pluginDir, string record, FormKey key, string editorId)
    {
        var changes = new List<(string File, string Change)>();
        var link = new Regex($@"(?<![0-9A-Fa-f]){Regex.Escape(Session.FormKeyText(key))}(?![\w.])", RegexOptions.IgnoreCase);
        foreach (var (file, relative, _) in FlatRecords(pluginDir))
        {
            if (file == record) continue;
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');
            var count = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("FormKey:", StringComparison.Ordinal)) continue;
                var changed = link.Replace(lines[i], editorId);
                if (changed != lines[i]) { lines[i] = changed; count++; }
            }
            if (count == 0) continue;
            File.WriteAllText(file, string.Join('\n', lines));
            changes.Add((relative, $"{(count == 1 ? "the link" : $"{count} links")} to {Session.FormKeyText(key)} now name{(count == 1 ? "s" : "")} " +
                $"{editorId}, the record whose FormKey line was taken out"));
        }
        return changes;
    }

    /// <summary>
    /// Takes out a top-level field written again the same, which the reader refuses as a field written twice: the one
    /// above says it already. The model's note+detailed #5 (eval round 36) wrote VersionControl's line over the FormKey line
    /// above it, was refused, and ran out of turns; five runs of rounds 23–33 were refused one and built again. So too a
    /// field with the same lines below it: told to delete one of two `Name:` blocks, the same three lines, the model sent an
    /// edit of those lines three times, each refused as not unique, and OpenClaw ended its turn (eval round 106's
    /// sword+detailed #5, which failed). Two values, or two blocks that differ, are left to the refusal (Twice).
    /// </summary>
    public static List<(string File, string Change)> DropRepeatedLines(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            var lines = File.ReadAllText(file).Split('\n').ToList();
            var known = FieldsOf(type);
            var seen = new Dictionary<string, (int Line, string Text)>(StringComparer.Ordinal);
            var dropped = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                if (TopLevelKey.Match(lines[i]) is not { Success: true } key || !known.Contains(key.Groups[1].Value)) continue;
                var name = key.Groups[1].Value;
                var count = BlockLength(lines, i);
                var block = string.Join('\n', lines.Skip(i).Take(count).Select(l => l.TrimEnd()));
                // Numbered as the file was written: the model knows it so.
                if (seen.TryAdd(name, (i + dropped + 1, block)) || seen[name].Text != block) continue;
                var (at, first) = (i + dropped + 1, seen[name].Line);
                changes.Add((relative, count == 1
                    ? $"removed line {at}, `{lines[i].Trim()}` — line {first} says the same, and a record has each field once"
                    : $"removed lines {at}–{at + count - 1}, `{name}:` and the {count - 1} line{(count == 2 ? "" : "s")} below it — lines " +
                      $"{first}–{first + count - 1} say the same, and a record has each field once"));
                lines.RemoveRange(i--, count);
                dropped += count;
            }
            if (dropped > 0) File.WriteAllText(file, string.Join('\n', lines));
        }
        return changes;
    }

    /// <summary>
    /// How many lines the top-level field at <paramref name="at"/> has: its own, and those below it that are indented, listed
    /// at the margin or comments, as YAML reads them into it — blank lines between them too, but none after the last.
    /// </summary>
    private static int BlockLength(List<string> lines, int at)
    {
        var count = 1;
        for (var i = at + 1; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line[0] is not (' ' or '\t' or '-' or '#')) break;
            count = i - at + 1;
        }
        return count;
    }

    /// <summary>
    /// Deletes from a record file the fields of another record type, which its own type has nowhere, left in where the
    /// file was written after a record of that type: Flame Blade Burn, written after the Iron Sword, kept the sword's Model,
    /// BasicStats, Critical and thirteen more beside its own Effects; told to delete them, the model planned the edit, its turn
    /// came back with no call, and the run failed (eval round 58's sword+detailed #5) — 9 of the 18 such refusals of
    /// rounds 40–58 were of a file so written, and two of those runs failed. Only fields the type has no other place for, two
    /// or more, all of one other record type, in a file with fields of its own type that one lacks: a file of the other
    /// type's fields alone is left to the refusal, which names that type's folder.
    /// </summary>
    public static List<(string File, string Change)> DropOtherTypesFields(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            var written = File.ReadAllText(file);
            var text = written.Replace("\r\n", "\n");
            var known = FieldsOf(type);
            var fields = TopLevelKey.Matches(text).Select(m => m.Groups[1].Value).Where(k => k != "MutagenObjectType").Distinct().ToList();
            // A spell's school is a field it has nowhere too, said as one to delete.
            var nowhere = fields.Where(k => !known.Contains(k) && HintIn(type, known, k, text) is null or SpellSchool).ToList();
            if (nowhere.Count < 2) continue;
            var own = fields.Where(known.Contains).ToList();
            var source = Groups.Values.OfType<Type>().Where(other => !other.IsAbstract && other != type).Distinct()
                .Where(other => nowhere.All(k => WhereIn(other, k) is not null) && own.Any(k => !FieldsOf(other).Contains(k)))
                .OrderByDescending(other => nowhere.Count(FieldsOf(other).Contains)).FirstOrDefault();
            if (source is null) continue;
            // Each field's block: its line, and the lines below it that are indented, listed or blank.
            var kept = new List<string>();
            var dropping = false;
            foreach (var line in text.Split('\n'))
            {
                if (line.Length > 0 && line[0] is not (' ' or '\t' or '-' or '#'))
                    dropping = TopLevelKey.Match(line) is { Success: true } key && nowhere.Contains(key.Groups[1].Value);
                if (!dropping) kept.Add(line);
            }
            var left = string.Join('\n', kept);
            File.WriteAllText(file, written.Contains("\r\n") ? left.Replace("\n", "\r\n") : left);
            changes.Add((relative, $"deleted {string.Join(", ", nowhere.Select(k => $"`{k}`"))} — {Article(source.Name)}'s fields, which " +
                $"{Article(type.Name)} has nowhere"));
        }
        return changes;
    }

    /// <summary>
    /// The value of a record's EditorID line as written, its quotes taken off — `Maven's Warning` too, which is no
    /// editor ID — or null where the record has no such line, or none with a value.
    /// </summary>
    internal static string? EditorIdWritten(string text)
    {
        if (WrittenEditorIdLine.Match(text) is not { Success: true } line || line.Groups["value"].Value is not { Length: > 0 } written) return null;
        return written.Length >= 2 && written[0] == '\'' && written[^1] == '\'' ? written[1..^1].Replace("''", "'")
            : written.Length >= 2 && written[0] == '"' && written[^1] == '"' ? written[1..^1]
            : written;
    }

    /// <summary>
    /// A group folder written in other letters — `weapons/` — renamed to its own, through a name of its own first: on a drive
    /// that ignores letter case, the build refused "the folder is spelled Weapons", moveRecord answered success and changed
    /// nothing, and a file written anew landed in the same folder (refusal probes of 2026-10-31). Left alone where the folder
    /// of the right name is there too, as Linux allows: those are two folders, and the refusal names the files.
    /// </summary>
    public static List<(string File, string Change)> FolderCase(string pluginDir)
    {
        var corrected = new List<(string File, string Change)>();
        if (!Directory.Exists(pluginDir)) return corrected;
        foreach (var folder in Directory.EnumerateDirectories(pluginDir))
        {
            var name = Path.GetFileName(folder);
            if (Groups.ContainsKey(name) || Groups.Keys.FirstOrDefault(g => g.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } group) continue;
            var target = Path.Combine(pluginDir, group);
            if (Directory.EnumerateDirectories(pluginDir).Any(d => Path.GetFileName(d) == group)) continue;
            var between = Path.Combine(pluginDir, $"{group}.renaming-{Guid.NewGuid():N}");
            Directory.Move(folder, between);
            Directory.Move(between, target);
            corrected.Add(($"{name}/", $"renamed the folder {name} to {group}, the group folder's name"));
        }
        return corrected;
    }

    /// <summary>A tab among a line's leading blanks.</summary>
    private static readonly Regex LeadingTab = new(@"^[ ]*\t[ \t]*");

    /// <summary>
    /// Lines indented with tabs, indented with spaces — each tab as many as the file's other lines indent by, two where none
    /// are — where the file then reads as YAML, and did not before: YAML indents with spaces alone, and the reader stopped at
    /// the first tab, named by the line above it (refusal probes of 2026-10-31, G01, G16, F19). Where it reads no better, the
    /// refusal names each tabbed line.
    /// </summary>
    public static List<(string File, string Change)> Untab(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, _) in FlatRecords(pluginDir))
        {
            var written = File.ReadAllText(file);
            var text = written.Replace("\r\n", "\n");
            var lines = text.Split('\n');
            var tabbed = Enumerable.Range(0, lines.Length).Where(i => LeadingTab.IsMatch(lines[i])).ToList();
            if (tabbed.Count == 0 || MisplacedFields.Root(text) is not null) continue;
            var step = lines.Where(l => l.Length > 0 && l[0] == ' ' && !LeadingTab.IsMatch(l) && l.Trim().Length > 0)
                .Select(MisplacedFields.Indent).DefaultIfEmpty(2).Min();
            foreach (var i in tabbed)
            {
                var lead = LeadingTab.Match(lines[i]).Value;
                lines[i] = lead.Replace("\t", new string(' ', step)) + lines[i][lead.Length..];
            }
            var spaced = string.Join('\n', lines);
            if (MisplacedFields.Root(spaced) is null) continue;
            File.WriteAllText(file, written.Contains("\r\n") ? spaced.Replace("\n", "\r\n") : spaced);
            var numbers = tabbed.Select(i => i + 1).ToList();
            changes.Add((relative, $"indented line{(numbers.Count == 1 ? "" : "s")} {Numbers(numbers)} with spaces, {step} for each tab — " +
                "YAML indents with spaces alone"));
        }
        return changes;
    }

    /// <summary>A line of quote marks alone.</summary>
    internal static readonly Regex LoneQuote = new(@"^[ \t]*[""']+[ \t]*$");

    /// <summary>
    /// A line of a quote mark alone taken out, where the file then reads and did not before: the quote opens a text that runs on
    /// to the file's end, and the refusal gave the parser's words — the model's sword ended in a line `"`, eight calls before it
    /// was gone (eval round 107's sword #1). The one line whose going alone makes the file read, so that a quote written as a
    /// line of a text stays; else all of them, where that makes it read.
    /// </summary>
    public static List<(string File, string Change)> DropLoneQuotes(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, _) in FlatRecords(pluginDir))
        {
            var written = File.ReadAllText(file);
            var text = written.Replace("\r\n", "\n");
            var lines = text.Split('\n');
            var quotes = Enumerable.Range(0, lines.Length).Where(i => LoneQuote.IsMatch(lines[i])).ToList();
            if (quotes.Count == 0 || MisplacedFields.Root(text) is not null) continue;
            string Without(List<int> gone) => string.Join('\n', lines.Where((_, i) => !gone.Contains(i)));
            var alone = quotes.Where(i => MisplacedFields.Root(Without([i])) is not null).ToList();
            var dropped = alone.Count == 1 ? alone : MisplacedFields.Root(Without(quotes)) is not null ? quotes : [];
            if (dropped.Count == 0) continue;
            var kept = Without(dropped);
            File.WriteAllText(file, written.Contains("\r\n") ? kept.Replace("\n", "\r\n") : kept);
            changes.Add((relative, $"removed line{(dropped.Count == 1 ? "" : "s")} {Numbers(dropped.Select(i => i + 1).ToList())}, " +
                $"{string.Join(", ", dropped.Select(i => $"`{lines[i].Trim()}`").Distinct())} — a quote mark alone opens a quoted text that " +
                "runs on to the end of the file, and writes no field"));
        }
        return changes;
    }

    /// <summary>Line numbers as words: "4", "4 and 6", "29–31", "2, 5 and 9–11".</summary>
    internal static string Numbers(List<int> numbers)
    {
        var runs = new List<string>();
        for (var i = 0; i < numbers.Count;)
        {
            var j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1) j++;
            runs.Add(j > i ? $"{numbers[i]}–{numbers[j]}" : $"{numbers[i]}");
            i = j + 1;
        }
        return runs.Count == 1 ? runs[0] : $"{string.Join(", ", runs[..^1])} and {runs[^1]}";
    }

    /// <summary>A record's own stamp written with no value: the reader takes a number or a truth there, and the build writes them.</summary>
    private static readonly Regex EmptyStamp = new(@"^(?<key>VersionControl|FormVersion|Version2|MajorRecordFlagsRaw|IsCompressed)[ \t]*:[ \t]*(?:#.*)?$");

    /// <summary>
    /// A record's stamp written empty — `VersionControl:` — taken out: the reader refused the file, "VersionControl was null", and
    /// the build writes a new record's stamps itself. 5 note and spell runs of eval rounds 95–101 wrote one, each refused.
    /// </summary>
    public static List<(string File, string Change)> DropEmptyStamps(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, _) in FlatRecords(pluginDir))
        {
            var written = File.ReadAllText(file);
            var lines = written.Replace("\r\n", "\n").Split('\n').ToList();
            var empty = Enumerable.Range(0, lines.Count).Where(i => EmptyStamp.IsMatch(lines[i])
                && (i + 1 >= lines.Count || lines[i + 1].Length == 0 || lines[i + 1][0] is not (' ' or '\t' or '-'))).ToList();
            if (empty.Count == 0) continue;
            foreach (var i in empty)
                changes.Add((relative, $"removed line {i + 1}, `{lines[i].Trim()}` — written with no value, which the reader refuses; " +
                    "the build writes a record's stamps itself"));
            foreach (var i in empty.OrderByDescending(i => i)) lines.RemoveAt(i);
            var text = string.Join('\n', lines);
            File.WriteAllText(file, written.Contains("\r\n") ? text.Replace("\n", "\r\n") : text);
        }
        return changes;
    }

    /// <summary>
    /// A record's text written at the margin as one value — `Name: Steel Sword` — written as the two lines a text takes, its
    /// language and its value: the one way to read it. Refused, eval round 131's quest+detailed #3 wrote it so for its sword's
    /// Name and Description, each a build of its 25 turns, and ran out of them. A block (`|-`), a quoted empty text and a text
    /// in a part are left to the check.
    /// </summary>
    public static List<(string File, string Change)> TextsInTwoLines(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            var texts = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => typeof(Mutagen.Bethesda.Strings.ITranslatedStringGetter).IsAssignableFrom(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
                .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            if (texts.Count == 0) continue;
            var written = File.ReadAllText(file);
            var lines = written.Replace("\r\n", "\n").Split('\n').ToList();
            var changed = false;
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                // A flow mapping, `{TargetLanguage: English, Value: …}`, is a text already; ~ and null are none.
                if (Regex.Match(lines[i], @"^(?<key>[A-Za-z]\w*):[ \t]+(?<value>[^\s#|>&*!'""~{\[][^#]*?|'[^']+'|""[^""]+"")[ \t]*(?:#.*)?$") is not { Success: true } one
                    || !texts.Contains(one.Groups["key"].Value) || one.Groups["value"].Value.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
                // A value whose lines go on below it is no one value.
                if (i + 1 < lines.Count && lines[i + 1].Length > 0 && lines[i + 1][0] is ' ' or '\t') continue;
                var (key, value) = (one.Groups["key"].Value, one.Groups["value"].Value);
                lines.RemoveAt(i);
                lines.InsertRange(i, [$"{key}:", "  TargetLanguage: English", $"  Value: {value}"]);
                changes.Add((relative, $"line {i + 1}, `{key}: {value}`, written as a text's two lines — `{key}:`, then `TargetLanguage: English` " +
                    $"and `Value: {value}` below it: a text written as one value is read as none"));
                changed = true;
            }
            if (!changed) continue;
            var text = string.Join('\n', lines);
            File.WriteAllText(file, written.Contains("\r\n") ? text.Replace("\n", "\r\n") : text);
        }
        return changes;
    }

    /// <summary>
    /// An EditorID written with characters no editor ID has — `Maven's Warning`, `MavenNote:MavenWarning` — written with
    /// its letters, digits and underscores alone, where no other record has that name. Read as none, `Maven's Warning`
    /// was answered "has no EditorID" three builds long, and the copied FormKey beside it kept the note an override of
    /// Maven's (eval round 39's note #5); 7 runs of rounds 23–39 wrote one. A name taken is left to the note.
    /// </summary>
    public static List<(string File, string Change)> CleanEditorIds(string pluginDir, RecordIndex index)
    {
        var changes = new List<(string File, string Change)>();
        var own = OwnPlugin(pluginDir);
        var files = FlatRecords(pluginDir).Select(f => (f.File, f.Relative, Text: File.ReadAllText(f.File))).ToList();
        var taken = files.Select(f => Identity(f.Text).EditorId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, relative, text) in files)
        {
            if (EditorIdWritten(text) is not { } written || EditorIdName.IsMatch(written)) continue;
            var name = Regex.Replace(written, "[^A-Za-z0-9_]", "");
            if (!EditorIdName.IsMatch(name) || taken.Contains(name)
                || index.ByEditorId.TryGetValue(name, out var holder) && holder.ModKey != own) continue;
            var line = WrittenEditorIdLine.Match(text);
            File.WriteAllText(file, text[..line.Index] + $"EditorID: {name}" + text[(line.Index + line.Length)..]);
            taken.Add(name);
            changes.Add((relative, $"wrote `EditorID: {name}` for `{written}` — an editor ID is letters, digits and underscores alone, " +
                "by which links and tools like SPID name the record"));
        }
        return changes;
    }

    /// <summary>
    /// Takes the project's own FormKey out of the files that share it, where no build gave it to one of them: the model wrote
    /// 000800 into a new sword and its enchantment both (eval round 39's sword+detailed #5), and 4 of the 8 runs of
    /// rounds 23–39 refused two files of one FormKey failed. Where the last built plugin has the record, the file of
    /// its editor ID keeps it; where none of them is that record, or that plugin cannot be read, the refusal stays.
    /// </summary>
    public static List<(string File, string Change)> DropSharedFormKeys(string pluginDir, SkyrimRelease release)
    {
        var changes = new List<(string File, string Change)>();
        if (OwnPlugin(pluginDir) is not { } own) return changes;
        var shared = FlatRecords(pluginDir).Select(f => (f.File, f.Relative, Text: File.ReadAllText(f.File)))
            .Where(f => Identity(f.Text).FormKey is { } key && key.ModKey == own)
            .GroupBy(f => Identity(f.Text).FormKey!.Value).Where(g => g.Count() > 1).ToList();
        if (shared.Count == 0) return changes;
        var built = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginDir))!, "build", own.FileName);
        Dictionary<FormKey, string?> given;
        try
        {
            using var plugin = File.Exists(built) ? SkyrimMod.Create(release).FromPath(new ModPath(own, built)).Construct() : null;
            given = plugin?.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => r.EditorID) ?? [];
        }
        catch (Exception)
        {
            return changes;
        }
        foreach (var group in shared)
        {
            string? keeper = null;
            if (given.TryGetValue(group.Key, out var builtId))
            {
                keeper = group.FirstOrDefault(f => string.Equals(Identity(f.Text).EditorId, builtId, StringComparison.OrdinalIgnoreCase)).Relative;
                if (keeper is null) continue;
            }
            foreach (var (file, relative, text) in group.Where(f => f.Relative != keeper))
            {
                var line = FormKeyLine.Match(text);
                var end = line.Index + line.Length;
                if (end < text.Length && text[end] == '\n') end++;
                File.WriteAllText(file, text[..line.Index] + text[end..]);
                var others = group.Where(f => f.Relative != relative).Select(f => f.Relative).ToList();
                changes.Add((relative, $"removed `{line.Value.TrimEnd('\r')}` — " + (keeper is not null
                    ? $"a build gave it to {keeper}'s record"
                    : $"{string.Join(" and ", others)} {(others.Count == 1 ? "has" : "have")} it too, and no build gave it to " +
                        (others.Count == 1 ? "either" : "any of them")) +
                    "; a new record has no FormKey line, and the build numbers it"));
            }
        }
        return changes;
    }

    /// <summary>
    /// Gives the project's own record written with no EditorID its file's name, where that is an editor ID no other
    /// record has: the author's own name for it. With none, no link of the project and no tool like SPID can name the
    /// record, and the build's notes call it "a Spell" — the model wrote 13 spells so in eval rounds 25–36. A file name that
    /// is no editor ID, one the load order or the project has, or one two such files share, is left to a note (ProjectNotes).
    /// </summary>
    public static List<(string File, string Change)> NameFromFiles(string pluginDir, RecordIndex index)
    {
        var changes = new List<(string File, string Change)>();
        var own = OwnPlugin(pluginDir);
        var files = FlatRecords(pluginDir).Select(f => (f.File, f.Relative, Text: File.ReadAllText(f.File))).Where(f => !Empty(f.Text)).ToList();
        var taken = files.Select(f => Identity(f.Text).EditorId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unnamed = files
            .Where(f => !AnyEditorIdLine.IsMatch(f.Text) && (Identity(f.Text).FormKey is not { } key || key.ModKey == own))
            .Select(f => (f.File, f.Relative, f.Text, Name: Path.GetFileNameWithoutExtension(f.File)))
            .Where(f => EditorIdName.IsMatch(f.Name)).ToList();
        foreach (var (file, relative, text, name) in unnamed)
        {
            // The load order's own name would take the links meant for its record: the project's resolve first.
            if (unnamed.Count(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 1 || taken.Contains(name)
                || index.ByEditorId.TryGetValue(name, out var holder) && holder.ModKey != own) continue;
            var lines = text.Split('\n').ToList();
            lines.Insert(lines.TakeWhile(line => HeadLine.IsMatch(line)).Count(), $"EditorID: {name}{(text.Contains("\r\n") ? "\r" : "")}");
            File.WriteAllText(file, string.Join('\n', lines));
            changes.Add((relative, $"wrote `EditorID: {name}`, the file's name — a record has an editor ID of its own, by which " +
                "links and tools like SPID name it"));
        }
        return changes;
    }

    /// <summary>
    /// Gives a new record — no FormKey line — written with the editor ID of a record of the load order an editor ID of its
    /// own, from its in-game name: every link by the one it had would name it, and the game would have two records of one
    /// editor ID. Refused, the model half-did the build's own advice for addPatch's renamed copy — took out its FormKey line,
    /// kept its EditorID — then named it so and wrote a second file of that name, and answered unbuilt (eval round 54's
    /// note+detailed #3); in rounds 31 and 51 a magic effect so written took its spell's link from Skyrim.esm's. A link
    /// by the old editor ID names the load order's record again.
    /// </summary>
    public static List<(string File, string Change)> RenameTakenEditorIds(string pluginDir, RecordIndex index, Dictionary<string, string>? unneeded = null)
    {
        var changes = new List<(string File, string Change)>();
        var own = OwnPlugin(pluginDir);
        var files = FlatRecords(pluginDir).Select(f => (f.File, f.Relative, Text: File.ReadAllText(f.File))).Where(f => !Empty(f.Text)).ToList();
        var taken = files.Select(f => Identity(f.Text).EditorId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Free(string name) => !taken.Contains(name) && !index.ByEditorId.ContainsKey(name);
        foreach (var (file, relative, text) in files)
        {
            if (Identity(text) is not { FormKey: null, EditorId: { } editorId } || !index.ByEditorId.TryGetValue(editorId, out var key)
                || key.ModKey == own || !index.ByFormKey.TryGetValue(key, out var entry) || EditorIdLine.Match(text) is not { Success: true } line)
                continue;
            // From the in-game name, or the old name and the plugin's.
            var name = EditorIdFromName(ProjectNotes.NameIn(text), Free)
                ?? Numbered(editorId + (own is { } plugin ? Regex.Replace(plugin.Name, "[^A-Za-z0-9]", "") : "New"), Free);
            taken.Add(name);
            var value = line.Groups[1];
            File.WriteAllText(file, text[..value.Index] + name + text[(value.Index + value.Length)..]);
            var linker = files.FirstOrDefault(f => f.File != file && LinkBy(editorId).IsMatch(f.Text)).Relative;
            // A copy nothing names: its own faults, refused, are said after its delete (Check). Eval round 130's world+detailed #4
            // wrote the gold, the Steel Sword and steel arrows its chest holds as records of its own; refused for their fields
            // first, it wrote them anew until its turns ran out, the "nothing has this one" said only among the corrections.
            if (linker is not null && unneeded is not null)
                unneeded[relative.Replace('\\', '/')] = $"a second {entry.Type} the game holds for nothing — {linker} names {editorId}, " +
                    $"{key.ModKey.FileName}'s, and nothing of the project names this one: delete it rather than fix it, " +
                    $"{ToolCalls.Delete(pluginDir, relative, build: true)}; only where this one was meant — then name {name} in {linker} — fix it: ";
            changes.Add((relative,$"wrote `EditorID: {name}` for `EditorID: {editorId}` — {editorId} is {key.ModKey.FileName}'s " +
                $"{entry.Type} ({Session.FormKeyText(key)}), and this file, with no FormKey line, is a new record of its own; a link by " +
                $"{editorId} names {key.ModKey.FileName}'s. " + (linker is null
                    ? "To change that one instead, copy it in with addPatch and write this file empty"
                    : $"{linker} names {editorId}, so it has {key.ModKey.FileName}'s, and nothing has this one: where the game's " +
                        $"{editorId} was meant, this file is a second {entry.Type} the game holds for nothing — one call deletes it: " +
                        $"{ToolCalls.Delete(pluginDir, relative)}; where this one was meant, name {name} there")));
        }
        return changes;
    }

    /// <summary>
    /// A line of a record file whose value is this editor ID: a link by it — `Object: Gold001`, `- Gold001`. A quest's script
    /// properties named the game's gold and sword by editor ID while the model wrote a gold and a sword of the project's own
    /// under those IDs, which the build renamed: the links reached the game's, and the copies nothing (eval round 127's
    /// quest+detailed #4).
    /// </summary>
    private static Regex LinkBy(string editorId) =>
        new($@"^[ \t]*(?:-[ \t]*)?(?:(?!EditorID[ \t]*:)[A-Za-z_]\w*[ \t]*:[ \t]*)?{Regex.Escape(editorId)}[ \t]*(?:#[^\r\n]*)?\r?$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Gives one of two new records of different types written under one editor ID an editor ID of its own: the one a link
    /// of the other names — the type the link takes says which record it means — as the editor ID and its type's word,
    /// StormBoltEffect, with every link of the project's records that named it so pointed at it. Refused as one record written
    /// twice, with "delete Spells/StormBolt.yaml", the model deleted its spell and wrote it again, refused five times, and ran
    /// out of turns (eval round 84's spell+detailed #3: the magic effect and the spell casting it, both StormBolt). Two of
    /// one type, or with no link that says which is meant, or a link each way, are left to the refusal.
    /// </summary>
    public static List<(string File, string Change)> RenameSharedEditorIds(string pluginDir, RecordIndex index)
    {
        var changes = new List<(string File, string Change)>();
        var own = OwnPlugin(pluginDir);
        var files = FlatRecords(pluginDir).Select(f => (f.File, f.Relative, f.Type, Text: File.ReadAllText(f.File))).Where(f => !Empty(f.Text)).ToList();
        var taken = files.Select(f => Identity(f.Text).EditorId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Free(string name) => !taken.Contains(name) && !index.ByEditorId.ContainsKey(name);
        var shared = files.Where(f => Identity(f.Text) is { EditorId: not null } id && (id.FormKey is not { } key || key.ModKey == own))
            .GroupBy(f => Identity(f.Text).EditorId!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 2 && g.Select(f => f.Type).Distinct().Count() == 2).ToList();
        foreach (var pair in shared)
        {
            var id = pair.Key;
            // The links of a record's file that name the editor ID in a field taking the other's type.
            List<string> Naming((string File, string Relative, Type Type, string Text) namer, Type target) =>
                RecordLinks.LinksTo(namer.Text, namer.Type)
                    .Where(l => l.Value.Equals(id, StringComparison.OrdinalIgnoreCase) && l.Takes.IsAssignableFrom(target))
                    .Select(l => l.Path).ToList();
            var (first, second) = (pair.First(), pair.Last());
            var (firstNames, secondNames) = (Naming(first, second.Type), Naming(second, first.Type));
            if ((firstNames.Count > 0) == (secondNames.Count > 0)) continue;
            var (named, namer) = firstNames.Count > 0 ? (second, first) : (first, second);
            if (EditorIdLine.Match(named.Text) is not { Success: true } line) continue;
            var name = Numbered(id + TypeWord(named.Type), Free);
            // Every record of the project but the renamed one, with its links that meant it.
            var pointing = files.Where(f => f.Relative != named.Relative)
                .Select(f => (f.Relative, f.Type, Paths: Naming(f, named.Type))).Where(f => f.Paths.Count > 0).ToList();
            var value = line.Groups[1];
            File.WriteAllText(named.File, named.Text[..value.Index] + name + named.Text[(value.Index + value.Length)..]);
            var before = pointing.ToDictionary(p => p.Relative, p => files.First(f => f.Relative == p.Relative).Text);
            try
            {
                foreach (var (relative, _, paths) in pointing)
                    RecordEdits.Edit(pluginDir, relative, new System.Text.Json.Nodes.JsonObject(
                        paths.Select(p => KeyValuePair.Create(p, (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(name)))), null);
            }
            catch (HelperException)
            {
                // A link editRecord cannot set: everything as it was, and the refusal says what to do.
                File.WriteAllText(named.File, named.Text);
                foreach (var (relative, text) in before) File.WriteAllText(files.First(f => f.Relative == relative).File, text);
                continue;
            }
            taken.Add(name);
            changes.Add((named.Relative, $"wrote `EditorID: {name}` for `EditorID: {id}` — {namer.Relative}, {Article(namer.Type.Name)}, has that " +
                $"editor ID too, and an editor ID names one record: this {named.Type.Name}, which it names, has one of its own"));
            changes.AddRange(pointing.Select(p => (p.Relative, $"pointed {string.Join(", ", p.Paths.Select(x => $"`{x}`"))} at {name}, the " +
                $"{named.Type.Name} {named.Relative}, which {(p.Paths.Count == 1 ? "it" : "they")} named as {id}" +
                (p.Relative == namer.Relative ? $" — {id} stays this {namer.Type.Name}'s" : ""))));
        }
        return changes;
    }

    /// <summary>The word a renamed record's editor ID gets after the one it shared: a magic effect's Effect.</summary>
    internal static string TypeWord(Type type) => type.Name switch
    {
        "MagicEffect" => "Effect",
        "ObjectEffect" => "Enchantment",
        _ => type.Name
    };

    /// <summary>
    /// An editor ID made from an in-game name — its words, each capitalized: Maven's Warning is MavensWarning — the first
    /// of it, it numbered 2, 3… that `free` takes; null for a name with no letter to begin one.
    /// </summary>
    internal static string? EditorIdFromName(string? words, Func<string, bool> free)
    {
        var named = words is null ? ""
            : string.Concat(Regex.Split(Regex.Replace(words, "['’]", ""), "[^A-Za-z0-9]+").Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return named.Length > 0 && char.IsLetter(named[0]) ? Numbered(named, free) : null;
    }

    /// <summary>The name, or it numbered 2, 3… up to 99, the first that `free` takes.</summary>
    internal static string Numbered(string stem, Func<string, bool> free) =>
        Enumerable.Range(1, 99).Select(n => n == 1 ? stem : $"{stem}{n}").First(free);

    /// <summary>Throws what is wrong with the files, as the request's fault; answers how many there are, and those left out.</summary>
    public static (int Records, List<(string File, string Copy)> LeftOut) Require(string pluginDir, RecordIndex? index = null, Dictionary<string, string>? unneeded = null)
    {
        var (records, problems, leftOut) = Check(pluginDir, index, unneeded);
        if (problems.Count > 0)
        {
            throw new HelperException("badRecord",
                $"{problems.Count} record file(s) cannot be built as written: {string.Join("; ", problems.Take(12))}" +
                (problems.Count > 12 ? "; …" : "") +
                ". Write each record the way readRecord shows a record of its type — the same field names, nesting and " +
                "folder — changing only the values.");
        }
        return (records, leftOut);
    }

    /// <summary>
    /// A failure of the YAML reader as the request's fault: the file it names, the innermost cause, and
    /// the line the cause points at — "Expected 'MappingStart', got 'Scalar' (at Line: 2…)" alone sent a
    /// model nowhere (eval round 10).
    /// </summary>
    public static HelperException Unreadable(Exception error, string pluginDir)
    {
        var cause = error;
        while (cause.InnerException is not null) cause = cause.InnerException;
        var path = Regex.Match(error.Message, @"filepath:\s*(.+?)\s*$").Groups[1].Value;
        var where = path.Length > 0 ? Path.GetRelativePath(pluginDir, path).Replace('\\', '/') : "A record file";
        var message = cause.Message.Trim();
        var quoted = "";
        if (path.Length > 0 && File.Exists(path) && Regex.Match(message, @"\(at Line: (\d+), Col: (\d+)") is { Success: true } at)
        {
            var lines = File.ReadAllLines(path);
            var number = int.Parse(at.Groups[1].Value);
            if (number >= 1 && number <= lines.Length)
                quoted = $" Line {number} is `{lines[number - 1].Trim()}`{Expected(message)}.";
        }
        else if (path.Length > 0 && File.Exists(path) && message.Contains("hex string") && Groups.GetValueOrDefault(where.Split('/')[0]) is { } type)
            quoted = BytesWritten(File.ReadAllLines(path), type);
        else if (path.Length > 0 && File.Exists(path) && Regex.Match(message, @"^(\w+) was null\.?$") is { Success: true } nulled)
            quoted = EmptyWritten(File.ReadAllLines(path), nulled.Groups[1].Value);
        return new HelperException("badRecord",
            $"{where} cannot be read as a record: {message}{quoted} Write it the way readRecord shows a record of its " +
            "type — the same field names, nesting and value formats.");
    }

    /// <summary>
    /// The lines that write a field of bytes otherwise than as hex — `Unused2: 0` for the Iron Sword's Critical `Unused2:
    /// 0x000000` — for the reader's "not a valid hex string", which names no line: the model's sword was so refused three builds
    /// running (eval round 43's sword #3), and round 58's sword+detailed #5 wrote Critical's Unused2 so. Empty where none is.
    /// </summary>
    private static string BytesWritten(string[] lines, Type type)
    {
        var wrong = ByteFields(lines, type).Where(f => !WholeBytes(f.Value)).Select(f => $"line {f.Line + 1}, `{lines[f.Line].Trim()}`").ToList();
        return wrong.Count == 0 ? ""
            : $" Of bytes, written otherwise: {string.Join(", ", wrong)} — a field of bytes is written as readRecord shows it, 0x and two hex " +
                "digits a byte, as `Unused2: 0x000000`.";
    }

    /// <summary>
    /// The lines that write a field empty — `ObjectEffect: ''` — for the reader's "ObjectEffect was null", which names no
    /// line: the model's Flame Blade was so refused three builds running, and its edits of `''` found it twice in the file
    /// (eval round 94's sword+detailed #1). A field at the margin is named with editRecord's path, which needs no old text.
    /// Empty where none is.
    /// </summary>
    private static string EmptyWritten(string[] lines, string key)
    {
        var empty = new Regex($@"^([ \t]*){Regex.Escape(key)}:[ \t]*(?:''|""""|~|null)?[ \t]*(?:#.*)?$");
        var found = lines.Select((line, at) => (Line: line, At: at, Match: empty.Match(line.TrimEnd('\r')))).Where(l => l.Match.Success).ToList();
        if (found.Count == 0) return "";
        return $" Written empty: {string.Join(", ", found.Select(l => $"line {l.At + 1}, `{l.Line.Trim()}`"))} — write its value there, " +
            "for a link the editor ID of the record it names, or delete the line for none" +
            (found.All(l => l.Match.Groups[1].Length == 0)
                ? $"; editRecord does either with no old text to match, `set` with the path `{key}` or `remove` with it."
                : ".");
    }

    /// <summary>Each field of bytes a record file writes, with a value (see <see cref="ValueFields"/>).</summary>
    private static List<(int Line, Type? Part, string? PartKey, string Key, string Value)> ByteFields(string[] lines, Type type) =>
        ValueFields(lines, type).Where(f => Bytes(f.Held)).Select(f => (f.Line, f.Part, f.PartKey, f.Key, f.Value)).ToList();

    /// <summary>
    /// Each field a record file writes with a value on its line, found line by line in the part its indent puts it in — Critical's
    /// Unused is a number, Data's three bytes: its line (from 0), the part it is a field of and that part's key, the field, the value,
    /// the type the field holds, and the kind of its part that has the field.
    /// </summary>
    private static List<(int Line, Type? Part, string? PartKey, string Key, string Value, Type Held, Type Owner)> ValueFields(string[] lines, Type type)
    {
        var parts = new List<(int Column, Type Part, string Key)>();
        var found = new List<(int, Type?, string?, string, string, Type, Type)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (Regex.Match(lines[i], @"^(?<lead>[ \t]*(?:-[ \t]+)?)(?<key>[A-Za-z_]\w*)[ \t]*:[ \t]*(?<value>[^\s#]*)") is not { Success: true } field) continue;
            var column = field.Groups["lead"].Length;
            while (parts.Count > 0 && parts[^1].Column >= column) parts.RemoveAt(parts.Count - 1);
            var key = field.Groups["key"].Value;
            if (KindsOf(parts.Count > 0 ? parts[^1].Part : type).Select(kind => (Kind: kind, Property: kind.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.Name == key))).FirstOrDefault(p => p.Property is not null) is not { Property: { } property } owned)
                continue;
            var held = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var value = field.Groups["value"].Value;
            if (value.Length > 0)
                found.Add((i, parts.Count > 0 ? parts[^1].Part : null, parts.Count > 0 ? parts[^1].Key : null, key, value, held, owned.Kind));
            else if (!Bytes(held))
                parts.Add((column, held != typeof(string) && held.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(held)
                    ? held.GetGenericArguments()[0] : held, key));
        }
        return found;
    }

    /// <summary>Whether a field holds bytes, which a file writes as hex.</summary>
    private static bool Bytes(Type held) =>
        held == typeof(byte[]) || held.IsGenericType && held.GetGenericArguments() is [var of] && of == typeof(byte) && held.Name.Contains("MemorySlice");

    /// <summary>Bytes as readRecord writes them: 0x and two hex digits a byte.</summary>
    private static bool WholeBytes(string value) => Regex.IsMatch(value, "^0x(?:[0-9A-Fa-f]{2})+$");

    /// <summary>
    /// Writes a number written in hex in its digits, and a field of bytes written as the number 0, or as zero bytes of another
    /// length, as zero bytes of its type's length — each the value written, as readRecord writes it. The model's sword wrote the three fields under Data it did not
    /// know alike, a number between two fields of bytes: all `0x` and twelve bytes, refused as "Data.Unknown2 … takes a whole
    /// number", then all `0`, refused as bytes written otherwise, and back, six builds, until its turns ran out (eval round 80's
    /// sword+detailed #4). Bytes written as another number, and hex too large for its field, are still refused.
    /// </summary>
    public static List<(string File, string Change)> MendNumbersAndBytes(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            var lines = File.ReadAllText(file).Split('\n');
            var mended = new List<string>();
            foreach (var f in ValueFields(lines, type))
            {
                var bytes = Bytes(f.Held);
                if ((bytes ? ZeroBytes(f.Owner, f.Key, f.Value) : NumberOfHex(f.Held, f.Value)) is not { } now) continue;
                lines[f.Line] = new Regex($@"(\b{Regex.Escape(f.Key)}[ \t]*:[ \t]*){Regex.Escape(f.Value)}").Replace(lines[f.Line], m => m.Groups[1].Value + now, 1);
                var was = f.Value.Length > 16 ? $"{f.Value[..16]}…" : f.Value;
                var shown = now.Length > 16 ? $"{now[..16]}…" : now;
                mended.Add($"wrote line {f.Line + 1}, {(f.PartKey is { } part ? $"{part}'s " : "")}`{f.Key}: {was}` as `{f.Key}: {shown}` — " +
                    (!bytes ? "a number, written in hex; readRecord writes it in digits"
                        : WholeBytes(f.Value) ? $"a field of {(now.Length - 2) / 2} zero bytes, written as {(f.Value.Length - 2) / 2}"
                            : $"a field of {(now.Length - 2) / 2} bytes, written as the number 0; readRecord writes bytes as 0x and two hex digits a byte"));
            }
            if (mended.Count == 0) continue;
            File.WriteAllText(file, string.Join('\n', lines));
            changes.AddRange(mended.Select(m => (relative, m)));
        }
        return changes;
    }

    /// <summary>
    /// Words written for a field of an NPC's AIData that it has no value of, each with the value meant and what the word is
    /// instead: the eval's NPC runs of rounds 87–106 wrote Aggression Average 376 times, Neutral 23 and Passive twice, and
    /// Responsibility Average once, beside NoCrime 227 times.
    /// </summary>
    private static readonly (Type Held, string Meant, Dictionary<string, string> Words)[] AIWords =
    [
        (typeof(Aggression), "Unaggressive", new(StringComparer.OrdinalIgnoreCase)
        {
            ["Average"] = "Average is a Confidence",
            ["Neutral"] = "Neutral is a Mood",
            ["Passive"] = "no Aggression is named Passive",
            ["Peaceful"] = "no Aggression is named Peaceful"
        }),
        (typeof(Responsibility), "NoCrime", new(StringComparer.OrdinalIgnoreCase) { ["Average"] = "Average is a Confidence" })
    ];

    /// <summary>An NPC's AIData written in braces on one line, with one of a field's words.</summary>
    private static Regex InBraces(Type held, Dictionary<string, string> words) =>
        new($@"^(AIData:[ \t]*\{{[^}}#]*\b{held.Name}:[ \t]*)({string.Join("|", words.Keys)})\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Writes an NPC's `Aggression: Average` as Unaggressive: Aggression names no average — Unaggressive, Aggressive,
    /// VeryAggressive, Frenzied; Average is a Confidence. 30 of the 80 NPC runs of eval rounds 76–83 wrote it, each was refused,
    /// and each set Unaggressive in one more call — the value the official plugins' named NPCs have most often, which the
    /// refusal names. So too Neutral and the other words for it (AIWords): told Neutral is none of the four, a model
    /// set Aggressive, which its task did not ask (eval round 106's npc+detailed #2, which failed); and Responsibility's Average,
    /// NoCrime, the value nearly every run wrote. Written in braces too, as the npcs skill's reference writes AIData.
    /// </summary>
    public static List<(string File, string Change)> MendAIData(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            if (type != typeof(Npc)) continue;
            var lines = File.ReadAllText(file).Split('\n');
            var mended = new List<(int Line, Type Held, string Word)>();
            foreach (var (held, meant, words) in AIWords)
            {
                var named = string.Join("|", words.Keys);
                foreach (var f in ValueFields(lines, type).Where(f => f.Held == held && words.ContainsKey(f.Value)))
                {
                    lines[f.Line] = Regex.Replace(lines[f.Line], $@"(\b{held.Name}[ \t]*:[ \t]*)(?:{named})\b", $"${{1}}{meant}", RegexOptions.IgnoreCase);
                    mended.Add((f.Line, held, f.Value));
                }
                var braces = InBraces(held, words);
                for (var i = 0; i < lines.Length; i++)
                    if (braces.Match(lines[i]) is { Success: true } braced)
                    {
                        lines[i] = braces.Replace(lines[i], $"${{1}}{meant}", 1);
                        mended.Add((i, held, braced.Groups[2].Value));
                    }
            }
            if (mended.Count == 0) continue;
            File.WriteAllText(file, string.Join('\n', lines));
            changes.AddRange(mended.OrderBy(m => m.Line).Select(m =>
            {
                var (_, meant, words) = AIWords.First(w => w.Held == m.Held);
                var names = Enum.GetNames(m.Held);
                return (relative, $"wrote line {m.Line + 1}, AIData's `{m.Held.Name}: {m.Word}` as `{m.Held.Name}: {meant}` — {m.Held.Name} " +
                    $"takes {string.Join(", ", names[..^1])} or {names[^1]}, and {words[m.Word]}; {RecordLinks.MostOften(m.Held)}");
            }));
        }
        return changes;
    }

    /// <summary>
    /// An NPC's level written as a whole number, `Level: 5`, written as the fixed level it says, `Level: {MutagenObjectType:
    /// NpcLevel, Level: 5}`: a level is its kind and its number, and the reader refused the number alone — 11 NPC runs of eval
    /// rounds 89–107, each 1–5 calls before it built. A level that follows the player's has no number of its own; where the file
    /// has CalcMinLevel and CalcMaxLevel too, the build's warning for a fixed level beside its bounds gives the call for one.
    /// </summary>
    public static List<(string File, string Change)> MendLevel(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            if (type != typeof(Npc)) continue;
            var lines = File.ReadAllText(file).Split('\n');
            var mended = new List<string>();
            foreach (var f in ValueFields(lines, type).Where(f => f.Key == "Level" && f.Held == typeof(ANpcLevel)))
            {
                if (!short.TryParse(f.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var level)) continue;
                var fixedLevel = $"{{MutagenObjectType: NpcLevel, Level: {level}}}";
                lines[f.Line] = new Regex(@"(\bLevel[ \t]*:[ \t]*)\d+").Replace(lines[f.Line], m => m.Groups[1].Value + fixedLevel, 1);
                mended.Add($"wrote line {f.Line + 1}, {(f.PartKey is { } part ? $"{part}'s " : "")}`Level: {f.Value}` as `Level: {fixedLevel}` — " +
                    "a level written as a number is a fixed one, NpcLevel's; one that follows the player's is " +
                    "`{MutagenObjectType: PcLevelMult, LevelMult: 1}`, between CalcMinLevel and CalcMaxLevel");
            }
            if (mended.Count == 0) continue;
            File.WriteAllText(file, string.Join('\n', lines));
            changes.AddRange(mended.Select(m => (relative, m)));
        }
        return changes;
    }

    private static readonly Regex EmptyLevelLine = new(@"^(?<indent>[ \t]*)Level[ \t]*:[ \t]*(?:#.*)?\r?$");
    private static readonly Regex NameFieldLine = new(@"^[ \t]+(?<key>TargetLanguage|Value)[ \t]*:");
    private static readonly Regex MarginNameLine = new(@"^Name[ \t]*:", RegexOptions.Multiline);

    /// <summary>
    /// An NPC's name written below `Level:` — a name's lines, `TargetLanguage:` and `Value:` — where the NPC has no `Name:`,
    /// written as its Name at the margin: the model's Hilda had `Level:` where `Name:` was meant, and refused for a level that
    /// names no kind, it set Configuration.Level.TargetLanguage, then a level, and answered with a Hilda the game shows nameless
    /// (eval round 108's npc #1). With a Name of its own, the refusal says the lines are a name's (RecordLinks).
    /// </summary>
    public static List<(string File, string Change)> NameOutOfLevel(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            if (type != typeof(Npc)) continue;
            var text = File.ReadAllText(file);
            if (MarginNameLine.IsMatch(text)) continue;
            var lines = text.Split('\n').ToList();
            static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;
            for (var i = 0; i < lines.Count; i++)
            {
                if (EmptyLevelLine.Match(lines[i]) is not { Success: true } header) continue;
                var indent = header.Groups["indent"].Length;
                var below = new List<int>();
                for (var j = i + 1; j < lines.Count && (lines[j].Trim().Length == 0 || IndentOf(lines[j]) > indent); j++)
                    if (lines[j].Trim().Length > 0) below.Add(j);
                var keys = below.Select(j => NameFieldLine.Match(lines[j])).ToList();
                if (below.Count == 0 || keys.Any(k => !k.Success) || keys.All(k => k.Groups["key"].Value != "Value")) continue;
                var eol = lines[i].EndsWith('\r') ? "\r" : "";
                var name = new List<string> { "Name:" + eol };
                name.AddRange(below.Select(j => "  " + lines[j].TrimStart(' ', '\t')));
                var written = string.Join(", ", below.Select(j => $"`{lines[j].Trim()}`"));
                lines.RemoveRange(i, below[^1] - i + 1);
                // At the margin below the EditorID, or the FormKey, where a vanilla record has it near the top.
                var at = lines.FindIndex(l => Regex.IsMatch(l, @"^EditorID[ \t]*:"));
                if (at < 0) at = lines.FindIndex(l => Regex.IsMatch(l, @"^FormKey[ \t]*:"));
                lines.InsertRange(at + 1, name);
                File.WriteAllText(file, string.Join('\n', lines));
                changes.Add((relative, $"wrote `Level:` (line {i + 1}) and the lines below it, {written}, as the NPC's `Name:` at the margin — " +
                    $"they are a name's lines, and {Identity(text).EditorId ?? "the NPC"} had no Name; a level is " +
                    "`Level: {MutagenObjectType: …}` under Configuration"));
                break;
            }
        }
        return changes;
    }

    private static readonly Regex AnyLevelLine = new(@"^[ \t]*Level[ \t]*:", RegexOptions.Multiline);
    private static readonly Regex TemplateLink = new(@"^[ \t]*Template[ \t]*:[ \t]*(?!(?:Null|null|~)[ \t]*(?:#|\r?$))[^\s#]", RegexOptions.Multiline);

    /// <summary>
    /// A new NPC's level bounds, CalcMinLevel and CalcMaxLevel, with no Level line, given the level they bound — one that follows
    /// the player's, `Level: {MutagenObjectType: PcLevelMult, LevelMult: 1}`, written above them: with no Level, its level is
    /// fixed at 0, which bounds do not bound. 25 NPC files of eval rounds 87–108 were first written so; warned of the fixed level,
    /// two runs made both of the warning's calls, the level and the bounds removed (rounds 106's and 108's npc+detailed #2). A
    /// level written beside the bounds keeps the warning (LevelBounds); an NPC with a template, which may give its level, is left.
    ///
    /// An empty `Level:` beside the bounds, nothing below it, is no level either, and the level is written in its place: the
    /// model wrote the bounds below `Level:`, the build moved them out beside it, and refused the empty Level; it took seven
    /// calls on the level to build (eval round 109's npc+detailed #1, round 104's #3).
    /// </summary>
    public static List<(string File, string Change)> LevelForBounds(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        var own = OwnPlugin(pluginDir);
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            if (type != typeof(Npc)) continue;
            var text = File.ReadAllText(file);
            if (Identity(text).FormKey is { } key && key.ModKey != own) continue;
            if (TemplateLink.IsMatch(text)) continue;
            var lines = text.Split('\n').ToList();
            var bounds = ValueFields(lines.ToArray(), type)
                .Where(f => f.PartKey == "Configuration" && f.Key is "CalcMinLevel" or "CalcMaxLevel").ToList();
            if (bounds.Count == 0 || bounds.All(f => short.TryParse(f.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n == 0)) continue;
            var first = bounds.MinBy(f => f.Line);
            var indent = lines[first.Line][..(lines[first.Line].Length - lines[first.Line].TrimStart(' ', '\t').Length)];
            const string follows = "{MutagenObjectType: PcLevelMult, LevelMult: 1}";
            var levels = lines.Select((line, at) => (Line: line, At: at)).Where(l => AnyLevelLine.IsMatch(l.Line)).ToList();
            string where;
            if (levels.Count == 0)
            {
                lines.Insert(first.Line, $"{indent}Level: {follows}{(lines[first.Line].EndsWith('\r') ? "\r" : "")}");
                where = $"as line {first.Line + 1}, above Configuration's `{first.Key}: {first.Value}` — {Identity(text).EditorId ?? "the NPC"} " +
                    "had level bounds and no Level, a level fixed at 0 that they do not bound";
            }
            else if (levels is [var level] && EmptyLevelLine.Match(level.Line) is { Success: true } empty && empty.Groups["indent"].Value == indent
                && lines.Skip(level.At + 1).FirstOrDefault(l => l.Trim().Length > 0) is var next
                && (next is null || next.Length - next.TrimStart(' ', '\t').Length <= indent.Length))
            {
                lines[level.At] = $"{indent}Level: {follows}{(level.Line.EndsWith('\r') ? "\r" : "")}";
                where = $"in line {level.At + 1}, `Level:` with nothing below it — {Identity(text).EditorId ?? "the NPC"} had level bounds, " +
                    $"Configuration's `{first.Key}: {first.Value}`, and an empty Level, which is no level";
            }
            else continue;
            File.WriteAllText(file, string.Join('\n', lines));
            changes.Add((relative, $"wrote `Level: {follows}` {where}; they bound a level that " +
                "follows the player's, as this one does between them. A fixed level is `Level: {MutagenObjectType: NpcLevel, Level: 10}`, " +
                "with no bounds"));
        }
        return changes;
    }

    /// <summary>
    /// A field of bytes written as the number 0, or as zero bytes of another length, as zero bytes of the length its type gives
    /// it — the sword's Data.Unused, three bytes, was written as twelve; null for any other value, or a length unknown.
    /// </summary>
    private static string? ZeroBytes(Type owner, string key, string value)
    {
        if (!Regex.IsMatch(value, "^(?:0[xX])?0+$")) return null;
        int? length;
        try
        {
            length = owner.GetProperty(key)?.GetValue(System.Activator.CreateInstance(owner)) switch
            {
                byte[] held => held.Length,
                { } slice => slice.GetType().GetProperty("Length")?.GetValue(slice) as int?,
                null => null
            };
        }
        catch (Exception e) when (e is MissingMethodException or TargetInvocationException or MemberAccessException)
        {
            return null;
        }
        return length is > 0 && "0x" + new string('0', length.Value * 2) is var zeros && zeros != value ? zeros : null;
    }

    /// <summary>A number field written in hex, in its digits — a whole number where it fits the field, a fraction's only as 0; null for any other.</summary>
    private static string? NumberOfHex(Type held, string value)
    {
        if (!held.IsPrimitive || held == typeof(bool) || held == typeof(char) || Regex.Match(value, "^0[xX]([0-9A-Fa-f]+)$") is not { Success: true } hex) return null;
        var number = BigInteger.Parse("0" + hex.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (held == typeof(float) || held == typeof(double)) return number.IsZero ? "0" : null;
        var (min, max) = (new BigInteger(Convert.ToDecimal(held.GetField("MinValue")!.GetValue(null), CultureInfo.InvariantCulture)),
            new BigInteger(Convert.ToDecimal(held.GetField("MaxValue")!.GetValue(null), CultureInfo.InvariantCulture)));
        return number >= min && number <= max ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Takes out a model's texture hashes — the `Data` of a `Model` — copied otherwise than as whole bytes, which the reader
    /// refuses as "not a valid hex string": the model's note copied Maven's note's 122 hex digits as 123, and, refused for
    /// its text's lines before the reader came to them, built no more (eval round 78's note #4). The game shows a model
    /// without them, and the Creation Kit writes them anew; any other field of bytes is still refused, named by its line.
    /// </summary>
    public static List<(string File, string Change)> DropBrokenModelData(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in FlatRecords(pluginDir))
        {
            var lines = File.ReadAllText(file).Split('\n').ToList();
            var broken = ByteFields(lines.ToArray(), type)
                .Where(f => f.Key == "Data" && f.Part?.Name.EndsWith("Model", StringComparison.Ordinal) == true && !WholeBytes(f.Value))
                .ToList();
            if (broken.Count == 0) continue;
            foreach (var f in broken.OrderByDescending(f => f.Line)) lines.RemoveAt(f.Line);
            File.WriteAllText(file, string.Join('\n', lines));
            foreach (var f in broken)
            {
                var digits = Regex.Match(f.Value, "^0x([0-9A-Fa-f]*)$") is { Success: true } hex ? $"{hex.Groups[1].Length} hex digits" : "no hex number";
                var shown = f.Value.Length > 14 ? $"{f.Value[..14]}…" : f.Value;
                changes.Add((relative, $"removed line {f.Line + 1}, {f.PartKey}'s `Data: {shown}` — a model's texture hashes, written as " +
                    $"{digits} where they are whole bytes, two hex digits each; the game shows the model without them, and the " +
                    "Creation Kit writes them anew"));
            }
        }
        return changes;
    }

    /// <summary>What the reader's "Expected X, got Y" means for the line it names.</summary>
    private static string Expected(string message) => Regex.Match(message, @"Expected '(\w+)', got '(\w+)'") switch
    {
        { Success: true } m when m.Groups[1].Value == "MappingStart" =>
            ": its field takes fields of its own, indented on the lines below it, not one value",
        { Success: true } m when m.Groups[1].Value == "SequenceStart" =>
            ": its field takes a list, one `- ` item per line below it",
        { Success: true } m when m.Groups[1].Value == "Scalar" =>
            ": its field takes one value on its own line, not fields or a list below it",
        // A field written as an item of a list of names: `- CalcMinLevel: 6` among an NPC's Flags (refusal probes of 2026-11-01).
        { Success: true } m when m.Groups[1].Value == "SequenceEnd" && m.Groups[2].Value == "MappingStart" =>
            ": it is a field written as an item of a list of names — write it without its dash, below the list, as deep as the list's own line",
        // Below a field of one value, or indented deeper than the fields beside it (eval probe 10 after round 11).
        { Success: true } m when m.Groups[1].Value == "MappingEnd" =>
            ": it is indented below a field that takes one value on its own line, or deeper than the fields beside it",
        _ => ""
    };
}
