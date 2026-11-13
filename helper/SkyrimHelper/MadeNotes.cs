using System.Globalization;
using System.Text.RegularExpressions;
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
    public static List<string> Shouts(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        // The five masters' shouts the player learns — named, a word with a translation each — and how many of them have three words.
        var learned = new Lazy<(int All, int Three)>(() =>
        {
            var shouts = index.ByFormKey.Values.Where(e => e.Signature == "SHOU" && e.Name is { Length: > 0 }
                    && KindParts.Masters.Contains(e.FormKey.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase))
                .Select(e => lookup.TryResolve<IShoutGetter>(e.FormKey, out var s) ? s : null).OfType<IShoutGetter>()
                .Where(s => s.WordsOfPower.Count > 0 && s.WordsOfPower.All(w => !w.Word.IsNull)).ToList();
            return (shouts.Count, shouts.Count(s => s.WordsOfPower.Count == 3));
        });
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
            // Fewer words than the game's shouts: asked for a new dragon shout, eval round 130's shout #3 built one of one word as a
            // plain success.
            if (shout.WordsOfPower.Count < 3 && learned.Value is { All: >= 5 } game && game.Three >= 0.9 * game.All)
            {
                var shoutId = shout.EditorID ?? Session.FormKeyText(shout.FormKey);
                var last = shout.WordsOfPower[^1];
                string Id(Mutagen.Bethesda.Plugins.IFormLinkGetter link) =>
                    mod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == link.FormKey)?.EditorID ?? Session.FormKeyText(link.FormKey);
                string Next(string id, int n) => System.Text.RegularExpressions.Regex.IsMatch(id, @"\d$")
                    ? System.Text.RegularExpressions.Regex.Replace(id, @"\d+$", m => (int.Parse(m.Value, CultureInfo.InvariantCulture) + n).ToString(CultureInfo.InvariantCulture))
                    : $"{id}{n + 1}";
                var count = shout.WordsOfPower.Count;
                var sets = string.Join(", ", Enumerable.Range(1, 3 - count).Select(n =>
                    $"{{\"path\": \"WordsOfPower[{count + n - 1}]\", \"value\": {{\"Word\": {ToolCalls.Quoted(Next(Id(last.Word), n))}, " +
                    $"\"Spell\": {ToolCalls.Quoted(Next(Id(last.Spell), n))}, \"RecoveryTime\": {(last.RecoveryTime * (n + 1)).ToString(CultureInfo.InvariantCulture)}}}}}"));
                notes.Add($"{relative}: {shoutId} has {(count == 1 ? "one word" : $"{count} words")} of power, and " +
                    (game.Three == game.All ? $"all {game.All} of the five masters' named shouts" : $"{game.Three} of the five masters' {game.All} named shouts") +
                    " have three — each word learned shouts it stronger, with a Voice spell of its own, and the shouts menu " +
                    $"shows the three. Write the other {(3 - count == 1 ? "one" : "two")} as its {(count == 1 ? "word is" : "words are")} written: a WordOfPower " +
                    $"each in WordsOfPower/ and a Voice spell each in Spells/, stronger than the last; then one call names them and builds again: " +
                    ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, sets, build: true) +
                    $" — with the EditorIDs you wrote. Only where the user asked for a shout of {(count == 1 ? "one word" : $"{count} words")} is it right as it is");
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

    /// <summary>
    /// A word of power of the project's named as its translation: the shouts menu shows a word's Name, in the dragon tongue (Fus),
    /// beside its Translation (Force), and this one reads "Thunder" twice. Asked for the words "Kreh" (Thunder), "Dahl" (Strike)
    /// and "Vund" (Storm), eval round 129's shout+detailed #2 gave them those EditorIDs and named each as its translation.
    /// </summary>
    public static List<string> Words(ISkyrimModGetter mod, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        foreach (var word in mod.WordsOfPower.Where(w => w.FormKey.ModKey == mod.ModKey))
        {
            if (word.Name?.String is not { Length: > 0 } name || word.Translation.String is not { Length: > 0 } translation
                || !string.Equals(name.Trim(), translation.Trim(), StringComparison.OrdinalIgnoreCase)
                || files.GetValueOrDefault(word.FormKey) is not { } relative) continue;
            // The dragon word its EditorID spells — Kreh, or WordKreh as the game's WordFus — where it is one plain word that is no
            // translation; a shout whose words the user never gave (RoarWord1, WordRoar1) is not noted: its names are the model's.
            var said = System.Text.RegularExpressions.Regex.Match(word.EditorID ?? "", "^(?:Word(?=[A-Z]))?(?<word>[A-Z][a-z]+)$");
            var own = said.Success ? said.Groups["word"].Value : "";
            if (own.Length == 0) continue;
            // Named and translated as the word its EditorID spells: the meaning is missing. Eval round 131's shout+detailed #5
            // wrote Kreh, Dahl and Vund each translated as itself, asked for "Kreh" (Thunder), and built as success.
            if (own.Equals(translation.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"{relative}: {word.EditorID} is translated '{translation.Trim()}', as it is named — the shouts menu shows a word's Name, " +
                    "in the dragon tongue, beside its Translation, the word's meaning, as the game's WordFus is named 'Fus' and translated " +
                    $"'Force', so this word reads '{name}' twice. Where the user gave its meaning, one call writes it and builds again: " +
                    ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, "{\"path\": \"Translation.Value\", \"value\": \"Meaning\"}", build: true) +
                    ", with the meaning the user gave in place of Meaning; only where the user gave none is it right as it is");
                continue;
            }
            notes.Add($"{relative}: {word.EditorID} is named '{name}', as its Translation is — the shouts menu shows a word's Name, in the dragon " +
                $"tongue, beside its Translation, as the game's WordFus is named 'Fus' and translated 'Force', so this word reads '{name}' twice. " +
                $"Where {own} is the word, one call names it so and builds again: " +
                ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, $"{{\"path\": \"Name.Value\", \"value\": {ToolCalls.Quoted(own)}}}", build: true) +
                "; only where the user named the word so is it right as it is");
        }
        return notes;
    }

    /// <summary>
    /// A new arrow, weapon or armor of the project's, named, that is flagged NonPlayable: the player can never have it — it is in
    /// no inventory, no merchant sells it, none is picked up. Asked for arrows that do more damage than steel ones, eval round
    /// 129's arrow #5 copied CWArrow, a NonPlayable Iron Arrow, made it new and built a success. A bound weapon is ProjectNotes'.
    /// </summary>
    public static List<string> Unplayable(ISkyrimModGetter mod, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        static string Flags<T>(T flags, T dropped) where T : struct, Enum =>
            string.Join(", ", Enum.GetValues<T>().Where(f => Convert.ToUInt64(f) != 0 && !f.Equals(dropped) && flags.HasFlag(f)).Select(f => ToolCalls.Quoted(f.ToString())));
        var items = mod.Ammunitions.Where(a => a.Flags.HasFlag(Ammunition.Flag.NonPlayable))
                .Select(a => ((IMajorRecordGetter)a, "Flags", Flags(a.Flags, Ammunition.Flag.NonPlayable), "SteelArrow"))
            .Concat(mod.Weapons.Where(w => w.Data is { } d && d.Flags.HasFlag(WeaponData.Flag.NonPlayable) && !d.Flags.HasFlag(WeaponData.Flag.BoundWeapon))
                .Select(w => ((IMajorRecordGetter)w, "Data.Flags", Flags(w.Data!.Flags, WeaponData.Flag.NonPlayable), "IronSword")))
            .Concat(mod.Armors.Where(a => a.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable))
                .Select(a => ((IMajorRecordGetter)a, "MajorFlags", Flags(a.MajorFlags, Armor.MajorFlag.NonPlayable), "IronHelmet")));
        foreach (var (record, path, kept, example) in items)
        {
            if (record.FormKey.ModKey != mod.ModKey || (record as ITranslatedNamedGetter)?.Name?.String is not { Length: > 0 }
                || files.GetValueOrDefault(record.FormKey) is not { } relative) continue;
            var id = record.EditorID ?? Session.FormKeyText(record.FormKey);
            var call = kept.Length > 0
                ? ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, $"{{\"path\": \"{path}\", \"value\": [{kept}]}}", build: true)
                : ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, "", ToolCalls.Quoted(path), build: true);
            notes.Add($"{relative}: {id} is flagged NonPlayable, under {path}, so the player can never have it — it is in no inventory, no " +
                $"merchant sells it, none is picked up; the game's own the player uses are not, as readRecord {{\"record\": \"{example}\"}} " +
                $"shows. One call takes the flag away and builds again: {call}; only one the player is never to have keeps it");
        }
        return notes;
    }

    /// <summary>The player's own NPC record, Skyrim.esm's Player.</summary>
    private static readonly Mutagen.Bethesda.Plugins.FormKey Player = Mutagen.Bethesda.Plugins.FormKey.Factory("000007:Skyrim.esm");

    /// <summary>
    /// A worldspace of the project's own whose cells have ground, in a light plugin that is no master. In the game test of
    /// 2026-11-08, such a worldspace's cells stood without their ground — flat at its default land height, under its water —
    /// and the same plugin as a full one, or as a master as well as light (as Bethesda's Giant's Tooth is), showed the ground.
    /// newWorldspace makes its plugin full; this catches a header made light again, or a worldspace written by hand.
    /// </summary>
    public static List<string> LightWorldspace(ISkyrimModGetter mod, string pluginDir)
    {
        var flags = mod.ModHeader.Flags;
        if (!flags.HasFlag(SkyrimModHeader.HeaderFlag.Small) || flags.HasFlag(SkyrimModHeader.HeaderFlag.Master)) return [];
        var worlds = mod.Worldspaces.Where(w => w.FormKey.ModKey == mod.ModKey
                && w.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items).Any(c => c.Landscape is not null))
            .Select(w => w.EditorID ?? Session.FormKeyText(w.FormKey)).ToList();
        if (worlds.Count == 0) return [];
        return [$"RecordData.yaml: {string.Join(", ", worlds)} — a worldspace of the mod's own in a light plugin (the header's Small " +
            "flag), and the game shows the cells of such a worldspace without their ground: flat at its default land height, under " +
            $"its water. Take Small out of the header's Flags in {Path.Combine(pluginDir, "RecordData.yaml")} — the plugin is then a full " +
            "one and takes a slot in the load order — and build again"];
    }

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
    /// A copy of a game NPC given an item its Items had not: that NPC carries it, always — and none of the others of its kind,
    /// which take what they carry from the leveled item lists their Items name; an NPC that takes its inventory from its
    /// template carries none of its own Items at all. Asked for bandits that can carry silver swords, eval round 129's leveled
    /// #2, #3 and #4 copied a bandit — a boss, the bandits' template — and added the Silver Sword to its Items, two of them
    /// after reading the NPC skill, which sends what NPCs of a kind carry to the list; each build answered success.
    /// </summary>
    public static List<string> NpcItems(ISkyrimModGetter mod, ILinkCache lookup, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        var own = mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey).ToDictionary(r => r.FormKey);
        IMajorRecordGetter? Resolve(Mutagen.Bethesda.Plugins.FormKey key) =>
            own.GetValueOrDefault(key) ?? (lookup.TryResolve<IItemGetter>(key, out var found) ? found : null);
        foreach (var npc in mod.Npcs.Where(n => n.FormKey.ModKey != mod.ModKey && n.FormKey != Player))
        {
            if (files.GetValueOrDefault(npc.FormKey) is not { } relative || !lookup.TryResolve<INpcGetter>(npc.FormKey, out var game)) continue;
            var had = (game.Items ?? []).Select(i => i.Item.Item.FormKey).ToList();
            var added = new List<IMajorRecordGetter>();
            foreach (var entry in npc.Items ?? [])
                if (!had.Remove(entry.Item.Item.FormKey) && Resolve(entry.Item.Item.FormKey) is { } item and not ILeveledItemGetter)
                    added.Add(item);
            if (added.Count == 0) continue;
            var name = npc.EditorID ?? Session.FormKeyText(npc.FormKey);
            var shown = string.Join(", ", added.Select(a => a.EditorID ?? Session.FormKeyText(a.FormKey)));
            var templated = !npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Inventory);
            var note = $"{relative}: {name}, a copy of the game's NPC ({Session.FormKeyText(npc.FormKey)}), is given {shown} in its Items, which " +
                $"the game's has not — " + (templated
                    ? $"and it takes its inventory from its template, {(lookup.TryResolve<INpcSpawnGetter>(npc.Template.FormKey, out var template) ? template.EditorID : null) ?? Session.FormKeyText(npc.Template.FormKey)}, " +
                      "so the game gives it none of its own Items: this changes nothing in game"
                    : $"{name} itself carries it, always, as does each NPC that takes its inventory from it as a template, and none of the " +
                      "others of its kind") +
                ". What the NPCs of a kind carry — a sword more among the bandits' weapons — comes from the leveled item list their Items name";
            var first = added[0];
            if (CarriedFrom(templated ? npc.Template.FormKey : npc.FormKey, first, lookup) is { } list)
            {
                var count = list.Entries?.Count ?? 0;
                var level = list.Entries?.Select(e => (int?)e.Data?.Level).Min() ?? 1;
                var reference = first.EditorID ?? Session.FormKeyText(first.FormKey);
                var listId = list.EditorID ?? Session.FormKeyText(list.FormKey);
                note += $": {listId} ({Session.FormKeyText(list.FormKey)}) for {name}'s. Three calls give {reference} to them instead, every entry the " +
                    $"list has kept, and build: {ToolCalls.Delete(pluginDir, relative)}; then addPatch {{\"project\": {ToolCalls.Quoted(ToolCalls.ProjectOf(pluginDir))}, " +
                    $"\"records\": [{ToolCalls.Quoted(Session.FormKeyText(list.FormKey))}]}}; then " +
                    ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), listId, $"{{\"path\": \"Entries[{count}]\", \"value\": {{\"Data\": {{\"Level\": {level}, " +
                        $"\"Reference\": {ToolCalls.Quoted(reference)}, \"Count\": 1}}}}}}", build: true);
            }
            else note += ": the leveled-lists skill finds it, readSkill {\"skill\": \"skyrim-modding-leveled-lists\"}; and delete this copy, " +
                ToolCalls.Delete(pluginDir, relative);
            notes.Add(note + $". Keep the copy only where the user asked for {name} itself to carry it");
        }
        return notes;
    }

    /// <summary>
    /// A copy of a game's leveled list that holds entries the game's has not in place of some the game's has: those are handed
    /// out no more. An entry set at an index the list has writes over the one there — `Entries[{count}]` adds one after the
    /// last. Asked for bandits that can also carry silver swords, eval round 130's leveled #3 set `Entries[3]` and `Entries[0]`
    /// on three of the bandits' lists, and #4 an entry's Reference, each build a plain success. A copy that only lost entries
    /// is a removal, and not noted.
    /// </summary>
    public static List<string> ListEntries(ISkyrimModGetter mod, ILinkCache lookup, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        var own = mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey).ToDictionary(r => r.FormKey);
        string Named(Mutagen.Bethesda.Plugins.FormKey key) => own.GetValueOrDefault(key)?.EditorID
            ?? (lookup.TryResolve<IItemGetter>(key, out var item) ? item.EditorID : null)
            ?? (lookup.TryResolve<INpcSpawnGetter>(key, out var npc) ? npc.EditorID : null)
            ?? (lookup.TryResolve<ISpellRecordGetter>(key, out var spell) ? spell.EditorID : null)
            ?? Session.FormKeyText(key);
        static List<(Mutagen.Bethesda.Plugins.FormKey Reference, short Level, short Count)> Of<T>(IEnumerable<T>? entries,
            Func<T, (Mutagen.Bethesda.Plugins.FormKey, short, short)?> data) =>
            (entries ?? []).Select(data).OfType<(Mutagen.Bethesda.Plugins.FormKey, short, short)>().ToList();
        var lists = mod.LeveledItems.Where(l => l.FormKey.ModKey != mod.ModKey)
                .Select(l => ((IMajorRecordGetter)l, Of(l.Entries, e => e.Data is { } d ? (d.Reference.FormKey, d.Level, d.Count) : null),
                    lookup.TryResolve<ILeveledItemGetter>(l.FormKey, out var g) ? Of(g.Entries, e => e.Data is { } d ? (d.Reference.FormKey, d.Level, d.Count) : null) : null))
            .Concat(mod.LeveledNpcs.Where(l => l.FormKey.ModKey != mod.ModKey)
                .Select(l => ((IMajorRecordGetter)l, Of(l.Entries, e => e.Data is { } d ? (d.Reference.FormKey, d.Level, d.Count) : null),
                    lookup.TryResolve<ILeveledNpcGetter>(l.FormKey, out var g) ? Of(g.Entries, e => e.Data is { } d ? (d.Reference.FormKey, d.Level, d.Count) : null) : null)));
        foreach (var (copy, has, game) in lists)
        {
            if (game is null || files.GetValueOrDefault(copy.FormKey) is not { } relative) continue;
            var lost = game.ToList();
            var added = new List<(Mutagen.Bethesda.Plugins.FormKey Reference, short Level, short Count)>();
            foreach (var entry in has)
                if (lost.IndexOf(entry) is var at and >= 0) lost.RemoveAt(at); else added.Add(entry);
            if (lost.Count == 0 || added.Count == 0) continue;
            string Shown((Mutagen.Bethesda.Plugins.FormKey Reference, short Level, short Count) e) =>
                $"{Named(e.Reference)} at level {e.Level}{(e.Count == 1 ? "" : $", {e.Count} of it")}";
            var name = copy.EditorID ?? Session.FormKeyText(copy.FormKey);
            var sets = string.Join(", ", lost.Select((e, i) => $"{{\"path\": \"Entries[{has.Count + i}]\", \"value\": {{\"Data\": {{\"Level\": {e.Level}, " +
                $"\"Reference\": {ToolCalls.Quoted(Named(e.Reference))}, \"Count\": {e.Count}}}}}}}"));
            var them = lost.Count == 1 ? "it" : "them";
            notes.Add($"{relative}: this copy of {name} holds {string.Join(", ", added.Select(Shown))} in place of {lost.Count} of the game's " +
                $"entries, {string.Join(", ", lost.Select(Shown))}, which the list hands out no more — an entry set at an index the list has, " +
                $"Entries[3], writes over the one there; Entries[{has.Count}] adds one after the last. Where the user asked to add, not to " +
                $"replace, one call puts {them} back and builds again: {ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, sets, build: true)}; " +
                $"only where the user asked for {them} gone is this right as it is");
        }
        return notes;
    }

    /// <summary>
    /// A copy of a game's record whose Keywords hold keywords the game's has not in place of some it has: what those gave it is
    /// gone — a weapon without WeapTypeSword is no sword to the perks, one without VendorItemWeapon bought by no merchant. Asked
    /// for an Iron Sword that counts as silver, 4 of eval round 135's 5 keyword runs set its Keywords whole, WeapMaterialIron
    /// left out and a FormID from memory put in, each build a plain success. A copy that only lost keywords is a removal, and
    /// not noted.
    /// </summary>
    public static List<string> KeywordsReplaced(ISkyrimModGetter mod, ILinkCache lookup, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        foreach (var copy in mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey != mod.ModKey))
        {
            if (copy is not IKeywordedGetter<IKeywordGetter> keyworded || files.GetValueOrDefault(copy.FormKey) is not { } relative) continue;
            if (!lookup.TryResolve(copy.FormKey, copy.Registration.GetterType, out var game) || game is not IKeywordedGetter<IKeywordGetter> theirs) continue;
            var has = (keyworded.Keywords ?? []).Select(k => k.FormKey).ToList();
            var was = (theirs.Keywords ?? []).Select(k => k.FormKey).ToList();
            var lost = was.Where(k => !has.Contains(k)).Distinct().ToList();
            var added = has.Where(k => !was.Contains(k)).Distinct().ToList();
            if (lost.Count == 0 || added.Count == 0) continue;
            string Named(Mutagen.Bethesda.Plugins.FormKey key) =>
                lookup.TryResolve<IKeywordGetter>(key, out var keyword) && keyword.EditorID is { } id ? id : Session.FormKeyText(key);
            var name = copy.EditorID ?? Session.FormKeyText(copy.FormKey);
            var sets = string.Join(", ", lost.Select((k, i) => $"{{\"path\": \"Keywords[{has.Count + i}]\", \"value\": {ToolCalls.Quoted(Named(k))}}}"));
            var them = lost.Count == 1 ? "it" : "them";
            notes.Add($"{relative}: this copy of {name} has the keyword{(added.Count == 1 ? "" : "s")} {string.Join(", ", added.Select(Named))} in " +
                $"place of {lost.Count} of the game's, {string.Join(", ", lost.Select(Named))}, and in game it loses what {(lost.Count == 1 ? "that one gives" : "those give")} — Keywords " +
                $"set whole is the list as written; Keywords[{has.Count}] adds one after the last. Where the user asked to add, not to replace, " +
                $"one call puts {them} back and builds again: {ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, sets, build: true)}; " +
                $"only where the user asked for {them} gone is this right as it is");
        }
        return notes;
    }

    /// <summary>
    /// The leveled item list an NPC's kind takes an item of this one's kind from: of the lists its Items name — or its
    /// inventory template's — and those inside them, the one that hands out the most items, every one like this one: a
    /// weapon held as it is held (one-handed, two-handed, a bow, a staff), an armor worn in a slot it fills, else a record of its
    /// type. LItemBanditWeapon1H for a sword given a bandit: LItemBanditWeapon hands out two-handed ones too, LItemBanditSword
    /// fewer.
    /// </summary>
    private static ILeveledItemGetter? CarriedFrom(Mutagen.Bethesda.Plugins.FormKey npcKey, IMajorRecordGetter item, ILinkCache lookup)
    {
        var lists = new List<ILeveledItemGetter>();
        var seen = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();
        var queue = new Queue<(Mutagen.Bethesda.Plugins.FormKey Key, int Depth)>([(npcKey, 0)]);
        while (queue.Count > 0)
        {
            var (key, depth) = queue.Dequeue();
            if (depth > 5 || !seen.Add(key)) continue;
            if (lookup.TryResolve<INpcGetter>(key, out var npc))
            {
                if (!npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Inventory))
                    queue.Enqueue((npc.Template.FormKey, depth + 1));
                else foreach (var entry in npc.Items ?? []) queue.Enqueue((entry.Item.Item.FormKey, depth + 1));
            }
            else if (lookup.TryResolve<ILeveledItemGetter>(key, out var list))
            {
                lists.Add(list);
                foreach (var entry in list.Entries ?? [])
                    if (entry.Data?.Reference.FormKeyNullable is { } inner) queue.Enqueue((inner, depth + 1));
            }
        }
        List<IMajorRecordGetter> Leaves(ILeveledItemGetter list)
        {
            var leaves = new List<IMajorRecordGetter>();
            var inside = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();
            void Walk(ILeveledItemGetter at, int depth)
            {
                if (depth > 4 || !inside.Add(at.FormKey)) return;
                foreach (var entry in at.Entries ?? [])
                {
                    if (entry.Data?.Reference.FormKeyNullable is not { } inner) continue;
                    if (lookup.TryResolve<ILeveledItemGetter>(inner, out var sub)) Walk(sub, depth + 1);
                    else if (lookup.TryResolve<IItemGetter>(inner, out var leaf)) leaves.Add(leaf);
                }
            }
            Walk(list, 0);
            return leaves;
        }
        return lists.Select(l => (List: l, Leaves: Leaves(l)))
            .Where(l => l.Leaves.Count > 0 && l.Leaves.All(leaf => Alike(leaf, item)))
            .OrderByDescending(l => l.Leaves.Count).Select(l => l.List).FirstOrDefault();
    }

    /// <summary>Whether two items are of one kind for a list: a weapon held alike, an armor sharing a slot, else one type.</summary>
    private static bool Alike(IMajorRecordGetter a, IMajorRecordGetter b) => (a, b) switch
    {
        (IWeaponGetter x, IWeaponGetter y) => Held(x) == Held(y),
        (IArmorGetter x, IArmorGetter y) => ((x.BodyTemplate?.FirstPersonFlags ?? 0) & (y.BodyTemplate?.FirstPersonFlags ?? 0)) != 0,
        _ => a.GetType() == b.GetType()
    };

    /// <summary>How a weapon is held: one-handed, two-handed, a bow or crossbow, a staff.</summary>
    private static string Held(IWeaponGetter weapon) => weapon.Data?.AnimationType switch
    {
        WeaponAnimationType.TwoHandSword or WeaponAnimationType.TwoHandAxe => "two-handed",
        WeaponAnimationType.Bow or WeaponAnimationType.Crossbow => "bow",
        WeaponAnimationType.Staff => "staff",
        _ => "one-handed"
    };

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
                (copyFile is null ? "" : $", and {copyFile} is then named by nothing: delete it, which builds again too, {ToolCalls.Delete(pluginDir, copyFile, build: true)}") +
                // A new mod is no ask for a new item: eval round 132's recipe #1 answered "since your request was to create a new mod,
                // the system maintained the new record", on this warning and its call in the nextStep.
                $". A recipe for {game.Name} in a new mod is the game's {game.Name}: a new mod is no ask for a new {type}. Only where the " +
                $"user asked for a {type} of its own beside the game's — another of its name — is the copy meant; then nothing to change, " +
                "and this warning stays");
        }
        notes.AddRange(CopiedMaterials(mod, index, pluginDir, records, files));
        return notes;
    }

    /// <summary>
    /// A new record of the project named as a scroll that is no Scroll — a Spell, a Book, a misc item — in a project with no
    /// Scroll: a scroll is a record of its own, cast once from the inventory. Asked for a Scroll of Storms, 8 of eval round
    /// 144's 10 scroll runs wrote a Spell, with a Book or a MiscItem beside it or none, and answered it a scroll.
    /// </summary>
    public static List<string> NotScrolls(ISkyrimModGetter mod, string pluginDir)
    {
        if (mod.Scrolls.Any(s => s.FormKey.ModKey == mod.ModKey)) return [];
        var files = EffectCasting.Files(pluginDir);
        return mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey && r is ISpellGetter or IBookGetter or IMiscItemGetter)
            .Where(r => (r as INamedGetter)?.Name is { } name && Regex.IsMatch(name, @"\bscroll\b", RegexOptions.IgnoreCase))
            .Select(r => $"{files.GetValueOrDefault(r.FormKey) ?? r.EditorID}: {r.EditorID} \"{((INamedGetter)r).Name}\" is {RecordFiles.Article(RecordIndex.TypeName(r))}, " +
                "and a scroll is a record of its own: a Scroll in Scrolls/, cast once from the inventory, which carries the spell's CastType " +
                "(3), TargetType and Effects itself — as the game's LightningBoltScroll has them (readRecord {\"record\": \"LightningBoltScroll\"}, " +
                "the skyrim-modding-magic skill's \"A scroll\"). Where a scroll was asked for, write it there; where this " +
                $"{RecordIndex.TypeName(r)} was meant, nothing to change, and this warning stays")
            .ToList();
    }

    /// <summary>
    /// A new NPC, leveled list, container, encounter zone, location, activator, race or spell of the project's that nothing in the
    /// plugin names — no reference places the NPC, no list,
    /// alias, outfit, container or script property holds either — so the game never shows or uses it. Four of eval round 145's
    /// follower and package runs built a new NPC placed in no cell and answered her in the inn; leveled-spell #3 made its copy of
    /// LSpellBandit01FireFrostShock a new list nothing uses, and the game's list stayed as it was.
    /// </summary>
    public static List<string> Unused(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var made = mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey
            && r is INpcGetter or ILeveledItemGetter or ILeveledNpcGetter or ILeveledSpellGetter or IContainerGetter or IEncounterZoneGetter
                or ILocationGetter or IActivatorGetter or IRaceGetter or ISpellGetter).ToList();
        if (made.Count == 0) return [];
        var named = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();
        foreach (var record in mod.EnumerateMajorRecords())
            foreach (var link in record.EnumerateFormLinks())
                if (link.FormKey != record.FormKey) named.Add(link.FormKey);
        var files = EffectCasting.Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        var cells = mod.EnumerateMajorRecords().OfType<ICellGetter>().ToList();
        var notes = new List<string>();
        foreach (var record in made.Where(r => !named.Contains(r.FormKey)))
        {
            var id = record.EditorID ?? Session.FormKeyText(record.FormKey);
            var file = files.GetValueOrDefault(record.FormKey) ?? id;
            if (record is INpcGetter npc)
            {
                var cell = cells is [var only] ? $" — the project's cell {only.EditorID ?? Session.FormKeyText(only.FormKey)} is one" : "";
                notes.Add($"{file}: {id}{(npc.Name?.String is { Length: > 0 } name ? $" \"{name}\"" : "")}, a new NPC, is placed in no cell, and nothing in " +
                    $"the plugin names {id} — no leveled list, quest alias or script property — so the game never shows {id}. " +
                    GameNpcNamed(npc.Name?.String, lookup, index, mod.ModKey, pluginDir, file) + $"Where the user asked " +
                    $"for {id} somewhere, an inn or a house, a PlacedNpc file in that cell's Temporary/ folder places {id}{cell}: readRecord of the " +
                    $"cell answers `newReference`, the path and the lines to write, with `MutagenObjectType: PlacedNpc` first and `Base: {id}`. " +
                    "Where the NPC record alone was asked for, nothing to change, and this warning stays");
                continue;
            }
            // A race of the mod's own no NPC has: asked for wolves in the wild that can also be ice wolves, eval round 152's
            // leveled-npc #1 wrote an "Ice Wolf" race alone, a copy of WolfRace, and answered the ice wolves made.
            if (record is IRaceGetter race)
            {
                notes.Add($"{file}: {id}{(race.Name?.String is { Length: > 0 } name ? $" \"{name}\"" : "")}, a new Race, is the race of no NPC of " +
                    $"the plugin, so the game shows nothing of it: a creature or a person of a kind is an NPC with `Race: {id}`, placed in a cell " +
                    "or brought by a leveled list. " + GameNpcNamed(race.Name?.String, lookup, index, mod.ModKey, pluginDir, file) +
                    "Where the race record alone was asked for, nothing to change, and this warning stays");
                continue;
            }
            // A chest of the mod's own placed nowhere: eval round 147's key #3 built one, its lock and key with it, and answered.
            if (record is IContainerGetter chest)
            {
                var cell = cells is [var only] ? $" — the project's cell {only.EditorID ?? Session.FormKeyText(only.FormKey)} is one" : "";
                notes.Add($"{file}: {id}{(chest.Name?.String is { Length: > 0 } name ? $" \"{name}\"" : "")}, a new Container, is placed in no cell, and " +
                    $"nothing in the plugin names {id}, so the game never shows it. Where the user asked for it somewhere, a house or a room, a " +
                    $"PlacedObject file in that cell's Temporary/ folder places it{cell}: readRecord of the cell answers `newReference`, the path " +
                    $"and the lines to write, with `Base: {id}` — and its lock, where it has one, is that reference's `Lock:`, `Level:` and `Key:` " +
                    "below it. Where the container record alone was asked for, nothing to change, and this warning stays");
                continue;
            }
            // A lever of the mod's own placed nowhere, as a chest: a placement written into its own record and deleted when refused,
            // eval round 150's activator runs answered levers in Breezehome that stood nowhere.
            if (record is IActivatorGetter activator)
            {
                var cell = cells is [var only] ? $" — the project's cell {only.EditorID ?? Session.FormKeyText(only.FormKey)} is one" : "";
                notes.Add($"{file}: {id}{(activator.Name?.String is { Length: > 0 } name ? $" \"{name}\"" : "")}, a new Activator, is placed in no cell, " +
                    $"and nothing in the plugin names {id}, so the game never shows it. Where the user asked for it somewhere, a house or a room, a " +
                    $"PlacedObject file in that cell's Temporary/ folder places it{cell}: readRecord of the cell answers `newReference`, the path " +
                    $"and the lines to write, with `Base: {id}`. Where the activator record alone was asked for, nothing to change, and this " +
                    "warning stays");
                continue;
            }
            // A zone of the mod's own no cell takes: eval round 148's encounterzone+detailed #2 wrote one for the mine, named no cell
            // by it, and answered the mine made more dangerous.
            if (record is IEncounterZoneGetter)
            {
                var cellFiles = RecordFiles.CellRecords(pluginDir).Where(c => c.Type == typeof(Cell)).Select(c => c.Relative).ToList();
                var cellCall = cellFiles is [var cellFile]
                    ? $" — the project's cell {cellFile[..cellFile.LastIndexOf('/')].Split('/')[^1]} is one, and one call sets it and builds: " +
                      ToolCalls.Edit(project, cellFile, $"{{\"path\": \"EncounterZone\", \"value\": {ToolCalls.Quoted(id)}}}", build: true)
                    : "";
                notes.Add($"{file}: {id}, a new EncounterZone, is named by no cell of the plugin, so no place takes its levels: a cell takes a " +
                    $"zone by its own `EncounterZone:` line, and the zone's Location does not set it. Where the user asked for it in a place — a " +
                    $"cave, a mine, a house — copy that place's cell with addPatch and set the copy's EncounterZone to {id}{cellCall}. Where the " +
                    "zone record alone was asked for, nothing to change, and this warning stays");
                continue;
            }
            // A location of the mod's own no cell is in: 6 of the 10 location+detailed runs of eval rounds 144 and 149 wrote the location
            // and the room beside it, set no cell's Location to it, and answered the room in its location.
            if (record is ILocationGetter location)
            {
                var cellFiles = RecordFiles.CellRecords(pluginDir).Where(c => c.Type == typeof(Cell)).Select(c => c.Relative).ToList();
                var cellCall = cellFiles is [var cellFile]
                    ? $" — the project's cell {cellFile[..cellFile.LastIndexOf('/')].Split('/')[^1].Split(" - ")[0]} is one, and one call sets it " +
                      "and builds: " + ToolCalls.Edit(project, cellFile, $"{{\"path\": \"Location\", \"value\": {ToolCalls.Quoted(id)}}}", build: true)
                    : "";
                notes.Add($"{file}: {id}{(location.Name?.String is { Length: > 0 } name ? $" \"{name}\"" : "")}, a new Location, is the location of " +
                    "no cell of the plugin and is named by nothing in it, so no place of the game is in it: a cell is in a location by its own " +
                    $"`Location:` line, and the location names no cell. Where the user asked for a place in it — a room, a house, a camp — set " +
                    $"that cell's Location to {id}{cellCall}. Where the location record alone was asked for, nothing to change, and this " +
                    "warning stays");
                continue;
            }
            // A spell of the mod's own nothing gives, beside a tome of the mod's that teaches the game's: asked for a tome that teaches
            // Firebolt, eval round 144's spelltome #4 wrote a spell beside it and built it with no word. A new spell alone is a
            // spell the user asked for (the eval's spell task: "no spell tome is needed"), and no note.
            if (record is ISpellGetter spell)
            {
                var taught = mod.Books.Where(b => b.FormKey.ModKey == mod.ModKey)
                    .Select(b => (Book: b, Spell: b.Teaches is IBookSpellGetter teaches && !teaches.Spell.IsNull ? teaches.Spell.FormKey : (Mutagen.Bethesda.Plugins.FormKey?)null))
                    .FirstOrDefault(t => t.Spell is { } s && s.ModKey != mod.ModKey);
                if (taught.Spell is not { } learnt) continue;
                var gameSpell = index.ByFormKey.TryGetValue(learnt, out var entry) && entry.EditorId is { } learntId ? learntId : Session.FormKeyText(learnt);
                notes.Add($"{file}: {id}{(spell.Name?.String is { Length: > 0 } name ? $" \"{name}\"" : "")}, a new Spell, is named by nothing in the " +
                    "plugin — no spell tome teaches it, no NPC, perk, list or script property has it — so the player never gets it: " +
                    $"{taught.Book.EditorID ?? Session.FormKeyText(taught.Book.FormKey)} teaches {learnt.ModKey.FileName}'s {gameSpell}. Where the " +
                    $"game's spell was meant, this one is a second spell the game holds for nothing — one call deletes it: " +
                    $"{ToolCalls.Delete(pluginDir, file, build: true)}; where this spell is to be learnt, name {id} in the tome's `Teaches:`, " +
                    $"`Spell: {id}`. Where the spell record alone was asked for, nothing to change, and this warning stays");
                continue;
            }
            var type = RecordIndex.TypeName(record);
            var origin = GameListLike(record, lookup, index, mod.ModKey);
            notes.Add($"{file}: {id}, a new {type}, is named by nothing in the plugin — no NPC, outfit, container or other list holds it — so " +
                "the game never uses it. " + (origin is { } game
                    ? $"It holds what the game's {game.EditorId} ({Session.FormKeyText(game.FormKey)}) holds: where that list was to change, this " +
                      "file is to be its copy again, the change written into it — one call makes it that copy and builds: " +
                      ToolCalls.Edit(project, file, $"{{\"path\": \"FormKey\", \"value\": {ToolCalls.Quoted(Session.FormKeyText(game.FormKey))}}}" +
                          (game.EditorId is { } gameId ? $", {{\"path\": \"EditorID\", \"value\": {ToolCalls.Quoted(gameId)}}}" : ""), build: true) +
                      ". "
                    : "Where a list of the game's was to change, copy that one with addPatch and change the copy; where this list is to be used, " +
                      "link it from what holds it. ") +
                "Where the user asked for this list alone, nothing to change, and this warning stays");
        }
        return notes;
    }

    /// <summary>
    /// A new NPC of the project's that one of its leveled lists holds, whose name a non-unique NPC of the load order has: the
    /// game's own, with its own looks and stats, where the user asked for the game's. Told the game's Ice Wolf and its list, all
    /// three plain leveled-npc runs of eval round 150 put an Ice Wolf of their own into LCharWolf — a copy of EncWolf, a grey
    /// wolf by that name — and built with no word.
    /// </summary>
    public static List<string> GameNamedInLists(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var notes = new List<string>();
        var lists = mod.LeveledNpcs.ToList();
        if (lists.Count == 0) return notes;
        var files = EffectCasting.Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        foreach (var npc in mod.Npcs.Where(n => n.FormKey.ModKey == mod.ModKey))
        {
            if (npc.Name?.String is not { Length: > 0 } name) continue;
            var holder = lists.Select(l => (List: l, At: (l.Entries ?? []).Select((e, i) => (e, i))
                    .Where(x => x.e.Data?.Reference.FormKey == npc.FormKey).Select(x => x.i).ToList()))
                .FirstOrDefault(h => h.At.Count > 0);
            if (holder.List is null) continue;
            var twins = index.ByFormKey.Values.Where(e => e.Type == "Npc" && e.FormKey.ModKey != mod.ModKey && e.EditorId is not null
                && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FormKey.ModKey.FileName == "Skyrim.esm" ? 0 : 1).ToList();
            if (twins.Count is 0 or > 3 || !lookup.TryResolve<INpcGetter>(twins[0].FormKey, out var twin)
                || twin.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique)) continue;
            var twinId = twins[0].EditorId!;
            var id = npc.EditorID ?? Session.FormKeyText(npc.FormKey);
            var file = files.GetValueOrDefault(npc.FormKey) ?? id;
            var listId = holder.List.EditorID ?? Session.FormKeyText(holder.List.FormKey);
            var listFile = files.GetValueOrDefault(holder.List.FormKey) ?? listId;
            var set = string.Join(", ", holder.At.Select(i => $"{{\"path\": \"Entries[{i}].Data.Reference\", \"value\": {ToolCalls.Quoted(twinId)}}}"));
            notes.Add($"{file}: {id} \"{name}\", a new NPC, is in {listId}, and the game has an NPC of that name already: {twinId} " +
                $"({Session.FormKeyText(twins[0].FormKey)}), another NPC, with its own looks and stats, which {id} is not — in game {listId} brings " +
                $"{id}, as its file writes it. Where the user asked for the game's {name}, one call puts {twinId} in its place and builds: " +
                ToolCalls.Edit(project, listFile, set, build: true) + $"; this file is then left over: {ToolCalls.Delete(pluginDir, file)}. Where an " +
                "NPC of the mod's own of that name was meant, nothing to change, and this warning stays");
        }
        return notes;
    }

    /// <summary>
    /// A new ingredient of the project's that nothing gives — no plant, list, container, recipe or script names it — where the
    /// project has a plant of its own that gives another or none, or places a plant of the game's, or the ingredient itself.
    /// Asked for a plant to pick for a new leaf, eval round 145's flora runs built a Moonpetal giving nothing (+detailed #1),
    /// placed the game's potato plant (#3) or the leaf itself (#1), each with no word. A new ingredient alone is no note.
    /// </summary>
    public static List<string> Ungiven(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var own = mod.ModKey;
        var ingredients = mod.Ingredients.Where(i => i.FormKey.ModKey == own).ToList();
        if (ingredients.Count == 0) return [];
        var all = mod.EnumerateMajorRecords().ToList();
        var placed = all.OfType<IPlacedObjectGetter>().Where(p => p.FormKey.ModKey == own && !p.Base.IsNull).ToList();
        // What the project's records name, but where they are placed: a leaf lying in a cell is no plant giving it. A cell's and a
        // worldspace's links are those of what they hold too.
        var named = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();
        foreach (var record in all.Where(r => r is not (IPlacedObjectGetter or ICellGetter or IWorldspaceGetter)))
            foreach (var link in record.EnumerateFormLinks())
                if (link.FormKey != record.FormKey) named.Add(link.FormKey);
        var plants = all.Where(r => r.FormKey.ModKey == own && r is ITreeGetter or IFloraGetter).ToList();
        var files = EffectCasting.Files(pluginDir);
        var placedFiles = RecordFiles.CellRecords(pluginDir).Where(c => c.Type != typeof(Cell))
            .Select(c => (c.Relative, Key: RecordFiles.Identity(File.ReadAllText(c.File)).FormKey)).Where(c => c.Key is not null)
            .GroupBy(c => c.Key!.Value).ToDictionary(g => g.Key, g => g.First().Relative);
        var project = ToolCalls.ProjectOf(pluginDir);
        string Id(IMajorRecordGetter r) => r.EditorID ?? Session.FormKeyText(r.FormKey);
        string NameOf(Mutagen.Bethesda.Plugins.FormKey key) => index.ByFormKey.TryGetValue(key, out var e) && e.EditorId is { } id ? id : Session.FormKeyText(key);
        Mutagen.Bethesda.Plugins.FormKey? GivesOf(IMajorRecordGetter plant) => plant switch
        {
            ITreeGetter tree => tree.Ingredient.FormKeyNullable,
            IFloraGetter flora => flora.Ingredient.FormKeyNullable,
            _ => null
        };
        const string example = "TreeFloraMountainFlower01Blue";
        var notes = new List<string>();
        foreach (var leaf in ingredients.Where(i => !named.Contains(i.FormKey)))
        {
            var leafId = Id(leaf);
            var newPlant = $"{leafId}Plant";
            // A plant of the project's own that gives another or none.
            if (plants.FirstOrDefault(p => GivesOf(p) is not { } given || given.ModKey != own) is { } plant)
            {
                var file = files.GetValueOrDefault(plant.FormKey) ?? Id(plant);
                var gives = GivesOf(plant) is { } given ? $"the game's {NameOf(given)}" : "nothing";
                notes.Add($"{file}: {Id(plant)}, the project's plant, gives {gives} when picked, and {leafId}, the project's new ingredient, is " +
                    "given by nothing — no plant, list or container names it. A plant gives what it is picked for by its one `Ingredient:` line: " +
                    $"one call makes {Id(plant)} give {leafId} and builds: " +
                    ToolCalls.Edit(project, file, $"{{\"path\": \"Ingredient\", \"value\": {ToolCalls.Quoted(leafId)}}}", build: true) +
                    $". Where it was to give {gives}, nothing to change, and this warning stays");
                continue;
            }
            if (plants.Count > 0) continue;
            // A plant of the game's placed: it gives the game's ingredient, not the project's.
            if (placed.FirstOrDefault(p => p.Base.FormKey.ModKey != own && index.ByFormKey.TryGetValue(p.Base.FormKey, out var b)
                    && b.Type is "Tree" or "Flora") is { } gamePlant)
            {
                var file = placedFiles.GetValueOrDefault(gamePlant.FormKey) ?? Id(gamePlant);
                var baseId = NameOf(gamePlant.Base.FormKey);
                var gameGives = lookup.TryResolve<ITreeGetter>(gamePlant.Base.FormKey, out var tree) ? tree.Ingredient.FormKeyNullable
                    : lookup.TryResolve<IFloraGetter>(gamePlant.Base.FormKey, out var flora) ? flora.Ingredient.FormKeyNullable : null;
                notes.Add($"{file}: {Id(gamePlant)} places the game's {baseId}" + (gameGives is { } g ? $", which gives {NameOf(g)} when picked" : "") +
                    $", and {leafId}, the project's new ingredient, is given by no plant. A plant of the project's own that gives it is a record " +
                    $"of its own: write Trees/{newPlant}.yaml as the game's {baseId} is written (readRecord {{\"record\": \"{baseId}\"}}), with " +
                    $"`EditorID: {newPlant}`, the plant's Name, and `Ingredient: {leafId}`; then one call stands it here and builds: " +
                    ToolCalls.Edit(project, file, $"{{\"path\": \"Base\", \"value\": {ToolCalls.Quoted(newPlant)}}}", build: true) +
                    $". Where the game's {baseId} was to stand there, nothing to change, and this warning stays");
                continue;
            }
            // The ingredient itself placed: it lies there to be picked up once.
            if (placed.FirstOrDefault(p => p.Base.FormKey == leaf.FormKey) is { } lying)
            {
                var file = placedFiles.GetValueOrDefault(lying.FormKey) ?? Id(lying);
                notes.Add($"{file}: {Id(lying)} places {leafId}, the ingredient itself: it lies there to be picked up once, and is gone. A plant " +
                    $"that gives it when picked, and grows it again, is a Tree of the project's own with `Ingredient: {leafId}`, as the game's " +
                    $"{example} has it (readRecord {{\"record\": \"{example}\"}}): write Trees/{newPlant}.yaml so, with `EditorID: {newPlant}` and " +
                    "the plant's Name; then one call stands it here in the leaf's stead and builds: " +
                    ToolCalls.Edit(project, file, $"{{\"path\": \"Base\", \"value\": {ToolCalls.Quoted(newPlant)}}}", build: true) +
                    ". Where the leaf itself was to lie there, nothing to change, and this warning stays");
            }
        }
        return notes;
    }

    /// <summary>The words of a thing used where it stands, by which a new MiscItem's name or editor ID says it is an Activator.</summary>
    internal static readonly Regex UsedWhereItStands = new(@"(?i:\b(lever|switch|button|handle|crank|valve|pull ?chain)s?\b)|(?<=[a-z])(Lever|Switch|Button|Handle|Crank|Valve)(?![a-z])");

    /// <summary>
    /// A new MiscItem of the project's that carries a script or is named a lever, a switch, a button: a misc item is picked up when
    /// activated, and a thing used where it stands is an Activator. Asked for a lever that gives gold when pulled, 4 of eval round
    /// 150's 5 failed activator runs wrote it in MiscItems/, and one built so, its script attached, with no word.
    /// </summary>
    public static List<string> MiscActivators(ISkyrimModGetter mod, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        foreach (var item in mod.MiscItems.Where(m => m.FormKey.ModKey == mod.ModKey))
        {
            var scripted = item.VirtualMachineAdapter?.Scripts is { Count: > 0 };
            var name = item.Name?.String;
            var lever = UsedWhereItStands.IsMatch($"{name} {item.EditorID}");
            if (!scripted && !lever && !RecordFiles.Seat.IsMatch($"{name} {item.EditorID}")) continue;
            var id = item.EditorID ?? Session.FormKeyText(item.FormKey);
            var file = files.GetValueOrDefault(item.FormKey) ?? id;
            // A chair, a bench, a bed written as a misc item: eval round 144's furniture #5 built its Reading Chair so, to be picked up.
            if (!scripted && !lever)
            {
                var seat = file.StartsWith("MiscItems/", StringComparison.Ordinal)
                    ? $"one call moves this file there, {ToolCalls.Move(pluginDir, file, $"Furniture/{file["MiscItems/".Length..]}")}, and its reference in a cell keeps its Base; then"
                    : "move this file there with moveRecord, then";
                notes.Add($"{file}: {id}{(name is { Length: > 0 } ? $" \"{name}\"" : "")} is a MiscItem — a thing the player picks up and carries: " +
                    "activating one where it lies takes it into the inventory, and nobody sits on it. A chair, a bench, a bed — a thing sat or " +
                    "lain on — is Furniture, in Furniture/, as the game's WoodenChair01 has it (readRecord {\"record\": \"WoodenChair01\"}: its " +
                    $"Model, and its Markers, where and how the player sits): {seat} write it with Furniture's fields, its Markers as " +
                    "WoodenChair01's. Where an item to pick up and carry was meant, nothing to change, and this warning stays");
                continue;
            }
            var move = file.StartsWith("MiscItems/", StringComparison.Ordinal)
                ? $"one call moves this file there, {ToolCalls.Move(pluginDir, file, $"Activators/{file["MiscItems/".Length..]}")}, and its reference in a cell keeps its Base; then"
                : "move this file there with moveRecord, then";
            notes.Add($"{file}: {id}{(name is { Length: > 0 } ? $" \"{name}\"" : "")} is a MiscItem{(scripted ? " with a script" : "")} — a thing the player " +
                "picks up and carries: activating one where it lies takes it into the inventory. A lever, a button, a switch — a thing used " +
                "where it stands, that does something when pulled or pressed — is an Activator, in Activators/, as the game's NorLever01 has it " +
                $"(readRecord {{\"record\": \"NorLever01\"}}: its Model, its Name, its script by its VirtualMachineAdapter): {move} write it with " +
                "an Activator's fields, as NorLever01's. Where an item to pick up and carry was meant, nothing to change, and this warning stays");
        }
        return notes;
    }

    /// <summary>
    /// An effect of the project's ingredient, potion, spell, enchantment or scroll that names no magic effect — written `000000`, or
    /// a link that resolved to none: in game it does nothing. Eval round 151's ingredient+detailed #2 wrote two of its four
    /// effects `000000:Skyrim.esm`, and its build answered "no effect magnitude 20" in `built` alone.
    /// </summary>
    public static List<string> EmptyEffects(ISkyrimModGetter mod, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        foreach (var record in mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey))
        {
            IReadOnlyList<IEffectGetter>? effects = record switch
            {
                IIngredientGetter i => i.Effects, IIngestibleGetter p => p.Effects, ISpellGetter s => s.Effects, IObjectEffectGetter o => o.Effects,
                IScrollGetter sc => sc.Effects, _ => null
            };
            if (effects is null) continue;
            // `000000:Skyrim.esm` is no null link until the plugin is written and read back.
            var empty = effects.Select((e, i) => (e, i)).Where(x => x.e.BaseEffect.IsNull || x.e.BaseEffect.FormKey.ID == 0)
                .Select(x => $"Effects[{x.i}]").ToList();
            if (empty.Count == 0) continue;
            var id = record.EditorID ?? Session.FormKeyText(record.FormKey);
            var file = files.GetValueOrDefault(record.FormKey) ?? id;
            var kinds = record is IIngredientGetter or IIngestibleGetter
                ? "an ingredient's and a potion's are the game's Alch… effects, as AlchFortifyDestruction, AlchWeaknessFire, AlchResistFrost"
                : "a spell's are those the game's spells of its kind name";
            notes.Add($"{file}: {id}'s {string.Join(" and ", empty)} name{(empty.Count == 1 ? "s" : "")} no magic effect — written 000000, or a link " +
                $"to none — so in game {(empty.Count == 1 ? "it does" : "they do")} nothing. Write the magic effect meant as BaseEffect, by its " +
                $"editor ID — {kinds}; findRecords with types [\"MGEF\"] finds one — or delete {(empty.Count == 1 ? "that item" : "those items")}");
        }
        return notes;
    }

    /// <summary>The hues a light's name says it has — a frost light's blue — by the words in it.</summary>
    private static readonly (Regex Words, string[] Hues, string Said)[] NamedHues =
    [
        (new(@"(?i)blue|frost|ice|icy|cold|winter|snow|azure|cyan|teal|moon"), ["blue", "cyan", "purple", "white"], "cold, blue"),
        (new(@"(?i)fire|flame|ember|torch|candle|warm|hearth|lava|sun"), ["red", "orange", "yellow", "white"], "warm, orange"),
        (new(@"(?i)green|poison|venom|toxic|forest"), ["green", "cyan", "yellow"], "green"),
        (new(@"(?i)\bred\b|(?<=[a-z])Red(?![a-z])|blood|crimson"), ["red", "orange", "pink"], "red"),
        (new(@"(?i)purple|violet|shadow|void|arcane"), ["purple", "pink", "blue"], "purple")
    ];

    /// <summary>
    /// A light of the project's whose name says one hue — a frost light — and whose color is another: written `'#0000CC3F'` for
    /// blue, the alpha last, it builds green — the digits after # are read as 00, then red, green and blue. Eval rounds 151's light
    /// #3 and 152's light+detailed #2 each built a green "frost light" so, `built` saying green, and answered it blue.
    /// </summary>
    public static List<string> LightColors(ISkyrimModGetter mod, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        foreach (var light in mod.Lights.Where(l => files.ContainsKey(l.FormKey)))
        {
            var said = $"{light.EditorID} {light.Name?.String}";
            if (NamedHues.FirstOrDefault(h => h.Words.IsMatch(said)) is not { Words: not null } named) continue;
            var (r, g, b) = (light.Color.R, light.Color.G, light.Color.B);
            var word = BuiltRecords.ColorWord(r, g, b);
            if (named.Hues.Any(h => word.EndsWith(h, StringComparison.Ordinal)) || word is "black" or "gray") continue;
            var file = files[light.FormKey];
            var id = light.EditorID ?? Session.FormKeyText(light.FormKey);
            var written = Regex.Match(File.ReadAllText(Path.Combine(pluginDir, file)), @"^Color:[ \t]*['""]?(#?[0-9A-Fa-f]{6,8})", RegexOptions.Multiline);
            var (example, called) = named.Said == "cold, blue" ? ("#00A0C8FF", "a cold pale blue") : named.Said == "warm, orange" ? ("#00FFA040", "a warm orange")
                : named.Said == "green" ? ("#0040FF40", "green") : named.Said == "red" ? ("#00FF2020", "red") : ("#00A040FF", "purple");
            notes.Add($"{file}: {id} is named for a {named.Said} light, and its color is {word}: #{r:X2}{g:X2}{b:X2} (red, green, blue)" +
                (written.Success ? $", written `{written.Groups[1].Value}`" : "") + ". A light's color is written `'#00RRGGBB'` — the two digits " +
                $"after # unused, then red, green and blue — so the color it is named for is written like `'{example}'`, {called}; one call sets " +
                "it and builds: " + ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), file, $"{{\"path\": \"Color\", \"value\": {ToolCalls.Quoted(example)}}}", build: true) +
                ". Where the user asked for this color, nothing to change, and this warning stays");
        }
        return notes;
    }

    /// <summary>
    /// The effects of the project's ingredient, potion, spell, enchantment or scroll written by FormID alone — no editor ID, no
    /// comment naming one — each named with the call that writes it so. Written from memory, Fortify Destruction as 03EB25,
    /// AlchFortifyConjuration, three rounds of ingredient+detailed runs (eval rounds 150–152) built the wrong effect, `built` naming it.
    /// </summary>
    public static List<string> EffectsByFormId(ISkyrimModGetter mod, RecordIndex index, string pluginDir)
    {
        var files = EffectCasting.Files(pluginDir);
        var notes = new List<string>();
        foreach (var record in mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey && files.ContainsKey(r.FormKey)))
        {
            IReadOnlyList<IEffectGetter>? effects = record switch
            {
                IIngredientGetter i => i.Effects, IIngestibleGetter p => p.Effects, ISpellGetter s => s.Effects, IObjectEffectGetter o => o.Effects,
                IScrollGetter sc => sc.Effects, _ => null
            };
            if (effects is not { Count: > 0 }) continue;
            var file = files[record.FormKey];
            var lines = Regex.Matches(File.ReadAllText(Path.Combine(pluginDir, file)), @"^[ \t]*-?[ \t]*BaseEffect:[ \t]*(?<value>[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline)
                .Select(m => m.Groups["value"].Value).ToList();
            if (lines.Count != effects.Count) continue;
            var bare = new List<(int At, string Id)>();
            for (var i = 0; i < effects.Count; i++)
            {
                if (!Regex.IsMatch(lines[i], @"^['""]?[0-9A-Fa-f]{1,8}:[^\s#'""]+['""]?$") || effects[i].BaseEffect.IsNull
                    || !index.ByFormKey.TryGetValue(effects[i].BaseEffect.FormKey, out var entry) || entry.EditorId is null) continue;
                bare.Add((i, entry.EditorId));
            }
            if (bare.Count == 0) continue;
            var id = record.EditorID ?? Session.FormKeyText(record.FormKey);
            var set = string.Join(", ", bare.Select(b => $"{{\"path\": \"Effects[{b.At}].BaseEffect\", \"value\": {ToolCalls.Quoted(b.Id)}}}"));
            notes.Add($"{file}: {id}'s effects are written by FormID alone, which says nothing of what each is: " +
                string.Join(", ", bare.Select(b => $"Effects[{b.At}] is {b.Id} ({lines[b.At].Trim('\'', '"')})")) +
                ". Where each is the effect asked for, one call writes them by editor ID, as the build reads them, and builds: " +
                ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), file, set, build: true) +
                "; where one is not, write the editor ID of the one meant in its place — an ingredient's and a potion's are the game's " +
                "Alch… effects, as AlchFortifyDestruction; findRecords with types [\"MGEF\"] finds one");
        }
        return notes;
    }

    /// <summary>
    /// A copy of a game light that nothing in the plugin places: its change reaches every place the game has that light, and no
    /// room gets a light of the mod's own. Asked for a new light of the mod's own placed in Breezehome, eval rounds 150 and 151's
    /// light+detailed #3 each copied a game light with addPatch, changed its radius, placed nothing, and answered it made.
    /// </summary>
    public static List<string> GameLights(ISkyrimModGetter mod, string pluginDir)
    {
        var copies = mod.Lights.Where(l => l.FormKey.ModKey != mod.ModKey).ToList();
        if (copies.Count == 0) return [];
        var named = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();
        foreach (var record in mod.EnumerateMajorRecords())
            foreach (var link in record.EnumerateFormLinks())
                if (link.FormKey != record.FormKey) named.Add(link.FormKey);
        var files = EffectCasting.Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        var notes = new List<string>();
        foreach (var light in copies.Where(l => !named.Contains(l.FormKey)))
        {
            var id = light.EditorID ?? Session.FormKeyText(light.FormKey);
            var file = files.GetValueOrDefault(light.FormKey) ?? id;
            var own = Regex.Replace($"{project}{id}", "[^A-Za-z0-9_]", "");
            notes.Add($"{file}: overrides {id} of {light.FormKey.ModKey.FileName}, a light of the game's, and changes it wherever the game places " +
                $"it — and the plugin places no light: nothing in it names {id}. A light of the mod's own, standing in one room, is a new record " +
                "placed there: one call makes this copy a new record, keeping what it holds and what was changed in it, and builds — " +
                ToolCalls.Edit(project, file, $"{{\"path\": \"EditorID\", \"value\": {ToolCalls.Quoted(own)}}}", "\"FormKey\"", build: true) +
                $" — then a PlacedObject file in the room's Temporary/ folder places it: readRecord of the cell answers `newReference`, the path " +
                $"and the lines to write, with `Base: {own}`. Where the user asked for the game's {id} to change wherever it stands, nothing to " +
                "change, and this warning stays");
        }
        return notes;
    }

    /// <summary>
    /// For a new NPC placed nowhere: the load order's NPC of the same in-game name, and the game's leveled lists of NPCs of its
    /// race, with the calls that put it in one — or "" where the game has none of that name. Asked for wolves in the wild that can
    /// also be ice wolves, all 8 failed plain leveled-npc runs of eval rounds 144 and 149 wrote an "Ice Wolf" of their own,
    /// placed nowhere, where the game has EncWolfIce and its wolves come from LCharWolf.
    /// </summary>
    private static string GameNpcNamed(string? named, ILinkCache lookup, RecordIndex index, Mutagen.Bethesda.Plugins.ModKey own, string pluginDir, string file)
    {
        if (named is not { Length: > 0 } name) return "";
        var twins = index.ByFormKey.Values.Where(e => e.Type == "Npc" && e.FormKey.ModKey != own && e.EditorId is not null
            && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FormKey.ModKey.FileName == "Skyrim.esm" ? 0 : 1).ToList();
        // A unique one — Hulda, Lydia — is no NPC a leveled list brings, and a new NPC of a person's name is a person of the mod's own.
        if (twins.Count is 0 or > 3 || !lookup.TryResolve<INpcGetter>(twins[0].FormKey, out var twin)
            || twin.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique)) return "";
        var race = twin.Race.FormKey;
        var raceId = index.ByFormKey.TryGetValue(race, out var raceEntry) ? raceEntry.EditorId : null;
        // The game's lists of NPCs of that race alone, the twin not among them, most entries first.
        var lists = new List<(RecordIndex.Entry Entry, int Count)>();
        foreach (var entry in index.ByFormKey.Values.Where(e => e.Type == "LeveledNpc" && e.FormKey.ModKey != own && e.EditorId is not null))
        {
            if (!lookup.TryResolve<ILeveledNpcGetter>(entry.FormKey, out var list) || list.Entries is not { Count: > 0 } entries) continue;
            var held = entries.Select(e => e.Data?.Reference.FormKey).OfType<Mutagen.Bethesda.Plugins.FormKey>().ToList();
            if (held.Contains(twin.FormKey)) continue;
            var npcs = held.Select(k => lookup.TryResolve<INpcGetter>(k, out var n) ? n : null).ToList();
            if (npcs.Any(n => n is null || n.Race.FormKey != race)) continue;
            lists.Add((entry, entries.Count));
        }
        var shown = lists.OrderByDescending(l => l.Count).ThenBy(l => l.Entry.EditorId, StringComparer.Ordinal).Take(3).ToList();
        if (shown.Count == 0) return "";
        var twinId = twins[0].EditorId!;
        var project = ToolCalls.ProjectOf(pluginDir);
        var said = $"The game has an NPC of that name already: {string.Join(", ", twins.Select(t => $"{t.EditorId} ({Session.FormKeyText(t.FormKey)})"))}" +
            $"{(raceId is null ? "" : $", {RecordFiles.Article(raceId)}")}. Where the user asked for the game's {name} to turn up — among those a " +
            $"leveled list brings, as the game's animals and enemies come from one — that list is to hold {twinId}, not a new NPC: ";
        var first = shown[0].Entry;
        var copy = $"LeveledNpcs/{first.EditorId} - {Session.FormKeyText(first.FormKey).Replace(':', '_')}.yaml";
        return said + $"the game's lists of {raceId ?? "its race's"} NPCs are {string.Join(", ", shown.Select(l => $"{l.Entry.EditorId} ({Session.FormKeyText(l.Entry.FormKey)}, {l.Count} {(l.Count == 1 ? "entry" : "entries")})"))}. " +
            $"For {first.EditorId}: addPatch {{\"project\": {ToolCalls.Quoted(project)}, \"records\": [\"{Session.FormKeyText(first.FormKey)}\"]}}, then one call adds " +
            $"{twinId} after its {shown[0].Count} entries and builds: " +
            ToolCalls.Edit(project, copy, $"{{\"path\": \"Entries[{shown[0].Count}]\", \"value\": {{\"Data\": {{\"Level\": 1, \"Reference\": {ToolCalls.Quoted(twinId)}, \"Count\": 1}}}}}}", build: true) +
            $"; and this file is left over: {ToolCalls.Delete(pluginDir, file)}. ";
    }

    /// <summary>
    /// The load order's list of the same type whose entries a new list holds all of — its copy made a new record — or null:
    /// two entries or more, the new list holding at most three more.
    /// </summary>
    private static RecordIndex.Entry? GameListLike(IMajorRecordGetter list, ILinkCache lookup, RecordIndex index, Mutagen.Bethesda.Plugins.ModKey own)
    {
        var held = EntriesOf(list);
        if (held.Count < 2) return null;
        var type = RecordIndex.TypeName(list);
        RecordIndex.Entry? best = null;
        var bestCount = 0;
        foreach (var entry in index.ByFormKey.Values.Where(e => e.Type == type && e.FormKey.ModKey != own))
        {
            if (!lookup.TryResolve(entry.FormKey, list.Registration.GetterType, out var game)) continue;
            var theirs = EntriesOf(game);
            if (theirs.Count < 2 || held.Count > theirs.Count + 3 || !theirs.All(held.Contains)) continue;
            if (theirs.Count > bestCount) (best, bestCount) = (entry, theirs.Count);
        }
        return best;
    }

    private static HashSet<Mutagen.Bethesda.Plugins.FormKey> EntriesOf(IMajorRecordGetter list) => list switch
    {
        ILeveledItemGetter items => items.Entries?.Select(e => e.Data?.Reference.FormKey).OfType<Mutagen.Bethesda.Plugins.FormKey>().ToHashSet() ?? [],
        ILeveledNpcGetter npcs => npcs.Entries?.Select(e => e.Data?.Reference.FormKey).OfType<Mutagen.Bethesda.Plugins.FormKey>().ToHashSet() ?? [],
        ILeveledSpellGetter spells => spells.Entries?.Select(e => e.Data?.Reference.FormKey).OfType<Mutagen.Bethesda.Plugins.FormKey>().ToHashSet() ?? [],
        _ => []
    };

    /// <summary>The kinds of record a player carries, and so a recipe's material may be.</summary>
    private static readonly HashSet<string> CarriedKinds = new(["Ingestible", "Ingredient", "MiscItem", "Weapon", "Armor", "Ammunition", "Book",
        "SoulGem", "Key", "Scroll"], StringComparer.Ordinal);

    /// <summary>
    /// A recipe of the project's whose material is a new record of the project's with the in-game name of an item of the load
    /// order's, of any kind: the player carries the game's, and nothing gives the copy, so the recipe is never made. Asked for a
    /// soup of two potatoes and a salt pile, 4 of eval round 144's 5 plain cooking runs made a Potato and a Salt Pile of their
    /// own — as ingredients, where the game's potato is FoodPotato, an Ingestible — and built them with no word.
    /// </summary>
    private static List<string> CopiedMaterials(ISkyrimModGetter mod, RecordIndex index, string pluginDir,
        IReadOnlyDictionary<Mutagen.Bethesda.Plugins.FormKey, IMajorRecordGetter> records, IReadOnlyDictionary<Mutagen.Bethesda.Plugins.FormKey, string> files)
    {
        var notes = new List<string>();
        foreach (var recipe in mod.ConstructibleObjects.Where(c => c.FormKey.ModKey == mod.ModKey && c.Items is not null))
        {
            if (files.GetValueOrDefault(recipe.FormKey) is not { } relative) continue;
            for (var at = 0; at < recipe.Items!.Count; at++)
            {
                if (!records.TryGetValue(recipe.Items[at].Item.Item.FormKey, out var material) || (material as INamedGetter)?.Name is not { Length: > 0 } name)
                    continue;
                var game = index.ByFormKey.Values.Where(e => CarriedKinds.Contains(e.Type) && e.FormKey.ModKey != mod.ModKey && !e.Unplayable
                        && e.EditorId is not null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Type == RecordIndex.TypeName(material) ? 0 : 1).FirstOrDefault();
                if (game is null) continue;
                var copy = material.EditorID ?? Session.FormKeyText(material.FormKey);
                var copyFile = files.GetValueOrDefault(material.FormKey);
                notes.Add($"{relative}: {recipe.EditorID ?? Session.FormKeyText(recipe.FormKey)} takes {copy} \"{name}\", a new {RecordIndex.TypeName(material)} " +
                    $"of this mod's own, as a material — the player carries the game's {game.EditorId} (\"{game.Name}\", {RecordFiles.Article(game.Type)}, " +
                    $"{Session.FormKeyText(game.FormKey)}), and nothing gives the copy, so the recipe is never made. One call makes it take the game's " +
                    "and builds again: " +
                    ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, $"{{\"path\": \"Items[{at}].Item.Item\", \"value\": {ToolCalls.Quoted(game.EditorId!)}}}", build: true) +
                    (copyFile is null ? "" : $"; {copyFile} is then named by nothing: delete it, which builds again too, {ToolCalls.Delete(pluginDir, copyFile, build: true)}") +
                    ". Only where the mod gives the player its own — a material of its own beside the game's — is the copy meant; then nothing to " +
                    "change, and this warning stays");
            }
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
