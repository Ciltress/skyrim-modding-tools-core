using System.Globalization;
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
            // A shout with no words, or a word that casts no spell, does nothing when shouted: eval round 128's shout runs wrote a
            // spell's fields into the shout, deleted them as refused, and built a shout of no words, or of one word naming no
            // spell — each build a plain success.
            // `Spell: 000000:Skyrim.esm`, round 128's shout #3, is no spell either.
            static bool None(Mutagen.Bethesda.Plugins.IFormLinkGetter link) => link.IsNull || link.FormKey.ID == 0;
            var hollow = shout.WordsOfPower.Select((w, i) => (Word: w, Index: i)).Where(w => None(w.Word.Spell) || None(w.Word.Word)).ToList();
            if (shout.WordsOfPower.Count == 0 || hollow.Count > 0)
            {
                var said = shout.WordsOfPower.Count == 0
                    ? $"{shout.EditorID ?? Session.FormKeyText(shout.FormKey)} has no WordsOfPower, so shouting it does nothing"
                    : $"{shout.EditorID ?? Session.FormKeyText(shout.FormKey)}'s " + string.Join(" and ", hollow.Select(w =>
                        $"WordsOfPower[{w.Index}] names {(None(w.Word.Spell) && None(w.Word.Word) ? "no Word and no Spell" : None(w.Word.Spell) ? "no Spell" : "no Word")}")) +
                      (hollow.Count == 1 ? ", so that word does nothing" : ", so those words do nothing");
                notes.Add($"{relative}: {said} — a shout is three words, each a `Word:` (a WordOfPower of the mod's own, in WordsOfPower/), " +
                    "its `Spell:` (a spell of `Type: Voice`, in Spells/, its effect with the word's strength) and its `RecoveryTime:`: " +
                    "the magic skill's *A shout* writes one whole, readSkill {\"skill\": \"skyrim-modding-magic\"}, as the game's Frost Breath has it, " +
                    "readRecord {\"record\": \"FrostBreathShout\"}");
                continue;
            }
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

    /// <summary>The player's own NPC record, Skyrim.esm's Player.</summary>
    private static readonly Mutagen.Bethesda.Plugins.FormKey Player = Mutagen.Bethesda.Plugins.FormKey.Factory("000007:Skyrim.esm");

    /// <summary>
    /// A copy of the Player, the player's NPC: what it holds reaches a new game's player only, never one already saved, and a
    /// start or race mod that copies the player too takes one of the two. Asked for 500 gold when a game starts, eval round
    /// 128's quest #3 never read the quests skill, copied the Player and set its gold to 500, and the build answered success.
    /// </summary>
    public static List<string> PlayerCopy(ISkyrimModGetter mod, string pluginDir)
    {
        if (!mod.Npcs.Any(n => n.FormKey == Player)) return [];
        if (EffectCasting.Files(pluginDir).GetValueOrDefault(Player) is not { } relative) return [];
        return [$"{relative}: a copy of the Player (000007:Skyrim.esm), the player's own NPC — what it holds reaches the player of a " +
            "new game only, never one already saved, and a start or race mod that copies the player too takes one of the two " +
            "copies. To give the player gold, items, spells or perks, a quest that starts with the game gives them once from its " +
            "script: the quests skill's *Give the player something when a new game starts* writes one, readSkill " +
            "{\"skill\": \"skyrim-modding-quests-and-scripts\"}; then delete this copy, " + ToolCalls.Delete(pluginDir, relative) +
            ". Keep it only where the player's own record is what the user asked to change"];
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
                $"; only where the user asked for a new {type} of its own beside the game's is the copy meant — then nothing to change, and this warning stays");
        }
        return notes;
    }

    /// <summary>
    /// A recipe of the project's whose perk condition is turned around — HasPerk NotEqualTo 1, LessThan 1, EqualTo 0 — so it is
    /// shown only to a player without the perk. Copied from a tempering recipe's, eval round 128's recipe+detailed #3 had it, and
    /// its build was a plain success.
    /// </summary>
    public static List<string> RecipePerks(ISkyrimModGetter mod, ILinkCache lookup, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        foreach (var recipe in mod.ConstructibleObjects.Where(c => c.FormKey.ModKey == mod.ModKey))
        {
            if (files.GetValueOrDefault(recipe.FormKey) is not { } relative) continue;
            for (var i = 0; i < recipe.Conditions.Count; i++)
            {
                if (recipe.Conditions[i] is not IConditionFloatGetter condition || condition.Data is not IHasPerkConditionDataGetter has) continue;
                var value = condition.ComparisonValue;
                var without = condition.CompareOperator switch
                {
                    CompareOperator.NotEqualTo or CompareOperator.LessThan => value == 1,
                    CompareOperator.EqualTo or CompareOperator.LessThanOrEqualTo => value == 0,
                    _ => false
                };
                if (!without) continue;
                var perk = lookup.TryResolve<IPerkGetter>(has.Perk.Link.FormKey, out var found) && found.EditorID is { } id ? id : Session.FormKeyText(has.Perk.Link.FormKey);
                notes.Add($"{relative}: Conditions[{i}] is HasPerk {perk} {condition.CompareOperator} {value.ToString(CultureInfo.InvariantCulture)}, so the " +
                    $"recipe is shown only to a player without {perk} — where it needs the perk, one call makes it EqualTo 1, as the game's " +
                    "RecipeWeaponSteelSword has it, and builds again: " +
                    ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative,
                        $"{{\"path\": \"Conditions[{i}].CompareOperator\", \"value\": \"EqualTo\"}}, {{\"path\": \"Conditions[{i}].ComparisonValue\", \"value\": 1}}", build: true) +
                    "; where it is meant for a player without it, nothing to change, and this warning stays");
            }
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
