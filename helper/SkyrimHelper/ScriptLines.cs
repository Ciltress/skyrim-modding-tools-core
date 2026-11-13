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
            // Each property of the kind Mutagen names for what it holds.
            var kinded = false;
            foreach (var script in scripts.OfType<JsonObject>())
            {
                if (script["Properties"] is not JsonArray props) continue;
                for (var i = 0; i < props.Count; i++)
                {
                    if (props[i] is not JsonObject property || Kinded(property) is not { } mended) continue;
                    props[i] = mended.Property;
                    said.Add(mended.Said);
                    kinded = true;
                }
            }
            if (said.Count == 0) continue;
            lines.RemoveRange(at, count);
            lines.InsertRange(at, RecordEdits.Render("VirtualMachineAdapter", adapter, 0));
            File.WriteAllText(file, string.Join('\n', lines));
            changes.Add((relative, $"wrote {string.Join("; ", said)} — a script is attached as an item of `Scripts:` under " +
                "`VirtualMachineAdapter:`, `- Name: <the script>` and its `Properties:` below it, as readRecord shows the game's levers" +
                (kinded ? "; a property is `- MutagenObjectType:` the kind Mutagen names for what it holds — ScriptObjectProperty and " +
                    "`Object:` for a record (a Message, an Actor, a MiscItem…), ScriptIntProperty, ScriptFloatProperty, ScriptBoolProperty " +
                    "or ScriptStringProperty and `Data:` for a value — then `Name:`; its Papyrus type is the script's own line" : "")));
        }
        return changes;
    }

    /// <summary>The kinds of a script's property, as Mutagen names them.</summary>
    private static readonly HashSet<string> PropertyKinds = new(["ScriptObjectProperty", "ScriptIntProperty", "ScriptFloatProperty",
        "ScriptBoolProperty", "ScriptStringProperty", "ScriptObjectListProperty", "ScriptIntListProperty", "ScriptFloatListProperty",
        "ScriptBoolListProperty", "ScriptStringListProperty"], StringComparer.Ordinal);

    /// <summary>The fields a property of any kind has.</summary>
    private static readonly HashSet<string> PropertyFields = new(["MutagenObjectType", "Name", "Flags", "Object", "Alias", "Unused", "Data",
        "Objects"], StringComparer.Ordinal);

    /// <summary>A kind written as Papyrus's value type, or Mutagen's without its words: `Int`, `IntProperty`, `ScriptBool`.</summary>
    private static readonly Regex ValueKind = new(@"^(?:Script)?(?<type>Int|Integer|Float|Bool|Boolean|String)(?:Property)?$", RegexOptions.IgnoreCase);

    /// <summary>
    /// A property written with a kind Mutagen has not, or none, made the kind its value says: `Object:` a record's, `Data:` a value's
    /// by the kind written or the value itself; `Value:` read as either; a property written as its name and value alone,
    /// `- WelcomeMessage: WelcomeMessageMsg`, given `Name:`. Eval round 144's message runs wrote `MutagenObjectType: MessageProperty`,
    /// Papyrus's type, were told the Object was "a Quest's, under Data in each item of DialogConditions", and 4 of 10 answered unbuilt.
    /// Null where the property is Mutagen's already or says nothing of what it holds.
    /// </summary>
    private static (JsonObject Property, string Said)? Kinded(JsonObject property)
    {
        var written = property["MutagenObjectType"] is JsonValue k ? k.ToString() : null;
        if (written is not null && PropertyKinds.Contains(written)) return null;
        var name = property["Name"]?.DeepClone();
        var rest = property.Where(f => !PropertyFields.Contains(f.Key)).ToList();
        JsonNode? held = null;
        var from = "";
        if (property["Object"] is { } linked) (held, from) = (linked, "Object");
        else if (property["Objects"] is JsonArray) (held, from) = (property["Objects"], "Objects");
        else if (property["Data"] is { } data) (held, from) = (data, "Data");
        else if (property["Value"] is JsonValue value) (held, from) = (value, "Value");
        // `- WelcomeMessage: WelcomeMessageMsg`: one field, none a property's, its name the property's.
        else if (name is null && rest is [(var key, JsonValue alone)])
        {
            name = JsonValue.Create(key);
            (held, from) = (alone, key);
        }
        if (held is null) return null;

        string kind;
        if (from == "Objects") kind = "ScriptObjectListProperty";
        else if (from == "Object") kind = "ScriptObjectProperty";
        else if (held is not JsonValue scalar) return null;
        else if (written is not null && ValueKind.Match(written) is { Success: true } said)
            kind = said.Groups["type"].Value.ToLowerInvariant() switch
            {
                "int" or "integer" => "ScriptIntProperty", "float" => "ScriptFloatProperty", "bool" or "boolean" => "ScriptBoolProperty", _ => "ScriptStringProperty"
            };
        else if (scalar.TryGetValue<bool>(out _)) kind = "ScriptBoolProperty";
        else if (scalar.TryGetValue<long>(out _)) kind = "ScriptIntProperty";
        else if (scalar.TryGetValue<double>(out _)) kind = "ScriptFloatProperty";
        else kind = "ScriptObjectProperty";
        var field = kind switch { "ScriptObjectProperty" => "Object", "ScriptObjectListProperty" => "Objects", _ => "Data" };
        if (kind == written && field == from) return null;

        var mended = new JsonObject { ["MutagenObjectType"] = kind };
        if (name is not null) mended["Name"] = name;
        if (property["Flags"] is { } flags) mended["Flags"] = flags.DeepClone();
        mended[field] = held.DeepClone();
        foreach (var (key, value) in property)
            if (key is not ("MutagenObjectType" or "Name" or "Flags") && key != from) mended[key] = value?.DeepClone();
        var shown = name?.ToString() ?? "a";
        return (mended, $"{(written is null ? "no kind" : $"`MutagenObjectType: {written}`")} of {shown} property as `MutagenObjectType: {kind}`" +
            (from == field ? "" : from == name?.ToString() ? $", `{from}: …` as `Name: {from}` and `{field}:`" : $", its `{from}:` as `{field}:`"));
    }
}
