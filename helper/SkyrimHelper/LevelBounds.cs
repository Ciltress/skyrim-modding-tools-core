using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// A new NPC's level bounds beside a fixed level, as a note beside a build that succeeds: CalcMinLevel and CalcMaxLevel bound
/// a level that follows the player's, and beside a fixed one the game reads neither. Asked for a Hilda "whose level follows
/// the player's between 5 and 30", a model wrote the two bounds and no Level line — a fixed level of 0 — and answered that her
/// level follows the player's between 5 and 30, the build's `built` line reading "level 0" (eval round 103's npc+detailed #1
/// and #5). A copy of one of the game's own has it too: 467 of the 2,002 NPCs of a fixed level in its five masters write bounds
/// beside it, as EncDremoraWarlock06 (level 46, 7 to 45). And a level that follows the
/// player's at 0 times it, which holds the NPC at its lowest level; and a fixed level of 0 with no bounds, a file written with
/// no level at all.
/// </summary>
internal static class LevelBounds
{
    private const string FollowsPlayer = "{\"path\": \"Configuration.Level\", \"value\": {\"MutagenObjectType\": \"PcLevelMult\", \"LevelMult\": 1}}";
    private const string Bounds = "\"Configuration.CalcMinLevel\", \"Configuration.CalcMaxLevel\"";
    private const string LowestAndHighest = "{\"path\": \"Configuration.CalcMinLevel\", \"value\": 1}, {\"path\": \"Configuration.CalcMaxLevel\", \"value\": 0}";
    private const string FixedLevel = "{\"path\": \"Configuration.Level\", \"value\": {\"MutagenObjectType\": \"NpcLevel\", \"Level\": 10}}";
    private static readonly Regex AnyLevelLine = new(@"^[ \t]*Level[ \t]*:", RegexOptions.Multiline);

    public static List<string> Notes(ISkyrimModGetter mod, string pluginDir)
    {
        var notes = new List<string>();
        var npcs = mod.Npcs.Where(n => n.FormKey.ModKey == mod.ModKey).ToList();
        if (npcs.Count == 0) return notes;
        var files = EffectCasting.Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        foreach (var npc in npcs)
        {
            var configuration = npc.Configuration;
            // One whose template gives its stats has the template's level.
            if (!npc.Template.IsNull && configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Stats)) continue;
            if (files.GetValueOrDefault(npc.FormKey) is not { } relative) continue;
            // A level that follows the player's at 0 times it: told to delete its `Mult: 1`, a model built Hilda "level 0 × the
            // player's, 5 to 25" — level 5 for good — and answered her as made (eval round 114's npc #4).
            if (configuration.Level is IPcLevelMultGetter { LevelMult: <= 0 } nothing)
            {
                notes.Add($"{relative}: {npc.EditorID ?? Session.FormKeyText(npc.FormKey)}'s level follows the player's at {nothing.LevelMult} " +
                    $"times it, so it never rises above its CalcMinLevel {configuration.CalcMinLevel} — LevelMult is the multiple, 1 the player's " +
                    "own level. One call makes it follow the player's and builds again: " +
                    ToolCalls.Edit(project, relative, "{\"path\": \"Configuration.Level.LevelMult\", \"value\": 1}", build: true));
                continue;
            }
            if (configuration.Level is not INpcLevelGetter level) continue;
            if (configuration.CalcMinLevel == 0 && configuration.CalcMaxLevel == 0)
            {
                if (level.Level != 0) continue;
                // A fixed level of 0 and no bounds: asked for a Hilda "whose level follows the player's between 5 and 30", a model
                // wrote her with no Level, CalcMinLevel or CalcMaxLevel line, the build's `built` line read "level 0", and it
                // answered her as asked (eval round 121's npc+detailed #3). Of the 2,002 NPCs of a fixed level in the game's five
                // masters, 3 are at 0, each beside bounds, which the note below takes (2,376 and the same 3 in its 80 plugins).
                var unwritten = !AnyLevelLine.IsMatch(File.ReadAllText(Path.Combine(pluginDir, relative)));
                notes.Add($"{relative}: {npc.EditorID ?? Session.FormKeyText(npc.FormKey)}'s level is fixed at 0" +
                    (unwritten ? " — its file has no `Level` line" : "") + ", and of the game's 2,002 NPCs of a fixed level, 3 are at 0. " +
                    "Where the user asked for a level that follows the player's, one call makes it follow it and builds again: " +
                    ToolCalls.Edit(project, relative, FollowsPlayer + ", " + LowestAndHighest, build: true) +
                    " — the lowest and highest level the user named in place of 1 and 0, if they named them (a CalcMaxLevel of 0 is no " +
                    "highest); where the user asked for a fixed level, one call sets it and builds again: " +
                    ToolCalls.Edit(project, relative, FixedLevel, build: true) + " — the level the user named in place of 10. Where the " +
                    "user named no level, the first call as it stands.");
                continue;
            }
            notes.Add($"{relative}: {npc.EditorID ?? Session.FormKeyText(npc.FormKey)}'s level is fixed at {level.Level}, so its " +
                $"CalcMinLevel {configuration.CalcMinLevel} and CalcMaxLevel {configuration.CalcMaxLevel} do nothing — they bound a level " +
                "that follows the player's. One call makes it follow the player's between them and builds again: " +
                ToolCalls.Edit(project, relative, FollowsPlayer, build: true) +
                "; where the user asked for a fixed level, one call removes the two instead: " +
                ToolCalls.Edit(project, relative, "", Bounds, build: true));
        }
        return notes;
    }

    /// <summary>
    /// A call that makes an NPC's level follow the player's and removes its bounds in one: the note above gives the two as one
    /// call or the other, and a model made both — Hilda's level then followed the player's from 1 to no limit, where the task
    /// asked 5 to 30 (eval round 106's npc+detailed #2, which failed). Refused with each call alone; null for any other call.
    /// </summary>
    public static string? BothCalls(string pluginDir, string relative, string type, JsonObject? set, JsonArray? remove)
    {
        if (type != "Npc" || set is null || remove is null) return null;
        var follows = set.Any(f => Regex.IsMatch(f.Key, @"^(?:Configuration\.)?Level$", RegexOptions.IgnoreCase)
            && (f.Value?.ToJsonString() ?? "").Contains("PcLevelMult", StringComparison.OrdinalIgnoreCase));
        var bounds = remove.Select(r => r?.GetValue<string>() ?? "")
            .Where(p => Regex.IsMatch(p, @"^(?:Configuration\.)?Calc(?:Min|Max)Level$", RegexOptions.IgnoreCase)).ToList();
        if (!follows || bounds.Count == 0) return null;
        var project = ToolCalls.ProjectOf(pluginDir);
        return $"This call makes the level follow the player's and removes {string.Join(" and ", bounds)}, the bounds that level keeps " +
            "to — so it would go from 1 to no limit. The two are one call or the other: where the user asked for a level that follows " +
            "the player's, set it alone and the bounds stay: " + ToolCalls.Edit(project, relative, FollowsPlayer, build: true) +
            "; where the user asked for a fixed level, remove the bounds alone: " + ToolCalls.Edit(project, relative, "", Bounds, build: true) +
            ". Nothing was written.";
    }
}
