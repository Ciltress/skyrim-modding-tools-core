using System.Reflection;
using Mutagen.Bethesda.Strings;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SkyrimHelper;

/// <summary>
/// Moves a field written where its record's type has none to the one place it goes, before the files are checked:
/// a part's field written at the margin, under that part — an NPC's level, under Configuration — and a field written
/// a level too deep, up below the part it was written in; a magic effect's strength, into the item of Effects that
/// names it. Refused, these were what failed runs had met most: 13 of the 27 runs that failed in eval rounds 23–32 had
/// been told to move a field, and moved it back and forth — an NPC's level into Level, then to the margin, build after
/// build (round 32). A field with no one place — one "in each item of Effects", or one the place has already — is left
/// to the refusal.
/// </summary>
public static class MisplacedFields
{
    private const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;

    private static readonly HashSet<string> Strength = new(StringComparer.Ordinal) { "Magnitude", "Area", "Duration" };

    /// <summary>
    /// Moves what has one place, in each record file of the plugin folder; answers each file and what was moved.
    /// <paramref name="copiedFrom"/> names the load order's record a part's field keeps the value of — by the record's type, its
    /// file's text, the part, the field, the value kept and the one written at the margin — where the margin's is to take its
    /// place; null where it is not.
    /// </summary>
    public static List<(string File, string Change)> Move(string pluginDir, Func<Type, string, string, string, string, string, string?>? copiedFrom = null)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir))
        {
            var written = File.ReadAllText(file);
            var text = written.Replace("\r\n", "\n");
            // A file of another type's fields alone is no record of its folder's to put in order: the refusal sends it there.
            if (RecordFiles.WrittenAsOther(type, text) is not null) continue;
            // One move at a time, each found in the text the last one left: the lines below it have shifted.
            for (var moves = 0; moves < 16 && NextMove(text, type, copiedFrom) is { } move; moves++)
            {
                text = move.Text;
                changes.Add((relative, move.Change));
            }
            if (text != written.Replace("\r\n", "\n")) File.WriteAllText(file, written.Contains("\r\n") ? text.Replace("\n", "\r\n") : text);
        }
        return changes;
    }

    /// <summary>
    /// A magic effect's `Data` of Magnitude, Area and Duration, which a MagicEffect has nowhere, taken to the item of
    /// Effects that names it, where one record of the project has one such item: taken out where the item has the
    /// same already, moved in where it has none. 6 of the 8 runs the model was refused one in eval rounds 30–43 had written
    /// the spell's strength in its new effect as well, 40 in both, and 4 of those failed — round 43's spell+detailed
    /// #5 took it out, wrote it back, and answered unbuilt. Named by no item or several, or by one whose Data
    /// differs, it is left to the refusal.
    /// </summary>
    public static List<(string File, string Change)> MoveEffectData(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        var files = RecordFiles.FlatRecords(pluginDir)
            .Select(f => (f.File, f.Relative, f.Type, Text: File.ReadAllText(f.File).Replace("\r\n", "\n"))).ToList();
        foreach (var (file, relative, _, text) in files.Where(f => f.Type == typeof(Mutagen.Bethesda.Skyrim.MagicEffect)).ToList())
        {
            if (Root(text) is not { } root || root.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == "Data") is not { } dataKey
                || root.Children[dataKey] is not YamlMappingNode { Style: not MappingStyle.Flow, Children.Count: > 0 } data
                || data.Children.Any(c => c.Key is not YamlScalarNode { Value: { } k } || !Strength.Contains(k)
                    || c.Value is not YamlScalarNode { Value.Length: > 0 } || c.Value.End.Line != c.Key.Start.Line))
                continue;
            var (formKey, editorId) = RecordFiles.Identity(text);
            var formId = formKey is { } key ? Session.FormKeyText(key) : null;
            var name = editorId ?? formId ?? relative;
            var naming = files.Where(f => f.Relative != relative)
                .SelectMany(f => Root(f.Text) is { } other && At(other, "Effects") is YamlSequenceNode effects
                    ? effects.Children.Select((entry, at) => (Namer: f, Item: entry as YamlMappingNode, Index: at)) : [])
                .Where(n => n.Item is { } entry && At(entry, "BaseEffect") is YamlScalarNode { Value: { } named }
                    && (named.Equals(editorId, StringComparison.OrdinalIgnoreCase) || named.Equals(formId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (naming is not [var (namer, item, index)] || item is null) continue;
            var strength = data.Children.Select(c => (Key: ((YamlScalarNode)c.Key).Value!, Value: ((YamlScalarNode)c.Value).Value!)).ToList();
            var shown = string.Join(", ", strength.Select(s => $"{s.Key}: {s.Value}"));
            var place = $"`Effects[{index}]` of {RecordFiles.Identity(namer.Text).EditorId ?? namer.Relative}";
            var lines = text.Split('\n').ToList();
            var dataLine = (int)dataKey.Start.Line - 1;
            string change;
            if (At(item, "Data") is YamlMappingNode theirs)
            {
                if (!strength.All(s => At(theirs, s.Key) is YamlScalarNode { Value: { } had } && Same(had, s.Value))) continue;
                change = $"removed `Data` ({shown}) — {place}, which names {name}, has it already, the same; a MagicEffect has no strength of its own";
            }
            else if (At(item, "Data") is null && item.Style != MappingStyle.Flow)
            {
                // Below the item's last line, as deep as its fields.
                var namerLines = namer.Text.Split('\n').ToList();
                var first = item.Children.Keys.First();
                var column = (int)first.Start.Column - 1;
                namerLines.InsertRange(BlockEnd(namerLines, (int)first.Start.Line - 1, column - 1),
                    strength.Select(s => $"{new string(' ', column + 2)}{s.Key}: {s.Value}").Prepend($"{new string(' ', column)}Data:"));
                var moved = string.Join('\n', namerLines);
                Save(namer.File, moved);
                files[files.FindIndex(f => f.Relative == namer.Relative)] = namer with { Text = moved };
                change = $"moved `Data` ({shown}) into {place}, which names {name} — a MagicEffect has no strength of its own: " +
                    "Magnitude, Area and Duration go under Data, in the Effects item that names it";
            }
            else continue;
            lines.RemoveRange(dataLine, BlockEnd(lines, dataLine, 0) - dataLine);
            Save(file, string.Join('\n', lines));
            changes.Add((relative, change));
        }
        return changes;
    }

    /// <summary>
    /// A part's field written at the margin right below the part's line, which is left with nothing below it — `Model:`, then
    /// `File: Weapons\Iron\LongSword.nif` two spaces short — indented under the part again, with the part's other fields at
    /// the margin after it. Read so, a book was told "a Book has no field File (delete it)", a call that failed, and built
    /// without a mesh once the line was deleted by hand; a weapon's texture hashes below it were taken to the margin as the
    /// weapon's Data (refusal probes of 2026-10-31). Runs before Dedent.
    /// </summary>
    public static List<(string File, string Change)> IntoEmptyPart(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir))
        {
            var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
            var fields = RecordFiles.FieldsOf(type);
            var changed = false;
            for (var at = 0; at < lines.Length - 1; at++)
            {
                if (EmptyPartLine.Match(lines[at]) is not { Success: true } partLine) continue;
                var part = partLine.Groups["key"].Value;
                if (!fields.Contains(part) || type.GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p => p.Name == part) is not { } property)
                    continue;
                var partType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                // A text's two lines are the YAML check's to name (a text's Value is no field of the type that holds it).
                if (partType == typeof(string) || !partType.IsClass || typeof(ITranslatedStringGetter).IsAssignableFrom(partType)) continue;
                var partFields = RecordFiles.FieldsOf(partType);
                // Its fields written at the margin, each with the lines indented below it.
                var moved = new List<string>();
                var i = at + 1;
                while (i < lines.Length && MarginKey.Match(lines[i]) is { Success: true } margin && partFields.Contains(margin.Groups["key"].Value)
                       && !fields.Contains(margin.Groups["key"].Value))
                {
                    moved.Add(margin.Groups["key"].Value);
                    lines[i] = "  " + lines[i];
                    i++;
                    while (i < lines.Length && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0])) i++;
                }
                if (moved.Count == 0) continue;
                changes.Add((relative, $"moved {Quoted(moved)} under `{part}:` (line {at + 1}) — {(moved.Count == 1 ? "it is" : "they are")} " +
                    $"{part}'s, and `{part}:` had nothing below it"));
                changed = true;
            }
            if (changed) Save(file, string.Join('\n', lines));
        }
        return changes;
    }

    /// <summary>A part's line at the margin with nothing on it: `Model:`.</summary>
    private static readonly System.Text.RegularExpressions.Regex EmptyPartLine = new(@"^(?<key>[A-Z][A-Za-z0-9]*):[ \t]*$");

    /// <summary>A field's line at the margin: `File: …`.</summary>
    private static readonly System.Text.RegularExpressions.Regex MarginKey = new(@"^(?<key>[A-Za-z][A-Za-z0-9_]*):(?:[ \t]|$)");

    /// <summary>A field at the margin with a value of its own on its line, plain — no text block, quote or flow below.</summary>
    private static readonly System.Text.RegularExpressions.Regex ValueAtMargin =
        new(@"^(?<key>[A-Z][A-Za-z0-9]*):[ \t]+[^\s'""|>\[{&*!#]");

    /// <summary>An indented `Key: value`, its value on its line.</summary>
    private static readonly System.Text.RegularExpressions.Regex IndentedValue = new(@"^(?<indent>[ \t]+)(?<key>[A-Za-z0-9_]+):[ \t]+\S");

    /// <summary>
    /// Fields of the record written indented below a field at the margin that has a value of its own, where the YAML
    /// reader stops — taken to the margin. The model's edits put Flame Blade's FirstPersonModel, EquipSound and UnequipSound two
    /// spaces in below `ImpactDataSet: WPNzBlade1HandImpactSet`, and it ran out of turns on the break (eval round 47's
    /// sword #5). Lines that are no field of the record at the margin — a part's, its own line lost — are left to the refusal.
    /// </summary>
    public static List<(string File, string Change)> Dedent(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir))
        {
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            if (Root(text) is not null) continue;
            var fields = RecordFiles.FieldsOf(type);
            var lines = text.Split('\n');
            var changed = false;
            for (var at = 0; at < lines.Length - 1; at++)
            {
                if (!ValueAtMargin.IsMatch(lines[at])) continue;
                var run = Enumerable.Range(at + 1, lines.Length - at - 1).TakeWhile(i => lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0])).ToList();
                var each = run.Select(i => IndentedValue.Match(lines[i])).ToList();
                if (run.Count == 0 || each.Any(m => !m.Success || !fields.Contains(m.Groups["key"].Value))
                    || each.Select(m => m.Groups["indent"].Value).Distinct().Count() > 1) continue;
                foreach (var i in run) lines[i] = lines[i].TrimStart();
                var keys = each.Select(m => m.Groups["key"].Value).ToList();
                var them = keys.Count == 1 ? "it" : "them";
                changes.Add((relative, $"moved {Quoted(keys)} to the margin — `{lines[at].Trim()}` above {them} has a value of its own, so " +
                    $"no line goes below it, and {RecordFiles.Article(type.Name)} has {them} at the margin"));
                changed = true;
            }
            if (changed) Save(file, string.Join('\n', lines));
        }
        return changes;
    }

    /// <summary>An item of a list written as a name alone — `- AutoCalcStats`, `- 013F42:Skyrim.esm  # RightHand` — and its dash's column.</summary>
    internal static readonly System.Text.RegularExpressions.Regex NameItem =
        new(@"^(?<dash> *)-[ \t]+(?<name>[^\s'""{\[|>&*!#](?:(?!: )[^#])*?)[ \t]*(?:#.*)?$");

    /// <summary>A part's line, its fields below it: `Configuration:`.</summary>
    private static readonly System.Text.RegularExpressions.Regex PartLine = new(@"^ *(?<key>[A-Za-z][A-Za-z0-9_]*):[ \t]*(?:#.*)?$");

    /// <summary>A field's line, with or without a value on it.</summary>
    private static readonly System.Text.RegularExpressions.Regex FieldStart = new(@"^ +(?<key>[A-Za-z][A-Za-z0-9_]*):(?:[ \t]|$)");

    /// <summary>
    /// The item of a list of names a line is written below, deeper than its dash, where YAML reads the line as more words of
    /// it: the item's line, the list's own line and its key, and the keys of the parts the list is in, from the margin; null
    /// where the line is below no such item.
    /// </summary>
    internal static (int Item, int List, string Key, List<string> Path)? UnderItem(string[] lines, int line)
    {
        var depth = Indent(lines[line]);
        var item = Array.FindLastIndex(lines, line - 1, l => l.Trim().Length > 0 && Indent(l) < depth);
        if (item < 1 || NameItem.Match(lines[item]) is not { Success: true } named || named.Groups["name"].Value.EndsWith(':')) return null;
        var dash = named.Groups["dash"].Length;
        var list = Array.FindLastIndex(lines, item - 1, l => l.Trim().Length > 0 && Indent(l) <= dash && !l.TrimStart().StartsWith('-'));
        if (list < 0 || PartLine.Match(lines[list]) is not { Success: true } key) return null;
        var path = new List<string>();
        var above = Indent(lines[list]);
        for (var i = list - 1; i >= 0 && above > 0; i--)
        {
            if (lines[i].Trim().Length == 0 || Indent(lines[i]) >= above) continue;
            if (PartLine.Match(lines[i]) is not { Success: true } part) return null;
            path.Insert(0, part.Groups["key"].Value);
            above = Indent(lines[i]);
        }
        return (item, list, key.Groups["key"].Value, path);
    }

    /// <summary>What a path of field names from the record leads to: the part, or a list's item; null where a name is no field.</summary>
    private static Type? PartAt(Type type, IEnumerable<string> path)
    {
        foreach (var key in path)
        {
            if (type.GetProperties(Public).FirstOrDefault(p => p.Name == key) is not { } property) return null;
            type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type != typeof(string) && type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                type = type.GetGenericArguments()[0];
        }
        return type;
    }

    /// <summary>
    /// Fields of a part written deeper than an item of a list of names above them, where the YAML reader stops — written as
    /// deep as the list's own line, beside it. The build moved an NPC's level lines under Configuration, and the model's edit
    /// indented them again, four spaces deep below `- AutoCalcStats`, an item of Flags: YAML read them as more words of that
    /// item, and the refusal blamed the level's value (eval round 107's npc+detailed #3). Lines that are no field of the part,
    /// or a file that reads no better, are left to the refusal.
    /// </summary>
    public static List<(string File, string Change)> OutFromUnderItems(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir))
        {
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            if (Root(text) is not null) continue;
            var lines = text.Split('\n');
            var moved = new List<string>();
            for (var at = 1; at < lines.Length; at++)
            {
                if (lines[at].Trim().Length == 0 || UnderItem(lines, at) is not { } under || under.Item != at - 1) continue;
                var run = Enumerable.Range(at, lines.Length - at)
                    .TakeWhile(i => lines[i].Trim().Length > 0 && Indent(lines[i]) > Indent(lines[under.Item])).ToList();
                var top = run.Min(i => Indent(lines[i]));
                var listDepth = Indent(lines[under.List]);
                var fields = PartAt(type, under.Path) is { } owner ? RecordFiles.FieldsOf(owner) : null;
                var heads = run.Where(i => Indent(lines[i]) == top).Select(i => (Line: i, Match: FieldStart.Match(lines[i]))).ToList();
                if (fields is null || heads.Any(h => !h.Match.Success || !fields.Contains(h.Match.Groups["key"].Value) || h.Match.Groups["key"].Value == under.Key))
                {
                    at = run[^1];
                    continue;
                }
                foreach (var i in run) lines[i] = lines[i][(top - listDepth)..];
                var keys = heads.Select(h => h.Match.Groups["key"].Value).ToList();
                var them = keys.Count == 1 ? "it" : "them";
                moved.Add($"moved {Quoted(keys)} (line{(heads.Count == 1 ? "" : "s")} {RecordFiles.Numbers(heads.Select(h => h.Line + 1).ToList())}) " +
                    $"from under `{lines[under.Item].Trim()}` to the depth of `{under.Key}:` — an item of a list of names holds no lines below it, " +
                    $"and YAML read {them} as more words of it; {(under.Path.Count > 0 ? under.Path[^1] : RecordFiles.Article(type.Name))} " +
                    $"has {them} beside its {under.Key}");
                at = run[^1];
            }
            var mended = string.Join('\n', lines);
            if (moved.Count == 0 || Root(mended) is null) continue;
            Save(file, mended);
            changes.AddRange(moved.Select(m => (relative, m)));
        }
        return changes;
    }

    /// <summary>
    /// A part's `MutagenObjectType:` written below its other fields, moved up as its first: the reader takes a part of several
    /// kinds by its first field. An NPC's level written with `LevelMult: 1` above its kind was refused "cannot be read as a
    /// record: Object reference not set to an instance of an object", which names nothing, and the model wrote the file four
    /// times before it built (eval round 110's npc #5); editRecord writes the kind first since round 109. A kind on a line of
    /// its own, or in braces on one line; any other is left to the refusal. A cell's references too, whose kind is their
    /// file's: a chest's reference written again with its FormKey line above `MutagenObjectType: PlacedObject` was refused
    /// so twice, and its run ended unbuilt (the mini-round after eval round 128, world+detailed #2).
    /// </summary>
    public static List<(string File, string Change)> KindFirst(string pluginDir)
    {
        var changes = new List<(string File, string Change)>();
        var files = RecordFiles.FlatRecords(pluginDir).Select(f => (f.File, f.Relative))
            .Concat(RecordFiles.CellRecords(pluginDir).Select(c => (c.File, c.Relative)))
            .Concat(RecordFiles.DialogRecords(pluginDir).Select(d => (d.File, d.Relative)));
        foreach (var (file, relative) in files)
        {
            var written = File.ReadAllText(file);
            var text = written.Replace("\r\n", "\n");
            for (var moves = 0; moves < 16 && Root(text) is { } root && KindLast(root, "", text.Split('\n')) is { } move; moves++)
            {
                text = move.Text;
                changes.Add((relative, move.Change));
            }
            if (text != written.Replace("\r\n", "\n")) Save(file, text);
        }
        return changes;
    }

    /// <summary>The first part at or below <paramref name="node"/> whose kind is not its first field, written with it first; or null.</summary>
    private static Moved? KindLast(YamlNode node, string path, string[] lines)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                var keys = mapping.Children.Keys.ToList();
                // An archetype's kind at a magic effect's margin is no kind of the record: moved first, it read as the file's, and the
                // refusal's "move it under Archetype" was undone (refusal probe after eval round 128's effect+detailed #5).
                if (keys.FindIndex(k => k is YamlScalarNode { Value: "MutagenObjectType" }) is var at and > 0
                    && !(path.Length == 0 && mapping.Children[keys[at]] is YamlScalarNode { Value: { } named } && named.EndsWith("Archetype", StringComparison.Ordinal))
                    && KindFirstIn(mapping, keys[0], keys[at], path, lines) is { } moved)
                    return moved;
                foreach (var (key, value) in mapping.Children)
                    if (key is YamlScalarNode { Value: { } name } && KindLast(value, path.Length == 0 ? name : $"{path}.{name}", lines) is { } inner)
                        return inner;
                return null;
            case YamlSequenceNode list:
                for (var i = 0; i < list.Children.Count; i++)
                    if (KindLast(list.Children[i], $"{path}[{i}]", lines) is { } inner) return inner;
                return null;
            default:
                return null;
        }
    }

    /// <summary><paramref name="mapping"/>'s kind written as its first field, above <paramref name="first"/>; null where it cannot be moved so.</summary>
    private static Moved? KindFirstIn(YamlMappingNode mapping, YamlNode first, YamlNode kindKey, string path, string[] lines)
    {
        if (mapping.Children[kindKey] is not YamlScalarNode { Value: { Length: > 0 } kind } value || value.End.Line != kindKey.Start.Line) return null;
        var part = path.Length == 0 ? "the record's" : $"{path}'s";
        string? mended;
        string change;
        if (mapping.Style == MappingStyle.Flow)
        {
            // Fields of one line, each a plain value: the closing brace is the first after the last value.
            if (mapping.Start.Line != mapping.End.Line || mapping.Children.Values.Any(v => v is not YamlScalarNode)) return null;
            var row = (int)mapping.Start.Line - 1;
            var line = lines[row];
            var from = (int)mapping.Start.Column - 1;
            var close = line.IndexOf('}', (int)mapping.Children.Values.Last().End.Column - 1);
            if (line[from] != '{' || close < 0) return null;
            var to = close + 1;
            var braced = line[from..to];
            var pairs = mapping.Children.Keys.OrderBy(k => k == kindKey ? 0 : 1)
                .Select(k => line[((int)k.Start.Column - 1)..((int)mapping.Children[k].End.Column - 1)]);
            var reordered = "{" + string.Join(", ", pairs) + "}";
            var all = (string[])lines.Clone();
            all[row] = line[..from] + reordered + line[to..];
            mended = string.Join('\n', all);
            change = $"wrote line {row + 1}, {part} `{braced}`, as `{reordered}`";
        }
        else
        {
            var (kindRow, firstRow, column) = ((int)kindKey.Start.Line - 1, (int)first.Start.Line - 1, (int)first.Start.Column - 1);
            if ((int)kindKey.Start.Column - 1 != column || lines[kindRow][..column].Trim().Length > 0) return null;
            var all = lines.ToList();
            var kindLine = all[kindRow][column..];
            var firstLine = all[firstRow];
            all.RemoveAt(kindRow);
            all[firstRow] = new string(' ', column) + firstLine[column..];
            all.Insert(firstRow, firstLine[..column] + kindLine);
            mended = string.Join('\n', all);
            change = $"moved `MutagenObjectType: {kind}` (line {kindRow + 1}) up to line {firstRow + 1}, as {part} first field, " +
                $"above `{firstLine[column..].Trim()}`";
        }
        return Root(mended) is null ? null
            : new Moved(mended, $"{change} — a part of several kinds is read by its first field, MutagenObjectType, which names its kind");
    }

    /// <summary>Whether two values written are the same: 40 and 40.0 are.</summary>
    internal static bool Same(string a, string b) =>
        a == b || double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) && x == y;

    /// <summary>Writes <paramref name="text"/>, of \n line ends, with the line ends the file has.</summary>
    internal static void Save(string file, string text) =>
        File.WriteAllText(file, File.ReadAllText(file).Contains("\r\n") ? text.Replace("\n", "\r\n") : text);

    private static YamlNode? At(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    /// <summary>The file's fields, or null where it reads as none — RecordLinks names a break.</summary>
    internal static YamlMappingNode? Root(string text)
    {
        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(text));
        }
        catch (YamlException)
        {
            return null;
        }
        return yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
    }

    private sealed record Moved(string Text, string Change);

    /// <summary>The first move the file needs — fields at the margin first, then those a level too deep — or null.</summary>
    private static Moved? NextMove(string text, Type type, Func<Type, string, string, string, string, string, string?>? copiedFrom)
    {
        if (Root(text) is not { } root) return null;
        var lines = text.Split('\n').ToList();
        var known = RecordFiles.FieldsOf(type);
        var parts = new Dictionary<string, List<YamlScalarNode>>(StringComparer.Ordinal);
        foreach (var key in root.Children.Keys.OfType<YamlScalarNode>())
            if (key.Value is { } name && !known.Contains(name) && RecordFiles.PartFor(type, name, text) is { } part)
                (parts.TryGetValue(part, out var keys) ? keys : parts[part] = []).Add(key);
        foreach (var (part, keys) in parts)
            if (Under(lines, root, type, part, keys, copiedFrom) is { } under) return under;
        return Up(lines, text, root, type, "", null, null, null) ?? Wrap(lines, root, type, "");
    }

    /// <summary>
    /// A part written as one value where the part has a field of its own name — an NPC's `- Item: 04F78D:Skyrim.esm`
    /// with `Count: 1` beside it, where an item is `Item:` with `Item:` and `Count:` below — written as that field, a
    /// level deeper, with the one-line fields beside it that the part has. Refused, both runs that wrote an
    /// NPC's items so answered without building again (eval rounds 36 and 38). Walked as Up walks a record.
    /// </summary>
    private static Moved? Wrap(List<string> lines, YamlMappingNode mapping, Type type, string path)
    {
        if (mapping.Children.TryGetValue(new YamlScalarNode("MutagenObjectType"), out var kind)
            && kind is YamlScalarNode { Value: { Length: > 0 } named } && RecordFiles.KindOf(type, named) is { } concrete)
            type = concrete;
        foreach (var (keyNode, value) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key } field) continue;
            var property = type.GetProperties(Public).Where(p => p.Name == key).OrderBy(p => p.DeclaringType == type ? 0 : 1).FirstOrDefault();
            if (property is null) continue;
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (propertyType == typeof(string) || typeof(ITranslatedStringGetter).IsAssignableFrom(propertyType)
                || RecordLinks.LinkTarget(propertyType) is not null) continue;
            var fieldPath = path.Length == 0 ? key : $"{path}.{key}";
            var listed = typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType);
            if (value is YamlScalarNode { Value: { Length: > 0 } one } && propertyType.IsClass && !propertyType.IsAbstract && !listed
                && propertyType.GetProperties(Public).Any(p => p.Name == key && p.CanWrite))
                return Wrapped(lines, mapping, field, one, type, propertyType, fieldPath);
            if (value is YamlMappingNode nested && !listed)
            {
                if (Wrap(lines, nested, propertyType, fieldPath) is { } moved) return moved;
            }
            else if (value is YamlSequenceNode items && RecordLinks.ElementType(propertyType) is { } element)
                for (var i = 0; i < items.Children.Count; i++)
                    if (items.Children[i] is YamlMappingNode inner && Wrap(lines, inner, element, $"{fieldPath}[{i}]") is { } moved)
                        return moved;
        }
        return null;
    }

    /// <summary>The part at <paramref name="at"/>, written as <paramref name="one"/>, written as its own field below it, with those beside it it has.</summary>
    private static Moved? Wrapped(List<string> lines, YamlMappingNode mapping, YamlScalarNode at, string one, Type type, Type part, string path)
    {
        var key = at.Value!;
        var (line, column) = ((int)at.Start.Line - 1, (int)at.Start.Column - 1);
        var beside = mapping.Children
            .Where(c => c.Key is YamlScalarNode { Value: { } n } && n != key && n != "MutagenObjectType" && !Has(type, n) && part.GetProperties(Public).Any(p => p.Name == n))
            .Select(c => (Key: (YamlScalarNode)c.Key, c.Value)).ToList();
        // Only fields of one line, each on a line of its own: a block, or the field after a list item's dash, is left to the refusal.
        if (beside.Any(b => b.Value is not YamlScalarNode || b.Value.End.Line != b.Key.Start.Line
                || lines[(int)b.Key.Start.Line - 1][..((int)b.Key.Start.Column - 1)].Trim().Length > 0))
            return null;
        var rest = lines[line][(column + key.Length)..].TrimStart().TrimStart(':').Trim();
        var taken = beside.Select(b => (Line: (int)b.Key.Start.Line - 1, Text: lines[(int)b.Key.Start.Line - 1].Trim())).ToList();
        foreach (var (index, _) in taken.OrderByDescending(t => t.Line)) lines.RemoveAt(index);
        var keyLine = line - taken.Count(t => t.Line < line);
        var depth = new string(' ', column + 2);
        lines[keyLine] = lines[keyLine][..column] + $"{key}:";
        lines.InsertRange(keyLine + 1, taken.Select(t => depth + t.Text).Prepend($"{depth}{key}: {rest}"));
        var names = beside.Select(b => b.Key.Value!).ToList();
        return new Moved(string.Join('\n', lines),
            $"moved `{key}: {one}`{(names.Count > 0 ? $" and {Quoted(names)}" : "")} under `{path}:` — it takes fields of its own, " +
            string.Join(", ", part.GetProperties(Public).Where(p => p.CanWrite).Select(p => p.Name)));
    }

    /// <summary>
    /// Fields at the margin moved under the part they belong to, below its last line and as deep as its fields — or,
    /// where the record has no such part, under one written in their place. Told to move them, the model put them inside the
    /// field above, or wrote them back at the margin (eval rounds 22 and 32).
    /// </summary>
    private static Moved? Under(List<string> lines, YamlMappingNode root, Type type, string part, List<YamlScalarNode> keys,
        Func<Type, string, string, string, string, string, string?>? copiedFrom = null)
    {
        if (type.GetProperties(Public).FirstOrDefault(p => p.Name == part) is not { } property) return null;
        var names = keys.Select(k => k.Value!).ToList();
        int insertAt, indent;
        var create = false;
        if (root.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == part) is { } partKey)
        {
            var value = root.Children[partKey];
            if (value is YamlMappingNode { Style: not MappingStyle.Flow } mapping)
            {
                // A field the part has already would be written twice: which one is meant is the model's to say — but
                // the same one value says nothing more at the margin. The model's sword+detailed #2 set BasicStats' Value to
                // the 150 it had written at the margin, was told to move it, and answered unbuilt (eval round 37).
                if (mapping.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: { } n } && names.Contains(n)) is { Key: YamlScalarNode inPart } had)
                {
                    var atMargin = keys.First(k => k.Value == inPart.Value);
                    if (had.Value is not YamlScalarNode { Value: { } kept } keptNode || root.Children[atMargin] is not YamlScalarNode { Value: { } written })
                        return null;
                    var start = (int)atMargin.Start.Line - 1;
                    var dropped = lines[start].TrimEnd('\r').Trim();
                    // The part's one kept from the record the file was written after, the margin's the one written for it: the
                    // margin's in its place. Told to write the value meant under BasicStats, where the Iron Sword's 25 was, and
                    // delete the margin's 150, 2 of 7 runs of eval rounds 87–101 were refused again. Only where copiedFrom finds it
                    // clear which is which; else the refusal below stands.
                    if (kept != written)
                    {
                        if (keptNode.Start.Line != keptNode.End.Line
                            || copiedFrom?.Invoke(type, string.Join('\n', lines), part, inPart.Value!, kept, written) is not { } source)
                            return null;
                        var keptLine = (int)keptNode.Start.Line - 1;
                        var column = (int)keptNode.Start.Column - 1;
                        if (lines[keptLine].Length < column + kept.Length || lines[keptLine].Substring(column, kept.Length) != kept) return null;
                        lines[keptLine] = lines[keptLine][..column] + written + lines[keptLine][(column + kept.Length)..];
                        lines.RemoveRange(start, BlockEnd(lines, start, 0) - start);
                        return new Moved(string.Join('\n', lines), $"set `{part}`'s `{inPart.Value}: {kept}` to {written}, and removed `{dropped}` at the " +
                            $"margin — {kept} is {source}'s, the record this one was written after, and {written} the one written for it");
                    }
                    lines.RemoveRange(start, BlockEnd(lines, start, 0) - start);
                    return new Moved(string.Join('\n', lines), $"removed `{dropped}` at the margin — `{part}` has it already, the same");
                }
                indent = mapping.Children.Keys.FirstOrDefault() is { } first ? (int)first.Start.Column - 1 : 2;
            }
            else if (value is YamlScalarNode { Value: null or "" }) indent = 2;
            else return null;
            insertAt = BlockEnd(lines, (int)partKey.Start.Line - 1, 0);
        }
        else
        {
            // A part of several kinds names its kind first; which kind is not the build's to say.
            if ((Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType).IsAbstract) return null;
            (insertAt, indent, create) = ((int)keys[0].Start.Line - 1, 2, true);
        }
        var blocks = keys.Select(k => (Start: (int)k.Start.Line - 1, End: BlockEnd(lines, (int)k.Start.Line - 1, 0))).ToList();
        var taken = blocks.SelectMany(b => lines.Skip(b.Start).Take(b.End - b.Start))
            .Select(line => line.Length == 0 ? line : new string(' ', indent) + line).ToList();
        if (create) taken.Insert(0, $"{part}:");
        // Taken out from the last one up, so the lines above keep their numbers; put in where the part's block ended,
        // less the lines taken out above that.
        foreach (var (start, end) in blocks.OrderByDescending(b => b.Start)) lines.RemoveRange(start, end - start);
        insertAt -= blocks.Where(b => b.Start < insertAt).Sum(b => b.End - b.Start);
        lines.InsertRange(insertAt, taken);
        var them = names.Count > 1 ? "them" : "it";
        return new Moved(string.Join('\n', lines),
            $"moved {Quoted(names)} under `{part}`{(create ? $", a `{part}:` line written above {them}" : "")} — " +
            $"{RecordFiles.Article(type.Name)} has {them} there, not at the margin");
    }

    /// <summary>
    /// Fields written a level too deep, moved up below the part they were written in, as deep as the part — the first
    /// part found holding any, all of its at once. Walked as RecordLinks walks a record: a part's kind from its
    /// MutagenObjectType; a list's items have no field above them to move to. <paramref name="at"/> is the part's key,
    /// <paramref name="parent"/> and <paramref name="above"/> the type and mapping it is a field of.
    /// </summary>
    private static Moved? Up(List<string> lines, string text, YamlMappingNode mapping, Type type, string path,
        Type? parent, YamlMappingNode? above, YamlScalarNode? at)
    {
        if (mapping.Children.TryGetValue(new YamlScalarNode("MutagenObjectType"), out var kind)
            && kind is YamlScalarNode { Value: { Length: > 0 } named } && RecordFiles.KindOf(type, named) is { } concrete)
            type = concrete;
        if (at is not null && parent is not null && above is not null && !type.IsInterface)
        {
            var theirs = parent.GetProperties(Public).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var up = mapping.Children.Keys.OfType<YamlScalarNode>()
                .Where(k => k.Value is { } name && name != "MutagenObjectType" && !Has(type, name) && theirs.Contains(name)).ToList();
            // Written twice above, a field is refused as a duplicate: left to the refusal, as at the margin.
            if (up.Count > 0 && !above.Children.Keys.OfType<YamlScalarNode>().Any(k => up.Any(u => u.Value == k.Value))
                && UpFrom(lines, text, mapping, at, up, path) is { } moved)
                return moved;
        }
        foreach (var (keyNode, value) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key } field) continue;
            var property = type.GetProperties(Public).Where(p => p.Name == key).OrderBy(p => p.DeclaringType == type ? 0 : 1).FirstOrDefault();
            if (property is null) continue;
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            // A field of the part written inside one of its texts: the model's edit wrote `ObjectEffect: …` where Description's
            // Value was, the reader kept the text's own lines and skipped it without a word, and Flame Blade built with no
            // enchantment (eval round 53's sword #2). A text holds its TargetLanguage and its Value, or Values.
            if (typeof(ITranslatedStringGetter).IsAssignableFrom(propertyType) && value is YamlMappingNode { Style: not MappingStyle.Flow } words)
            {
                var up = words.Children.Keys.OfType<YamlScalarNode>()
                    .Where(k => k.Value is { } name && !RecordLinks.TextFields.Contains(name) && Has(type, name)).ToList();
                if (up.Count > 0 && !mapping.Children.Keys.OfType<YamlScalarNode>().Any(k => up.Any(u => u.Value == k.Value))
                    && UpFrom(lines, text, words, field, up, path.Length == 0 ? key : $"{path}.{key}") is { } lifted)
                    return lifted;
                continue;
            }
            if (propertyType == typeof(string) || typeof(ITranslatedStringGetter).IsAssignableFrom(propertyType)
                || RecordLinks.LinkTarget(propertyType) is not null) continue;
            var fieldPath = path.Length == 0 ? key : $"{path}.{key}";
            if (value is YamlMappingNode nested && !typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType))
            {
                if (Up(lines, text, nested, propertyType, fieldPath, type, mapping, field) is { } moved) return moved;
            }
            else if (value is YamlSequenceNode items && RecordLinks.ElementType(propertyType) is { } element)
            {
                var i = 0;
                foreach (var item in items.Children)
                    if (item is YamlMappingNode inner && Up(lines, text, inner, element, $"{fieldPath}[{i++}]", null, null, null) is { } moved)
                        return moved;
            }
        }
        return null;
    }

    /// <summary>The fields <paramref name="up"/> taken out of the part at <paramref name="at"/> and written below it, as deep as it is.</summary>
    private static Moved? UpFrom(List<string> lines, string text, YamlMappingNode mapping, YamlScalarNode at, List<YamlScalarNode> up, string path)
    {
        var names = up.Select(k => k.Value!).ToList();
        var partLine = (int)at.Start.Line - 1;
        var partColumn = (int)at.Start.Column - 1;
        if (mapping.Style == MappingStyle.Flow)
        {
            // `Level: {MutagenObjectType: PcLevelMult, LevelMult: 1, CalcMinLevel: 1}` — on one line, of one values only. The
            // node's End is its start: the brace that closes it is the first after its last value.
            var closing = text.IndexOf('}', (int)mapping.Children.Values.Last().End.Index);
            var lineEnd = text.IndexOf('\n', (int)mapping.Start.Index);
            if (mapping.Start.Line != at.Start.Line || closing < 0 || lineEnd >= 0 && lineEnd < closing
                || mapping.Children.Values.Any(v => v is not YamlScalarNode))
                return null;
            string Pair(KeyValuePair<YamlNode, YamlNode> c) => $"{Raw(text, c.Key)}: {Raw(text, c.Value)}";
            var lineStart = text.LastIndexOf('\n', Math.Max(0, (int)mapping.Start.Index - 1)) + 1;
            var line = lines[partLine];
            var (open, close) = ((int)mapping.Start.Index - lineStart, closing + 1 - lineStart);
            lines[partLine] = line[..open] + "{" +
                string.Join(", ", mapping.Children.Where(c => !names.Contains(((YamlScalarNode)c.Key).Value!)).Select(Pair)) + "}" + line[close..];
            lines.InsertRange(partLine + 1, mapping.Children.Where(c => names.Contains(((YamlScalarNode)c.Key).Value!))
                .Select(c => new string(' ', partColumn) + Pair(c)));
        }
        else
        {
            var partEnd = BlockEnd(lines, partLine, partColumn);
            var keys = mapping.Children.Keys.OfType<YamlScalarNode>().ToList();
            var blocks = new List<(int Start, int End)>();
            foreach (var key in up)
            {
                var start = (int)key.Start.Line - 1;
                var next = keys.IndexOf(key) + 1 < keys.Count ? (int)keys[keys.IndexOf(key) + 1].Start.Line - 1 : partEnd;
                var end = Math.Min(next, partEnd);
                while (end > start + 1 && lines[end - 1].Trim().Length == 0) end--;
                blocks.Add((start, end));
            }
            var depth = (int)up[0].Start.Column - 1 - partColumn;
            var taken = blocks.SelectMany(b => lines.Skip(b.Start).Take(b.End - b.Start)).ToList();
            // Each line as much shallower as the part's fields are deeper than the part; one less deep is not the field's.
            if (depth <= 0 || taken.Any(line => line.Trim().Length > 0 && !line.StartsWith(new string(' ', depth), StringComparison.Ordinal)))
                return null;
            foreach (var (start, end) in blocks.OrderByDescending(b => b.Start)) lines.RemoveRange(start, end - start);
            lines.InsertRange(partEnd - blocks.Sum(b => b.End - b.Start), taken.Select(line => line.Length < depth ? line.TrimStart() : line[depth..]));
        }
        var dot = path.LastIndexOf('.');
        var (part, into) = (path[(dot + 1)..], dot < 0 ? "the margin" : $"`{path[..dot]}`");
        return new Moved(string.Join('\n', lines),
            $"moved {Quoted(names)} out of `{path}` up to {into}, below `{part}` — " +
            (names.Count > 1 ? $"they are not fields of {part}" : $"it is not a field of {part}"));
    }

    /// <summary>Whether a part has the field — one of its kinds, where the part is of several and names none.</summary>
    internal static bool Has(Type type, string key) =>
        type.GetProperties(Public).Any(p => p.Name == key)
        || type.IsAbstract && type.Assembly.GetTypes().Any(t => !t.IsAbstract && type.IsAssignableFrom(t) && t.GetProperties(Public).Any(p => p.Name == key));

    /// <summary>
    /// Where the block of the field on line <paramref name="start"/> ends, as an index past its last line: the lines
    /// below it that are deeper than <paramref name="column"/>, blank, or a list's items at its depth — trailing blank
    /// lines left where they are.
    /// </summary>
    internal static int BlockEnd(List<string> lines, int start, int column)
    {
        var end = start + 1;
        while (end < lines.Count && (lines[end].Trim().Length == 0 || Indent(lines[end]) > column
            || Indent(lines[end]) == column && lines[end].TrimStart().StartsWith('-')))
            end++;
        while (end > start + 1 && lines[end - 1].Trim().Length == 0) end--;
        return end;
    }

    internal static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static string Raw(string text, YamlNode node) => text[(int)node.Start.Index..(int)node.End.Index];

    private static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"`{n}`"));
}
