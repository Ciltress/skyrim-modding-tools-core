using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// A new weapon or armor written after a record of the load order — one with its model — whose numbers the file changed
/// in part, the rest still that record's, as notes beside a build that succeeds. The model's Flame Blade, written from the
/// Iron Sword with 12 damage and a weight of 10, kept the Iron Sword's value of 25, and its answer told the user the value
/// of 150 they had asked for, the build's `built` line saying 25 beside it (eval rounds 87, 92 and 94, a sword+detailed
/// run each). A record copied whole, or changed in every number, has no note: it is the one kept in part where a number
/// is forgotten. Each note writes the call that sets the numbers kept.
/// </summary>
internal static class KeptNumbers
{
    private sealed record Number(string Path, string Word, string Mine, string Theirs);

    public static List<string> Notes(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var notes = new List<string>();
        var weapons = mod.Weapons.Where(w => w.FormKey.ModKey == mod.ModKey && w.BasicStats is not null).ToList();
        var armors = mod.Armors.Where(a => a.FormKey.ModKey == mod.ModKey).ToList();
        if (weapons.Count == 0 && armors.Count == 0) return notes;
        var files = EffectCasting.Files(pluginDir);

        // The load order's records of each model the player can have, of those carrying no enchantment and following no
        // template — the game's own Iron Sword, not an enchanted one of its look — in load order.
        Dictionary<string, List<T>> ByModel<T>(string type, Func<T, bool> plain, Func<T, string?> model) where T : class, IMajorRecordGetter
        {
            var byModel = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in index.ByFormKey.Values.Where(e => e.Type == type && e.FormKey.ModKey != mod.ModKey && !e.Unplayable))
                if (lookup.TryResolve<T>(entry.FormKey, out var record) && plain(record) && ModelPath(model(record)) is { } file)
                    (byModel.TryGetValue(file, out var list) ? list : byModel[file] = []).Add(record);
            return byModel;
        }

        if (weapons.Count > 0)
        {
            var byModel = ByModel<IWeaponGetter>("Weapon", w => w.Template.IsNull && w.ObjectEffect.IsNull, w => w.Model?.File.GivenPath);
            Number[] Numbers(IWeaponGetter mine, IWeaponGetter theirs) =>
            [
                new("BasicStats.Damage", "damage", Text(mine.BasicStats!.Damage), Text(theirs.BasicStats?.Damage)),
                new("BasicStats.Weight", "weight", Text(mine.BasicStats.Weight), Text(theirs.BasicStats?.Weight)),
                new("BasicStats.Value", "value", Text(mine.BasicStats.Value), Text(theirs.BasicStats?.Value))
            ];
            foreach (var weapon in weapons)
                if (ModelPath(weapon.Model?.File.GivenPath) is { } model && byModel.TryGetValue(model, out var candidates))
                    Note(weapon, Source(weapon, candidates, c => Numbers(weapon, c)), c => Numbers(weapon, c));
        }
        if (armors.Count > 0)
        {
            var byModel = ByModel<IArmorGetter>("Armor", a => a.TemplateArmor.IsNull && a.ObjectEffect.IsNull, a => a.WorldModel?.Male?.Model?.File.GivenPath);
            Number[] Numbers(IArmorGetter mine, IArmorGetter theirs) =>
            [
                new("ArmorRating", "armor rating", Text(mine.ArmorRating), Text(theirs.ArmorRating)),
                new("Weight", "weight", Text(mine.Weight), Text(theirs.Weight)),
                new("Value", "value", Text(mine.Value), Text(theirs.Value))
            ];
            foreach (var armor in armors)
                if (ModelPath(armor.WorldModel?.Male?.Model?.File.GivenPath) is { } model && byModel.TryGetValue(model, out var candidates))
                    Note(armor, Source(armor, candidates, c => Numbers(armor, c)), c => Numbers(armor, c));
        }
        return notes;

        void Note<T>(IMajorRecordGetter record, T source, Func<T, Number[]> numbersOf) where T : IMajorRecordGetter
        {
            var numbers = numbersOf(source);
            var kept = numbers.Where(n => n.Mine == n.Theirs).ToList();
            var changed = numbers.Where(n => n.Mine != n.Theirs).ToList();
            if (kept.Count == 0 || changed.Count == 0 || files.GetValueOrDefault(record.FormKey) is not { } relative) return;
            var name = record.EditorID ?? Session.FormKeyText(record.FormKey);
            var theirs = source.EditorID ?? Session.FormKeyText(source.FormKey);
            var one = kept.Count == 1;
            notes.Add($"{relative}: {name} has its own {Listed(changed.Select(n => n.Word))}, but its {Listed(kept.Select(n => n.Word))} " +
                $"{(one ? "is" : "are")} still {theirs}'s, {Listed(kept.Select(n => n.Mine))} — copied with its model; if the user asked " +
                $"for {(one ? "another" : "others")}, one call sets {(one ? "it" : "them")} and builds again: " +
                ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative,
                    string.Join(", ", kept.Select(n => $"{{\"path\": \"{n.Path}\", \"value\": {n.Mine}}}")), build: true) +
                $", with the {(one ? "number" : "numbers")} the user asked for in place of {Listed(kept.Select(n => n.Mine))} — " +
                $"{(one ? "one" : "a number")} the user left as {theirs}'s stays");
        }
    }

    /// <summary>
    /// The record a new one was written after, of those with its model: the one it shares the most links with — the
    /// keywords, sounds and impact sets a copy keeps — then the most numbers; of a tie, the first in load order. The Iron
    /// Sword's model is the Sword of Amazement's too, a demo sword with a staff's keyword, which Skyrim.esm lists first.
    /// </summary>
    private static T Source<T>(IMajorRecordGetter record, List<T> candidates, Func<T, Number[]> numbersOf) where T : IMajorRecordGetter
    {
        var links = record.EnumerateFormLinks().Select(l => l.FormKey).ToHashSet();
        return candidates
            .Select((candidate, at) => (Candidate: candidate, At: at,
                Links: candidate.EnumerateFormLinks().Select(l => l.FormKey).Distinct().Count(links.Contains),
                Numbers: numbersOf(candidate).Count(n => n.Mine == n.Theirs)))
            .OrderByDescending(c => c.Links).ThenByDescending(c => c.Numbers).ThenBy(c => c.At)
            .First().Candidate;
    }

    /// <summary>A model's path as the game finds it: backslashes, below the Meshes folder, in any case.</summary>
    private static string? ModelPath(string? given)
    {
        if (string.IsNullOrWhiteSpace(given)) return null;
        var file = given.Trim().Replace('/', '\\');
        return file.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? file[7..] : file;
    }

    private static string Text(float? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
    private static string Text(uint? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Text(ushort? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Listed(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}";
    }
}
