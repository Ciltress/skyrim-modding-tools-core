using System.IO.Compression;
using System.Text;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkyrimHelper;

/// <summary>
/// The offset table (OFST) of a worldspace the plugin makes (newWorldspace): for each cell of the worldspace's bounds, row by row
/// from its south-west, where that cell's record starts in the plugin, counted from the worldspace's own record; 0 where there is
/// none. The game's worldspaces all carry one, as the Creation Kit writes them. Whether the game needs it from a plugin is not
/// settled: the ground missing from a new worldspace in the game tests of 2026-11-08 was the light flag's
/// (MadeNotes.LightWorldspace), and the plugin whose ground showed carried the table. The layout is read off a light plugin's
/// worldspace of Bethesda's (Giant's Tooth, cckrtsse001_altar.esl): its 80 entries are those positions to the byte. Mutagen
/// does not write one, so the table is reserved before the plugin is written — its size fixes every position — and filled after.
/// </summary>
internal static class WorldOffsets
{
    /// <summary>An empty table for each worldspace the plugin makes, its bounds widened to cover all its cells.</summary>
    public static void Reserve(ISkyrimMod mod)
    {
        foreach (var world in mod.Worldspaces.Where(w => w.FormKey.ModKey == mod.ModKey))
        {
            var grids = world.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items).Select(c => c.Grid?.Point).OfType<P2Int>().ToList();
            if (grids.Count == 0) continue;
            var (minX, minY) = ((int)Math.Min(world.ObjectBoundsMin.X, grids.Min(g => g.X)), (int)Math.Min(world.ObjectBoundsMin.Y, grids.Min(g => g.Y)));
            var (maxX, maxY) = ((int)Math.Max(world.ObjectBoundsMax.X, grids.Max(g => g.X)), (int)Math.Max(world.ObjectBoundsMax.Y, grids.Max(g => g.Y)));
            world.ObjectBoundsMin = new P2Float(minX, minY);
            world.ObjectBoundsMax = new P2Float(maxX, maxY);
            world.OffsetData = new byte[(maxX - minX + 1) * (maxY - minY + 1) * 4];
        }
    }

    /// <summary>The reserved tables filled in from the plugin as written. Answers how many cells each worldspace's table found.</summary>
    public static Dictionary<string, int> Fill(string plugin, ISkyrimMod mod)
    {
        var filled = new Dictionary<string, int>();
        var worlds = mod.Worldspaces.Where(w => w.FormKey.ModKey == mod.ModKey && w.OffsetData is { Length: > 0 } && w.EditorID is not null).ToList();
        if (worlds.Count == 0) return filled;
        var data = File.ReadAllBytes(plugin);
        foreach (var world in worlds)
        {
            var (minX, minY, maxX) = ((int)world.ObjectBoundsMin.X, (int)world.ObjectBoundsMin.Y, (int)world.ObjectBoundsMax.X);
            var width = maxX - minX + 1;
            var (worldAt, cells) = Find(data, world.EditorID!);
            if (worldAt < 0) continue;
            var table = TableAt(data, worldAt);
            if (table < 0) continue;
            var length = world.OffsetData!.Value.Length;
            var count = 0;
            foreach (var (at, x, y) in cells)
            {
                var index = (y - minY) * width + (x - minX);
                if (index < 0 || (index + 1) * 4 > length) continue;
                BitConverter.TryWriteBytes(data.AsSpan(table + index * 4, 4), (uint)(at - worldAt));
                count++;
            }
            filled[world.EditorID!] = count;
        }
        File.WriteAllBytes(plugin, data);
        return filled;
    }

    /// <summary>Where the worldspace of this editor ID starts in the file, and each of its exterior cells: where it starts and its grid.</summary>
    private static (int WorldAt, List<(int At, int X, int Y)> Cells) Find(byte[] data, string editorId)
    {
        var worldAt = -1;
        var cells = new List<(int, int, int)>();
        void Walk(int start, int end, int? worldGroupEnd)
        {
            var at = start;
            while (at + 24 <= end)
            {
                var kind = Encoding.ASCII.GetString(data, at, 4);
                var size = BitConverter.ToInt32(data, at + 4);
                if (kind == "GRUP")
                {
                    // The worldspace's children: the group right after its record (type 1), holding its cells.
                    var type = BitConverter.ToInt32(data, at + 12);
                    var inWorld = worldGroupEnd ?? (type == 1 && worldAt >= 0 && cells.Count == 0 && at > worldAt ? at + size : null);
                    Walk(at + 24, at + size, inWorld);
                    at += size;
                    continue;
                }
                var flags = BitConverter.ToUInt32(data, at + 8);
                if (kind == "WRLD" && worldAt < 0 && Subrecord(Body(data, at, size, flags), "EDID") is { } edid
                    && Encoding.ASCII.GetString(edid).TrimEnd('\0') == editorId)
                    worldAt = at;
                // The persistent cell (flag 0x400) is not in the table.
                else if (kind == "CELL" && worldGroupEnd is not null && (flags & 0x400) == 0
                    && Subrecord(Body(data, at, size, flags), "XCLC") is { Length: >= 8 } grid)
                    cells.Add((at, BitConverter.ToInt32(grid, 0), BitConverter.ToInt32(grid, 4)));
                at += 24 + size;
            }
        }
        var header = BitConverter.ToInt32(data, 4);
        Walk(24 + header, data.Length, null);
        return (worldAt, cells);
    }

    /// <summary>Where the OFST table's bytes start in the file, inside the worldspace's record (written uncompressed).</summary>
    private static int TableAt(byte[] data, int worldAt)
    {
        var size = BitConverter.ToInt32(data, worldAt + 4);
        if ((BitConverter.ToUInt32(data, worldAt + 8) & 0x40000) != 0) return -1;
        var at = worldAt + 24;
        var end = at + size;
        int? big = null;
        while (at + 6 <= end)
        {
            var kind = Encoding.ASCII.GetString(data, at, 4);
            var length = (int)BitConverter.ToUInt16(data, at + 4);
            at += 6;
            if (kind == "XXXX") { big = BitConverter.ToInt32(data, at); at += length; continue; }
            if (big is { } b) { length = b; big = null; }
            if (kind == "OFST") return at;
            at += length;
        }
        return -1;
    }

    private static byte[] Body(byte[] data, int at, int size, uint flags)
    {
        var body = data.AsSpan(at + 24, size).ToArray();
        if ((flags & 0x40000) == 0) return body;
        using var zlib = new ZLibStream(new MemoryStream(body, 4, body.Length - 4), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        zlib.CopyTo(plain);
        return plain.ToArray();
    }

    private static byte[]? Subrecord(byte[] body, string wanted)
    {
        var at = 0;
        int? big = null;
        while (at + 6 <= body.Length)
        {
            var kind = Encoding.ASCII.GetString(body, at, 4);
            var length = (int)BitConverter.ToUInt16(body, at + 4);
            at += 6;
            if (kind == "XXXX") { big = BitConverter.ToInt32(body, at); at += length; continue; }
            if (big is { } b) { length = b; big = null; }
            if (kind == wanted) return body.AsSpan(at, Math.Min(length, body.Length - at)).ToArray();
            at += length;
        }
        return null;
    }
}
