using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SkyrimHelper;

/// <summary>
/// A record's flags written otherwise than as its Flags list, put right before the files are checked: a flag written as a
/// field of its own is listed among the Flags it is one of, and a field of a part written as an item of the part's Flags is
/// written as that field again.
/// </summary>
internal static class FlagLines
{
    private const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>The values that set a flag written as a field: `Unique: true`, `Unique:`.</summary>
    private static readonly HashSet<string> On = new(StringComparer.OrdinalIgnoreCase) { "true", "yes", "on", "1", "" };

    /// <summary>The values that leave it off: `Unique: false`.</summary>
    private static readonly HashSet<string> Off = new(StringComparer.OrdinalIgnoreCase) { "false", "no", "off", "0" };

    /// <summary>
    /// A flag written as a field — `Unique: true` at an NPC's margin or under its Configuration — listed among the Flags it is
    /// one of, its line taken out: refused "a Npc has no field Unique (it is one of the Flags under Configuration: delete it here
    /// and list Unique there)", 3 of the 4 runs of eval rounds 87–105 that wrote one were refused twice. Written false, the line
    /// is taken out where the Flags do not list it. A value of neither, or false for a flag the Flags list, is left to the refusal.
    /// </summary>
    public static List<(string File, string Change)> IntoFlags(string pluginDir) => EachFile(pluginDir, Listed);

    /// <summary>
    /// A part's field written as an item of its Flags — an NPC's `- Level: {MutagenObjectType: PcLevelMult, LevelMult: 1}` below
    /// `- AutoCalcStats`, told an NPC's level is three lines "each indented as its Flags line is" (RecordFiles.NpcLevel, reworded
    /// since) — written as the part's field, below the list. 9 NPC runs of eval rounds 94–106 wrote their level so, CalcMinLevel
    /// and CalcMaxLevel too in two. An item naming a field the part has already is taken out where it says the same, and else
    /// left to the refusal.
    /// </summary>
    public static List<(string File, string Change)> OutOfFlags(string pluginDir) => EachFile(pluginDir, Lifted);

    /// <summary>
    /// `None` among Flags whose kind has no flag of that name — or the Flags written `None` — taken out: no flag is the list
    /// without it. Refused "Flags has None — it takes one of …", eval round 146's storymanager #3 was then told by editRecord to
    /// keep None in the list, and answered unbuilt.
    /// </summary>
    public static List<(string File, string Change)> NoneOutOfFlags(string pluginDir) => EachFile(pluginDir, NoneOut);

    private static (string Text, string Change)? NoneOut(string text, Type type)
    {
        if (MisplacedFields.Root(text) is not { } root) return null;
        var lines = text.Split('\n').ToList();
        static bool IsNone(string? name) => string.Equals(name?.Trim().Trim('"', '\''), "None", StringComparison.OrdinalIgnoreCase);
        foreach (var (mapping, owner, path) in Mappings(root, type))
            foreach (var (keyNode, value) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { } key } || owner.GetProperties(Public).FirstOrDefault(p => p.Name == key) is not { } property
                    || !IsFlags(property.PropertyType)
                    || Enum.GetNames(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType).Contains("None", StringComparer.OrdinalIgnoreCase))
                    continue;
                var label = path is null ? key : $"{path}.{key}";
                var keyRow = (int)keyNode.Start.Line - 1;
                var keyColumn = (int)keyNode.Start.Column - 1;
                switch (value)
                {
                    case YamlSequenceNode { Style: not SequenceStyle.Flow } items when items.Children.OfType<YamlScalarNode>().FirstOrDefault(i => IsNone(i.Value)) is { } none:
                        lines.RemoveAt((int)none.Start.Line - 1);
                        if (items.Children.Count == 1) lines[keyRow] = $"{new string(' ', keyColumn)}{key}: []";
                        return (string.Join('\n', lines), $"took None out of {label}: {label} have no flag None — no flag is the list without it");
                    case YamlSequenceNode { Style: SequenceStyle.Flow } flow when flow.Start.Line == keyNode.Start.Line && flow.End.Line == keyNode.Start.Line
                            && flow.Children.OfType<YamlScalarNode>().Any(i => IsNone(i.Value)):
                        lines[keyRow] = $"{new string(' ', keyColumn)}{key}: [{string.Join(", ", flow.Children.OfType<YamlScalarNode>().Where(i => !IsNone(i.Value)).Select(i => i.Value))}]";
                        return (string.Join('\n', lines), $"took None out of {label}: {label} have no flag None — no flag is the list without it");
                    case YamlScalarNode { Value: var said } when IsNone(said) && value.Start.Line == keyNode.Start.Line:
                        lines[keyRow] = $"{new string(' ', keyColumn)}{key}: []";
                        return (string.Join('\n', lines), $"wrote {label} as [], no flag: {label} have no flag None");
                }
            }
        return null;
    }

    private static List<(string File, string Change)> EachFile(string pluginDir, Func<string, Type, (string Text, string Change)?> next)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir))
        {
            var written = File.ReadAllText(file).Replace("\r\n", "\n");
            var text = written;
            // One at a time, each found in the text the last one left: the lines below it have shifted.
            for (var n = 0; n < 16 && next(text, type) is { } made; n++)
            {
                text = made.Text;
                changes.Add((relative, made.Change));
            }
            if (text != written) MisplacedFields.Save(file, text);
        }
        return changes;
    }

    /// <summary>
    /// The record's fields, and those of each part written below it — the type each mapping is of, and its path; a part of
    /// several kinds by the kind its MutagenObjectType names.
    /// </summary>
    private static IEnumerable<(YamlMappingNode Mapping, Type Type, string? Path)> Mappings(YamlMappingNode root, Type type)
    {
        yield return (root, type, null);
        foreach (var (keyNode, value) in root.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key } || value is not YamlMappingNode { Style: not MappingStyle.Flow } part
                || PartType(type, key) is not { } partType) continue;
            if (partType.IsAbstract)
            {
                if (part.Children.TryGetValue(new YamlScalarNode("MutagenObjectType"), out var kind) && kind is YamlScalarNode { Value: { } named }
                    && RecordFiles.KindOf(partType, named) is { } concrete) partType = concrete;
                else continue;
            }
            yield return (part, partType, key);
        }
    }

    /// <summary>The type of a part the record writes as fields of its own — Configuration, BasicStats — or null for a value, a text, a link or a list.</summary>
    private static Type? PartType(Type type, string key)
    {
        if (type.GetProperties(Public).FirstOrDefault(p => p.Name == key) is not { } property) return null;
        var held = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return held.IsClass && held != typeof(string) && held.Namespace == typeof(Weapon).Namespace
            && !typeof(IMajorRecordGetter).IsAssignableFrom(held) && !typeof(ITranslatedStringGetter).IsAssignableFrom(held)
            && !typeof(IFormLinkGetter).IsAssignableFrom(held) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(held) ? held : null;
    }

    private static bool IsFlags(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) is { IsEnum: true } named && named.IsDefined(typeof(FlagsAttribute), false);

    /// <summary>The first flag the file writes as a field, listed among its Flags; null where it writes none so.</summary>
    private static (string Text, string Change)? Listed(string text, Type type)
    {
        if (MisplacedFields.Root(text) is not { } root) return null;
        var lines = text.Split('\n').ToList();
        foreach (var (mapping, owner, path) in Mappings(root, type))
            foreach (var (keyNode, value) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { } key } || MisplacedFields.Has(owner, key)
                    || RecordFiles.FlagPlace(owner, key) is not (var part, { } flag)
                    || value is not YamlScalarNode { Value: var said } || value.End.Line != keyNode.Start.Line && said is { Length: > 0 }) continue;
                said ??= "";
                if (!On.Contains(said) && !Off.Contains(said)) continue;
                var line = (int)keyNode.Start.Line - 1;
                var written = lines[line].Trim();
                // The mapping whose Flags list it: this one, or its part's.
                var target = part is null ? mapping : mapping.Children.TryGetValue(new YamlScalarNode(part), out var held) ? held : null;
                var label = (part is null ? path : path is null ? part : $"{path}.{part}") is { } at ? $"{at}'s Flags" : "the record's Flags";
                var flags = target is YamlMappingNode withFlags && withFlags.Children.TryGetValue(new YamlScalarNode("Flags"), out var list) ? list : null;
                var listed = flags is YamlSequenceNode items && items.Children.OfType<YamlScalarNode>().Any(i => string.Equals(i.Value, flag, StringComparison.OrdinalIgnoreCase));
                if (Off.Contains(said))
                {
                    if (listed) continue;
                    lines.RemoveAt(line);
                    return (string.Join('\n', lines), $"removed line {line + 1}, `{written}` — {flag} is one of {label}, which do not list it, " +
                        $"and a flag is an item of that list, not a field");
                }
                if (listed)
                {
                    lines.RemoveAt(line);
                    return (string.Join('\n', lines), $"removed line {line + 1}, `{written}` — {flag} is one of {label}, which list it already");
                }
                if (Added(lines, target, flags, part, flag, line) is not { } done) continue;
                return (done, $"listed `{flag}` among {label} for line {line + 1}, `{written}`, which is taken out — a flag is an item of its " +
                    "Flags list, not a field of its own");
            }
        return null;
    }

    /// <summary>
    /// The text with <paramref name="flag"/> listed among the Flags of <paramref name="target"/> — the mapping that has them, or
    /// the part <paramref name="part"/> not written yet — and the flag's line <paramref name="line"/> taken out; null where the Flags are
    /// written otherwise than as a list, or the part as one value.
    /// </summary>
    private static string? Added(List<string> lines, YamlNode? target, YamlNode? flags, string? part, string flag, int line)
    {
        var insert = new List<string>();
        int at;
        if (target is YamlMappingNode { Style: not MappingStyle.Flow } mapping && mapping.Children.Count > 0)
        {
            var flagsKey = mapping.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == "Flags");
            var column = (int)mapping.Children.Keys.First().Start.Column - 1;
            switch (flags)
            {
                case YamlSequenceNode { Style: SequenceStyle.Flow } flow when flow.Start.Line == flow.End.Line:
                {
                    var row = (int)flow.Start.Line - 1;
                    var close = lines[row].LastIndexOf(']');
                    if (close < 0) return null;
                    lines[row] = lines[row][..close] + (flow.Children.Count > 0 ? $", {flag}" : flag) + lines[row][close..];
                    lines.RemoveAt(line);
                    return string.Join('\n', lines);
                }
                case YamlSequenceNode { Style: not SequenceStyle.Flow, Children.Count: > 0 } items:
                {
                    var last = (int)items.Children[^1].Start.Line - 1;
                    var dash = MisplacedFields.Indent(lines[last]);
                    at = last + 1;
                    while (at < lines.Count && lines[at].Trim().Length > 0 && MisplacedFields.Indent(lines[at]) > dash) at++;
                    insert.Add($"{new string(' ', dash)}- {flag}");
                    break;
                }
                case YamlScalarNode { Value: null or "" } when flagsKey is not null:
                    at = (int)flagsKey.Start.Line;
                    insert.Add($"{new string(' ', (int)flagsKey.Start.Column - 1)}- {flag}");
                    break;
                case null:
                    // As readRecord writes them: the Flags first among a part's fields; among the record's, where the flag was.
                    at = column == 0 ? line : (int)mapping.Children.Keys.First().Start.Line - 1;
                    insert.Add($"{new string(' ', column)}Flags:");
                    insert.Add($"{new string(' ', column)}- {flag}");
                    break;
                default:
                    return null;
            }
        }
        else if (part is not null && target is null or YamlScalarNode { Value: null or "" } && MisplacedFields.Indent(lines[line]) == 0)
        {
            // A part not written, or written with nothing below it: its Flags below its line, where the flag was.
            var partLine = lines.FindIndex(l => l.TrimEnd() == $"{part}:");
            if (target is not null && partLine < 0) return null;
            at = target is null ? line : partLine + 1;
            if (target is null) insert.Add($"{part}:");
            insert.Add("  Flags:");
            insert.Add($"  - {flag}");
        }
        else return null;
        if (at > line)
        {
            lines.InsertRange(at, insert);
            lines.RemoveAt(line);
        }
        else
        {
            lines.RemoveAt(line);
            lines.InsertRange(at, insert);
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The items of a part's Flags that are fields of the part, written as those fields below the list — all of one list at once,
    /// named by the lines they were written on — and an item saying what the part's field says already, taken out; null where
    /// no list holds one. Where every item goes, the list's line goes too: a list with no item reads as none.
    /// </summary>
    private static (string Text, string Change)? Lifted(string text, Type type)
    {
        if (MisplacedFields.Root(text) is not { } root) return null;
        var lines = text.Split('\n').ToList();
        foreach (var (mapping, owner, path) in Mappings(root, type))
            foreach (var (keyNode, value) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { } key } listKey || value is not YamlSequenceNode { Style: not SequenceStyle.Flow } items
                    || owner.GetProperties(Public).FirstOrDefault(p => p.Name == key) is not { } property || !IsFlags(property.PropertyType)) continue;
                var column = (int)listKey.Start.Column - 1;
                var fields = mapping.Children.Where(c => c.Key is YamlScalarNode { Value: not null })
                    .ToDictionary(c => ((YamlScalarNode)c.Key).Value!, c => c.Value, StringComparer.Ordinal);
                var lifted = new List<(int Start, int End, List<string> Lines, List<string> Names)>();
                var dropped = new List<(int Start, int End, string Written)>();
                foreach (var item in items.Children)
                {
                    if (item is not YamlMappingNode { Style: not MappingStyle.Flow } held || held.Children.Count == 0
                        || held.Children.Keys.Any(k => k is not YamlScalarNode { Value: { } name } || name == key || !MisplacedFields.Has(owner, name)))
                        continue;
                    var start = (int)item.Start.Line - 1;
                    var dash = MisplacedFields.Indent(lines[start]);
                    if (!lines[start][dash..].StartsWith("- ", StringComparison.Ordinal)) continue;
                    var end = start + 1;
                    while (end < lines.Count && (lines[end].Trim().Length == 0 || MisplacedFields.Indent(lines[end]) > dash)) end++;
                    while (end > start + 1 && lines[end - 1].Trim().Length == 0) end--;
                    var names = held.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToList();
                    // Said by the part's own field already, the same: the item says nothing more.
                    if (names.All(fields.ContainsKey))
                    {
                        if (held.Children.All(c => fields[((YamlScalarNode)c.Key).Value!] is YamlScalarNode { Value: { } kept }
                                && c.Value is YamlScalarNode { Value: { } said } && MisplacedFields.Same(kept, said)))
                            dropped.Add((start, end, lines[start].Trim()));
                        continue;
                    }
                    // A field the part has, or another item names, twice over: which one is meant is the model's to say.
                    if (names.Any(fields.ContainsKey) || lifted.Any(l => l.Names.Intersect(names).Any())) continue;
                    var shift = dash + 2 - column;
                    var block = lines.Skip(start + 1).Take(end - start - 1).ToList();
                    if (block.Any(l => l.Trim().Length > 0 && MisplacedFields.Indent(l) < dash + 2)) continue;
                    var taken = new List<string> { new string(' ', column) + lines[start][(dash + 2)..] };
                    taken.AddRange(block.Select(l => l.Trim().Length == 0 ? l : l[shift..]));
                    lifted.Add((start, end, taken, names));
                }
                if (lifted.Count == 0 && dropped.Count == 0) continue;
                var listLine = (int)listKey.Start.Line - 1;
                var gone = lifted.Select(l => (l.Start, l.End)).Concat(dropped.Select(d => (d.Start, d.End))).ToList();
                // Below the list's last line first, so the items above keep their numbers; then the items, from the last up.
                lines.InsertRange(MisplacedFields.BlockEnd(lines, listLine, column), lifted.SelectMany(l => l.Lines));
                foreach (var (from, to) in gone.OrderByDescending(b => b.Start)) lines.RemoveRange(from, to - from);
                if (gone.Count == items.Children.Count) lines.RemoveAt(listLine);
                var where = path is null ? key : $"{path}.{key}";
                var owned = path is null ? "the record" : path;
                var said = new List<string>();
                if (lifted.Count > 0)
                {
                    var names = lifted.SelectMany(l => l.Names).Select(n => $"`{n}`").ToList();
                    var one = names.Count == 1;
                    said.Add($"wrote {string.Join(", ", names)} ({Lines(lifted.Select(l => l.Start + 1).ToList())}), {(lifted.Count == 1 ? "an item" : "items")} " +
                        $"of {where}, as {(one ? "a field" : "fields")} of {owned} below that list — {(one ? $"{names[0].Trim('`')} is a field" : "each is a field")} " +
                        $"of {owned}, and an item of {key} is a flag's name alone");
                }
                foreach (var (start, _, written) in dropped)
                    said.Add($"removed line {start + 1}, `{written}`, an item of {where} — {owned} has that field already, the same");
                if (gone.Count == items.Children.Count) said.Add($"and the `{key}:` line, left with no item");
                return (string.Join('\n', lines), string.Join("; ", said));
            }
        return null;
    }

    /// <summary>"line 18", "lines 18–21", "lines 18 and 20".</summary>
    private static string Lines(List<int> numbers) =>
        numbers.Count == 1 ? $"line {numbers[0]}"
        : numbers.Zip(numbers.Skip(1)).All(p => p.Second == p.First + 1) ? $"lines {numbers[0]}–{numbers[^1]}"
        : $"lines {string.Join(", ", numbers[..^1])} and {numbers[^1]}";
}
