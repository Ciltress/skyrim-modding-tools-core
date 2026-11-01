using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// What a record of the project needs to be anything in game, beside what its kind carries (KindParts): an NPC its race,
/// refused without one — an actor of no race has no body — and a leveled list its entries, each giving one or more, as
/// notes. Each built with no word (refusal probes of 2026-10-31, P22, L13, L14).
/// </summary>
internal static class RecordNeeds
{
    /// <summary>The project's new NPCs with no race and no template that gives them one, as refusals naming the race to give.</summary>
    public static List<string> Raceless(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var raceless = mod.Npcs.Where(n => n.FormKey.ModKey == mod.ModKey && n.Race.IsNull
            && !(!n.Template.IsNull && n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))).ToList();
        if (raceless.Count == 0) return [];
        var npcs = index.ByFormKey.Values.Where(e => e.Signature == "NPC_" && e.FormKey.ModKey != mod.ModKey)
            .Select(e => lookup.TryResolve<INpcGetter>(e.FormKey, out var npc) ? npc : null)
            .OfType<INpcGetter>().Where(n => !n.Race.IsNull).ToList();
        string Named(FormKey key) => index.ByFormKey.TryGetValue(key, out var entry) && entry.EditorId is { } id ? id : Session.FormKeyText(key);
        var files = EffectCasting.Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        var problems = new List<string>();
        foreach (var npc in raceless)
        {
            var name = npc.EditorID ?? Session.FormKeyText(npc.FormKey);
            var relative = files.GetValueOrDefault(npc.FormKey);
            // The races the load order's NPCs of its voice have most often: a FemaleNord voice is a Nord's, a FemaleEvenToned one
            // any race's — the user's word names the race, these only the likely ones.
            var voiced = npc.Voice.IsNull ? [] : npcs.Where(n => n.Voice.FormKey == npc.Voice.FormKey).ToList();
            var among = voiced.Count > 0 ? voiced : npcs;
            var races = among.GroupBy(n => n.Race.FormKey).OrderByDescending(g => g.Count()).Take(3).ToList();
            var said = $"{relative ?? name}: {name} has no Race — an NPC takes its body, height, skills and voices from its race, and one " +
                "with none has no body in game";
            if (races.Count == 0 || relative is null)
            {
                problems.Add(said + ": write `Race:` with the race meant, as readRecord shows an NPC's");
                continue;
            }
            var first = Named(races[0].Key);
            var whose = voiced.Count > 0 ? $"the load order's NPCs with its voice, {Named(npc.Voice.FormKey)}," : "the load order's NPCs";
            problems.Add(said + ". One call gives it a race and builds again: " +
                ToolCalls.Edit(project, relative, $"{{\"path\": \"Race\", \"value\": {ToolCalls.Quoted(first)}}}", build: true) +
                $" — with the race the user asked for in place of {first}; {whose} are most often " +
                string.Join(", ", races.Select(r => $"{Named(r.Key)} ({r.Count()} of {among.Count})")));
        }
        return problems;
    }

    /// <summary>
    /// The project's leveled lists that give nothing: a new one with no entries, and an entry of Count 0 in any of them, as
    /// notes. A list of the load order emptied by its copy is a way to take what it gives out of the game, and is left alone.
    /// </summary>
    public static List<string> ListNotes(ISkyrimModGetter mod, string pluginDir)
    {
        var lists = mod.LeveledItems.Select(l => (Record: (IMajorRecordGetter)l, Counts: l.Entries?.Select(e => (int?)e.Data?.Count).ToList()))
            .Concat(mod.LeveledNpcs.Select(l => ((IMajorRecordGetter)l, l.Entries?.Select(e => (int?)e.Data?.Count).ToList())))
            .Concat(mod.LeveledSpells.Select(l => ((IMajorRecordGetter)l, l.Entries?.Select(e => (int?)e.Data?.Count).ToList())))
            .ToList();
        if (lists.Count == 0) return [];
        var files = EffectCasting.Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        var notes = new List<string>();
        foreach (var (record, counts) in lists)
        {
            if (files.GetValueOrDefault(record.FormKey) is not { } relative) continue;
            var name = record.EditorID ?? Session.FormKeyText(record.FormKey);
            if ((counts?.Count ?? 0) == 0)
            {
                if (record.FormKey.ModKey == mod.ModKey)
                    notes.Add($"{relative}: {name} has no Entries, so it gives nothing in game — write what it gives under Entries, each " +
                        "`- Data:` with its Level, Reference and Count below it, as readRecord {\"record\": \"LItemGems75\"} shows a list's");
                continue;
            }
            var empty = counts!.Select((count, i) => (count, i)).Where(c => c.count == 0).Select(c => c.i).ToList();
            if (empty.Count == 0) continue;
            notes.Add($"{relative}: {name}'s {Items(empty)} {(empty.Count == 1 ? "has" : "have")} Count 0, which gives nothing — one call gives " +
                $"{(empty.Count == 1 ? "it" : "each")} one, and builds again: " +
                ToolCalls.Edit(project, relative, string.Join(", ", empty.Select(i => $"{{\"path\": \"Entries[{i}].Data.Count\", \"value\": 1}}")), build: true));
        }
        return notes;
    }

    private static string Items(List<int> items) =>
        items.Count == 1 ? $"Entries[{items[0]}]" : $"Entries {string.Join(", ", items.Take(items.Count - 1).Select(i => $"[{i}]"))} and [{items[^1]}]";
}
