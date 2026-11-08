using System.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;

namespace SkyrimHelper;

/// <summary>
/// A cell's navmesh made from what actors walk on and bump into: the collision of the meshes its references show (read with
/// NiflySharp), handed to Recast (DotRecast), and given back as the game stores a navmesh — triangles counter-clockwise seen
/// from above, each edge's neighbour, the triangle before each load door, and the grid the game looks triangles up in. The
/// layout of each part was read off the game's own navmeshes (docs/adr/0014).
/// </summary>
internal static class Navmesh
{
    /// <summary>Game units in one Havok unit: collision is stored in Havok's.</summary>
    public const float HavokScale = 69.99124908f;

    /// <summary>
    /// What actors stand on or cannot pass: the layers of fixed collision. Clutter is left out (it moves, and the game's
    /// navmeshes run under it), as are triggers, water and everything that only detects.
    /// </summary>
    private static readonly HashSet<SkyrimLayer> Solid =
    [
        SkyrimLayer.SKYL_STATIC, SkyrimLayer.SKYL_ANIMSTATIC, SkyrimLayer.SKYL_TRANSPARENT, SkyrimLayer.SKYL_TREES,
        SkyrimLayer.SKYL_PROPS, SkyrimLayer.SKYL_TERRAIN, SkyrimLayer.SKYL_GROUND, SkyrimLayer.SKYL_INVISIBLE_WALL,
        SkyrimLayer.SKYL_STAIRHELPER, SkyrimLayer.SKYL_NAVCUT, SkyrimLayer.SKYL_TRANSPARENT_SMALL
    ];

    /// <summary>Triangles: three vertex indices each.</summary>
    public sealed record Geometry(List<Vector3> Vertices, List<int> Triangles)
    {
        public Geometry() : this([], []) { }
        public int TriangleCount => Triangles.Count / 3;

        public void Add(Geometry other, Matrix4x4 transform)
        {
            var start = Vertices.Count;
            Vertices.AddRange(other.Vertices.Select(v => Vector3.Transform(v, transform)));
            Triangles.AddRange(other.Triangles.Select(i => i + start));
        }

        public Vector3 Centre(int triangle) =>
            (Vertices[Triangles[triangle * 3]] + Vertices[Triangles[triangle * 3 + 1]] + Vertices[Triangles[triangle * 3 + 2]]) / 3;
    }

    /// <summary>How Recast reads the walkable space: game units, degrees.</summary>
    public sealed record Settings(float CellSize = 8, float CellHeight = 4, float MaxSlope = 50, float ActorHeight = 120,
        float ActorRadius = 8, float MaxClimb = 32);

    // ---------------------------------------------------------------- collision

    /// <summary>
    /// The fixed collision of a mesh, in its own space and game units: each node's collision object on the solid layers,
    /// through the node tree's transforms. Null for a file NiflySharp cannot read.
    /// </summary>
    public static Geometry? Collision(byte[] nif)
    {
        var file = new NifFile();
        try
        {
            using var stream = new MemoryStream(nif);
            if (file.Load(stream, new NifFileLoadOptions()) != 0) return null;
        }
        catch (Exception) { return null; }
        var geometry = new Geometry();
        foreach (var root in file.GetRootNodes()) Visit(file, root, Matrix4x4.Identity, geometry);
        return geometry;
    }

    // System.Numerics matrices take row vectors: a child's transform comes before its parent's.
    private static void Visit(NifFile nif, NiAVObject obj, Matrix4x4 parent, Geometry into)
    {
        var r = obj.Rotation;
        var local = new Matrix4x4(r.M11, r.M21, r.M31, 0, r.M12, r.M22, r.M32, 0, r.M13, r.M23, r.M33, 0, 0, 0, 0, 1)
            * Matrix4x4.CreateScale(obj.Scale == 0 ? 1 : obj.Scale);
        local.Translation = obj.Translation;
        var world = local * parent;
        if (obj.CollisionObject is { } reference && nif.GetBlock(reference) is bhkNiCollisionObject collision
            && nif.GetBlock(collision.Body) is bhkWorldObject body && Solid.Contains(body.HavokFilter.Layer_SL))
        {
            var bodyTransform = Matrix4x4.Identity;
            if (body is bhkRigidBodyT moved)
            {
                var info = moved.RigidBodyInfo_bRBCI2010;
                var rotation = new Quaternion(info.Rotation.X, info.Rotation.Y, info.Rotation.Z, info.Rotation.W);
                if (rotation.LengthSquared() > 0.5f) bodyTransform = Matrix4x4.CreateFromQuaternion(rotation);
                bodyTransform.Translation = new Vector3(info.Translation.X, info.Translation.Y, info.Translation.Z) * HavokScale;
            }
            Shape(nif, nif.GetBlock(body.Shape), Matrix4x4.CreateScale(HavokScale) * bodyTransform * world, into);
        }
        if (obj is NiNode node)
            foreach (var child in node.Children.References)
                if (nif.GetBlock(child) is NiAVObject avObject) Visit(nif, avObject, world, into);
    }

    private static void Shape(NifFile nif, INiObject? shape, Matrix4x4 m, Geometry into)
    {
        switch (shape)
        {
            case bhkMoppBvTreeShape mopp: Shape(nif, nif.GetBlock(mopp.Shape), m, into); break;
            case bhkListShape list: foreach (var c in list.SubShapes.References) Shape(nif, nif.GetBlock(c), m, into); break;
            case bhkConvexListShape list: foreach (var c in list.SubShapes.References) Shape(nif, nif.GetBlock(c), m, into); break;
            case bhkConvexTransformShape t: Shape(nif, nif.GetBlock(t.Shape), Havok(t.Transform) * m, into); break;
            case bhkTransformShape t: Shape(nif, nif.GetBlock(t.Shape), Havok(t.Transform) * m, into); break;
            case bhkBoxShape box: Box(-box.Dimensions, box.Dimensions, m, into); break;
            case bhkSphereShape sphere: Box(new Vector3(-sphere.Radius), new Vector3(sphere.Radius), m, into); break;
            case bhkCapsuleShape capsule:
                {
                    var radius = new Vector3(Math.Max(capsule.Radius1, capsule.Radius2));
                    Box(Vector3.Min(capsule.FirstPoint, capsule.SecondPoint) - radius, Vector3.Max(capsule.FirstPoint, capsule.SecondPoint) + radius, m, into);
                    break;
                }
            case bhkConvexVerticesShape convex: Hull(convex.Vertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToList(), m, into); break;
            case bhkCompressedMeshShape compressed when nif.GetBlock(compressed.Data) is bhkCompressedMeshShapeData data: Compressed(data, m, into); break;
            case bhkPackedNiTriStripsShape packed when nif.GetBlock(packed.Data) is hkPackedNiTriStripsData data:
                {
                    var start = into.Vertices.Count;
                    into.Vertices.AddRange(data.Vertices.Select(v => Vector3.Transform(v, m)));
                    foreach (var t in data.Triangles) Triangle(into, start + t.Triangle.V1, start + t.Triangle.V2, start + t.Triangle.V3);
                    break;
                }
        }
    }

    // A Havok 4x4 transform's translation is in Havok units, as its shape's are: the scale applies after both.
    private static Matrix4x4 Havok(NiflySharp.Structs.Matrix44 t) =>
        new(t.M11, t.M21, t.M31, 0, t.M12, t.M22, t.M32, 0, t.M13, t.M23, t.M33, 0, t.M14, t.M24, t.M34, 1);

    /// <summary>
    /// A compressed mesh: chunks of vertices stored as steps of the data's error from the chunk's corner, each chunk turned and
    /// moved by its transform, its indices strips first and single triangles after; and the big triangles over big vertices.
    /// </summary>
    private static void Compressed(bhkCompressedMeshShapeData data, Matrix4x4 m, Geometry into)
    {
        foreach (var chunk in data.Chunks)
        {
            var t = data.ChunkTransforms[chunk.TransformIndex];
            var chunkTransform = Matrix4x4.CreateFromQuaternion(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W));
            chunkTransform.Translation = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
            var corner = new Vector3(chunk.Translation.X, chunk.Translation.Y, chunk.Translation.Z);
            var start = into.Vertices.Count;
            foreach (var v in chunk.Vertices)
                into.Vertices.Add(Vector3.Transform(Vector3.Transform(corner + new Vector3(v.X, v.Y, v.Z) * data.Error, chunkTransform), m));
            var indices = chunk.Indices;
            var offset = 0;
            foreach (var length in chunk.Strips)
            {
                for (var k = 0; k + 2 < length; k++)
                    if (k % 2 == 0) Triangle(into, start + indices[offset + k], start + indices[offset + k + 1], start + indices[offset + k + 2]);
                    else Triangle(into, start + indices[offset + k + 1], start + indices[offset + k], start + indices[offset + k + 2]);
                offset += length;
            }
            for (var k = offset; k + 2 < indices.Count; k += 3) Triangle(into, start + indices[k], start + indices[k + 1], start + indices[k + 2]);
        }
        var bigStart = into.Vertices.Count;
        into.Vertices.AddRange(data.BigVerts.Select(v => Vector3.Transform(new Vector3(v.X, v.Y, v.Z), m)));
        foreach (var t in data.BigTris) Triangle(into, bigStart + t.Triangle.V1, bigStart + t.Triangle.V2, bigStart + t.Triangle.V3);
    }

    private static void Triangle(Geometry into, int a, int b, int c)
    {
        if (a == b || b == c || a == c) return;
        into.Triangles.Add(a); into.Triangles.Add(b); into.Triangles.Add(c);
    }

    private static readonly int[] BoxFaces = [0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5];

    private static void Box(Vector3 lo, Vector3 hi, Matrix4x4 m, Geometry into)
    {
        var start = into.Vertices.Count;
        for (var i = 0; i < 8; i++)
            into.Vertices.Add(Vector3.Transform(new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z), m));
        into.Triangles.AddRange(BoxFaces.Select(i => start + i));
    }

    /// <summary>A convex shape's hull: each face whose plane has every point on one side, wound outwards. Many points: their box.</summary>
    private static void Hull(List<Vector3> points, Matrix4x4 m, Geometry into)
    {
        if (points.Count < 4) return;
        if (points.Count > 64) { Box(points.Aggregate(Vector3.Min), points.Aggregate(Vector3.Max), m, into); return; }
        var start = into.Vertices.Count;
        into.Vertices.AddRange(points.Select(v => Vector3.Transform(v, m)));
        var centre = points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Count;
        for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
                for (var k = j + 1; k < points.Count; k++)
                {
                    var normal = Vector3.Cross(points[j] - points[i], points[k] - points[i]);
                    if (normal.LengthSquared() < 1e-12f) continue;
                    normal = Vector3.Normalize(normal);
                    var d = Vector3.Dot(normal, points[i]);
                    int above = 0, below = 0;
                    foreach (var q in points)
                    {
                        var e = Vector3.Dot(normal, q) - d;
                        if (e > 1e-4f) above++; else if (e < -1e-4f) below++;
                    }
                    if (above > 0 && below > 0) continue;
                    if (Vector3.Dot(normal, centre) - d > 0) Triangle(into, start + i, start + k, start + j);
                    else Triangle(into, start + i, start + j, start + k);
                }
    }

    /// <summary>
    /// Where a reference's mesh stands: scaled, turned by its rotation's z, then y, then x, each clockwise seen down its axis
    /// (as skyrim_cell.py places it in Blender), and moved to its position.
    /// </summary>
    public static Matrix4x4 Placement(Noggog.P3Float position, Noggog.P3Float rotation, float scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationZ(-rotation.Z) * Matrix4x4.CreateRotationY(-rotation.Y)
        * Matrix4x4.CreateRotationX(-rotation.X) * Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);

    // ---------------------------------------------------------------- generation

    /// <summary>
    /// The walkable surface of the collision as triangles, counter-clockwise seen from above as the game's are. Recast takes y
    /// up: the game's (x, y, z) is Recast's (x, z, -y).
    /// </summary>
    public static Geometry Generate(Geometry collision, Settings s)
    {
        if (collision.TriangleCount == 0) return new Geometry();
        var vertices = new float[collision.Vertices.Count * 3];
        for (var i = 0; i < collision.Vertices.Count; i++)
        {
            var v = collision.Vertices[i];
            (vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]) = (v.X, v.Z, -v.Y);
        }
        var input = new RcSampleInputGeomProvider(vertices, collision.Triangles.ToArray());
        var config = new RcConfig(RcPartition.WATERSHED, s.CellSize, s.CellHeight, s.MaxSlope, s.ActorHeight, s.ActorRadius, s.MaxClimb,
            regionMinSize: 6, regionMergeSize: 20, edgeMaxLen: 300, edgeMaxError: 2f, vertsPerPoly: 3, detailSampleDist: 0,
            detailSampleMaxError: 1, filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
            walkableAreaMod: new RcAreaModification(63), buildMeshDetail: false);
        var built = new RcBuilder().Build(input, new RcBuilderConfig(config, input.GetMeshBoundsMin(), input.GetMeshBoundsMax()), false);
        var mesh = built.Mesh;
        var result = new Geometry();
        for (var i = 0; i < mesh.nverts; i++)
            result.Vertices.Add(new Vector3(mesh.bmin.X + mesh.verts[i * 3] * mesh.cs, -(mesh.bmin.Z + mesh.verts[i * 3 + 2] * mesh.cs),
                mesh.bmin.Y + mesh.verts[i * 3 + 1] * mesh.ch));
        for (var p = 0; p < mesh.npolys; p++)
        {
            var (a, b, c) = (mesh.polys[p * mesh.nvp * 2], mesh.polys[p * mesh.nvp * 2 + 1], mesh.polys[p * mesh.nvp * 2 + 2]);
            var (va, vb, vc) = (result.Vertices[a], result.Vertices[b], result.Vertices[c]);
            var turn = (vb.X - va.X) * (vc.Y - va.Y) - (vb.Y - va.Y) * (vc.X - va.X);
            if (turn >= 0) result.Triangles.AddRange([a, b, c]); else result.Triangles.AddRange([a, c, b]);
        }
        return result;
    }

    /// <summary>
    /// The pieces of a navmesh that no edge joins, each as its triangles. Recast also walks table tops and shelves no actor
    /// reaches; the caller keeps the pieces that matter.
    /// </summary>
    public static List<List<int>> Pieces(Geometry mesh)
    {
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (var t = 0; t < mesh.TriangleCount; t++)
            for (var e = 0; e < 3; e++)
            {
                var key = EdgeKey(mesh.Triangles[t * 3 + e], mesh.Triangles[t * 3 + (e + 1) % 3]);
                if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = [];
                list.Add(t);
            }
        var piece = Enumerable.Repeat(-1, mesh.TriangleCount).ToArray();
        var pieces = new List<List<int>>();
        for (var first = 0; first < piece.Length; first++)
        {
            if (piece[first] >= 0) continue;
            var members = new List<int>();
            var open = new Stack<int>([first]);
            piece[first] = pieces.Count;
            while (open.Count > 0)
            {
                var t = open.Pop();
                members.Add(t);
                for (var e = 0; e < 3; e++)
                    foreach (var n in byEdge[EdgeKey(mesh.Triangles[t * 3 + e], mesh.Triangles[t * 3 + (e + 1) % 3])])
                        if (piece[n] < 0) { piece[n] = pieces.Count; open.Push(n); }
            }
            pieces.Add(members);
        }
        return pieces.OrderByDescending(p => p.Count).ToList();
    }

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>These triangles alone, with only the vertices they use, renumbered in order.</summary>
    public static Geometry Only(Geometry mesh, IEnumerable<int> triangles)
    {
        var kept = triangles.OrderBy(t => t).ToList();
        var used = kept.SelectMany(t => new[] { mesh.Triangles[t * 3], mesh.Triangles[t * 3 + 1], mesh.Triangles[t * 3 + 2] }).Distinct().Order().ToList();
        var index = used.Select((old, i) => (old, i)).ToDictionary(x => x.old, x => x.i);
        return new Geometry(used.Select(i => mesh.Vertices[i]).ToList(),
            kept.SelectMany(t => new[] { index[mesh.Triangles[t * 3]], index[mesh.Triangles[t * 3 + 1]], index[mesh.Triangles[t * 3 + 2]] }).ToList());
    }

    /// <summary>
    /// The neighbour across each edge — edge 0 from vertex 0 to 1, edge 1 from 1 to 2, edge 2 from 2 to 0 — or -1: the triangle
    /// that runs the same edge the other way.
    /// </summary>
    public static short[] Neighbours(Geometry mesh)
    {
        var owner = new Dictionary<(int, int), int>();
        for (var t = 0; t < mesh.TriangleCount; t++)
            for (var e = 0; e < 3; e++) owner[(mesh.Triangles[t * 3 + e], mesh.Triangles[t * 3 + (e + 1) % 3])] = t;
        var across = new short[mesh.Triangles.Count];
        for (var t = 0; t < mesh.TriangleCount; t++)
            for (var e = 0; e < 3; e++)
                across[t * 3 + e] = owner.TryGetValue((mesh.Triangles[t * 3 + (e + 1) % 3], mesh.Triangles[t * 3 + e]), out var n) ? (short)n : (short)-1;
        return across;
    }

    /// <summary>The triangle whose centre is nearest a point: the game's triangle before a door is the one nearest the door.</summary>
    public static int Nearest(Geometry mesh, Vector3 point) =>
        Enumerable.Range(0, mesh.TriangleCount).MinBy(t => Vector3.DistanceSquared(mesh.Centre(t), point));

    /// <summary>
    /// The grid the game finds a navmesh's triangles by: the vertices' bounds cut into divisor × divisor cells, row by row (y,
    /// then x), each cell listing the triangles whose bounds overlap it, as a count and the indices.
    /// </summary>
    public static (uint Divisor, float StepX, float StepY, Vector3 Min, Vector3 Max, byte[] Cells) Grid(Geometry mesh)
    {
        var min = mesh.Vertices.Aggregate(Vector3.Min);
        var max = mesh.Vertices.Aggregate(Vector3.Max);
        var divisor = Math.Clamp((int)MathF.Round(MathF.Max(max.X - min.X, max.Y - min.Y) / 224f), 1, 12);
        float stepX = (max.X - min.X) / divisor, stepY = (max.Y - min.Y) / divisor;
        var cells = Enumerable.Range(0, divisor * divisor).Select(_ => new List<int>()).ToList();
        int Column(float x) => stepX <= 0 ? 0 : Math.Clamp((int)((x - min.X) / stepX), 0, divisor - 1);
        int Row(float y) => stepY <= 0 ? 0 : Math.Clamp((int)((y - min.Y) / stepY), 0, divisor - 1);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = (mesh.Vertices[mesh.Triangles[t * 3]], mesh.Vertices[mesh.Triangles[t * 3 + 1]], mesh.Vertices[mesh.Triangles[t * 3 + 2]]);
            var (lo, hi) = (Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
            for (var row = Row(lo.Y); row <= Row(hi.Y); row++)
                for (var column = Column(lo.X); column <= Column(hi.X); column++) cells[row * divisor + column].Add(t);
        }
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        foreach (var cell in cells)
        {
            writer.Write(cell.Count);
            foreach (var t in cell) writer.Write((ushort)t);
        }
        writer.Flush();
        return ((uint)divisor, stepX, stepY, min, max, bytes.ToArray());
    }

    // ---------------------------------------------------------------- comparison

    /// <summary>
    /// How much of one navmesh lies on another: its surface sampled every 16 units, a sample on the other where that covers
    /// its x and y within 48 units of its height. With the area sampled, in square units.
    /// </summary>
    public static (double Share, double Area) On(Geometry mesh, Geometry other)
    {
        var samples = Samples(mesh);
        if (samples.Count == 0) return (0, 0);
        var hits = samples.Count(s => Enumerable.Range(0, other.TriangleCount).Any(t => HeightAt(other, t, s.X, s.Y) is { } z && MathF.Abs(z - s.Z) < 48));
        return ((double)hits / samples.Count, samples.Count * 256.0);
    }

    private static List<Vector3> Samples(Geometry mesh)
    {
        var samples = new List<Vector3>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = (mesh.Vertices[mesh.Triangles[t * 3]], mesh.Vertices[mesh.Triangles[t * 3 + 1]], mesh.Vertices[mesh.Triangles[t * 3 + 2]]);
            var (lo, hi) = (Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
            for (var x = MathF.Floor(lo.X / 16) * 16 + 8; x < hi.X; x += 16)
                for (var y = MathF.Floor(lo.Y / 16) * 16 + 8; y < hi.Y; y += 16)
                    if (HeightAt(mesh, t, x, y) is { } z) samples.Add(new Vector3(x, y, z));
        }
        return samples;
    }

    private static float? HeightAt(Geometry mesh, int t, float x, float y)
    {
        var (a, b, c) = (mesh.Vertices[mesh.Triangles[t * 3]], mesh.Vertices[mesh.Triangles[t * 3 + 1]], mesh.Vertices[mesh.Triangles[t * 3 + 2]]);
        var d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(d) < 1e-6f) return null;
        var l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
        var l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
        var l3 = 1 - l1 - l2;
        return l1 >= -1e-4f && l2 >= -1e-4f && l3 >= -1e-4f ? l1 * a.Z + l2 * b.Z + l3 * c.Z : null;
    }
}
