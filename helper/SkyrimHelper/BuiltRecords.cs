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

    private static IEnumerable<string> Parts(IMajorRecordGetter record, Func<IFormLinkGetter, string> id)
    {
        switch (record)
        {
            case IWeaponGetter weapon:
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
                break;
            case IBookGetter book:
                yield return $"weight {Number(book.Weight)}, value {book.Value}";
                break;
        }
    }

    private static string Effects(IReadOnlyList<IEffectGetter> effects, Func<IFormLinkGetter, string> id) =>
        effects.Count == 0
            ? "no effects"
            : string.Join(", ", effects.Select(e => (e.BaseEffect.IsNull ? "no effect" : id(e.BaseEffect)) + (e.Data is { } data
                ? $" magnitude {Number(data.Magnitude)}" + (data.Duration > 0 ? $" for {data.Duration} s" : "") +
                    (data.Area > 0 ? $" in {data.Area} ft" : "")
                : "")));

    private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
