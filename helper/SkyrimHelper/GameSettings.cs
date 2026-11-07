using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// Game settings: a setting's kind, which its name says — fJumpHeightMin is a float, iLevelUp… an integer — and a project's
/// setting the game does not have, which nothing reads.
/// </summary>
internal static class GameSettings
{
    /// <summary>The kind a setting's first letter names, as the game reads its value.</summary>
    private static readonly Dictionary<char, string> ByLetter = new()
    {
        ['f'] = "GameSettingFloat", ['i'] = "GameSettingInt", ['s'] = "GameSettingString", ['b'] = "GameSettingBool"
    };

    /// <summary>The game's executable, as the core names it at its start (Session.GameVersion); null before.</summary>
    public static string? GameExe { get; set; }

    private static byte[]? exeBytes;

    /// <summary>
    /// Whether the engine knows a setting of this name: its executable holds every name it reads, as a string of its own — the
    /// game's plugins carry only some (iTrainingNumAllowedPerLevel, fXPPerSkillRank are in none, and a plugin's record of the
    /// name changes them). Null where the executable is not known.
    /// </summary>
    public static bool? EngineKnows(string name)
    {
        if (GameExe is null || !File.Exists(GameExe)) return null;
        exeBytes ??= File.ReadAllBytes(GameExe);
        var pattern = new byte[name.Length + 2];
        System.Text.Encoding.ASCII.GetBytes(name, 0, name.Length, pattern, 1);
        return exeBytes.AsSpan().IndexOf(pattern) >= 0;
    }

    private static readonly Regex KindLine = new(@"^MutagenObjectType:[ \t]*(?<kind>\w+)[ \t]*$", RegexOptions.Multiline);

    /// <summary>
    /// Each setting file's kind made the one the game reads it as: an override's the game's record's, a new one's its name's first
    /// letter's. An integer written for fJumpHeightMin built, and the game would read its four bytes as a float (2026-11-07).
    /// </summary>
    public static List<(string File, string Change)> Kinds(string pluginDir, RecordIndex index)
    {
        var changes = new List<(string File, string Change)>();
        var own = RecordFiles.OwnPlugin(pluginDir);
        foreach (var (file, relative, _) in RecordFiles.FlatRecords(pluginDir).Where(f => f.Relative.StartsWith("GameSettings/", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            if (KindLine.Match(text) is not { Success: true } line || RecordFiles.Identity(text) is not { EditorId: { Length: > 1 } editorId } identity) continue;
            var written = line.Groups["kind"].Value;
            var game = identity.FormKey is { } key && key.ModKey != own && index.ByFormKey.TryGetValue(key, out var entry) ? entry.Type : null;
            var meant = game ?? (ByLetter.TryGetValue(editorId[0], out var byName) && char.IsUpper(editorId[1]) ? byName : null);
            if (meant is null || meant == written || !written.StartsWith("GameSetting", StringComparison.Ordinal)) continue;
            File.WriteAllText(file, text[..line.Groups["kind"].Index] + meant + text[(line.Groups["kind"].Index + line.Groups["kind"].Length)..]);
            changes.Add((relative, $"wrote `MutagenObjectType: {meant}` for `{written}` — {editorId} is " +
                (game is not null ? $"the game's {meant}" : $"named as a {meant[11..].ToLowerInvariant()} setting, its first letter says so") +
                ", and the game reads its value as one"));
        }
        return changes;
    }

    /// <summary>
    /// The project's new game settings: the game reads only its own, by their names, so a new one does nothing unless a script
    /// or an SKSE plugin asks for it — each warned with the game's settings closest to its name. A new fMyJumpHeight built as
    /// a plain success, the jump unchanged (2026-11-07).
    /// </summary>
    public static List<string> Notes(ISkyrimModGetter mod, RecordIndex index, string pluginDir)
    {
        var notes = new List<string>();
        var files = RecordFiles.FlatRecords(pluginDir).Where(f => f.Relative.StartsWith("GameSettings/", StringComparison.Ordinal))
            .Select(f => (f.Relative, RecordFiles.Identity(File.ReadAllText(f.File)).FormKey)).Where(f => f.FormKey is not null)
            .ToDictionary(f => f.FormKey!.Value, f => f.Relative);
        foreach (var setting in mod.GameSettings.Where(s => s.FormKey.ModKey == mod.ModKey))
        {
            if (setting.EditorID is not { } id || !files.TryGetValue(setting.FormKey, out var relative)) continue;
            // A setting of the engine's own that no plugin carries is changed by a new record of its name: the way to do so.
            if (EngineKnows(id) is not false) continue;
            var closest = Closest(id, index);
            notes.Add($"{relative}: {id} is no setting of the game — neither a plugin of the load order nor the game itself has one of " +
                "that name: the game reads its settings by their names, and one of a name it does not know only a script or an SKSE plugin " +
                "that asks for it by name. " + (closest.Count > 0
                    ? $"The game's closest to its name: {string.Join(", ", closest)} — where one of those was meant, copy it in with addPatch " +
                      $"{{\"project\": {ToolCalls.Quoted(ToolCalls.ProjectOf(pluginDir))}, \"records\": [\"{closest[0]}\"]}}, change its Data, and delete this file. "
                    : "") + "Where a script or a plugin of the mod reads this one, nothing to change, and this warning stays");
        }
        return notes;
    }

    /// <summary>The game's settings sharing the most words of a name — fMyJumpHeight: fJumpHeightMin, fJumpHeightMinSE… — at most five.</summary>
    private static List<string> Closest(string name, RecordIndex index)
    {
        static HashSet<string> Words(string id) => Regex.Matches(id.Length > 1 ? id[1..] : id, @"[A-Z][a-z]+|[A-Z]+(?![a-z])|\d+")
            .Select(m => m.Value.ToLowerInvariant()).ToHashSet();
        var words = Words(name);
        if (words.Count == 0) return [];
        return index.ByFormKey.Values.Where(e => e.Type.StartsWith("GameSetting", StringComparison.Ordinal) && e.EditorId is not null
                && !e.EditorId.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(e => (Id: e.EditorId!, Shared: Words(e.EditorId!).Count(words.Contains)))
            .Where(e => e.Shared > 0).OrderByDescending(e => e.Shared).ThenBy(e => e.Id.Length).Take(5).Select(e => e.Id).ToList();
    }
}
