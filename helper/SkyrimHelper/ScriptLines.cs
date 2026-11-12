using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SkyrimHelper;

/// <summary>
/// A record's scripts written as a script is attached, where what was written can mean one thing only: under
/// `VirtualMachineAdapter:`, `Scripts:` a list of scripts, each `- Name: <script>` with its `Properties:` below it.
/// </summary>
public static class ScriptLines
{
    /// <summary>The names a script's own name was written under, in an item of Scripts or beside it.</summary>
    private static readonly string[] NameKeys = ["Name", "ScriptName", "Scriptname", "Script", "ScriptFile"];

    /// <summary>
    /// The script block of each record file, written anew where a script was named as `Scripts: X`, as a list of names, as
    /// `Scriptname:` in its item or `ScriptName:` beside Scripts, or with its Properties beside Scripts where there is one script:
    /// eval round 152's activator+detailed #3 wrote `Scripts: GoldGrantScript` with Properties beside it, then `- Scriptname:`,
    /// was refused each way three times, and ran out of turns with its lever unbuilt.
    /// </summary>
    public static List<(string File, string Change)> Attached(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, _) in RecordFiles.FlatRecords(pluginDir))
        {
            var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n').ToList();
            var at = lines.FindIndex(l => Regex.IsMatch(l, @"^VirtualMachineAdapter:[ \t]*$"));
            if (at < 0) continue;
            var count = RecordFiles.BlockLength(lines, at);
            YamlNode? block;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(string.Join('\n', lines.Skip(at).Take(count))));
                block = ((YamlMappingNode)stream.Documents[0].RootNode).Children.Values.FirstOrDefault();
            }
            catch (Exception e) when (e is YamlException or InvalidCastException) { continue; }
            if (block is null || RecordEdits.FromYaml(block) is not JsonObject adapter) continue;

            var said = new List<string>();
            // A script named beside Scripts, or in its place: `ScriptName: X` with no Scripts.
            if (adapter["Scripts"] is null && NameKeys.Skip(1).FirstOrDefault(k => adapter[k] is JsonValue) is { } beside)
            {
                adapter["Scripts"] = adapter[beside]!.ToString();
                adapter.Remove(beside);
                said.Add($"`{beside}:` as its script");
            }
            // One name, or a list of names.
            if (adapter["Scripts"] is JsonValue one && one.TryGetValue<string>(out var single))
            {
                adapter["Scripts"] = new JsonArray(new JsonObject { ["Name"] = single });
                said.Add($"`Scripts: {single}` as the one script it names, `- Name: {single}`");
            }
            else if (adapter["Scripts"] is JsonArray listed && listed.Any(s => s is JsonValue))
            {
                adapter["Scripts"] = new JsonArray(listed.Select(s => s is JsonValue v ? new JsonObject { ["Name"] = v.ToString() } : s?.DeepClone()).ToArray());
                said.Add("each script named alone as an item `- Name: <script>`");
            }
            if (adapter["Scripts"] is not JsonArray scripts) continue;
            // An item's name under another key: Name first, as the build writes it.
            for (var i = 0; i < scripts.Count; i++)
            {
                if (scripts[i] is not JsonObject script || script["Name"] is not null
                    || NameKeys.Skip(1).FirstOrDefault(k => script[k] is JsonValue) is not { } other) continue;
                var renamed = new JsonObject { ["Name"] = script[other]!.DeepClone() };
                foreach (var (key, value) in script) if (key != other) renamed[key] = value?.DeepClone();
                scripts[i] = renamed;
                said.Add($"`{other}:` of its script as `Name:`");
            }
            // Properties beside Scripts, of its one script.
            if (adapter["Properties"] is JsonArray properties && scripts is [JsonObject only] && only["Properties"] is null)
            {
                only["Properties"] = properties.DeepClone();
                adapter.Remove("Properties");
                said.Add("`Properties:`, beside Scripts, under its one script");
            }
            if (said.Count == 0) continue;
            lines.RemoveRange(at, count);
            lines.InsertRange(at, RecordEdits.Render("VirtualMachineAdapter", adapter, 0));
            File.WriteAllText(file, string.Join('\n', lines));
            changes.Add((relative, $"wrote {string.Join("; ", said)} — a script is attached as an item of `Scripts:` under " +
                "`VirtualMachineAdapter:`, `- Name: <the script>` and its `Properties:` below it, as readRecord shows the game's levers"));
        }
        return changes;
    }
}
