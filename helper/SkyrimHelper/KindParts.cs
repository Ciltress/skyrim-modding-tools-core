using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// A new weapon or armor that lacks what the load order's records of its kind carry, as notes beside a build that
/// succeeds: a link — a first-person model, a sound, an impact set — or a keyword that 9 in 10 of the load order's plain,
/// playable records of its kind have (a weapon's kind its animation, an armor's its weight and first slot), an armor's
/// Armature, and a weapon on the mesh of another kind's weapons. 9 sword runs of eval rounds 87–105 passed with a sword
/// that had no FirstPersonModel, invisible in the player's hands in first person, and two of them on a mesh the game has
/// (rounds 89 and 103); round 89's sword #2 was on a war axe's mesh. Each note writes the one call that copies what is
/// missing from the record of its kind most like it: on its mesh where one is, then the one sharing the most links.
/// </summary>
internal static class KindParts
{
    /// <summary>The share of a kind's records that carry a part for a record of the kind to be expected to.</summary>
    private const double Usual = 0.9;
    /// <summary>The fewest records of a kind its parts are read from.</summary>
    private const int Fewest = 5;

    /// <summary>What a record lacking a part looks like in game, where it shows.</summary>
    private static readonly Dictionary<string, string> InGame = new()
    {
        ["FirstPersonModel"] = "without a FirstPersonModel the player's hands hold nothing in first person",
        ["EquipSound"] = "drawn in silence",
        ["UnequipSound"] = "sheathed in silence",
        ["ImpactDataSet"] = "a hit shows no sparks or blood and makes no sound",
        ["PickUpSound"] = "silent when picked up",
        ["PutDownSound"] = "silent when put down",
        ["Keywords"] = "perks, smithing and vendors read an item's kind by its keywords",
        ["Armature"] = "with no Armature nothing is drawn on the body when it is worn",
        ["Model"] = "with no Model nothing shows when it is dropped, or in the inventory",
        ["WorldModel"] = "with no WorldModel nothing shows when it is dropped, or in the inventory"
    };

    /// <summary>The game's own plugins, whose records are offered before a Creation Club plugin's.</summary>
    internal static readonly string[] Masters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    /// <summary>The keywords that say which part of the body an armor or a piece of clothing is for.</summary>
    private static readonly string[] SlotKeywords = ["ArmorHelmet", "ArmorCuirass", "ArmorGauntlets", "ArmorBoots", "ArmorShield",
        "ClothingHead", "ClothingBody", "ClothingHands", "ClothingFeet", "ClothingCirclet", "ClothingNecklace", "ClothingRing"];

    /// <summary>A record of the load order, its kind, its mesh and the parts it carries.</summary>
    private sealed record Known(IMajorRecordGetter Record, int At, string? Kind, string? Model, Dictionary<string, FormKey> Links,
        HashSet<FormKey> Keywords, List<FormKey> Armature, bool Plain);

    public static List<string> Notes(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var notes = new List<string>();
        var own = mod.ModKey;
        var mine = mod.Weapons.Where(w => w.FormKey.ModKey == own && w.Template.IsNull).Cast<IMajorRecordGetter>()
            .Concat(mod.Armors.Where(a => a.FormKey.ModKey == own && a.TemplateArmor.IsNull))
            .ToList();
        if (mine.Count == 0) return notes;
        var files = EffectCasting.Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        var dummy = index.ByEditorId.TryGetValue("Dummy", out var dummyKey) ? dummyKey : (FormKey?)null;
        var loaded = new Dictionary<string, List<Known>>();
        List<Known> Loaded(string signature) => loaded.TryGetValue(signature, out var list) ? list : loaded[signature] = index.ByFormKey.Values
            .Where(e => e.Signature == signature && !e.Unplayable && e.FormKey.ModKey != own)
            .Select(e => lookup.TryResolve(e.FormKey, e.GetterType, out var record) ? record : null)
            .OfType<IMajorRecordGetter>()
            .Select((record, at) => Read(record, at))
            .ToList();
        string Named(FormKey key) => index.ByFormKey.TryGetValue(key, out var entry) && entry.EditorId is { } id ? id
            : mod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == key)?.EditorID ?? Session.FormKeyText(key);

        foreach (var record in mine)
        {
            if (files.GetValueOrDefault(record.FormKey) is not { } relative) continue;
            var me = Read(record, -1);
            if (me.Kind is null)
            {
                if (record is IArmorGetter armor && SlotNote(armor, me, Loaded("ARMO"), index, relative, project, Named) is { } slot) notes.Add(slot);
                continue;
            }
            // Of another weight than its keyword says: held against armors of that weight, the note asked for their keywords.
            if (record is IArmorGetter weighed && WeightNote(weighed, me, index, relative, project) is { } weight)
            {
                notes.Add(weight);
                continue;
            }
            var all = Loaded(record is IWeaponGetter ? "WEAP" : "ARMO");
            // Not the Civil War's practice weapons, keyword Dummy: 9 of the 48 plain one-handed swords, and none with a type keyword.
            var kind = all.Where(k => k.Kind == me.Kind && k.Plain && !(dummy is { } practice && k.Keywords.Contains(practice))).ToList();
            var name = record.EditorID ?? Session.FormKeyText(record.FormKey);
            var label = record is IWeaponGetter ? $"{me.Kind} weapons" : $"{me.Kind} armors";

            // On the mesh of another kind's weapons: shown as one of them in the world and in the hand.
            if (record is IWeaponGetter && me.Model is { } model && kind.Count > 0
                && all.Where(k => string.Equals(k.Model, model, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } onIt
                && onIt.All(k => k.Kind != me.Kind) && Like(me, kind) is { Model: { } likeModel } likeKind)
            {
                var other = onIt.GroupBy(k => k.Kind).OrderByDescending(g => g.Count()).First().Key;
                var set = new List<string> { Field("Model.File", likeModel) };
                if (likeKind.Links.TryGetValue("FirstPersonModel", out var hands)) set.Add(Field("FirstPersonModel", Named(hands)));
                notes.Add($"{relative}: {name}'s model, {model}, is the mesh of {onIt.Count} {other} weapon{(onIt.Count == 1 ? "" : "s")} of the " +
                    $"load order and of no {me.Kind} one — in game {name} looks like {RecordFiles.Article(other ?? "other")} weapon. " +
                    $"{Shown(likeKind.Record)}, the {me.Kind} weapon most like it, is on {likeModel}: one call gives {name} that model" +
                    (set.Count > 1 ? " and the first-person model with it" : "") + $", and builds again: {ToolCalls.Fix(project, relative, string.Join(", ", set))}");
            }

            if (kind.Count < Fewest) continue;
            var lacking = kind.SelectMany(k => k.Links.Keys).Distinct()
                .Where(part => !me.Links.ContainsKey(part) && kind.Count(k => k.Links.ContainsKey(part)) >= Usual * kind.Count)
                .OrderBy(part => part, StringComparer.Ordinal).ToList();
            var keywords = kind.SelectMany(k => k.Keywords).Distinct()
                .Where(key => !me.Keywords.Contains(key) && kind.Count(k => k.Keywords.Contains(key)) >= Usual * kind.Count).ToList();
            var noArmature = record is IArmorGetter && me.Armature.Count == 0 && kind.Count(k => k.Armature.Count > 0) >= Usual * kind.Count;
            // No mesh at all: nothing shows on the ground or in the inventory (eval round 128's armor+detailed #1, a helmet with none).
            var modelField = record is IWeaponGetter ? "Model" : "WorldModel";
            var noModel = me.Model is null && kind.Count(k => k.Model is not null) >= Usual * kind.Count;
            if (lacking.Count == 0 && keywords.Count == 0 && !noArmature && !noModel) continue;

            var like = Like(me, kind.Where(k => lacking.All(k.Links.ContainsKey) && keywords.All(k.Keywords.Contains) && (!noArmature || k.Armature.Count > 0)
                    && (!noModel || k.Model is not null)).ToList())
                ?? Like(me, kind);
            var parts = lacking.Select(part => (Part: part, Said: part, Value: like?.Links.GetValueOrDefault(part))).ToList();
            var calls = parts.Where(p => p.Value is { } v && !v.IsNull).Select(p => Field(p.Part, Named(p.Value!.Value))).ToList();
            var said = lacking.Select(part => $"no {part}").ToList();
            if (noModel)
            {
                said.Insert(0, $"no {modelField}");
                if (like?.Model is { } mesh) calls.Insert(0, Field(record is IWeaponGetter ? "Model.File" : "WorldModel.Male.Model.File", mesh));
            }
            if (keywords.Count > 0)
            {
                said.Add($"not the keyword{(keywords.Count == 1 ? "" : "s")} {string.Join(" and ", keywords.Select(Named))}");
                calls.Add(List("Keywords", me.Keywords.Select(Named).Concat(keywords.Select(Named)).Distinct()));
            }
            if (noArmature)
            {
                said.Add("no Armature");
                if (like is { Armature.Count: > 0 }) calls.Add(List("Armature", like.Armature.Select(Named)));
            }
            var ways = (noModel ? [modelField] : Array.Empty<string>()).Concat(lacking).Concat(keywords.Count > 0 ? ["Keywords"] : [])
                .Concat(noArmature ? ["Armature"] : [])
                .Select(part => InGame.GetValueOrDefault(part)).OfType<string>().ToList();
            var one = lacking.Count + keywords.Count + (noArmature ? 1 : 0) + (noModel ? 1 : 0) == 1;
            notes.Add($"{relative}: {name} has {Joined(said)}, and 9 in 10 or more of the load order's {kind.Count} {label} have " +
                (one ? "it" : "each") + (ways.Count > 0 ? $" — {string.Join("; ", ways)}" : "") + ". " +
                (like is not null && calls.Count > 0
                    ? $"{Shown(like.Record)}, the one most like it, has {(one ? "it" : "them")}: one call writes {(one ? "it" : "them")} as " +
                      $"{Shown(like.Record)} has {(one ? "it" : "them")}, and builds again: " +
                      ToolCalls.Fix(project, relative, string.Join(", ", calls))
                    : "Write them as a record of its kind has them, as readRecord shows one"));
        }
        return notes;
    }

    /// <summary>
    /// An armor that fills no body slot — its BodyTemplate names no FirstPersonFlags — which cannot be worn, with what else it lacks
    /// to be seen: its slots read from the load order's plain armors of its weight that carry its slot keyword (ArmorHelmet), the
    /// slots most of them fill, and the call that writes them, its mesh and its Armature as the one most like it has them. Eval
    /// round 128's armor+detailed #1 wrote a helmet with ArmorHelmet among its keywords and no slot, mesh or Armature, built as a
    /// plain success: the kind of armor it is, read from its slot, was none, and no note was given.
    /// </summary>
    private static string SlotNote(IArmorGetter armor, Known me, List<Known> all, RecordIndex index, string relative, string project,
        Func<FormKey, string> named)
    {
        var name = armor.EditorID ?? Session.FormKeyText(armor.FormKey);
        var type = armor.BodyTemplate?.ArmorType;
        var slotWords = SlotKeywords.Where(w => index.ByEditorId.TryGetValue(w, out var key) && me.Keywords.Contains(key))
            .Select(w => (Word: w, Key: index.ByEditorId[w])).ToList();
        var lacks = new List<string>();
        if (me.Model is null) lacks.Add("no WorldModel, so nothing shows when it is dropped");
        if (me.Armature.Count == 0) lacks.Add("no Armature, so nothing is drawn on the body");
        var said = $"{relative}: {name} fills no body slot — its BodyTemplate names no FirstPersonFlags — so it cannot be worn" +
            (lacks.Count > 0 ? $"; it has {string.Join(", and ", lacks)}" : "");
        var kin = all.Where(k => k.Plain && k.Kind is not null && k.Record is IArmorGetter other
            && slotWords.Any(w => k.Keywords.Contains(w.Key)) && (type is null || other.BodyTemplate?.ArmorType == type)).ToList();
        if (slotWords.Count == 0 || kin.Count < Fewest)
            return said + ". Write the slots it fills under BodyTemplate, `FirstPersonFlags:` as deep as its `ArmorType:` line, each on a " +
                "line of its own — `Hair` and `Circlet` for a helmet, `Body` for a cuirass, `Hands`, `Feet`, `Shield` — with its WorldModel and " +
                "Armature, as readRecord of the game's armor of its kind shows them (ArmorSteelHelmetA, ArmorSteelCuirassA)";
        // The slots most of them fill, all of them; the one most like it among those, of the five masters where one is: by its links
        // alone a Creation Club helmet was offered first.
        var usual = kin.GroupBy(k => ((IArmorGetter)k.Record).BodyTemplate!.FirstPersonFlags).OrderByDescending(g => g.Count()).First();
        var official = usual.Where(k => Masters.Contains(k.Record.FormKey.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase)).ToList();
        var pool = official.Count > 0 ? official : usual.ToList();
        var like = Like(me, pool.Where(k => (me.Model is not null || k.Model is not null) && (me.Armature.Count > 0 || k.Armature.Count > 0)).ToList())
            ?? Like(me, pool)!;
        var flags = usual.Key;
        var slots = Enum.GetValues<BipedObjectFlag>().Where(f => f != 0 && flags.HasFlag(f)).Select(f => f.ToString()).Distinct().ToList();
        var calls = new List<string> { List("BodyTemplate.FirstPersonFlags", slots) };
        if (me.Model is null && like.Model is { } model) calls.Add(Field("WorldModel.Male.Model.File", model));
        if (me.Armature.Count == 0 && like.Armature.Count > 0) calls.Add(List("Armature", like.Armature.Select(named)));
        var word = slotWords.First(w => like.Keywords.Contains(w.Key)).Word;
        return said + $". {usual.Count()} of the load order's {kin.Count} {(type is null ? "" : $"{type} ")}armors with the keyword {word} fill " +
            $"{string.Join(" and ", slots)}; {Shown(like.Record)}, the one most like it, has {(calls.Count == 1 ? "them" : "them and the rest")}: " +
            $"one call writes {(calls.Count == 1 ? "its slots" : "them")} as {Shown(like.Record)} has them, and builds again: " +
            ToolCalls.Fix(project, relative, string.Join(", ", calls));
    }

    /// <summary>The keywords that say an armor's weight, and the ArmorType each goes with.</summary>
    private static readonly (string Keyword, ArmorType Type, string Said)[] Weights =
    [
        ("ArmorHeavy", ArmorType.HeavyArmor, "heavy armor — the Heavy Armor skill and its perks"),
        ("ArmorLight", ArmorType.LightArmor, "light armor — the Light Armor skill and its perks"),
        ("ArmorClothing", ArmorType.Clothing, "clothing — no armor skill")
    ];

    /// <summary>
    /// An armor whose keyword says one weight and whose ArmorType another — none written is LightArmor: the game counts it by its
    /// ArmorType, its skill and perks, and its keyword by the other. Asked for a heavy helmet, eval round 130's armor+detailed #1
    /// wrote the keyword ArmorHeavy and no ArmorType, and was warned only that light helmets carry ArmorLight.
    /// </summary>
    private static string? WeightNote(IArmorGetter armor, Known me, RecordIndex index, string relative, string project)
    {
        var type = armor.BodyTemplate?.ArmorType ?? ArmorType.LightArmor;
        var said = Weights.Where(w => index.ByEditorId.TryGetValue(w.Keyword, out var key) && me.Keywords.Contains(key)).ToList();
        if (said is not [var (keyword, meant, meaning)] || meant == type) return null;
        var counted = Weights.First(w => w.Type == type);
        var name = armor.EditorID ?? Session.FormKeyText(armor.FormKey);
        return $"{relative}: {name} has the keyword {keyword}, which says {meaning}, and its BodyTemplate's ArmorType is {type}" +
            (armor.BodyTemplate is null ? " (it has no BodyTemplate)" : "") + $", which the game counts it as: {counted.Said}. " +
            $"Where {keyword} says what was meant, one call makes it {meant} and builds again: " +
            ToolCalls.Fix(project, relative, Field("BodyTemplate.ArmorType", meant.ToString())) +
            $"; where {type} was, its keyword is {counted.Keyword} — set it in Keywords in place of {keyword}";
    }

    /// <summary>The record of a kind most like one: on its mesh where one is, then the one sharing the most links, then load order.</summary>
    private static Known? Like(Known me, List<Known> kind)
    {
        var mine = me.Links.Values.Concat(me.Keywords).ToHashSet();
        return kind
            .OrderByDescending(k => me.Model is not null && string.Equals(k.Model, me.Model, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(k => k.Links.Values.Concat(k.Keywords).Count(mine.Contains))
            .ThenBy(k => k.At)
            .FirstOrDefault();
    }

    /// <summary>A weapon's or armor's kind, mesh and parts; a plain one carries no enchantment and follows no template.</summary>
    private static Known Read(IMajorRecordGetter record, int at)
    {
        var (getter, kind, model, keywords, armature, plain) = record switch
        {
            IWeaponGetter w => (typeof(IWeaponGetter), w.Data?.AnimationType.ToString(), ModelFiles.Path(w.Model?.File.GivenPath),
                w.Keywords, (IReadOnlyList<IFormLinkGetter<IArmorAddonGetter>>?)null, w.Template.IsNull && w.ObjectEffect.IsNull),
            IArmorGetter a => (typeof(IArmorGetter), ArmorKind(a), ModelFiles.Path(a.WorldModel?.Male?.Model?.File.GivenPath),
                a.Keywords, a.Armature, a.TemplateArmor.IsNull && a.ObjectEffect.IsNull),
            _ => (typeof(IMajorRecordGetter), null, null, null, null, false)
        };
        var links = new Dictionary<string, FormKey>(StringComparer.Ordinal);
        foreach (var property in LinkProperties(getter))
            if (property.GetValue(record) is IFormLinkGetter { IsNull: false } link) links[property.Name] = link.FormKey;
        return new Known(record, at, kind, model, links, keywords?.Select(k => k.FormKey).ToHashSet() ?? [],
            armature?.Select(a => a.FormKey).ToList() ?? [], plain);
    }

    /// <summary>An armor's weight and the first body slot it fills: HeavyArmor Body, Clothing Feet.</summary>
    private static string? ArmorKind(IArmorGetter armor)
    {
        if (armor.BodyTemplate is not { } body) return null;
        var slot = Enum.GetValues<BipedObjectFlag>().FirstOrDefault(f => f != 0 && body.FirstPersonFlags.HasFlag(f));
        return slot == 0 ? null : $"{body.ArmorType} {slot}";
    }

    /// <summary>The one-link fields of a record type, those a record of its kind takes from its own: not its template or enchantment.</summary>
    private static readonly Dictionary<Type, List<PropertyInfo>> Properties = [];
    private static List<PropertyInfo> LinkProperties(Type getter) => Properties.TryGetValue(getter, out var known) ? known
        : Properties[getter] = getter.GetProperties()
            .Where(p => typeof(IFormLinkGetter).IsAssignableFrom(p.PropertyType) && p.Name is not ("Template" or "TemplateArmor" or "ObjectEffect"))
            .ToList();

    private static string Shown(IMajorRecordGetter record) => record.EditorID ?? Session.FormKeyText(record.FormKey);
    private static string Field(string path, string value) => $"{{\"path\": \"{path}\", \"value\": {ToolCalls.Quoted(value)}}}";
    private static string List(string path, IEnumerable<string> values) =>
        $"{{\"path\": \"{path}\", \"value\": [{string.Join(", ", values.Select(ToolCalls.Quoted))}]}}";
    private static string Joined(List<string> said) => said.Count == 1 ? said[0] : $"{string.Join(", ", said.Take(said.Count - 1))} and {said[^1]}";
}
