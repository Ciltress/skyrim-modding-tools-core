using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// A new spell's, enchantment's or scroll's effects, each cast as the record is. The game applies an
/// effect only through a spell of its own cast type and delivery, and the Creation Kit offers no other;
/// a build of text can name any. A small model built a Fire-and-Forget bolt on an atronach's
/// constant-effect cloak, which does nothing in game (eval probe 7 after round 11). What the game's own
/// plugins do otherwise is built all the same (<see cref="Shipped"/>).
/// </summary>
public static class EffectCasting
{
    /// <summary>
    /// A spell, enchantment or scroll: its effects' cast type, and the one written — 3 for a scroll. A
    /// spell with a HalfCostPerk is one a player casts, or an NPC's copy of one; a trap's, a hazard's and a
    /// creature's have none. Enchantments and scrolls count as the player's.
    /// </summary>
    private sealed record Caster(IMajorRecordGetter Record, string Kind, string Type, CastType Cast, CastType Written, TargetType Target,
        IReadOnlyList<IEffectGetter> Effects, bool Players = true);

    /// <summary>
    /// The ways the official plugins — Skyrim.esm, its update and DLCs, all 74 Creation Club plugins — cast
    /// an effect otherwise than its record: 15 of their 4,336 effects, in these ten shapes, read from every
    /// spell, enchantment and scroll of theirs. A build is not refused for what the game ships; several are
    /// cast by a script (the Skull of Corruption's, Mercer's Nightingale Strife). Kind, spell or enchantment
    /// type, then the record's and the effect's cast type and delivery.
    /// </summary>
    private static readonly HashSet<(string Kind, string Type, CastType Cast, CastType EffectCast, TargetType Target, TargetType EffectTarget)> ShippedShapes =
    [
        ("Scroll", "", CastType.Concentration, CastType.FireAndForget, TargetType.Self, TargetType.Self),                    // TGDetectLifeScroll
        ("Spell", "Spell", CastType.FireAndForget, CastType.FireAndForget, TargetType.TargetActor, TargetType.Aimed),       // DA16SkullOfCorruptionDamageSpell
        ("Spell", "Spell", CastType.FireAndForget, CastType.Concentration, TargetType.Aimed, TargetType.Aimed),             // TGNightingaleStrifeMercer
        ("Spell", "Spell", CastType.FireAndForget, CastType.ConstantEffect, TargetType.Self, TargetType.Self),              // ccRMSSSE001_AltarVaerminaSpell
        ("Spell", "Ability", CastType.ConstantEffect, CastType.ConstantEffect, TargetType.Self, TargetType.TargetLocation), // AbIceWraithFX
        ("Spell", "Ability", CastType.FireAndForget, CastType.FireAndForget, TargetType.Self, TargetType.Touch),            // TrapPoisonGasSpell01NotUsed
        ("Spell", "Ability", CastType.FireAndForget, CastType.ConstantEffect, TargetType.Self, TargetType.Self),            // DLC1AbKeeperWispCloak
        ("Spell", "Voice", CastType.FireAndForget, CastType.ConstantEffect, TargetType.Self, TargetType.Self),              // DLC1LD_ForgemasterFlameCloak
        ("ObjectEffect", "Enchantment", CastType.ConstantEffect, CastType.FireAndForget, TargetType.TargetActor, TargetType.Self), // MQClearSkyFogEnch
        ("ObjectEffect", "Enchantment", CastType.ConstantEffect, CastType.FireAndForget, TargetType.Touch, TargetType.Touch),      // DLC1nVampireMagicaDrainClawsENCH
    ];

    /// <summary>
    /// Whether an effect cast <paramref name="effectCast"/> on <paramref name="effectTarget"/> builds in a record cast
    /// <paramref name="cast"/> on <paramref name="target"/>: the same, or a way the official plugins cast it. A scroll's
    /// cast type 3 counts as FireAndForget.
    /// </summary>
    public static bool Fits(string kind, string type, CastType cast, CastType effectCast, TargetType target, TargetType effectTarget) =>
        EffectsCast(cast) == effectCast && target == effectTarget
        || ShippedShapes.Contains((kind, type, EffectsCast(cast), effectCast, target, effectTarget));

    /// <summary>The new records of the plugin whose effects are cast otherwise than they are, as messages naming the file and the way out.</summary>
    public static List<string> Mismatches(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var files = Files(pluginDir);
        var problems = new List<string>();
        // The project's effects offered the game's in their place already: one refusal says it once.
        var offered = new HashSet<FormKey>();
        foreach (var caster in Casters(mod).Where(c => c.Record.FormKey.ModKey == mod.ModKey))
        {
            var name = caster.Record.EditorID ?? Session.FormKeyText(caster.Record.FormKey);
            var where = files.TryGetValue(caster.Record.FormKey, out var relative) ? relative : name;
            for (var i = 0; i < caster.Effects.Count; i++)
            {
                if (caster.Effects[i].BaseEffect.IsNull) continue;
                var key = caster.Effects[i].BaseEffect.FormKey;
                var ownEffect = mod.MagicEffects.FirstOrDefault(m => m.FormKey == key);
                if ((ownEffect ?? (lookup.TryResolve<IMagicEffectGetter>(key, out var found) ? found : null)) is not { } effect) continue;
                if (Fits(caster.Kind, caster.Type, caster.Cast, effect.CastType, caster.Target, effect.TargetType)) continue;

                var effectName = effect.EditorID ?? Session.FormKeyText(key);
                var said = $"{where}: Effects[{i}].BaseEffect {effectName} is cast {effect.CastType} on {effect.TargetType}, and {name} " +
                    $"{caster.Cast} on {caster.Target} — an effect does nothing in {RecordFiles.Article(caster.Kind)} cast otherwise: ";
                if (caster.Kind == "Scroll" && effect.CastType == CastType.FireAndForget && effect.TargetType == caster.Target)
                {
                    problems.Add(said + "write CastType: 3 in the scroll, the cast type every scroll of the game has");
                    continue;
                }
                // Left out, CastType and TargetType read as ConstantEffect and Self: a weapon's enchantment without
                // them was told to take a cloak's effect in place of its fire (eval probe 8 after round 11).
                var text = relative is null ? "" : File.ReadAllText(Path.Combine(pluginDir, relative));
                var castLeftOut = effect.CastType != caster.Cast && !Regex.IsMatch(text, @"^CastType:", RegexOptions.Multiline);
                var targetLeftOut = effect.TargetType != caster.Target && !Regex.IsMatch(text, @"^TargetType:", RegexOptions.Multiline);
                if (castLeftOut || targetLeftOut)
                {
                    var give = new[] { effect.CastType != caster.Cast ? $"CastType: {effect.CastType}" : null, effect.TargetType != caster.Target ? $"TargetType: {effect.TargetType}" : null }
                        .OfType<string>();
                    problems.Add(said + $"write {string.Join(" and ", give)} in {name}, as its effect is cast — " +
                        $"{RecordFiles.Article(caster.Kind)} without them is cast ConstantEffect on Self");
                    continue;
                }
                // Or the game's effect in its place: told only to give both the same, the model kept its own Storm Bolt effect,
                // wrote it field by field through four refusals, and built it with no school and no resistance (eval
                // round 73's spell+detailed #3) — where 29 of its 30 other spell runs of rounds 71 and 73 named ShockDamageFFAimed.
                if (ownEffect is not null)
                {
                    var instead = InPlace(ownEffect, caster, i, mod, lookup, index, pluginDir, files);
                    if (instead is not null) offered.Add(key);
                    problems.Add(said + $"give both the same CastType and TargetType, the ones the {caster.Kind} is meant to have" +
                        (instead is null ? "" : $"; or {instead}"));
                    continue;
                }
                var ways = Ways(effect, caster, i, lookup, index, mod.ModKey);
                problems.Add(said + (ways.Count > 0
                    ? string.Join("; or ", ways)
                    : $"find an effect cast {caster.Cast} on {caster.Target} with findRecords (types [\"MGEF\"]) and readRecord"));
            }
        }
        // A modifier that names no actor value changes nothing: the model's Storm Bolt effect, its Archetype written over at last,
        // built as a plain success (eval round 27).
        foreach (var effect in mod.MagicEffects.Where(m => m.FormKey.ModKey == mod.ModKey))
        {
            if (!Modifiers.TryGetValue(effect.Archetype.Type, out var usual) || effect.Archetype.ActorValue != ActorValue.None) continue;
            var name = effect.EditorID ?? Session.FormKeyText(effect.FormKey);
            var relative = files.GetValueOrDefault(effect.FormKey);
            var written = relative is not null && Regex.IsMatch(File.ReadAllText(Path.Combine(pluginDir, relative)), @"^Archetype:", RegexOptions.Multiline);
            var official = $"the official plugins' {effect.Archetype.Type} effects name Health most often: {usual} — each of their {ModifierCount} " +
                "modifier and absorb effects names an actor value";
            var use = offered.Contains(effect.FormKey) ? null : Uses(effect, mod).Select(u => ((Caster Caster, int Item)?)u).FirstOrDefault();
            var instead = use is { } found ? InPlace(effect, found.Caster, found.Item, mod, lookup, index, pluginDir, files) : null;
            problems.Add($"{relative ?? name}: " + (written
                ? $"{name} is a {effect.Archetype.Type} that names no actor value, so it changes nothing — write the one it changes under " +
                    $"Archetype, as deep as its other lines: `ActorValue: Health` for damage ({official})"
                : $"{name} has no Archetype, so it is a ValueModifier that names no actor value, and changes nothing — write `Archetype:` " +
                    $"at the margin with `MutagenObjectType: MagicEffectArchetype` and the actor value it changes below it, two spaces in: " +
                    $"`ActorValue: Health` for damage ({official})") + (instead is null ? "" : $"; or {instead}"));
        }
        return problems;
    }

    /// <summary>
    /// The project's new effects that damage Health and name no school or no resistance, as notes beside a build that
    /// succeeds: such an effect does its damage, but a spell of it belongs to no school — casting it trains no skill, and
    /// no skill lowers its cost — and no resistance lowers it. The model's Storm Bolt effect, written field by field through
    /// four refusals, was built so and passed for a Destruction spell of shock damage (eval round 73's spell+detailed #3).
    /// Each note names the lines the game's effect most like it has, and that effect in its place.
    /// </summary>
    public static List<string> Notes(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var files = Files(pluginDir);
        var notes = new List<string>();
        foreach (var effect in mod.MagicEffects.Where(m => m.FormKey.ModKey == mod.ModKey && Damages(m)))
        {
            // One no record names has its own note (ProjectNotes): it does nothing yet.
            var uses = Uses(effect, mod).OrderBy(u => u.Caster.Kind == "Spell" && u.Caster.Type == "Spell" ? 0 : 1).ToList();
            if (uses.Count == 0) continue;
            var (caster, item) = uses[0];
            var game = GameEffect(effect, caster, lookup, index, mod.ModKey);
            // A school is a spell's, cast by the player or an NPC as a player does; a resistance, where the game's effect most
            // like it has one — or, with none found, the five schools and no resistance to claim.
            var school = effect.MagicSkill == ActorValue.None && caster.Kind == "Spell" && caster.Type == "Spell";
            var resist = effect.ResistValue == ActorValue.None && game is { } like && like.Effect.ResistValue != ActorValue.None;
            if (!school && !resist) continue;

            var name = effect.EditorID ?? Session.FormKeyText(effect.FormKey);
            var spell = caster.Record.EditorID ?? Session.FormKeyText(caster.Record.FormKey);
            var relative = files.GetValueOrDefault(effect.FormKey);
            var lacks = school && resist ? "no MagicSkill and no ResistValue" : school ? "no MagicSkill" : "no ResistValue";
            var means = string.Join(", and ", new[]
            {
                school ? $"{spell} belongs to no school: casting it trains no skill, and no skill lowers its cost" : null,
                resist ? "no resistance lowers its damage" : null
            }.OfType<string>());
            var gameSchool = game?.Effect.MagicSkill is { } skill && skill != ActorValue.None ? skill.ToString() : null;
            var set = new[]
            {
                school && gameSchool is not null ? ("MagicSkill", gameSchool) : default,
                resist ? ("ResistValue", game!.Value.Effect.ResistValue.ToString()) : default
            }.Where(s => s.Item1 is not null).ToList();
            string write;
            if (set.Count > 0 && relative is not null)
                write = $"one call writes {(set.Count == 1 ? "it" : "both")}, as {Shown(game!.Value.Effect)}, the effect of {Shown(game.Value.Example)}, " +
                    $"{RecordFiles.Article(caster.Kind)} like {spell}, has {(set.Count == 1 ? "it" : "them")}, and builds again: " +
                    ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative,
                        string.Join(", ", set.Select(s => $"{{\"path\": \"{s.Item1}\", \"value\": \"{s.Item2}\"}}")), build: true);
            else
                write = "write " + string.Join(" and ", set.Select(s => $"`{s.Item1}: {s.Item2}`")) + " at the margin";
            if (school && gameSchool is null)
                write = (set.Count > 0 ? write + ", and " : "write ") +
                    "`MagicSkill:` with its school at the margin — Alteration, Conjuration, Destruction, Illusion or Restoration";
            var instead = InPlace(effect, caster, item, mod, lookup, index, pluginDir, files);
            notes.Add($"{relative ?? name}: {name} damages Health and names {lacks}, so {means} — {write}" +
                (instead is null ? "" : $"; or, with the game's art and sounds too, {instead}"));
        }
        return notes;
    }

    /// <summary>The project's record files by the FormKey each writes.</summary>
    private static Dictionary<FormKey, string> Files(string pluginDir) => RecordFiles.FlatRecords(pluginDir)
        .Select(f => (f.Relative, RecordFiles.Identity(File.ReadAllText(f.File)).FormKey))
        .Where(f => f.FormKey is not null)
        .GroupBy(f => f.FormKey!.Value).ToDictionary(g => g.Key, g => g.First().Relative);

    /// <summary>An effect that takes Health by its magnitude: a value modifier on Health, flagged Detrimental.</summary>
    private static bool Damages(IMagicEffectGetter effect) =>
        effect.Archetype.Type is MagicEffectArchetype.TypeEnum.ValueModifier or MagicEffectArchetype.TypeEnum.DualValueModifier
        && effect.Archetype.ActorValue == ActorValue.Health
        && effect.Flags.HasFlag(MagicEffect.Flag.Detrimental);

    /// <summary>The project's spells, enchantments and scrolls that name the effect, each with the item that does.</summary>
    private static IEnumerable<(Caster Caster, int Item)> Uses(IMagicEffectGetter effect, ISkyrimModGetter mod) =>
        Casters(mod).Where(c => c.Record.FormKey.ModKey == mod.ModKey)
            .SelectMany(c => c.Effects.Select((e, i) => (Caster: c, Item: i, Effect: e)))
            .Where(u => !u.Effect.BaseEffect.IsNull && u.Effect.BaseEffect.FormKey == effect.FormKey)
            .Select(u => (u.Caster, u.Item));

    /// <summary>
    /// The game's effect to name in the record in place of the project's own, as the words that say so — the file deleted
    /// where no other record of the project names it; null where the load order has none like it.
    /// </summary>
    private static string? InPlace(IMagicEffectGetter own, Caster caster, int item, ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index,
        string pluginDir, Dictionary<FormKey, string> files)
    {
        if (GameEffect(own, caster, lookup, index, mod.ModKey) is not { } game) return null;
        var (effect, example) = game;
        var name = caster.Record.EditorID ?? Session.FormKeyText(caster.Record.FormKey);
        var gameName = effect.EditorID ?? Session.FormKeyText(effect.FormKey);
        var said = $"name the game's {gameName} in its place, as {Shown(example)} does — in {name}'s Effects[{item}] `BaseEffect: {gameName}`";
        var alone = Uses(own, mod).All(u => u.Caster.Record.FormKey == caster.Record.FormKey);
        return files.GetValueOrDefault(own.FormKey) is { } relative && alone
            ? said + $" — and delete {relative.Replace('\\', '/')}: {ToolCalls.Delete(pluginDir, relative)}"
            : said;
    }

    /// <summary>
    /// The load order's effect most like the project's own, and a record that names it: among the records of the kind
    /// cast as the project's is — the player's, where any is — each effect they cast as it is, by what it and its record
    /// share with the project's: keywords most (MagicDamageShock), then the spell's half-cost perk and menu art, the
    /// effect's resistance and school where it names them, its actor value, its place among the Effects, and a word of
    /// the editor IDs (Storm Bolt, Lightning Bolt). One that shares nothing is none: no effect is offered by chance.
    /// For a spell, only an effect with a school, where any has one: Storm Call's lightning, a shout's spell with Lightning
    /// Bolt's perk and art, has none, and was offered for Storm Bolt over Lightning Bolt's on two words of its name.
    /// </summary>
    private static (IMagicEffectGetter Effect, IMajorRecordGetter Example)? GameEffect(IMagicEffectGetter own, Caster caster, ILinkCache lookup,
        RecordIndex index, ModKey mine)
    {
        var item = caster.Effects.ToList().FindIndex(e => !e.BaseEffect.IsNull && e.BaseEffect.FormKey == own.FormKey);
        var kin = Kin(caster, lookup, index, mine).Where(k => k.Cast == caster.Cast && k.Target == caster.Target).ToList();
        if (kin.Any(k => k.Players)) kin = kin.Where(k => k.Players).ToList();
        var effects = new Dictionary<FormKey, IMagicEffectGetter?>();
        IMagicEffectGetter? EffectOf(FormKey key) => effects.TryGetValue(key, out var known) ? known
            : effects[key] = lookup.TryResolve<IMagicEffectGetter>(key, out var found) ? found : null;

        var keywords = Keywords(own);
        var perk = PerkOf(caster.Record);
        var art = ArtOf(caster.Record);
        var words = RecordLinks.WordsOf(caster.Record.EditorID ?? "");
        // Its element by its words, where it names no resistance: the model's "Fire Damage", an effect of a name alone in Flame
        // Blade Burn, was offered the game's frost on a tie (eval round 82's sword+detailed #5).
        var element = own.ResistValue != ActorValue.None ? ActorValue.None
            : ElementOf(words.Concat(RecordLinks.WordsOf(own.EditorID ?? "")).Concat(NameWords(own.Name?.String)));
        // Ice Spike's damage is its first of four effects, all with its perk and art: its slow was offered by a shorter name.
        int Score(Caster record, IMagicEffectGetter effect, int at) =>
            3 * Keywords(effect).Intersect(keywords).Count()
            + (perk is not null && PerkOf(record.Record) == perk ? 2 : 0)
            + (art is not null && ArtOf(record.Record) == art ? 2 : 0)
            + (own.ResistValue != ActorValue.None && effect.ResistValue == own.ResistValue ? 2 : 0)
            + (element != ActorValue.None && effect.ResistValue == element ? 2 : 0)
            + (own.MagicSkill != ActorValue.None && effect.MagicSkill == own.MagicSkill ? 2 : 0)
            + (own.Archetype.ActorValue != ActorValue.None && effect.Archetype.ActorValue == own.Archetype.ActorValue ? 1 : 0)
            + (at == item ? 1 : 0)
            + (RecordLinks.SharedWords(words, RecordLinks.WordsOf(record.Record.EditorID ?? "")).Count > 0 ? 1 : 0);
        // Like it where it says what it does: damage to Health is not offered a cloak's or a heal's effect.
        bool Fits(IMagicEffectGetter effect) =>
            (own.Archetype.ActorValue == ActorValue.None || effect.Archetype.ActorValue == own.Archetype.ActorValue)
            && (!own.Flags.HasFlag(MagicEffect.Flag.Detrimental) || effect.Flags.HasFlag(MagicEffect.Flag.Detrimental));

        var scored = kin.SelectMany(record => record.Effects.Select((e, at) => (Record: record, At: at,
                Effect: e.BaseEffect.IsNull ? null : EffectOf(e.BaseEffect.FormKey))))
            .Where(use => use.Effect is { } effect && effect.CastType == use.Record.Cast && effect.TargetType == use.Record.Target && Fits(effect))
            .Select(use => (use.Record, Effect: use.Effect!, Score: Score(use.Record, use.Effect!, use.At)))
            .Where(use => use.Score > 0)
            .ToList();
        if (caster.Kind == "Spell" && caster.Type == "Spell" && scored.Any(use => use.Effect.MagicSkill != ActorValue.None))
            scored = scored.Where(use => use.Effect.MagicSkill != ActorValue.None).ToList();
        if (scored.Count == 0) return null;
        var best = scored.GroupBy(use => use.Effect.FormKey)
            .OrderByDescending(g => g.Max(use => use.Score))
            .ThenByDescending(g => g.Count())
            .ThenBy(g => g.First().Effect.EditorID?.Length ?? int.MaxValue)
            .First();
        var example = best.OrderByDescending(use => use.Score)
            .ThenBy(use => use.Record.Record.EditorID?.Length ?? int.MaxValue)
            .ThenBy(use => use.Record.Record.EditorID, StringComparer.Ordinal)
            .First();
        return (example.Effect, example.Record.Record);
    }

    private static HashSet<FormKey> Keywords(IMagicEffectGetter effect) =>
        effect.Keywords?.Where(k => !k.IsNull).Select(k => k.FormKey).ToHashSet() ?? [];

    private static FormKey? PerkOf(IMajorRecordGetter record) =>
        record is ISpellGetter spell && !spell.HalfCostPerk.IsNull ? spell.HalfCostPerk.FormKey : null;

    private static FormKey? ArtOf(IMajorRecordGetter record) =>
        record is ISpellGetter spell && !spell.MenuDisplayObject.IsNull ? spell.MenuDisplayObject.FormKey : null;

    /// <summary>The words of each element's damage, upper-cased, by the resistance that lowers it.</summary>
    private static readonly (ActorValue Resist, string[] Words)[] Elements =
    [
        (ActorValue.ResistFire, ["FIRE", "FLAME", "FLAMES", "BURN", "BURNING", "BLAZE", "INFERNO"]),
        (ActorValue.ResistFrost, ["FROST", "ICE", "COLD", "FREEZE", "FROZEN", "CHILL"]),
        (ActorValue.ResistShock, ["SHOCK", "LIGHTNING", "STORM", "SPARK", "SPARKS", "THUNDER"])
    ];

    /// <summary>The resistance of the one element the words name — fire for FLAME and BURN — or None for none, or more than one.</summary>
    private static ActorValue ElementOf(IEnumerable<string> words)
    {
        var named = words.ToHashSet();
        return Elements.Where(e => e.Words.Any(named.Contains)).Select(e => e.Resist).ToList() is [var one] ? one : ActorValue.None;
    }

    /// <summary>A name's words, upper-cased: "Fire Damage" → FIRE, DAMAGE.</summary>
    private static IEnumerable<string> NameWords(string? name) =>
        Regex.Split(name ?? "", "[^A-Za-z]+").Where(w => w.Length > 0).Select(w => w.ToUpperInvariant());

    /// <summary>
    /// The archetypes that change an actor value, and how many of the official plugins' effects of each name Health,
    /// the one they name most often: every one of their 1,133 names an actor value (2,108 magic effects of the 80
    /// official plugins audited, 2026-10-21).
    /// </summary>
    private static readonly Dictionary<MagicEffectArchetype.TypeEnum, string> Modifiers = new()
    {
        [MagicEffectArchetype.TypeEnum.ValueModifier] = "282 of 496",
        [MagicEffectArchetype.TypeEnum.PeakValueModifier] = "53 of 405",
        [MagicEffectArchetype.TypeEnum.DualValueModifier] = "131 of 160",
        [MagicEffectArchetype.TypeEnum.Absorb] = "37 of 72"
    };
    private const string ModifierCount = "1,133";

    private static Caster Of(ISpellGetter s) => new(s, "Spell", s.Type.ToString(), EffectsCast(s.CastType), s.CastType, s.TargetType, s.Effects, !s.HalfCostPerk.IsNull);
    private static Caster Of(IObjectEffectGetter e) => new(e, "ObjectEffect", e.EnchantType.ToString(), EffectsCast(e.CastType), e.CastType, e.TargetType, e.Effects);
    private static Caster Of(IScrollGetter s) => new(s, "Scroll", "", EffectsCast(s.CastType), s.CastType, s.TargetType, s.Effects);

    private static IEnumerable<Caster> Casters(ISkyrimModGetter mod) =>
        mod.Spells.Select(s => Of(s)).Concat(mod.ObjectEffects.Select(e => Of(e))).Concat(mod.Scrolls.Select(s => Of(s)));

    /// <summary>The load order's spells, enchantments or scrolls of the record's kind and type, the project's own left out.</summary>
    private static IEnumerable<Caster> Kin(Caster caster, ILinkCache lookup, RecordIndex index, ModKey own) => index.ByFormKey.Values
        .Where(e => e.Type == caster.Kind && e.FormKey.ModKey != own)
        .Select(e => caster.Kind switch
        {
            "Spell" => lookup.TryResolve<ISpellGetter>(e.FormKey, out var spell) ? Of(spell) : null,
            "ObjectEffect" => lookup.TryResolve<IObjectEffectGetter>(e.FormKey, out var enchantment) ? Of(enchantment) : null,
            _ => lookup.TryResolve<IScrollGetter>(e.FormKey, out var scroll) ? Of(scroll) : null
        })
        .OfType<Caster>()
        .Where(c => c.Type == caster.Type);

    /// <summary>
    /// The cast type a record's effects have: its own, but FireAndForget for 3, the scroll's, which Mutagen's
    /// CastType does not name — all 103 regular scrolls of the official plugins are cast 3 with fire-and-forget
    /// effects, and Saints and Seducers' would not build until this said so.
    /// </summary>
    private static CastType EffectsCast(CastType cast) => cast == (CastType)3 ? CastType.FireAndForget : cast;

    /// <summary>
    /// The ways out, as the lines to write: as the load order's records of the kind cast effects like the
    /// one named — the player's spells, where any does — up to two ways of casting, the fewest lines to
    /// change first, each with the effect most of them name and a record to mirror; and keeping the
    /// effect, where such records are cast as it is. Told "name an effect cast FireAndForget instead, and give StormBolt its TargetType: … on
    /// Touch, ShockDamageFFAimed … on Aimed", a model wrote CastType: Aimed; and weapons' enchantment
    /// effects were offered first, by their names alone (eval probe 9 after round 11).
    /// </summary>
    private static List<string> Ways(IMagicEffectGetter named, Caster caster, int item, ILinkCache lookup, RecordIndex index, ModKey own)
    {
        var effects = new Dictionary<FormKey, IMagicEffectGetter?>();
        IMagicEffectGetter? EffectOf(FormKey key) => effects.TryGetValue(key, out var known) ? known
            : effects[key] = lookup.TryResolve<IMagicEffectGetter>(key, out var found) ? found : null;

        var kin = Kin(caster, lookup, index, own).ToList();
        // Only records cast as their effects are: a way out is never one of the shapes the game ships by exception.
        var uses = kin.SelectMany(record => record.Effects.Where(e => !e.BaseEffect.IsNull)
                .Select(e => (Record: record, Effect: EffectOf(e.BaseEffect.FormKey))))
            .Where(use => use.Effect is { } effect && effect.CastType == use.Record.Cast && effect.TargetType == use.Record.Target)
            .Select(use => (use.Record, Effect: use.Effect!))
            .ToList();
        var like = uses.Where(use => Like(named, use.Effect, strict: true)).ToList();
        if (like.Count == 0 && (named.ResistValue != ActorValue.None || named.MagicSkill != ActorValue.None))
            like = uses.Where(use => Like(named, use.Effect, strict: false)).ToList();
        // A spell the player casts is the one to mirror: by number alone, a shock bolt was sent to a trap's
        // and a hazard's (TrapLightningBolt01, HazardWallOfShockSpell) before Lightning Bolt.
        if (like.Any(use => use.Record.Players)) like = like.Where(use => use.Record.Players).ToList();
        var castable = kin.Any(k => k.Players) ? kin.Where(k => k.Players).ToList() : kin;

        var name = caster.Record.EditorID ?? Session.FormKeyText(caster.Record.FormKey);
        var ways = like.GroupBy(use => (use.Record.Cast, use.Record.Target))
            .Select(shape =>
            {
                var best = shape.GroupBy(use => use.Effect.FormKey)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => RecordLinks.SharedWords(RecordLinks.WordsOf(named.EditorID ?? ""), RecordLinks.WordsOf(g.First().Effect.EditorID ?? "")).Count)
                    .ThenBy(g => g.First().Effect.EditorID?.Length ?? int.MaxValue)
                    .First();
                var example = best.Select(use => use.Record)
                    .OrderBy(r => r.Record.EditorID?.Length ?? int.MaxValue).ThenBy(r => r.Record.EditorID, StringComparer.Ordinal).First();
                return (Lines: Lines(caster, item, named, best.First().Effect, example.Written, shape.Key.Target), Uses: shape.Count(), Example: example.Record);
            })
            .Where(way => way.Lines.Count > 0)
            .OrderBy(way => way.Lines.Count).ThenByDescending(way => way.Uses)
            .Take(2)
            .ToList();
        var said = ways.Select(way => $"write {name} the way {Shown(way.Example)} is cast: {Joined(way.Lines)}").ToList();

        var keepWritten = caster.Kind == "Scroll" && named.CastType == CastType.FireAndForget ? (CastType)3 : named.CastType;
        var keep = Lines(caster, item, named, named, keepWritten, named.TargetType);
        if (keep.Count > 0 && castable.Any(k => k.Cast == named.CastType && k.Target == named.TargetType)
            && !ways.Any(way => way.Lines.SequenceEqual(keep)))
            said.Add($"keep {named.EditorID ?? Session.FormKeyText(named.FormKey)} and write {Joined(keep)} in {name}");
        return said;
    }

    /// <summary>
    /// Whether an effect does what the one named does: the same resistance and school, where it has them,
    /// and, strictly, the same archetype on the same actor value — damage to Health, not a cloak.
    /// </summary>
    private static bool Like(IMagicEffectGetter named, IMagicEffectGetter effect, bool strict) =>
        (named.ResistValue == ActorValue.None || effect.ResistValue == named.ResistValue)
        && (named.MagicSkill == ActorValue.None || effect.MagicSkill == named.MagicSkill)
        && (!strict || effect.Archetype.Type == named.Archetype.Type && effect.Archetype.ActorValue == named.Archetype.ActorValue);

    /// <summary>The lines that cast a record as another is, with that one's effect: only those that change.</summary>
    private static List<string> Lines(Caster caster, int item, IMagicEffectGetter named, IMagicEffectGetter effect, CastType written, TargetType target)
    {
        var lines = new List<string>();
        if (EffectsCast(written) != caster.Cast || written != caster.Written && caster.Kind == "Scroll") lines.Add($"`CastType: {written}`");
        if (target != caster.Target) lines.Add($"`TargetType: {target}`");
        if (effect.FormKey != named.FormKey) lines.Add($"in Effects[{item}] `BaseEffect: {effect.EditorID ?? Session.FormKeyText(effect.FormKey)}`");
        return lines;
    }

    private static string Joined(List<string> lines) =>
        lines.Count == 1 ? lines[0] : $"{string.Join(", ", lines.Take(lines.Count - 1))} and {lines[^1]}";

    private static string Shown(IMajorRecordGetter record) =>
        record.EditorID is { } id ? $"{id} ({Session.FormKeyText(record.FormKey)})" : Session.FormKeyText(record.FormKey);
}
