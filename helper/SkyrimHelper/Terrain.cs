using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SkyrimHelper;

/// <summary>
/// An exterior cell's ground as its project file writes it: the `Landscape` block of the cell's RecordData.yaml, its
/// `VertexHeightMap` — an `Offset` and 33 rows of 33 signed steps of 8 units, along each row and down the first column —
/// and its `VertexNormals`, 33 rows of 33 `'x, y, z'` bytes (`'0, 0, 127'` points straight up). A height is `h[y, x]`,
/// the vertex at x · 128, y · 128 from the cell's corner, as CellScene.Heights reads it from a plugin.
/// </summary>
internal static class Terrain
{
    public const int Size = 33;
    public const float Spacing = CellContents.CellSize / 32f;

    private static readonly Regex Row = new(@"^[ \t]*-[ \t]*\[(?<items>[^\]]*)\]", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>Whether a cell's file holds its ground.</summary>
    public static bool HasLandscape(string text) => Regex.IsMatch(text, @"^Landscape:[ \t]*\r?$", RegexOptions.Multiline);

    /// <summary>The `Landscape:` block of a cell's file, its lines from the key on; null where it has none.</summary>
    public static string? LandscapeBlock(string text)
    {
        var lines = Lines(text);
        var at = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^Landscape:[ \t]*$"));
        if (at < 0) return null;
        var end = at + 1;
        while (end < lines.Length && (lines[end].Length == 0 || lines[end].StartsWith(' ') || lines[end].StartsWith('-'))) end++;
        return string.Join('\n', lines[at..end]);
    }

    /// <summary>The heights a file's `VertexHeightMap` gives, `h[y, x]`; null where it has none, or not 33 by 33.</summary>
    public static float[,]? HeightsIn(string text)
    {
        var lines = Lines(text);
        var at = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^  VertexHeightMap:[ \t]*$"));
        if (at < 0) return null;
        float? offset = null;
        var rows = new List<int[]>();
        for (var i = at + 1; i < lines.Length && (lines[i].StartsWith("    ") || lines[i].StartsWith("  -") || lines[i].Length == 0); i++)
        {
            if (Regex.Match(lines[i], @"^    Offset:[ \t]*(?<n>-?[\d.]+)") is { Success: true } o) offset = float.Parse(o.Groups["n"].Value, CultureInfo.InvariantCulture);
            else if (Row.Match(lines[i]) is { Success: true } r)
                rows.Add(r.Groups["items"].Value.Split(',').Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray());
        }
        if (offset is null || rows.Count != Size || rows.Any(r => r.Length != Size)) return null;
        var heights = new float[Size, Size];
        var rowStart = offset.Value;
        for (var y = 0; y < Size; y++)
        {
            rowStart += rows[y][0];
            var height = rowStart;
            heights[y, 0] = height * 8;
            for (var x = 1; x < Size; x++)
            {
                height += rows[y][x];
                heights[y, x] = height * 8;
            }
        }
        return heights;
    }

    /// <summary>
    /// Heights as a `VertexHeightMap`'s offset and rows: each height to the nearest step of 8, each step between neighbours
    /// within a byte — one steeper is cut there, and the heights after it follow the cut one (counted in `clamped`).
    /// </summary>
    public static (float Offset, int[][] Rows, int Clamped) Encode(float[,] heights)
    {
        var clamped = 0;
        var rows = new int[Size][];
        var q = new int[Size, Size];
        for (var y = 0; y < Size; y++) for (var x = 0; x < Size; x++) q[y, x] = (int)Math.Round(heights[y, x] / 8, MidpointRounding.AwayFromZero);
        var offset = q[0, 0];
        var previousRowStart = offset;
        for (var y = 0; y < Size; y++)
        {
            rows[y] = new int[Size];
            var step = Clamp(q[y, 0] - previousRowStart, ref clamped);
            rows[y][0] = step;
            var height = previousRowStart + step;
            previousRowStart = height;
            for (var x = 1; x < Size; x++)
            {
                var next = Clamp(q[y, x] - height, ref clamped);
                rows[y][x] = next;
                height += next;
            }
        }
        return (offset, rows, clamped);
    }

    private static int Clamp(int step, ref int clamped)
    {
        if (step is >= sbyte.MinValue and <= sbyte.MaxValue) return step;
        clamped++;
        return Math.Clamp(step, sbyte.MinValue, sbyte.MaxValue);
    }

    /// <summary>
    /// The normal at each vertex as the file writes it, `'x, y, z'` bytes, from the slope between its neighbours — those
    /// of the cells beside it where `at` has them, so a normal on the edge two cells share is the same in both.
    /// </summary>
    public static string[][] Normals(float[,] heights, Func<int, int, float?> at)
    {
        float H(int x, int y) => x is >= 0 and < Size && y is >= 0 and < Size ? heights[y, x] : at(x, y) ?? heights[Math.Clamp(y, 0, 32), Math.Clamp(x, 0, 32)];
        var rows = new string[Size][];
        for (var y = 0; y < Size; y++)
        {
            rows[y] = new string[Size];
            for (var x = 0; x < Size; x++)
            {
                var dx = (H(x + 1, y) - H(x - 1, y)) / (2 * Spacing);
                var dy = (H(x, y + 1) - H(x, y - 1)) / (2 * Spacing);
                var length = MathF.Sqrt(dx * dx + dy * dy + 1);
                byte B(float v) => unchecked((byte)(sbyte)Math.Clamp((int)Math.Round(v / length * 127), -127, 127));
                rows[y][x] = $"{B(-dx)}, {B(-dy)}, {B(1)}";
            }
        }
        return rows;
    }

    /// <summary>A cell file with its `VertexHeightMap` and `VertexNormals` written anew: every other line kept as it was.</summary>
    public static string Write(string text, (float Offset, int[][] Rows, int Clamped) heights, string[][] normals)
    {
        var lines = Lines(text).ToList();
        Replace(lines, "  VertexNormals:", normals.Select(r => $"  - [{string.Join(", ", r.Select(n => $"'{n}'"))}]").ToList());
        Replace(lines, "  VertexHeightMap:", [$"    Offset: {heights.Offset.ToString(CultureInfo.InvariantCulture)}", "    HeightMap:",
            .. heights.Rows.Select(r => $"    - [{string.Join(", ", r)}]")]);
        return string.Join('\n', lines);
    }

    /// <summary>The lines below a key of the Landscape block, up to the block's next key, in place of what was there.</summary>
    private static void Replace(List<string> lines, string key, List<string> body)
    {
        var at = lines.FindIndex(l => l.TrimEnd() == key);
        if (at < 0) throw new HelperException("badRecord", $"The cell's Landscape has no {key.Trim()} to write.");
        var end = at + 1;
        while (end < lines.Count && (lines[end].StartsWith("    ") || lines[end].StartsWith("  -") || lines[end].Length == 0 && end + 1 < lines.Count && lines[end + 1].StartsWith("  -"))) end++;
        lines.RemoveRange(at + 1, end - at - 1);
        lines.InsertRange(at + 1, body);
    }

    /// <summary>How much of an edit reaches a point a distance from its centre: all within the radius, none past the falloff, smoothly between.</summary>
    public static float Weight(float distance, float radius, float falloff)
    {
        if (distance <= radius) return 1;
        if (falloff <= 0 || distance >= radius + falloff) return 0;
        var t = 1 - (distance - radius) / falloff;
        return t * t * (3 - 2 * t);
    }

    /// <summary>Whether two height grids are the same to within a unit.</summary>
    public static bool Same(float[,] a, float[,] b)
    {
        for (var y = 0; y < Size; y++) for (var x = 0; x < Size; x++) if (Math.Abs(a[y, x] - b[y, x]) > 1) return false;
        return true;
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>A cell file's text with its Landscape block added from another rendering of the cell: at its end, before nothing.</summary>
    public static string WithLandscape(string text, string block)
    {
        var trimmed = text.TrimEnd('\n');
        var builder = new StringBuilder(trimmed).Append('\n').Append(block.TrimEnd('\n')).Append('\n');
        return builder.ToString();
    }
}
