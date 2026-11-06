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
            if (own.Length == 0 || own.Equals(translation.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
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
