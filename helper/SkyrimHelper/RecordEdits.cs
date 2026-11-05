using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Strings;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SkyrimHelper;

/// <summary>
/// Changes fields of one record file by their paths — `Data.Damage`, `Effects[0].Data.Magnitude` — with no text to match:
/// each field's lines changed in place, the rest of the file as it was, its comments included. 95 of the edits were
/// refused in eval rounds 40–47: the old text written from memory, indented otherwise than the file, or made stale by a
/// build. Set by its path, a field needs none of that (docs/adr/0008).
/// </summary>
public static class RecordEdits
{
    private const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
    private static readonly Regex Segment = new(@"^(?<key>[A-Za-z][A-Za-z0-9_]*)(?:\[(?<index>\d+)\])?$");

    private sealed record Step(string Key, int? Index);

    /// <summary>The call's project and record file, and the load order's records when one is set — what a refusal names.</summary>
    private sealed record Context(string PluginDir, string Relative, RecordIndex? Index);

    public static JsonObject Edit(string pluginDir, string record, JsonObject? set, JsonArray? remove, RecordIndex? index = null)
    {
        if ((set?.Count ?? 0) + (remove?.Count ?? 0) == 0)
            // A weapon's damage is BasicStats.Damage, and set a list of path and value: the example showed `Data.Damage`, which a weapon
            // has not, as a dotted key, the form a model's call lost in eval round 55 (refusal probes of 2026-10-31, E03, H01).
            throw new HelperException("badRequest", "Nothing to change: give `set`, a list of fields by path with their values — " +
                "[{\"path\": \"BasicStats.Damage\", \"value\": 12}, {\"path\": \"Name\", \"value\": \"Flame Blade\"}] — or `remove`, " +
                "paths of fields to delete — [\"Critical\"].");
        var (file, relative, type) = Find(pluginDir, record, out var found);
        if (LevelBounds.BothCalls(pluginDir, relative, type.Name, set, remove) is { } both) throw new HelperException("badRequest", both);
        var written = File.ReadAllText(file);
        var text = written.Replace("\r\n", "\n");
        // YAML that does not read, refused with its line and why, as the build says it. Sent to "the line buildProject names",
        // the model sent the same call three more times: the build had read the file once its corrections were made, and
        // refused it on a field, naming no line (eval round 111's npc #4; 5 runs since round 95).
        if (Unread(text) is { } broken)
            throw new HelperException("badRequest", $"{relative}'s YAML does not read, so no field can be found in it; nothing was written — " +
                $"{RecordLinks.YamlBreak(text, broken, type, pluginDir, relative)}. Write that right in the file with your file tools, " +
                "then call editRecord again.");
        var changes = found is null ? new List<string>() : [found];
        text = Apply(text, type, set, remove, changes, new Context(pluginDir, relative, index));
        // A change that left no YAML is this tool's fault, never written.
        if (Root(text) is null)
            throw new HelperException("badRequest", $"The change would leave {relative} no YAML to read; nothing was written. Change it with your file tools.");
        File.WriteAllText(file, written.Contains("\r\n") ? text.Replace("\n", "\r\n") : text);
        return new JsonObject
        {
            ["file"] = relative,
            ["changed"] = new JsonArray(changes.Select(c => (JsonNode)c).ToArray()),
            ["text"] = text
        };
    }

    /// <summary>
    /// What an editRecord call refused for want of its file would still change, now that a file it meant is written — nothing
    /// is written. The file is the one the call names, where the project has it now; else the one of <paramref name="among"/>,
    /// the files written since the refusal, whose record has every field the call names. Refused for an NPC the project had no
    /// file of yet, a model wrote her from an atronach, never sent the call — her race, her voice — again, and answered that it
    /// had made her a Nord (eval round 114's npc #2). Each change is tried alone: one editRecord would refuse is left out, and
    /// one the file holds already — a link by EditorID where the file has its FormID, a number or a word in another case — too.
    /// </summary>
    public static JsonObject Pending(string pluginDir, string record, JsonObject? set, JsonArray? remove, JsonArray? among, RecordIndex? index = null)
    {
        var removed = (remove ?? []).Select(r => r?.GetValue<string>() ?? "").ToList();
        var paths = (set ?? []).Select(f => f.Key).Concat(removed).ToList();
        var written = (among ?? []).Select(a => (a?.GetValue<string>() ?? "").Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<(string File, string Relative, Type Type)> meant;
        try { meant = [Find(pluginDir, record, out _)]; }
        catch (HelperException)
        {
            meant = RecordFiles.FlatRecords(pluginDir).Where(f => written.Contains(f.Relative) && paths.All(p => HasField(f.Type, p))).ToList();
        }
        if (meant is not [var (file, relative, type)]) return new JsonObject { ["file"] = null };
        var text = File.ReadAllText(file).Replace("\r\n", "\n");
        if (Unread(text) is not null) return new JsonObject { ["file"] = null };
        var context = new Context(pluginDir, relative, index);
        var held = Normal(FromYaml(Root(text)!), index);
        var differs = new JsonArray();
        var changes = new List<(string Path, JsonObject? Set, JsonArray? Remove)>(
            (set ?? []).Select(f => (f.Key, (JsonObject?)new JsonObject { [f.Key] = f.Value?.DeepClone() }, (JsonArray?)null))
                .Concat(removed.Select(p => (p, (JsonObject?)null, (JsonArray?)new JsonArray(JsonValue.Create(p))))));
        foreach (var (path, alone, gone) in changes)
        {
            string after;
            try { after = Apply(text, type, alone, gone, [], context); }
            catch (HelperException) { continue; }
            if (Root(after) is not { } changed || JsonNode.DeepEquals(held, Normal(FromYaml(changed), index))) continue;
            var steps = Placed(type, path).Steps;
            var there = At(text, steps) ?? At(text, Parse(path));
            differs.Add(new JsonObject
            {
                ["path"] = path,
                ["has"] = there is null ? null : Shown(there, index),
                ["removes"] = gone is not null
            });
        }
        return new JsonObject { ["file"] = relative, ["differs"] = differs };
    }

    /// <summary>Whether a path names a field the record type has — where it is placed, as editRecord places it.</summary>
    private static bool HasField(Type type, string path)
    {
        try
        {
            var (steps, label) = Placed(type, path);
            Types(type, steps, label);
            return true;
        }
        catch (HelperException) { return false; }
    }

    /// <summary>
    /// A record's fields as JSON to compare: a link as the record it names, by its FormID or its EditorID in the load order; a
    /// number as a number; a word in one case; a list of words — flags, keywords — in any order.
    /// </summary>
    private static JsonNode? Normal(JsonNode? node, RecordIndex? index) => node switch
    {
        JsonObject fields => new JsonObject(fields.Select(f => KeyValuePair.Create(f.Key, Normal(f.Value, index)))),
        JsonArray items when items.Select(i => Normal(i, index)).ToList() is var normal => new JsonArray(
            (normal.All(i => i is JsonValue v && v.TryGetValue<string>(out _))
                ? normal.OrderBy(i => i!.GetValue<string>(), StringComparer.Ordinal) : normal.AsEnumerable()).ToArray()),
        JsonValue one when one.TryGetValue<string>(out var said) => LinkOf(said, index) is { } key ? JsonValue.Create("@" + Session.FormKeyText(key))
            : double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number)
            : JsonValue.Create(said.Trim().ToLowerInvariant()),
        JsonValue one when double.TryParse(one.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
        _ => node?.DeepClone()
    };

    /// <summary>The record a value names: a FormID, or an EditorID of the load order; null for any other value.</summary>
    private static FormKey? LinkOf(string said, RecordIndex? index) =>
        FormKey.TryFactory(said.Trim(), out var key) ? key
            : index is not null && index.ByEditorId.TryGetValue(said.Trim(), out var named) ? named : null;

    /// <summary>A field's value as an answer shows it: a FormID with the EditorID of the record it names.</summary>
    private static string Shown(YamlNode node, RecordIndex? index) =>
        node is YamlScalarNode { Value: { } said } && FormKey.TryFactory(said.Trim(), out var key)
            && index?.ByFormKey.GetValueOrDefault(key)?.EditorId is { } named
            ? $"{said.Trim()} ({named})"
            : node is YamlScalarNode ? Shown(node) : Shown(FromYaml(node));

    /// <summary>A call's changes made to a file's text: each field set, then each removed.</summary>
    private static string Apply(string text, Type type, JsonObject? set, JsonArray? remove, List<string> changes, Context context)
    {
        foreach (var (path, value) in set ?? []) text = SetPath(text, type, path, value, changes, context);
        var setting = (set ?? []).Select(f => Placed(type, f.Key).Steps).ToList();
        foreach (var path in (remove ?? []).Select(r => r?.GetValue<string>() ?? ""))
        {
            // A field the file has, as sent, goes whatever its type has: told by the build to delete `Unique`, which an
            // NPC has nowhere, the model sent it to `remove`, and the call was refused for the field the type lacks (eval round
            // 69's npc+detailed #3).
            var sent = Parse(path);
            if ((FirstUnknown(type, sent).At >= 0 || PastText(type, sent)) && At(text, sent) is not null)
            {
                text = Remove(text, sent, path, changes);
                continue;
            }
            var (steps, label) = Placed(type, path);
            // One the same call sets is kept: in that call, `Level`, which the build had moved under Configuration, was
            // to be removed after Configuration.Level was set.
            if (setting.Any(s => s.Count >= steps.Count && s.Take(steps.Count).SequenceEqual(steps)))
            {
                changes.Add($"kept {label}: this call sets it");
                continue;
            }
            Types(type, steps, label);
            // Never another field than the one sent: `remove: ["Damage"]`, no Damage at the margin, removed BasicStats.Damage —
            // the weapon's damage, 0 in game.
            if (!steps.SequenceEqual(sent) && At(text, steps) is { } there)
            {
                var held = there is YamlScalarNode scalar ? $", {scalar.Value}" : "";
                throw new HelperException("badRequest", $"{path}: the file has no {path} where it was sent; {Upto(steps, steps.Count)}{held} " +
                    $"is the one it has — to delete that, remove [\"{Upto(steps, steps.Count)}\"]. Nothing was written.");
            }
            text = Remove(text, steps, label, changes);
        }
        return text;
    }

    /// <summary>The record file named by its path in the plugin folder, its file name, or the record's EditorID.</summary>
    private static (string File, string Relative, Type Type) Find(string pluginDir, string record, out string? found)
    {
        found = null;
        var flat = RecordFiles.FlatRecords(pluginDir).ToList();
        var wanted = (Path.IsPathRooted(record) ? Path.GetRelativePath(pluginDir, record) : record).Replace('\\', '/').Trim();
        var hit = flat.FirstOrDefault(f => f.Relative.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (hit == default) hit = flat.FirstOrDefault(f => Path.GetFileName(f.Relative).Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(f.Relative).Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (hit == default) hit = flat.FirstOrDefault(f => string.Equals(RecordFiles.Identity(File.ReadAllText(f.File)).EditorId, wanted, StringComparison.OrdinalIgnoreCase));
        if (hit != default) return hit;
        // The editor ID an addPatch copy's file is named by — `IronSword - 012EB7_Skyrim.esm.yaml` — once its record has another:
        // told the project's record files, a model named the Iron Sword's copy, its EditorID made FlameBlade, IronSword (eval
        // round 104's sword+detailed #5). One file alone; a folder sent must be the file's.
        var (folder, name) = wanted.LastIndexOf('/') is var slash and >= 0 ? (wanted[..slash], wanted[(slash + 1)..]) : ("", wanted);
        name = Regex.Replace(name, @"\.ya?ml$", "", RegexOptions.IgnoreCase);
        if (flat.Where(f => Path.GetFileNameWithoutExtension(f.Relative) is var stem && stem.IndexOf(" - ", StringComparison.Ordinal) is var cut and > 0
                && stem[..cut].Equals(name, StringComparison.OrdinalIgnoreCase)
                && (folder.Length == 0 || (Path.GetDirectoryName(f.Relative) ?? "").Replace('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase)))
            .ToList() is [var copy])
        {
            found = $"found {copy.Relative} by its file's name, {name} — its record's EditorID is " +
                $"{RecordFiles.Identity(File.ReadAllText(copy.File)).EditorId ?? "none"}, by which later calls name it";
            return copy;
        }
        throw new HelperException("notFound", $"No record file '{record}' in the project — its record files: " +
            (flat.Count == 0 ? "none yet; write one first" : string.Join(", ", flat.Take(20).Select(f => f.Relative)) + (flat.Count > 20 ? ", …" : "")) +
            ". Give the file as its path in the plugin folder, like Weapons/FlameBlade.yaml, or the record's EditorID.");
    }

    private static List<Step> Parse(string path)
    {
        var steps = path.Split('.').Select(s => Segment.Match(s.Trim())).ToList();
        if (path.Trim().Length == 0 || steps.Any(m => !m.Success))
        {
            // `BasicStats/Damage`, `BasicStats > Damage`: the path its separators make, where they make one (refusal probes, E14).
            var dotted = Regex.Replace(path.Trim(), @"\s*(?:[/\\>:]+|\s)\s*", ".");
            // A field's name in quotes, `\"MutagenObjectType\"`: a text's fields sent with their quotes escaped (eval round 110's npc #4).
            var unquoted = Regex.Replace(path.Trim(), @"\\*[""']", "");
            static bool IsPath(string p) => p.Split('.').All(s => Segment.IsMatch(s));
            throw new HelperException("badRequest", $"'{path}' is no field path: fields joined by dots, an item of a list by its " +
                (unquoted != path.Trim() && IsPath(unquoted)
                    ? $"number from 0, and a field's name has no quotes — this one is {unquoted}. A value's fields sent as a text with " +
                      $"its quotes escaped, `\\\"`, keep them in their names: send them as a mapping, {{\"{unquoted.Split('.')[^1]}\": …}}, not a text."
                    : dotted != path.Trim() && IsPath(dotted)
                    ? $"number from 0 — this one is {dotted}."
                    : "number from 0 — BasicStats.Damage, Name, Effects[0].Data.Magnitude."));
        }
        return steps.Select(m => new Step(m.Groups["key"].Value, m.Groups["index"].Success ? int.Parse(m.Groups["index"].Value) : null)).ToList();
    }

    /// <summary>
    /// A path, and its words for answers: where it was placed otherwise, as placed and as sent —
    /// "Configuration.CalcMinLevel (sent as Level.CalcMinLevel)".
    /// </summary>
    private static (List<Step> Steps, string Label) Placed(Type record, string path)
    {
        var sent = Parse(path);
        var steps = Place(record, sent);
        return (steps, steps.SequenceEqual(sent) ? path : $"{Upto(steps, steps.Count)} (sent as {path})");
    }

    /// <summary>
    /// The path to where the record has the field, where it was sent to a place without it and the place is plain: a
    /// field of the part above, sent below one of that part's fields — Configuration.Level.CalcMinLevel is
    /// Configuration.CalcMinLevel — and a field the top level lacks, of the one part RecordFiles names — an NPC's Level
    /// is Configuration's, a weapon's Damage BasicStats'. Sent Level.CalcMinLevel and told an NPC's level is "three lines
    /// under Configuration", the model sent Configuration.Level.CalcMinLevel, then the level whole with a brace too many,
    /// which Ollama dropped, and the run ended (eval round 60's npc+detailed #3). And a field of a list item's one part, sent
    /// at the item: an NPC's Items[6].Count is Items[6].Item.Count — told "move it under Item", the model set Items[6].Data,
    /// then Items[6].Data.Quantity, and answered the gold unchanged (eval round 127's quest #1).
    /// </summary>
    private static List<Step> Place(Type record, List<Step> sent)
    {
        var steps = sent.ToList();
        while (FirstUnknown(record, steps) is var (at, above, holder) && at >= 0)
        {
            if (at == 0 && RecordFiles.PartFor(record, steps[0].Key, "") is { } part && Property(record, part) is not null)
                steps.Insert(0, new Step(part, null));
            else if (at > 0 && steps[at - 1].Index is not null && holder is not null
                     && Regex.Match(RecordFiles.HintFor(holder, steps[at].Key), @"^ \(move it under (?<part>\w+)\)$") is { Success: true } under
                     && Property(holder, under.Groups["part"].Value) is { } inner
                     && Property(Nullable.GetUnderlyingType(inner.PropertyType) ?? inner.PropertyType, steps[at].Key) is not null)
                steps.Insert(at, new Step(under.Groups["part"].Value, null));
            // Only below a part: BasicStats.Damage.Value, Damage a number, was set as BasicStats.Value — the price, not the damage.
            else if (at > 0 && steps[at - 1].Index is null && above is not null && Property(above, steps[at].Key) is not null
                     && Property(above, steps[at - 1].Key) is { } before && IsPart(Nullable.GetUnderlyingType(before.PropertyType) ?? before.PropertyType))
                steps.RemoveAt(at - 1);
            else break;
        }
        return steps;
    }

    /// <summary>
    /// The first step of a path whose part has no field for it, the part holding the step before it, and the part that lacks
    /// it; -1 for none.
    /// </summary>
    private static (int At, Type? Above, Type? Holder) FirstUnknown(Type record, List<Step> steps)
    {
        var current = record;
        Type? above = null;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Key == "MutagenObjectType" || Translated(current)) break;
            if (Property(current, steps[i].Key) is not { } property) return (i, above, current);
            above = current;
            current = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (steps[i].Index is null) continue;
            if (Items(current) is not { } item) break;
            current = item;
        }
        return (-1, null, null);
    }

    /// <summary>
    /// A path into a text past what a text holds — `Name.ValueNumeric` — a field the file can have where it was written and
    /// no type has. The build refused it with "delete `ValueNumeric`", removing it was refused twice as a text to set, and
    /// the run ended on the third try (eval round 93's sword+detailed #3).
    /// </summary>
    private static bool PastText(Type record, List<Step> steps)
    {
        var current = record;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Key == "MutagenObjectType") return false;
            if (Translated(current)) return !(i == steps.Count - 1 && steps[i].Key is "Value" or "TargetLanguage");
            if (Property(current, steps[i].Key) is not { } property) return false;
            current = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (steps[i].Index is null) continue;
            if (Items(current) is not { } item) return false;
            current = item;
        }
        return false;
    }

    /// <summary>
    /// The type each step of the path holds, checked against the record's type: a field it has not is refused. A path below a
    /// link is refused with what <paramref name="linked"/> says of the record linked there — given the step below the link
    /// and the link's type — where it names one.
    /// </summary>
    private static List<Type> Types(Type record, List<Step> steps, string path, Func<int, Type, string?>? linked = null)
    {
        var types = new List<Type>();
        var current = record;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var where = i == 0 ? RecordFiles.Article(record.Name) : string.Join('.', steps.Take(i).Select(s => s.Index is { } n ? $"{s.Key}[{n}]" : s.Key));
            if (step.Key == "MutagenObjectType") { types.Add(typeof(string)); break; }
            // A text as the file writes it — `Name:`, then `TargetLanguage: English` and `Value: Iron Sword` — not as
            // Mutagen holds it: `Name.Value` was refused for no field Value, "its fields: String, StringsKey" (eval round 49).
            if (Translated(current))
            {
                if (step.Key is "Value" or "TargetLanguage" && i == steps.Count - 1) { types.Add(typeof(string)); break; }
                throw new HelperException("badRequest", $"{path}: {where} is a text, written as its Value and TargetLanguage — set " +
                    $"{where} to its words, or {where}.Value. Nothing was written.");
            }
            // A field below one value: BasicStats.Damage.Value, for a weapon's damage.
            if (i > 0 && !IsPart(current) && Property(current, step.Key) is null)
                throw new HelperException("badRequest", Items(current) is not null && !current.IsEnum
                    ? $"{path}: {where} is a list — name one of its items by its number from 0, {where}[0].{step.Key}. Nothing was written."
                    : RecordLinks.LinkTarget(current) is not null && linked?.Invoke(i, current) is { } named
                        ? $"{path}: {where} is a link to a record and has no fields — {named} Nothing was written."
                        : $"{path}: {where} is {OneValue(current)} and has no fields — set {where} itself, " +
                          $"{{\"path\": \"{where}\", \"value\": …}}. Nothing was written.");
            // A magic effect's Data, found by name, was a condition's: "move it in each item of Conditions", as the build said
            // until round 24.
            var property = Property(current, step.Key)
                ?? throw new HelperException("badRequest", $"{path}: {where} has no field {step.Key}" +
                    (i == 0 && record.Name == "MagicEffect" && step.Key == "Data" ? $" ({RecordFiles.EffectStrength})"
                        : RecordFiles.HintFor(current, step.Key) is { Length: > 0 } hint ? hint.Replace(RecordFiles.NpcLevel, NpcLevel)
                        // Where the record has it: `Data.Damage`, editRecord's own example once, is a weapon's BasicStats.Damage (refusal probes, H01).
                        : i > 0 && RecordFiles.WhereIn(record, step.Key) is { } there ? $" ({RecordFiles.Article(record.Name)}'s {step.Key} is {there})" : "") +
                    $" — its fields: {string.Join(", ", Fields(current).Take(40))}. Nothing was written.");
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (step.Index is not null)
                type = Items(type) ?? throw new HelperException("badRequest", $"{path}: {step.Key} is no list. Nothing was written.");
            types.Add(type);
            current = type;
        }
        return types;
    }

    /// <summary>A field of the type — of any of its kinds, for a field of several kinds.</summary>
    private static PropertyInfo? Property(Type type, string key) =>
        Kinds(type).SelectMany(t => t.GetProperties(Public)).Where(p => p.Name == key).OrderBy(p => p.DeclaringType == type ? 0 : 1).FirstOrDefault();

    private static IEnumerable<Type> Kinds(Type type) => type.IsAbstract
        ? type.Assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface && type.IsAssignableFrom(t))
        : [type];

    private static IEnumerable<string> Fields(Type type) =>
        Kinds(type).SelectMany(t => t.GetProperties(Public)).Where(p => p.CanWrite).Select(p => p.Name).Distinct().Order(StringComparer.Ordinal);

    private static bool Translated(Type type) => typeof(ITranslatedStringGetter).IsAssignableFrom(type);

    /// <summary>What one value is, for a path sent below it.</summary>
    private static string OneValue(Type type) =>
        type == typeof(bool) ? "a truth, true or false"
            : type.IsEnum ? "one of a set of words"
            : RecordLinks.LinkTarget(type) is not null ? "a link to a record"
            : type == typeof(string) ? "a word"
            : Items(type) is not null ? "a list"
            : type.IsPrimitive || type == typeof(decimal) ? "a number"
            : "one value";

    /// <summary>A part: a field that holds fields of its own — not a number, a word, a flag, a link, a list or a text.</summary>
    private static bool IsPart(Type type) =>
        !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal) && !Translated(type)
        && Items(type) is null && RecordLinks.LinkTarget(type) is null && Fields(type).Any();

    /// <summary>The text a path sets — Name for `Name` and for `Name.Value` — or null for a path to no text.</summary>
    private static string? TextOf(List<Step> steps, List<Type> types) =>
        Translated(types[^1]) ? Upto(steps, steps.Count)
            : steps.Count > 1 && types.Count > 1 && steps[^1] is { Key: "Value", Index: null } && Translated(types[^2]) ? Upto(steps, steps.Count - 1)
                : null;

    /// <summary>
    /// For a number sent, where the record keeps what it is worth in gold, with that number — a weapon's is BasicStats.Value,
    /// an armor's Value; empty for a record without one, or a truth sent.
    /// </summary>
    private static string WorthIn(Type record, JsonValue sent)
    {
        if (!sent.TryGetValue<double>(out _)) return "";
        try
        {
            var (steps, _) = Placed(record, "Value");
            var held = Types(record, steps, "Value")[^1];
            var path = Upto(steps, steps.Count);
            return held.IsPrimitive && held != typeof(bool)
                ? $"; what {RecordFiles.Article(record.Name)} is worth in gold is {path}, {{\"path\": \"{path}\", \"value\": {sent.ToJsonString()}}}"
                : "";
        }
        catch (HelperException)
        {
            return "";
        }
    }

    /// <summary>
    /// The item type of a list field, a flags field's its names, as the file writes them — `Flags:`, `- Female` below it.
    /// Told "TemplateFlags is no list" for TemplateFlags[5], the model edited the file by hand and broke its YAML twice (eval
    /// round 61's npc #1).
    /// </summary>
    private static Type? Items(Type type) =>
        type.IsEnum && type.IsDefined(typeof(FlagsAttribute), false) ? type : RecordLinks.ElementType(type);

    /// <summary>An NPC's level in paths: told it is "three lines under Configuration", as a file is, the model sent Configuration.Level.CalcMinLevel (eval round 60).</summary>
    private const string NpcLevel = "an NPC's level is Configuration.Level, {\"MutagenObjectType\": \"PcLevelMult\", \"LevelMult\": 1}, " +
        "with Configuration.CalcMinLevel and Configuration.CalcMaxLevel — the level follows the player's between those two; " +
        "or, for a fixed level, Configuration.Level alone, {\"MutagenObjectType\": \"NpcLevel\", \"Level\": 10}";

    // ---------------------------------------------------------------- set and remove

    /// <summary>
    /// One entry of `set`. An object for a part the file has sets the fields it names and keeps the others, as a JSON
    /// merge patch does: {"BasicStats": {"Damage": 12}}, written whole, took the Iron Sword's Value and Weight with it
    /// (eval round 49's patch #1). One naming another kind of the part is written whole. A text's Value is the text.
    /// </summary>
    private static string SetPath(string text, Type type, string path, JsonNode? value, List<string> changes, Context context)
    {
        var (steps, label) = Placed(type, path);
        // A field sent under another name — a weapon's Enchantments, its ObjectEffect — refused with the call that sets the one
        // meant: told "an enchantment is ObjectEffect", the model wrote the sword's file whole again, without it, and set
        // ObjectEffect two turns later (eval round 112's sword+detailed #3).
        if (steps is [{ Index: null } only] && Property(type, only.Key) is null && value is JsonValue
            && RecordFiles.SynonymFor(type, only.Key) is { } meant && Property(type, meant) is not null)
            throw new HelperException("badRequest", $"{label}: {RecordFiles.Article(type.Name)} has no field {only.Key}" +
                $"{RecordFiles.HintFor(type, only.Key)} — one call sets {meant} and builds again: " +
                ToolCalls.Edit(ToolCalls.ProjectOf(context.PluginDir), context.Relative, $"{{\"path\": \"{meant}\", \"value\": {value.ToJsonString()}}}", build: true) +
                ". Nothing was written.");
        var types = Types(type, steps, label, (below, link) => Linked(text, steps, below, link, context));
        if (value is JsonValue braced && braced.TryGetValue<string>(out var bracedText) && Fielded(bracedText, types[^1], out var unescaped) is { } parsed)
        {
            value = parsed;
            label += unescaped ? " (fields, sent as a text with its quotes escaped, `\\\"`, read as quotes)" : " (fields, sent as a text)";
        }
        // A text takes words: sent the number 150 for Name.Value, meaning the sword's price, the tool wrote `Name: 150` over the
        // whole Name, the build refused it, and the model answered that a weapon has no field for its price (eval round 80's
        // sword+detailed #2).
        if (value is JsonValue notWords && !notWords.TryGetValue<string>(out _) && TextOf(steps, types) is { } textPath)
            throw new HelperException("badRequest", $"{label}: {textPath} is a text, and {notWords.ToJsonString()} is no words — send " +
                $"them as one, {{\"path\": \"{textPath}.Value\", \"value\": \"…\"}}{WorthIn(type, notWords)}. Nothing was written.");
        // Fields of the part above, sent inside a part: an NPC's {"Level": {…, "CalcMinLevel": 5}} — set as Configuration's.
        if (value is JsonObject sent && sent.Where(f => f.Key != "MutagenObjectType" && Segment.IsMatch(f.Key)
                && Place(type, Parse($"{path}.{f.Key}")) is var placed && !(placed.Count > steps.Count && placed.Take(steps.Count).SequenceEqual(steps)))
                .Select(f => f.Key).ToList() is { Count: > 0 } outside)
        {
            var rest = (JsonObject)sent.DeepClone();
            foreach (var key in outside)
            {
                text = SetPath(text, type, $"{path}.{key}", sent[key]?.DeepClone(), changes, context);
                rest.Remove(key);
            }
            if (rest.Count == 0) return text;
            value = rest;
        }
        if (value is JsonObject { Count: > 0 } fields && At(text, steps) is YamlMappingNode there
            && (fields["MutagenObjectType"] is not JsonValue kind || kind.GetValue<object>().ToString() == Field(there, "MutagenObjectType")))
            return fields.Where(f => f.Key != "MutagenObjectType")
                .Aggregate(text, (merged, f) => SetPath(merged, type, $"{path}.{f.Key}", f.Value?.DeepClone(), changes, context));
        if (steps.Count > 1 && steps[^1] is { Key: "Value", Index: null } && Translated(types[^2]) && value is JsonValue)
            return Set(text, steps.SkipLast(1).ToList(), types.SkipLast(1).ToList(), value, label, changes);
        // A link sent with the comment a file writes after it — `013F42:Skyrim.esm # RightHand` — is the link alone: written in
        // quotes, comment and all, it was no FormID, and the build refused EquipmentType and three keywords so (eval round 92's
        // sword #2).
        if (RecordLinks.LinkTarget(Items(types[^1]) ?? types[^1]) is not null && Uncommented(value) is { } bare)
        {
            value = bare;
            label += " (sent with a `# comment` after the link, left out)";
        }
        // A list of names sent as a text: "- AutoCalcStats\n- Unique\n- Female", its line ends written `\n`, was set as one
        // text, which the build refused, then "AutoCalcStats\nUnique\nFemale", and the model answered as built (eval round 61's
        // npc+detailed #5).
        if (steps[^1].Index is null && value is JsonValue words && words.TryGetValue<string>(out var said)
            && Items(types[^1]) is { } item && (item.IsEnum || RecordLinks.LinkTarget(item) is not null))
        {
            value = Listed(text, steps, label, said);
            label += " (a list, sent as a text)";
        }
        // A list of parts sent as a text — a leveled list's Entries as a JSON list in a text, then as the lines a file writes —
        // was written as that text, in quotes, then as a block of text: the build after each refused the file, the same text
        // sent again answered success, and the model wrote the file whole (eval round 117's npc+detailed #5). A text that
        // reads as no list is refused.
        if (steps[^1].Index is null && value is JsonValue partsSent && partsSent.TryGetValue<string>(out var partsSaid)
            && Items(types[^1]) is { } part && typeof(Loqui.ILoquiObject).IsAssignableFrom(part))
        {
            value = ListOf(partsSaid) ?? throw new HelperException("badRequest", $"{label}: a list, and \"{Shown(partsSent)}\" reads as " +
                $"none — send its items as a JSON list, {{\"path\": \"{Upto(steps, steps.Count)}\", \"value\": [{{…}}, …]}}, or as the " +
                "lines a file writes them in, each item from `- `. Nothing was written.");
            label += " (a list, sent as a text)";
        }
        // A value the field cannot hold, refused before it is written: "twelve" for a damage, 12 for BasicStats, null — each was
        // written, and the build after it refused the file the tool had made (refusal probes of 2026-10-31, E06, E20, E23).
        if (Unwritable(types[^1], value, path) is { } unwritable)
            throw new HelperException("badRequest", $"{label}: {unwritable}. Nothing was written.");
        text = Set(text, steps, types, value, label, changes);
        return steps.Count > 1 && steps[^1] is { Key: "MutagenObjectType", Index: null } ? Rekinded(text, steps.SkipLast(1).ToList(), types[^2], changes) : text;
    }

    /// <summary>A link with a comment after it: `013F42:Skyrim.esm # RightHand`.</summary>
    private static readonly Regex Commented = new(@"^[ \t]*(?<link>[^\s#'""]+)[ \t]+#.*$");

    /// <summary>
    /// A link, or a list of links, sent with a `# comment` after each, as the links alone — a list sent as a text, line by line;
    /// null where no comment is.
    /// </summary>
    private static JsonNode? Uncommented(JsonNode? value)
    {
        string? Bare(string said) => Commented.Match(said) is { Success: true } m ? m.Groups["link"].Value : null;
        switch (value)
        {
            case JsonValue one when one.TryGetValue<string>(out var said):
                var lines = Regex.Split(said, @"\\n|\r?\n");
                if (lines.Length == 1) return Bare(said) is { } link ? JsonValue.Create(link) : null;
                if (!lines.Any(l => Bare(l.TrimStart().TrimStart('-')) is not null)) return null;
                return JsonValue.Create(string.Join("\n", lines.Select(l =>
                    Regex.Match(l, @"^(?<dash>[ \t]*-?[ \t]*)(?<rest>.*)$") is var m && Bare(m.Groups["rest"].Value) is { } bare ? m.Groups["dash"].Value + bare : l)));
            case JsonArray items when items.Any(i => i is JsonValue v && v.TryGetValue<string>(out var s) && Bare(s) is not null):
                return new JsonArray(items.Select(i => i is JsonValue v && v.TryGetValue<string>(out var s) && Bare(s) is { } b
                    ? JsonValue.Create(b) : i?.DeepClone()).ToArray());
            default:
                return null;
        }
    }

    /// <summary>What is wrong with a value for a field of a type, as words, or null where it can hold it.</summary>
    private static string? Unwritable(Type type, JsonNode? value, string path)
    {
        var field = Nullable.GetUnderlyingType(type) ?? type;
        if (value is null)
            return $"null writes no value — to delete the field, send it in `remove`: [\"{path}\"]";
        if (value is not JsonValue one) return null;
        var said = one.TryGetValue<string>(out var text) ? text : one.ToJsonString();
        if (field.IsEnum || field.IsPrimitive && field != typeof(char))
            return RecordLinks.Unfit(field, said) is { } unfit ? $"{said} — {unfit}" : null;
        if (!typeof(Loqui.ILoquiObject).IsAssignableFrom(field) || Translated(field)) return null;
        // A text, the fields as a file writes them: set as `"LevelMult: 1"`, an NPC's level was written as that text, and the
        // build after it refused it (eval round 109's npc+detailed #4). A text in braces is read as the fields before this.
        if (text is not null)
            return $"{path} is a part with fields of its own, and \"{said}\" is a text — send its fields as a mapping" +
                (MappingOf(said, field) is { } meant ? $", {{\"path\": \"{path}\", \"value\": {meant}}}"
                    : field.Name == "ANpcLevel" ? $"; {NpcLevel}"
                    : $", {{\"path\": \"{path}\", \"value\": {{\"{Fields(field).FirstOrDefault() ?? "…"}\": …}}}}");
        return $"{path} is a part with fields of its own, and {said} is one value — set the field meant below it, as " +
            $"{path}.{Fields(field).FirstOrDefault() ?? "…"}";
    }

    /// <summary>
    /// A text that reads as fields of a part, a line end written or sent as `\n`, as the JSON mapping to send — its kind first, named
    /// where one kind alone has them; null for a text that reads as no mapping, or names a field the part has not.
    /// </summary>
    private static string? MappingOf(string said, Type part)
    {
        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(Regex.Replace(said.Trim(), @"\\r\\n|\\n", "\n")));
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode mapping) return null;
            var fields = (JsonObject)FromYaml(mapping)!;
            var keys = fields.Select(f => f.Key).Where(k => k != "MutagenObjectType").ToList();
            if (keys.Count == 0 || keys.Any(k => Property(part, k) is null)) return null;
            if (part.IsAbstract && !fields.ContainsKey("MutagenObjectType")
                && Kinds(part).Where(k => keys.All(key => k.GetProperty(key, Public) is not null)).ToList() is [var kind])
                fields["MutagenObjectType"] = kind.Name;
            return new JsonObject(KindFirst(fields).Select(f => KeyValuePair.Create(f.Key, f.Value?.DeepClone()))).ToJsonString();
        }
        catch (Exception e) when (e is YamlException or ArgumentException or InvalidCastException) { return null; }
    }

    /// <summary>
    /// A text for a list of names, as the list of them: split at its line ends, written or as `\n`, and at its commas,
    /// each name without its `- `. One name alone for a list that holds others is refused, the whole list given: a list
    /// is set whole, and "Female" for an NPC's Flags would have taken its AutoCalcStats and Unique with it.
    /// </summary>
    private static JsonArray Listed(string text, List<Step> steps, string label, string said)
    {
        var names = Regex.Split(said.Trim().TrimStart('[').TrimEnd(']'), @"\\n|\r?\n|,")
            .Select(n => n.Trim().TrimStart('-').Trim().Trim('"', '\'')).Where(n => n.Length > 0).ToList();
        var held = At(text, steps) is YamlSequenceNode list ? list.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? "").ToList() : [];
        if (names.Count == 1 && held.Count > 0 && !held.SequenceEqual(names))
            throw new HelperException("badRequest", held.Contains(names[0])
                ? $"{label}: a list, set whole, and {names[0]} is among its items already: {Json(held)}. Nothing was written."
                : $"{label}: a list, set whole — to add {names[0]}, set it to {Json([.. held, names[0]])}. Nothing was written.");
        return new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
    }

    /// <summary>
    /// A text that reads as a list — JSON, or the lines a file writes, a line end written or sent as `\n`, a JSON text's quotes
    /// escaped once more read as quotes — as that list; null for a text that reads as none.
    /// </summary>
    private static JsonArray? ListOf(string said)
    {
        var written = said.Trim();
        var tried = new List<string> { written, Regex.Replace(written, @"\\r\\n|\\n", "\n") };
        if (written.StartsWith('[') && written.Contains("\\\"") && !Regex.IsMatch(written, @"(?<!\\)""")) tried.Add(Regex.Replace(written, @"\\([""\\])", "$1"));
        foreach (var each in tried.Distinct())
        {
            try
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(each));
                if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlSequenceNode list) return (JsonArray)FromYaml(list)!;
            }
            catch (Exception e) when (e is YamlException or ArgumentException or InvalidCastException) { }
        }
        return null;
    }

    private static string Json(IEnumerable<string> names) => new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()).ToJsonString();

    /// <summary>
    /// A part's fields sent as the text a file writes them in — `{MutagenObjectType: NpcLevel, Level: 5}`, as the build's
    /// refusal shows an NPC's level — as those fields, for a field that takes fields: set as that text, the model's level was
    /// refused five builds long (eval round 65's npc #1). Null for a text that reads as no mapping.
    ///
    /// A text — Name, Description — takes its words, braces and all, except its own two fields: the model sent a Name as
    /// `{"TargetLanguage": "English", "Value": "Hilda"}`, and the NPC was called that in game (eval round 68's
    /// npc+detailed #3). `{Hilda}` stays the words.
    ///
    /// Those two fields count also as the lines a file writes them in, a line end sent as `\n`: the model sent a spell's Name
    /// as `TargetLanguage: English\nValue: Storm Bolt`, the spell was called that in game, and its build answered success
    /// (eval round 100's spell+detailed #2).
    ///
    /// A JSON text escaped once more, every quote written `\"`, is read with its quotes, <paramref name="unescaped"/>: sent so
    /// for an NPC's level, `{\"MutagenObjectType\": \"PcLevelMult\", \"LevelMult\": 1}` was read as fields named with the
    /// quotes, refused as no field path, and the model lost four calls (eval round 110's npc #4).
    /// </summary>
    private static JsonObject? Fielded(string said, Type type, out bool unescaped)
    {
        var written = said.Trim();
        unescaped = written.StartsWith('{') && written.Contains("\\\"") && !Regex.IsMatch(written, @"(?<!\\)""");
        if (unescaped) written = Regex.Replace(written, @"\\([""\\])", "$1");
        var lines = Translated(type) && Regex.IsMatch(written, @"^(?:TargetLanguage|Value):");
        if (lines) written = Regex.Replace(written, @"\\r\\n|\\n", "\n");
        // A text lists its languages, so it is read as a text before anything else.
        if (!lines && (!written.StartsWith('{') || !written.EndsWith('}')) || !Translated(type) && (!type.IsClass || type == typeof(string)
            || Items(type) is not null || RecordLinks.LinkTarget(type) is not null))
            return null;
        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(written));
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode mapping) return null;
            var fields = (JsonObject)FromYaml(mapping)!;
            if (!Translated(type)) return fields;
            return fields.ContainsKey("Value") && fields.All(f => f.Key is "Value" or "TargetLanguage")
                ? new JsonObject { ["TargetLanguage"] = fields["TargetLanguage"]?.DeepClone() ?? "English", ["Value"] = fields["Value"]?.DeepClone() }
                : null;
        }
        catch (Exception e) when (e is YamlException or ArgumentException or InvalidCastException) { return null; }
    }

    /// <summary>A YAML node as JSON: a plain scalar that reads as a number or true/false as one, any other as its text.</summary>
    private static JsonNode? FromYaml(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(f => KeyValuePair.Create(((YamlScalarNode)f.Key).Value ?? "", FromYaml(f.Value)))),
        YamlSequenceNode list => new JsonArray(list.Children.Select(FromYaml).ToArray()),
        YamlScalarNode { Style: ScalarStyle.Plain, Value: var plain } when long.TryParse(plain, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) => whole,
        YamlScalarNode { Style: ScalarStyle.Plain, Value: var plain } when double.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        YamlScalarNode { Style: ScalarStyle.Plain, Value: var plain } when bool.TryParse(plain, out var flag) => flag,
        YamlScalarNode scalar => scalar.Value,
        _ => null
    };

    /// <summary>
    /// A part given another kind, without the fields of the kind it was that this one has not: an NpcLevel's `Level: 1`,
    /// left under a PcLevelMult, is refused by the build.
    /// </summary>
    private static string Rekinded(string text, List<Step> part, Type group, List<string> changes)
    {
        if (!group.IsAbstract || At(text, part) is not YamlMappingNode there || Field(there, "MutagenObjectType") is not { } named
            || RecordFiles.KindOf(group, named) is not { } kind)
            return text;
        var lacking = there.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "")
            .Where(k => k != "MutagenObjectType" && !kind.GetProperties(Public).Any(p => p.Name == k)).ToList();
        return lacking.Aggregate(text, (left, key) =>
            Remove(left, [.. part, new Step(key, null)], $"{Upto(part, part.Count)}.{key} ({RecordFiles.Article(kind.Name)} has no {key})", changes));
    }

    /// <summary>The node a path names in the file, or null where the file has none.</summary>
    private static YamlNode? At(string text, List<Step> steps)
    {
        YamlNode? node = Root(text);
        foreach (var step in steps)
        {
            if (node is not YamlMappingNode mapping || mapping.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == step.Key) is not { } key)
                return null;
            node = mapping.Children[key];
            if (step.Index is int n) node = node is YamlSequenceNode list && n < list.Children.Count ? list.Children[n] : null;
        }
        return node;
    }

    private static string? Field(YamlMappingNode mapping, string key) =>
        mapping.Children.FirstOrDefault(f => f.Key is YamlScalarNode k && k.Value == key).Value is YamlScalarNode value ? value.Value : null;

    private static string Set(string text, List<Step> steps, List<Type> types, JsonNode? value, string path, List<string> changes)
    {
        var root = Root(text) ?? throw Unreadable();
        YamlNode node = root;
        var indent = 0; // the column the current mapping's keys are written at
        YamlScalarNode? into = null; // the key of the part the current mapping is, where that key is on a line of its own
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var last = i == steps.Count - 1;
            if (node is not YamlMappingNode mapping)
                throw new HelperException("badRequest", $"{path}: {Upto(steps, i)} is written as one value, not as fields — set {Upto(steps, i)} whole. Nothing was written.");
            var key = mapping.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == step.Key);
            if (key is null)
            {
                // Missing from here on: written whole, below the fields the mapping has.
                if (steps.Skip(i).Any(s => s.Index is not null))
                    throw new HelperException("badRequest", $"{path}: there is no {step.Key} to take an item of — set {Upto(steps, i + 1).Split('[')[0]} whole, as a list. Nothing was written.");
                // A field the part's written kind has not, refused before writing: an NPC's Level, a PcLevelMult, took
                // `Configuration.Level.Level` — an NpcLevel's field, which a level of any kind is checked for (eval round 112's
                // npc+detailed #3).
                if (i > 0 && types[i - 1].IsAbstract && Field(mapping, "MutagenObjectType") is { } named
                    && RecordFiles.KindOf(types[i - 1], named) is { } kind && kind.GetProperty(step.Key, Public) is null)
                    throw new HelperException("badRequest", $"{path}: {Upto(steps, i)} is {RecordFiles.Article(kind.Name)}, which has no field " +
                        $"{step.Key}{RecordFiles.HintFor(kind, step.Key).Replace(RecordFiles.NpcLevel, NpcLevel)} — its fields: " +
                        $"{string.Join(", ", Fields(kind))}. Nothing was written.");
                var whole = Nest(steps.Skip(i).ToList(), types.Skip(i).ToList(), value);
                // A part written in braces on its line — `Level: {MutagenObjectType: PcLevelMult, LevelMult: 1}`, as the build
                // writes a level — takes a new field with its fields written as lines: below the braces, the new line left YAML
                // that does not read, refused as this tool's own fault (eval round 112's npc+detailed #3).
                if (mapping.Style == YamlDotNet.Core.Events.MappingStyle.Flow && into is not null && FromYaml(mapping) is JsonObject fields)
                {
                    fields[step.Key] = whole;
                    changes.Add($"added {path}: {Shown(value)}, with {Upto(steps, i)} written as lines");
                    return ReplaceField(text, into, mapping, Render(into.Value ?? "", fields, Column(into)));
                }
                var keyIndent = mapping.Children.Count > 0 ? Column(mapping.Children.Keys.First()) : indent;
                changes.Add($"added {path}: {Shown(value)}");
                return InsertAfter(text, mapping, ReferenceEquals(mapping, root), Render(step.Key, whole, keyIndent));
            }
            var child = mapping.Children[key];
            indent = Column(key) + 2;
            if (step.Index is int n)
            {
                // The item after the last, one value: added. Told Flags has 2 items, so [2] is none, the model set Flags whole
                // without the flags it had listed (eval round 89's npc+detailed #2).
                if (last && child is YamlSequenceNode { Style: not YamlDotNet.Core.Events.SequenceStyle.Flow, Children.Count: > 0 } growing
                    && n == growing.Children.Count && value is JsonValue && growing.Children.All(c => c is YamlScalarNode))
                {
                    var after = growing.Children[^1];
                    var dash = DashColumn(text, after);
                    changes.Add($"added {path}: {Shown(value)}, after the {n} item{(n == 1 ? "" : "s")} {step.Key} had");
                    var all = text.Split('\n').ToList();
                    all.Insert(LastLine(text, after) + 1, new string(' ', dash) + "- " + Scalar(value, dash + 2));
                    return string.Join('\n', all);
                }
                if (child is not YamlSequenceNode list || n >= list.Children.Count)
                    throw new HelperException("badRequest", $"{path}: {step.Key} has {(child as YamlSequenceNode)?.Children.Count ?? 0} item(s), so [{n}] is none — " +
                        $"set {step.Key} whole, as a list. Nothing was written.");
                var item = list.Children[n];
                if (last && list.Style == YamlDotNet.Core.Events.SequenceStyle.Flow)
                    throw new HelperException("badRequest", $"{path}: {step.Key} is written on one line, [a, b], so no item has a line of its own — " +
                        $"set {step.Key} whole, as a list. Nothing was written.");
                if (last)
                {
                    changes.Add($"set {path}: {Shown(item)} → {Shown(value)}");
                    return ReplaceItem(text, item, DashColumn(text, item), value);
                }
                node = item;
                indent = Column(item) + 2;
                into = null;
                continue;
            }
            // A part written empty — `Level:`, nothing below it — takes the fields set below it, written there whole: refused as
            // "written as one value", the model set the level whole with its kind last, which the reader could not read (eval
            // round 109's npc+detailed #1).
            if (!last && child is YamlScalarNode hollow && Empty(hollow) && !steps.Skip(i + 1).Any(s => s.Index is not null))
            {
                changes.Add($"set {path}: {Shown(value)}, below {Upto(steps, i + 1)}, which was empty");
                return ReplaceField(text, key, child, Render(step.Key, Nest(steps.Skip(i).ToList(), types.Skip(i).ToList(), value), Column(key)));
            }
            if (!last)
            {
                into = mapping.Style == YamlDotNet.Core.Events.MappingStyle.Flow ? null : key;
                node = child;
                continue;
            }
            // A text — Name, Description — takes its words: written into its Value.
            if (Translated(types[i]) && value is JsonValue words && words.TryGetValue<string>(out var said))
            {
                if (child is YamlMappingNode text2 && text2.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == "Value") is { } valueKey
                    && text2.Children[valueKey] is YamlScalarNode was)
                {
                    changes.Add($"set {path}: {Shown(was)} → {Shown(value)}");
                    return ReplaceScalar(text, was, Column(valueKey), JsonValue.Create(said));
                }
                value = new JsonObject { ["TargetLanguage"] = "English", ["Value"] = said };
            }
            changes.Add($"set {path}: {Shown(child)} → {Shown(value)}");
            return child is YamlScalarNode scalar && value is JsonValue && !Empty(scalar)
                ? ReplaceScalar(text, scalar, Column(key), value)
                : ReplaceField(text, key, child, Render(step.Key, value, Column(key)));
        }
        return text;
    }

    private static string Remove(string text, List<Step> steps, string path, List<string> changes)
    {
        var root = Root(text) ?? throw Unreadable();
        YamlNode node = root;
        YamlScalarNode? into = null; // the key of the part the current mapping is, where that key is on a line of its own
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (node is not YamlMappingNode mapping || mapping.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == step.Key) is not { } key)
            {
                changes.Add($"no {path} to remove");
                return text;
            }
            var child = mapping.Children[key];
            // A field of a part written in braces on its line, or an item of a list written so, goes from it, the rest written as
            // lines: the line of `Level: {MutagenObjectType: PcLevelMult, LevelMult: 1}` went whole with its LevelMult, and the NPC
            // with its level (refusal probes of 2026-11-02, B04).
            var flowItem = step.Index is int at && i == steps.Count - 1 && mapping.Style != YamlDotNet.Core.Events.MappingStyle.Flow
                && child is YamlSequenceNode { Style: YamlDotNet.Core.Events.SequenceStyle.Flow } flowList && at < flowList.Children.Count;
            if (flowItem && FromYaml(child) is JsonArray items)
            {
                items.RemoveAt(step.Index!.Value);
                if (items.Count == 0)
                {
                    changes.Add($"removed {path} and the {step.Key} it was the last of");
                    return RemoveLines(text, Line(key.Start), FieldEnd(text, key, child));
                }
                changes.Add($"removed {path}, with {step.Key} written as lines");
                return ReplaceField(text, key, child, Render(step.Key, items, Column(key)));
            }
            if (step.Index is null && i == steps.Count - 1 && mapping.Style == YamlDotNet.Core.Events.MappingStyle.Flow && into is not null
                && FromYaml(mapping) is JsonObject fields)
            {
                fields.Remove(step.Key);
                changes.Add($"removed {path}, with {Upto(steps, i)} written as lines");
                return ReplaceField(text, into, mapping, Render(into.Value ?? "", fields, Column(into)));
            }
            into = step.Index is null && mapping.Style != YamlDotNet.Core.Events.MappingStyle.Flow ? key : null;
            if (step.Index is int n)
            {
                if (child is not YamlSequenceNode list || n >= list.Children.Count) { changes.Add($"no {path} to remove"); return text; }
                if (i == steps.Count - 1)
                {
                    // The last item goes with its field: an `Items:` left with none reads as no list, and the build refused the
                    // file — so the call that deletes Hilda's placeholder inventory line failed (eval round 89's npc+detailed #3).
                    if (list.Children.Count == 1)
                    {
                        changes.Add($"removed {path} and the {step.Key} it was the last of");
                        return RemoveLines(text, Line(key.Start), FieldEnd(text, key, child));
                    }
                    changes.Add($"removed {path}");
                    return RemoveLines(text, Line(list.Children[n].Start), LastLine(text, list.Children[n]));
                }
                node = list.Children[n];
                continue;
            }
            if (i == steps.Count - 1)
            {
                changes.Add($"removed {path}");
                return RemoveLines(text, Line(key.Start), FieldEnd(text, key, child));
            }
            node = child;
        }
        return text;
    }

    /// <summary>The rest of a path written as nested fields: Data.Damage 12 is {Damage: 12} below Data, a part of several kinds named.</summary>
    private static JsonNode? Nest(List<Step> steps, List<Type> types, JsonNode? value)
    {
        var inner = value?.DeepClone();
        if (Translated(types[^1]) && inner is JsonValue words && words.TryGetValue<string>(out var said))
            inner = new JsonObject { ["TargetLanguage"] = "English", ["Value"] = said };
        for (var i = steps.Count - 1; i >= 1; i--)
        {
            var part = new JsonObject();
            // A part of several kinds names the one whose field this is: an NPC's Level with Level is an NpcLevel.
            if (types[i - 1].IsAbstract && Kinds(types[i - 1]).Where(k => k.GetProperty(steps[i].Key, Public) is not null).ToList() is [var kind])
                part["MutagenObjectType"] = kind.Name;
            part[steps[i].Key] = inner;
            inner = part;
        }
        return inner;
    }

    // ---------------------------------------------------------------- text

    private static string ReplaceScalar(string text, YamlScalarNode node, int keyColumn, JsonNode? value)
    {
        var start = (int)node.Start.Index;
        var end = (int)node.End.Index;
        // A block text ends where the next field begins: its own line ends are kept.
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        // The comment after a link names the record it named: with another value, it would say what is no longer there.
        var lineEnd = text.IndexOf('\n', end);
        var rest = text[end..(lineEnd < 0 ? text.Length : lineEnd)];
        if (Regex.IsMatch(rest, @"^[ \t]+#")) end += rest.Length;
        var rendered = Scalar(value, keyColumn + 2);
        if (start > 0 && text[start - 1] == ':') rendered = " " + rendered;
        return text[..start] + rendered + text[end..];
    }

    /// <summary>A field's lines — its key's line and every line of its value — written anew.</summary>
    private static string ReplaceField(string text, YamlScalarNode key, YamlNode value, List<string> lines) =>
        ReplaceLines(text, Line(key.Start), FieldEnd(text, key, value), lines);

    /// <summary>The last line of a field: its key's, for a value written empty — `Level:` — whose mark may sit on the next line.</summary>
    private static int FieldEnd(string text, YamlScalarNode key, YamlNode value) =>
        value is YamlScalarNode scalar && Empty(scalar) ? Line(key.Start) : Math.Max(Line(key.Start), LastLine(text, value));

    private static bool Empty(YamlScalarNode scalar) => scalar.Style == ScalarStyle.Plain && string.IsNullOrEmpty(scalar.Value);

    private static string ReplaceItem(string text, YamlNode item, int column, JsonNode? value)
    {
        var lines = Render("", value, column);
        var pad = new string(' ', column);
        if (value is JsonObject or JsonArray)
        {
            // The first field after the dash: `- BaseEffect: X`, the rest two spaces in.
            lines = lines.Skip(1).ToList();
            lines[0] = pad + "- " + lines[0].TrimStart();
        }
        else lines = [pad + "- " + Scalar(value, column + 2)];
        return ReplaceLines(text, Line(item.Start), LastLine(text, item), lines);
    }

    /// <summary>New lines below a mapping's last field — at the root, below the file's last line.</summary>
    private static string InsertAfter(string text, YamlMappingNode mapping, bool root, List<string> lines)
    {
        var all = text.Split('\n').ToList();
        var after = root || mapping.Children.Count == 0
            ? root ? all.FindLastIndex(l => l.Trim().Length > 0) : Line(mapping.Start)
            : Math.Max(LastLine(text, mapping.Children.Last().Value), Line(mapping.Children.Last().Key.Start));
        all.InsertRange(after + 1, lines);
        return string.Join('\n', all);
    }

    private static string ReplaceLines(string text, int first, int last, List<string> lines)
    {
        var all = text.Split('\n').ToList();
        all.RemoveRange(first, last - first + 1);
        all.InsertRange(first, lines);
        return string.Join('\n', all);
    }

    private static string RemoveLines(string text, int first, int last)
    {
        var all = text.Split('\n').ToList();
        all.RemoveRange(first, last - first + 1);
        return string.Join('\n', all);
    }

    /// <summary>
    /// A field written as Spriggit writes one: a value on its line, fields two spaces in, a list's items at the key's column. A
    /// part's `MutagenObjectType:` is its first line, in whatever order its fields were sent: the reader takes the kind from the
    /// first line, and a level sent as {"LevelMult": 1, "MutagenObjectType": "PcLevelMult"}, written so, could not be read —
    /// "Object reference not set to an instance of an object" (eval round 109's npc+detailed #1).
    /// </summary>
    private static List<string> Render(string key, JsonNode? value, int column)
    {
        var pad = new string(' ', column);
        var head = key.Length > 0 ? $"{pad}{key}:" : $"{pad}:";
        switch (value)
        {
            case JsonObject fields when fields.Count == 0:
                return [$"{head} {{}}"];
            case JsonObject fields:
                return [head, .. KindFirst(fields).SelectMany(f => Render(f.Key, f.Value, column + 2))];
            case JsonArray items when items.Count == 0:
                return [$"{head} []"];
            case JsonArray items:
                var lines = new List<string> { head };
                foreach (var item in items)
                {
                    if (item is JsonObject fields && fields.Count > 0)
                    {
                        var inner = KindFirst(fields).SelectMany(f => Render(f.Key, f.Value, column + 2)).ToList();
                        inner[0] = pad + "- " + inner[0].TrimStart();
                        lines.AddRange(inner);
                    }
                    else lines.Add($"{pad}- {Scalar(item, column + 2)}");
                }
                return lines;
            default:
                return [$"{head} {Scalar(value, column + 2)}"];
        }
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> KindFirst(JsonObject fields) =>
        fields.OrderBy(f => f.Key == "MutagenObjectType" ? 0 : 1);

    /// <summary>A value on its line: plain where YAML reads it as written, quoted where it would not, a text of several lines as a block.</summary>
    private static string Scalar(JsonNode? value, int column)
    {
        if (value is null) return "~";
        if (value is not JsonValue one) return value.ToJsonString();
        if (one.TryGetValue<bool>(out var flag)) return flag ? "true" : "false";
        if (!one.TryGetValue<string>(out var s)) return one.ToJsonString();
        if (s.Contains('\n'))
        {
            var pad = new string(' ', column);
            return "|-\n" + string.Join('\n', s.Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? "" : pad + l));
        }
        var plain = s.Length > 0 && !char.IsWhiteSpace(s[0]) && !char.IsWhiteSpace(s[^1]) && "-?:,[]{}#&*!|>'\"%@`".IndexOf(s[0]) < 0
            && !s.Contains(": ") && !s.Contains(" #") && !s.EndsWith(':')
            && !Regex.IsMatch(s, "^(?:true|false|null|yes|no|on|off|~)$", RegexOptions.IgnoreCase);
        return plain ? s : $"'{s.Replace("'", "''")}'";
    }

    // ---------------------------------------------------------------- reading

    private static YamlMappingNode? Root(string text)
    {
        var yaml = new YamlStream();
        try { yaml.Load(new StringReader(text)); }
        catch (YamlException) { return null; }
        return yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
    }

    /// <summary>Why the text's YAML does not read, or null where it does.</summary>
    private static YamlException? Unread(string text)
    {
        try { new YamlStream().Load(new StringReader(text)); return null; }
        catch (YamlException e) { return e; }
    }

    /// <summary>
    /// The record a link of the file names, for a path sent below it, as words that say where the field sent is set: the
    /// project's own record by its file, another plugin's to be copied first; null where the file names none the load order
    /// or the project has. Told only "ObjectEffect is a link to a record and has no fields — set ObjectEffect itself", the
    /// model went on to set a magnitude on an enchanted sword it had taken for the enchantment, and answered the sword built
    /// (eval round 111's sword+detailed #4).
    /// </summary>
    private static string? Linked(string text, List<Step> steps, int below, Type link, Context context)
    {
        if (RecordLinks.LinkTarget(link) is not { } target || At(text, steps.Take(below).ToList()) is not YamlScalarNode { Value: { Length: > 0 } held })
            return null;
        var where = Upto(steps, below);
        var rest = string.Join('.', steps.Skip(below).Select(s => s.Index is { } n ? $"{s.Key}[{n}]" : s.Key));
        var kind = RecordLinks.Kind(target);
        var record = target.Assembly.GetType($"{target.Namespace}.{kind}");
        // The field sent as the linked record has it: the path where it is one of its fields, else where its kind keeps that field.
        var field = record is null ? null : InRecord(record, rest);
        var last = steps[^1].Key;
        var there = field is not null ? $"{field} is that record's field"
            : record is not null && RecordFiles.WhereIn(record, last) is { } place ? $"{RecordFiles.Article(kind)}'s {last} is {place}, in that record"
            : "its fields are that record's";
        var project = ToolCalls.ProjectOf(context.PluginDir);
        var own = FormKey.TryFactory(held, out _) ? default
            : RecordFiles.FlatRecords(context.PluginDir).FirstOrDefault(f => f.Relative != context.Relative
                && string.Equals(RecordFiles.Identity(File.ReadAllText(f.File)).EditorId, held, StringComparison.OrdinalIgnoreCase));
        if (own != default)
            return $"here {held}, the project's own {kind}, in {own.Relative}: {there} — set it in that file, " +
                ToolCalls.Edit(project, own.Relative, $"{{\"path\": \"{field ?? rest}\", \"value\": …}}") + $"; or set {where} itself to another {kind}.";
        if (context.Index is not { } index) return null;
        var entry = FormKey.TryFactory(held, out var key) ? index.ByFormKey.GetValueOrDefault(key)
            : index.ByEditorId.TryGetValue(held, out var byName) ? index.ByFormKey.GetValueOrDefault(byName) : null;
        if (entry is null) return null;
        var id = entry.EditorId ?? Session.FormKeyText(entry.FormKey);
        return $"here {id} ({Session.FormKeyText(entry.FormKey)}), {RecordFiles.Article(kind)} of {entry.FormKey.ModKey.FileName}: {there}. " +
            $"To change it, copy that record into the project, addPatch {{\"project\": {ToolCalls.Quoted(project)}, \"records\": [{ToolCalls.Quoted(id)}]}}, " +
            $"or write a new {kind} from it, and set it there — {where} then names the new one; or set {where} itself to another {kind}.";
    }

    /// <summary>A path as a record type has it, its label; null where the type has no such field.</summary>
    private static string? InRecord(Type record, string path)
    {
        try
        {
            var (steps, label) = Placed(record, path);
            Types(record, steps, label);
            return label;
        }
        catch (HelperException) { return null; }
    }

    private static HelperException Unreadable() => new("badRequest",
        "The file's YAML does not read, so no field can be found in it; nothing was written. buildProject names the line where it breaks — fix that first.");

    /// <summary>The line of a mark, from 0.</summary>
    private static int Line(Mark mark) => (int)mark.Line - 1;

    /// <summary>The column a node starts at, from 0.</summary>
    private static int Column(YamlNode node) => (int)node.Start.Column - 1;

    /// <summary>The column of the dash before a list's item, from 0: `- OneHanded` at the margin is 0, its item at 2. Written at
    /// the item's column, a sword's third keyword came out under the second, which read as `VendorItemWeapon - MagicFortifyOneHanded`
    /// (eval round 106's sword #4).</summary>
    private static int DashColumn(string text, YamlNode item)
    {
        var dash = text.LastIndexOf('-', (int)item.Start.Index - 1);
        return dash - (text.LastIndexOf('\n', dash) + 1);
    }

    /// <summary>
    /// The last line of a node that holds any of it, from 0 — a block ends where the next field begins. A list's or a
    /// part's, from its last item: for a list written at its key's column, as Spriggit writes `Keywords:` and its
    /// items, the reader's end mark is its first item's, and set whole, the Iron Sword kept two keywords of three.
    /// </summary>
    private static int LastLine(string text, YamlNode node) => node switch
    {
        YamlSequenceNode list when list.Children.Count > 0 => Math.Max(Line(list.Start), list.Children.Max(item => LastLine(text, item))),
        YamlMappingNode fields when fields.Children.Count > 0 =>
            Math.Max(Line(fields.Start), fields.Children.Max(f => Math.Max(Line(f.Key.Start), f.Value is YamlScalarNode s && Empty(s) ? Line(f.Key.Start) : LastLine(text, f.Value)))),
        _ => ScalarEnd(text, node)
    };

    private static int ScalarEnd(string text, YamlNode node)
    {
        var end = (int)node.End.Index;
        while (end > (int)node.Start.Index && char.IsWhiteSpace(text[end - 1])) end--;
        return text[..Math.Max(end, (int)node.Start.Index)].Count(c => c == '\n');
    }

    private static string Upto(List<Step> steps, int count) =>
        string.Join('.', steps.Take(count).Select(s => s.Index is { } n ? $"{s.Key}[{n}]" : s.Key));

    private static string Shown(JsonNode? value) => value switch
    {
        null => "~",
        JsonValue one when one.TryGetValue<string>(out var s) => s.Length > 60 ? s[..57] + "…" : s,
        JsonValue one => one.ToJsonString(),
        _ => value.ToJsonString().Length > 60 ? value.ToJsonString()[..57] + "…" : value.ToJsonString()
    };

    private static string Shown(YamlNode node) => node switch
    {
        YamlScalarNode s => (s.Value ?? "").Length > 60 ? s.Value![..57] + "…" : s.Value ?? "",
        YamlSequenceNode list => $"{list.Children.Count} item(s)",
        YamlMappingNode fields => $"{fields.Children.Count} field(s)",
        _ => "?"
    };
}
