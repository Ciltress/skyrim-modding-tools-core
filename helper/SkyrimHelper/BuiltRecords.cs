using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// What a build wrote, one line per record, read back from the plugin: its name and the numbers a player sees. The model's
/// answer named an enchantment and a value its files did not hold — the name it had written was another, the call that set
/// the value had failed (eval round 87's sword+detailed #4): told from these lines, an answer says what the game gets.
/// </summary>
internal static class BuiltRecords
{
    /// <summary>A patch of hundreds of records is listed by its first ones, and how many more.</summary>
    internal const int Listed = 20;

    internal static List<string> Lines(ISkyrimModGetter written, ILinkCache lookup)
    {
        var own = written.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => r.EditorID);
        string Id(IFormLinkGetter link)
        {
            if (own.TryGetValue(link.FormKey, out var id)) return id ?? Session.FormKeyText(link.FormKey);
            return lookup.TryResolve(link.FormKey, out var found) && found.EditorID is { } theirs ? theirs : Session.FormKeyText(link.FormKey);
        }
        var records = written.EnumerateMajorRecords().ToList();
        var lines = records.Take(Listed).Select(r => Line(r, written.ModKey, Id)).ToList();
        if (records.Count > Listed) lines.Add($"and {records.Count - Listed} more records");
        return lines;
    }

    private static string Line(IMajorRecordGetter record, ModKey plugin, Func<IFormLinkGetter, string> id)
    {
        var name = record is ITranslatedNamedGetter named ? named.Name?.String : null;
        var head = $"{RecordIndex.TypeName(record)} {record.EditorID ?? Session.FormKeyText(record.FormKey)}" +
            (string.IsNullOrEmpty(name) ? "" : $" \"{name}\"") +
            (record.FormKey.ModKey == plugin ? " (new)" : $" (override of {record.FormKey.ModKey.FileName})");
        var parts = Parts(record, id).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? head : $"{head}: {string.Join("; ", parts)}";
    }

    /// <summary>
    /// A weapon's kind as the game wields it, by its AnimationType; a battleaxe and a warhammer, one type, by their keyword.
    /// Asked for a one-handed sword, the model wrote a copy of the Daedric Dagger, and nothing the build answered called it
    /// a dagger (eval round 117's sword #4).
    /// </summary>
    private static string? Kind(IWeaponGetter weapon, Func<IFormLinkGetter, string> id)
    {
        var keywords = (weapon.Keywords ?? []).Select(k => id(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return weapon.Data?.AnimationType switch
        {
            null => null,
            WeaponAnimationType.HandToHand => "hand-to-hand",
            WeaponAnimationType.OneHandSword => "a one-handed sword",
            WeaponAnimationType.OneHandDagger => "a dagger",
            WeaponAnimationType.OneHandAxe => "a war axe",
            WeaponAnimationType.OneHandMace => "a mace",
            WeaponAnimationType.TwoHandSword => "a greatsword",
            WeaponAnimationType.TwoHandAxe => keywords.Contains("WeapTypeWarhammer") ? "a warhammer"
                : keywords.Contains("WeapTypeBattleaxe") ? "a battleaxe" : "a two-handed axe or hammer",
            WeaponAnimationType.Bow => "a bow",
            WeaponAnimationType.Staff => "a staff",
            WeaponAnimationType.Crossbow => "a crossbow",
            var other => other.ToString()
        };
    }

    private static IEnumerable<string> Parts(IMajorRecordGetter record, Func<IFormLinkGetter, string> id)
    {
        switch (record)
        {
            case IWeaponGetter weapon:
                if (Kind(weapon, id) is { } kind) yield return kind;
                if (weapon.BasicStats is { } stats)
                    yield return $"damage {stats.Damage}, weight {Number(stats.Weight)}, value {stats.Value}";
                if (!weapon.ObjectEffect.IsNull)
                    yield return $"enchantment {id(weapon.ObjectEffect)}, " +
                        (weapon.EnchantmentAmount is > 0 ? $"charge {weapon.EnchantmentAmount}" : "no charge");
                break;
            case IArmorGetter armor:
                yield return $"armor rating {Number(armor.ArmorRating)}, weight {Number(armor.Weight)}, value {armor.Value}";
                if (!armor.ObjectEffect.IsNull) yield return $"enchantment {id(armor.ObjectEffect)}";
                break;
            case IObjectEffectGetter enchantment:
                yield return Effects(enchantment.Effects, id);
                break;
            case ISpellGetter spell:
                yield return $"cost {spell.BaseCost}";
                yield return Effects(spell.Effects, id);
                break;
            case IScrollGetter scroll:
                yield return Effects(scroll.Effects, id);
                break;
            case IIngestibleGetter potion:
                yield return $"weight {Number(potion.Weight)}, value {potion.Value}";
                yield return Effects(potion.Effects, id);
                break;
            // An ingredient's effects by name: written as FormIDs from memory, eval round 150's ingredient #2 and #3 built Fortify
            // Conjuration for Restore Magicka, the line saying only "Ingredient GlacialSalt (new)".
            case IIngredientGetter ingredient:
                yield return $"weight {Number(ingredient.Weight)}, value {ingredient.Value}";
                yield return Effects(ingredient.Effects, id);
                break;
            case ILightGetter light:
                // The color in words too: built as "#00CC3F (red, green, blue)", a light written `#0000CC3F` for blue — the alpha written
                // last — was answered a cold blue light by eval round 151's light #3, green in game.
                yield return $"radius {light.Radius}, color #{light.Color.R:X2}{light.Color.G:X2}{light.Color.B:X2} (red, green, blue): " +
                    ColorWord(light.Color.R, light.Color.G, light.Color.B);
                break;
            // A location's parent by name: written from memory, eval round 149's location+detailed #2 put its camp in 0130FF, not
            // the WhiterunHoldLocation it had read.
            case ILocationGetter location:
                yield return location.ParentLocation.IsNull ? "in no parent location" : $"in {id(location.ParentLocation)}";
                if (location.Keywords is { Count: > 0 } keywords) yield return $"keywords {string.Join(", ", keywords.Select(k => id(k)))}";
                break;
            case IMagicEffectGetter effect:
                yield return effect.MagicSkill == ActorValue.None ? "no school" : effect.MagicSkill.ToString();
                if (effect.ResistValue != ActorValue.None) yield return $"resisted by {effect.ResistValue}";
                break;
            case INpcGetter npc:
                yield return string.Join(", ", new[]
                {
                    npc.Race.IsNull ? "no race" : id(npc.Race),
                    npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? "female" : "male",
                    npc.Configuration.Level switch
                    {
                        INpcLevelGetter level => $"level {level.Level}",
                        IPcLevelMultGetter mult => $"level {Number(mult.LevelMult)} × the player's, {npc.Configuration.CalcMinLevel} to " +
                            (npc.Configuration.CalcMaxLevel == 0 ? "no limit" : npc.Configuration.CalcMaxLevel.ToString(CultureInfo.InvariantCulture)),
                        _ => ""
                    }
                }.Where(p => p.Length > 0));
                // Its class, voice and outfit too: the model's Hilda was built with no outfit where the task asked for iron
                // armor, and the line read only race, sex and level (eval round 108's npc+detailed #5).
                yield return string.Join(", ",
                    NpcPart(npc, "class", npc.Class, NpcConfiguration.TemplateFlag.Stats, id),
                    NpcPart(npc, "voice", npc.Voice, NpcConfiguration.TemplateFlag.Traits, id),
                    NpcPart(npc, "outfit", npc.DefaultOutfit, NpcConfiguration.TemplateFlag.Inventory, id));
                break;
            case IBookGetter book:
                yield return $"weight {Number(book.Weight)}, value {book.Value}";
                break;
        }
    }

    /// <summary>An NPC's linked part by its editor ID — "outfit ArmorIronAllOutfit" — its template's where a flag gives it that, or "no outfit".</summary>
    private static string NpcPart(INpcGetter npc, string part, IFormLinkGetter link, NpcConfiguration.TemplateFlag given, Func<IFormLinkGetter, string> id) =>
        !link.IsNull ? $"{part} {id(link)}"
            : !npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(given) ? $"{part} from its template" : $"no {part}";

    private static string Effects(IReadOnlyList<IEffectGetter> effects, Func<IFormLinkGetter, string> id) =>
        effects.Count == 0
            ? "no effects"
            : string.Join(", ", effects.Select(e => (e.BaseEffect.IsNull ? "no effect" : id(e.BaseEffect)) + (e.Data is { } data
                ? $" magnitude {Number(data.Magnitude)}" + (data.Duration > 0 ? $" for {data.Duration} s" : "") +
                    (data.Area > 0 ? $" in {data.Area} ft" : "")
                : "")));

    private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A color's hue in a word — white, red, orange, yellow, green, cyan, blue, purple, pink — pale or dark where it is.</summary>
    internal static string ColorWord(byte r, byte g, byte b)
    {
        var (max, min) = (Math.Max(r, Math.Max(g, b)), Math.Min(r, Math.Min(g, b)));
        if (max < 40) return "black";
        var saturation = (max - min) / (double)max;
        if (saturation < 0.15) return max > 200 ? "white" : "gray";
        double hue = max == r ? 60 * ((g - b) / (double)(max - min)) : max == g ? 60 * (2 + (b - r) / (double)(max - min)) : 60 * (4 + (r - g) / (double)(max - min));
        if (hue < 0) hue += 360;
        var word = hue switch
        {
            < 15 => "red", < 40 => "orange", < 70 => "yellow", < 160 => "green", < 200 => "cyan", < 260 => "blue", < 290 => "purple",
            < 340 => "pink", _ => "red"
        };
        return saturation < 0.4 ? $"pale {word}" : max < 110 ? $"dark {word}" : word;
    }
}
