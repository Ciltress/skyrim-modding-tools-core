using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// Notes beside a build that succeeds, on what the project made that builds and is not what it is named for: a shout's word
/// casting a spell that is no Voice spell, and a recipe making a new record named as the game's record of its type.
/// </summary>
internal static class MadeNotes
{
    /// <summary>
    /// A shout of the project's whose words cast spells that are no shout's: of the 332 spells the words of the five masters'
    /// 117 shouts cast, 330 are of `Type: Voice` and 2 Powers. Eval round 126's shout task went 0 of 10, no run's word casting
    /// a Voice spell, each build answering success.
    /// </summary>
    public static List<string> Shouts(ISkyrimModGetter mod, ILinkCache lookup, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        foreach (var shout in mod.Shouts.Where(s => s.FormKey.ModKey == mod.ModKey))
        {
            if (files.GetValueOrDefault(shout.FormKey) is not { } relative) continue;
            var spells = shout.WordsOfPower.Where(w => !w.Spell.IsNull)
                .Select(w => mod.Spells.FirstOrDefault(s => s.FormKey == w.Spell.FormKey)
                    ?? (lookup.TryResolve<ISpellGetter>(w.Spell.FormKey, out var found) ? found : null))
                .OfType<ISpellGetter>().Where(s => s.Type != SpellType.Voice).DistinctBy(s => s.FormKey).ToList();
            if (spells.Count == 0) continue;
            var name = shout.EditorID ?? Session.FormKeyText(shout.FormKey);
            string Named(ISpellGetter spell) => $"{spell.EditorID ?? Session.FormKeyText(spell.FormKey)}, {Kind(spell.Type)}";
            var calls = spells.Where(s => s.FormKey.ModKey == mod.ModKey && files.ContainsKey(s.FormKey))
                .Select(s => ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), files[s.FormKey], "{\"path\": \"Type\", \"value\": \"Voice\"}", build: true))
                .ToList();
            notes.Add($"{relative}: {name}'s words cast {string.Join(" and ", spells.Select(Named))} — a shout's words cast spells of " +
                "`Type: Voice` — 330 of the 332 spells the words of the game's shouts cast are, the other 2 Powers" +
                (calls.Count > 0 ? $": {(calls.Count == 1 ? "one call makes it" : "a call each makes them")} Voice and builds again: {string.Join("; then ", calls)}"
                    : "; name a Voice spell for each word, or one of this project's own with `Type: Voice`"));
        }
        return notes;
    }

    /// <summary>
    /// A recipe of the project's making a new record that has the in-game name of the load order's record of its type: at the
    /// workbench the player gets the copy, not the game's. Asked for the Ancient Nord Sword at the forge, eval round 126's
    /// recipe runs made a new sword of that name beside the game's DraugrSword in 7 of 10, each build answering success.
    /// </summary>
    public static List<string> CopiedNames(ISkyrimModGetter mod, RecordIndex index, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        var records = mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey).ToDictionary(r => r.FormKey);
        foreach (var recipe in mod.ConstructibleObjects.Where(c => c.FormKey.ModKey == mod.ModKey))
        {
            if (recipe.CreatedObject.FormKeyNullable is not { } made || !records.TryGetValue(made, out var record)) continue;
            if ((record as INamedGetter)?.Name is not { Length: > 0 } name || files.GetValueOrDefault(recipe.FormKey) is not { } relative) continue;
            var type = RecordIndex.TypeName(record);
            var game = index.ByFormKey.Values.FirstOrDefault(e => e.Type == type && e.FormKey.ModKey != mod.ModKey && !e.Unplayable
                && e.EditorId is not null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (game is null) continue;
            var copy = record.EditorID ?? Session.FormKeyText(record.FormKey);
            var copyFile = files.GetValueOrDefault(record.FormKey);
            notes.Add($"{relative}: {recipe.EditorID ?? Session.FormKeyText(recipe.FormKey)} makes {copy} \"{name}\", a new {type} of this mod's own " +
                $"with the name of the game's {game.EditorId} (\"{game.Name}\", {Session.FormKeyText(game.FormKey)}) — at the workbench the player gets " +
                $"the copy, not the game's. Where the game's {game.Name} was meant, one call makes the recipe make it and builds again: " +
                ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, $"{{\"path\": \"CreatedObject\", \"value\": {ToolCalls.Quoted(game.EditorId!)}}}", build: true) +
                (copyFile is null ? "" : $", and {copyFile} is then named by nothing: delete it, {ToolCalls.Delete(pluginDir, copyFile)}") +
                $"; where a {type} of its own was meant, nothing to change, and this warning stays");
        }
        return notes;
    }

    private static string Kind(SpellType type) => type switch
    {
        SpellType.Spell => "a spell",
        SpellType.LesserPower => "a lesser power",
        SpellType.Power => "a power",
        SpellType.Ability => "an ability",
        _ => $"a {type.ToString().ToLowerInvariant()}"
    };
}
