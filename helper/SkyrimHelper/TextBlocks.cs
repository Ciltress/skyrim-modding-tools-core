using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace SkyrimHelper;

/// <summary>
/// A record's texts of several lines written as literal blocks (`Value: |-`), whose lines are the text's own, where the
/// serializer writes folded ones (`Value: >-`), in which a line of the text is followed by a blank line and an empty line
/// by two. Maven's note writes a blank line between its parts; readRecord gave each as two, and 170 of the 357 copies of it
/// in eval rounds 87–123 wrote one — in game, the note without its blank lines. A block is written so only where it reads
/// back as the same text; a text the literal style cannot hold, as one whose first line starts with a space, stays folded.
/// </summary>
internal static class TextBlocks
{
    private static readonly Regex FoldedHead = new(@"^(?<head>[ \t]*(?:-[ \t]+)?[^\s#'""][^#]*?:)[ \t]+>(?<chomp>[-+]?)[ \t]*$");

    /// <summary>The YAML text with each folded block of several lines written as a literal one; LF line ends.</summary>
    public static string Literal(string yaml)
    {
        if (!yaml.Contains('>')) return yaml;
        var lines = yaml.Split('\n');
        var output = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var head = FoldedHead.Match(lines[i]);
            if (!head.Success)
            {
                output.Add(lines[i]);
                continue;
            }
            // The block: the lines below, blank or deeper than the line's own; blank lines after its last are the next field's.
            var column = Indent(lines[i]);
            var end = i + 1;
            while (end < lines.Length && (lines[end].Trim().Length == 0 || Indent(lines[end]) > column)) end++;
            while (end > i + 1 && lines[end - 1].Trim().Length == 0) end--;
            var block = lines[(i + 1)..end];
            var chomp = head.Groups["chomp"].Value;
            if (chomp != "+" && Value(">" + chomp, block) is { } text && text.Contains('\n')
                && Written(text, chomp, block) is { } literal && Value("|" + chomp, literal) == text)
            {
                output.Add($"{head.Groups["head"].Value} |{chomp}");
                output.AddRange(literal);
            }
            else output.AddRange(lines[i..end]);
            i = end - 1;
        }
        return string.Join('\n', output);
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ', '\t').Length;

    /// <summary>The text a block scalar of the indicator given holds, or null where YAML does not read it.</summary>
    private static string? Value(string indicator, IEnumerable<string> block)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader($"v: {indicator}\n" + string.Join('\n', block) + "\n"));
            return stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlMappingNode map
                && map.Children.TryGetValue(new YamlScalarNode("v"), out var node) && node is YamlScalarNode scalar ? scalar.Value : null;
        }
        catch (YamlDotNet.Core.YamlException) { return null; }
    }

    /// <summary>A text's lines as a literal block at the folded block's depth, or null where the chomping cannot end it so.</summary>
    private static List<string>? Written(string text, string chomp, string[] folded)
    {
        // `>-` strips the text's last line break, `>` keeps one: a literal block of the same indicator ends as its text does.
        if (chomp == "-" ? text.EndsWith('\n') : !text.EndsWith('\n') || text.EndsWith("\n\n")) return null;
        var body = chomp == "-" ? text : text[..^1];
        var depth = folded.FirstOrDefault(l => l.Trim().Length > 0) is { } first ? first[..Indent(first)] : "  ";
        return body.Split('\n').Select(l => l.Length == 0 ? "" : depth + l).ToList();
    }
}
