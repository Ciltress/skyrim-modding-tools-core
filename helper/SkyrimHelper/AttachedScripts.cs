using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// The Papyrus scripts the project's records attach, each with the properties the record fills, for the server to hold
/// against the scripts' sources and the game's scripts (scriptChecks.ts): a script the game does not have, one that does
/// not extend what its record is, an event the game never sends, a property filled that the script does not declare or
/// declared that nothing fills. Each builds without a word and does nothing in game. Eval round 126's quest #4 attached
/// StarterGoldScript and wrote no script; round 125's quest+detailed #2 gave its gold in OnQuestInit, which the game never
/// sends; its #5 filled GoldCoins, declared as a variable. A quest's aliases are listed too, each under its alias's name.
/// </summary>
internal static class AttachedScripts
{
    public static JsonArray List(ISkyrimModGetter mod, string pluginDir)
    {
        var listed = new JsonArray();
        var files = EffectCasting.Files(pluginDir);
        foreach (var record in mod.EnumerateMajorRecords())
        {
            if (record is not IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } adapter }) continue;
            if (files.GetValueOrDefault(record.FormKey) is not { } relative) continue;
            var scripts = Scripts(adapter.Scripts);
            var aliases = new JsonArray();
            if (adapter is IQuestAdapterGetter quest)
                foreach (var alias in quest.Aliases)
                    if (alias.Scripts.Count > 0)
                        aliases.Add(new JsonObject { ["alias"] = alias.Property.Alias, ["scripts"] = Scripts(alias.Scripts) });
            if (scripts.Count == 0 && aliases.Count == 0) continue;
            listed.Add(new JsonObject
            {
                ["file"] = relative.Replace('\\', '/'),
                ["editorId"] = record.EditorID ?? Session.FormKeyText(record.FormKey),
                ["type"] = RecordIndex.TypeName(record),
                ["scripts"] = scripts,
                ["aliases"] = aliases
            });
        }
        return listed;
    }

    /// <summary>Each script by its name, with the properties filled: each by its name, its kind, and for an object whether it names one.</summary>
    private static JsonArray Scripts(IReadOnlyList<IScriptEntryGetter> scripts) => new(scripts
        .Where(s => !string.IsNullOrWhiteSpace(s.Name))
        .Select(s => (JsonNode)new JsonObject
        {
            ["name"] = s.Name,
            ["properties"] = new JsonArray(s.Properties.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name,
                ["kind"] = p.GetType().Name.Replace("BinaryOverlay", "").Replace("Script", "").Replace("Property", ""),
                ["filled"] = p is not IScriptObjectPropertyGetter o || !o.Object.IsNull
            }).ToArray())
        }).ToArray());
}
