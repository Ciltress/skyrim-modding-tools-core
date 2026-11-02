using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// A new weapon or armor written after a record of the load order — one with its model — that kept what the user likely
/// asked to change, as notes beside a build that succeeds:
/// - its numbers changed in part, the rest still that record's. The model's Flame Blade, written from the Iron Sword with
///   12 damage and a weight of 10, kept the Iron Sword's value of 25, and its answer told the user the value of 150 they
///   had asked for, the build's `built` line saying 25 beside it (eval rounds 87, 92, 94 and 95, a sword+detailed run
///   each). A record copied whole, or changed in every number, has no such note: it is the one kept in part where a
///   number is forgotten.
/// - its name, that record's: addPatch's Iron Sword, given the EditorID FlameBlade and the numbers asked for, was made a
///   new record by the build and built as a second "Iron Sword", with no word (eval round 95's sword+detailed #1).
/// Each note writes the one call that sets what was kept. A note of numbers alone ends on the case where they are
/// right: the user named none. A task that names no number got the note for a damage the model chose, and in eval
/// rounds 96–99 five of eleven such runs made the call with the kept numbers unchanged, two set numbers of their own,
/// one of them unbuilt.
/// </summary>
internal static class KeptFromSource
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
            if (changed.Count == 0) kept = [];
            var named = NameOf(record) is { Length: > 0 } mine && mine == NameOf(source) ? mine : null;
            if ((kept.Count == 0 && named is null) || files.GetValueOrDefault(record.FormKey) is not { } relative) return;
            var name = record.EditorID ?? Session.FormKeyText(record.FormKey);
            var theirs = source.EditorID ?? Session.FormKeyText(source.FormKey);
            var said = new List<string>();
            if (named is not null) said.Add($"is still named '{named}', as {theirs} is, so the game has two of that name");
            if (kept.Count > 0)
                said.Add($"has its own {Listed(changed.Select(n => n.Word))}, but its {Listed(kept.Select(n => n.Word))} " +
                    $"{(kept.Count == 1 ? "is" : "are")} still {theirs}'s, {Listed(kept.Select(n => n.Mine))}");
            var set = (named is null ? [] : new[] { $"{{\"path\": \"Name.Value\", \"value\": {ToolCalls.Quoted(named)}}}" })
                .Concat(kept.Select(n => $"{{\"path\": \"{n.Path}\", \"value\": {n.Mine}}}"));
            var shown = (named is null ? [] : new[] { $"'{named}'" }).Concat(kept.Select(n => n.Mine)).ToList();
            var one = shown.Count == 1;
            // Numbers alone may be right: a user who named none left them as the copied record's. Said first: said last, after the
            // call, the call was made with the kept numbers unchanged — a build for nothing in 9 runs of eval rounds 87–105 — and
            // 2 runs set numbers nobody had asked for.
            var numbersAlone = named is null
                ? $" Where the user named no {string.Join(" or ", kept.Select(n => n.Word))}, {Listed(shown)} " +
                  $"{(one ? "is right as it is" : "are right as they are")}: nothing to change, and this warning stays —" +
                  " answer with the build."
                : "";
            notes.Add($"{relative}: {name} {string.Join(", and ", said)} — copied with its model.{numbersAlone} If the user asked for " +
                $"{(one ? "another" : "others")}, one call sets {(one ? "it" : "them")} and builds again: " +
                ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, string.Join(", ", set), build: true) +
                $", with what the user asked for in place of {Listed(shown)} — what the user left as {theirs}'s stays");
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

    private static string? NameOf(IMajorRecordGetter record) => record is ITranslatedNamedGetter named ? named.Name?.String : null;

    /// <summary>
    /// The load order's weapon a new weapon's file was written after, where its part's field keeps that weapon's value and the
    /// value written at the margin is the file's own: the IronSword whose BasicStats.Value is the 25 a Flame Blade kept, beside
    /// the 150 written for it. The weapon is found as Source finds it, by the file alone, never by a value — of the playable,
    /// plain weapons with the model the file names, the one whose links the file names most, one at least — so a value of the
    /// user's that some other weapon of that model has is never taken for a copied one. Null, and the refusal stands, where the
    /// weapons ranked first differ in that field, where <paramref name="kept"/> is not theirs, or where any weapon of that model
    /// has <paramref name="written"/>: which of the two is the file's own is then not the build's to guess.
    /// </summary>
    internal static string? CopiedFrom(ILinkCache lookup, RecordIndex index, Type type, string text, string part, string key, string kept,
        string written)
    {
        if (type != typeof(Weapon) || Mates(lookup, index, text) is not { Count: > 0 } weapons) return null;
        string? Held(IWeaponGetter weapon) =>
            weapon.GetType().GetProperty(part)?.GetValue(weapon) is { } partValue && partValue.GetType().GetProperty(key)?.GetValue(partValue) is { } held
                ? Convert.ToString(held, CultureInfo.InvariantCulture) : null;
        var mates = weapons.Select(m => (m.Name, Value: Held(m.Weapon), m.Shared)).ToList();
        if (mates.Max(m => m.Shared) is var most && most == 0) return null;
        var first = mates.Where(m => m.Shared == most).ToList();
        return first.All(m => SameNumber(m.Value, kept)) && !mates.Any(m => SameNumber(m.Value, written)) ? first[0].Name : null;
    }

    /// <summary>
    /// The load order's weapons a new weapon's file was written after, found as CopiedFrom finds them — of the playable, plain
    /// weapons with the file's model, those whose links the file names most, one at least — in load order; none for a file
    /// with no model, or no weapon of its model.
    /// </summary>
    internal static List<(string Name, IWeaponGetter Weapon)> Followed(ILinkCache lookup, RecordIndex index, string text)
    {
        var mates = Mates(lookup, index, text);
        if (mates.Count == 0 || mates.Max(m => m.Shared) is var most && most == 0) return [];
        return mates.Where(m => m.Shared == most).Select(m => (m.Name, m.Weapon)).ToList();
    }

    /// <summary>
    /// A link field's value on a weapon as a file writes it — its editor ID, or its FormID where it has none — or null where
    /// the weapon has none.
    /// </summary>
    internal static string? LinkOn(IWeaponGetter weapon, string field, RecordIndex index) =>
        weapon.GetType().GetProperty(field)?.GetValue(weapon) is IFormLinkGetter { IsNull: false } link
            ? index.ByFormKey.TryGetValue(link.FormKey, out var entry) && entry.EditorId is { } id ? id : Session.FormKeyText(link.FormKey)
            : null;

    /// <summary>The playable, plain weapons with a file's model, each with how many of its links the file names, by FormID or editor ID.</summary>
    private static List<(string Name, IWeaponGetter Weapon, int Shared)> Mates(ILinkCache lookup, RecordIndex index, string text)
    {
        if (MisplacedFields.Root(text) is not { } root
            || !root.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("Model"), out var model)
            || model is not YamlDotNet.RepresentationModel.YamlMappingNode modelFields
            || !modelFields.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("File"), out var fileNode)
            || ModelPath((fileNode as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value) is not { } file) return [];
        var mates = new List<(string Name, IWeaponGetter Weapon, int Shared)>();
        foreach (var entry in index.ByFormKey.Values.Where(e => e.Signature == "WEAP" && !e.Unplayable))
        {
            // Plain, as Notes reads the weapons a new one is written after: no template, no enchantment.
            if (!lookup.TryResolve<IWeaponGetter>(entry.FormKey, out var weapon) || !weapon.Template.IsNull || !weapon.ObjectEffect.IsNull) continue;
            if (!string.Equals(ModelPath(weapon.Model?.File), file, StringComparison.OrdinalIgnoreCase)) continue;
            // Its links the file names, by FormID or by editor ID.
            var shared = weapon.EnumerateFormLinks().Select(l => l.FormKey).Where(f => !f.IsNull).Distinct().Count(f =>
                text.Contains(Session.FormKeyText(f), StringComparison.OrdinalIgnoreCase)
                || index.ByFormKey.TryGetValue(f, out var linked) && linked.EditorId is { } id
                    && System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<!\w){System.Text.RegularExpressions.Regex.Escape(id)}(?!\w)"));
            mates.Add((weapon.EditorID ?? Session.FormKeyText(weapon.FormKey), weapon, shared));
        }
        return mates;
    }

    /// <summary>Whether a value read and one written are the same: 25 and 25.0 are.</summary>
    private static bool SameNumber(string? read, string written) =>
        read is not null && (read == written || double.TryParse(read, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b);

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
