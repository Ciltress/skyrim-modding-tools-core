using System.Globalization;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkyrimHelper;

/// <summary>
/// What is placed in a cell, for readRecord to show beside the cell's own record: each plugin's version of a cell holds the
/// references that plugin places there, so they are gathered from every version, the winning one of each reference kept.
/// The cell's text alone said nothing of them, and no eval round 125 or 126 world run placed a chest: five wrote it into the
/// cell's file under fields of their own, one looked for a placed reference in a mod's text, and one answered that a
/// reference is nothing the tools can write. The objects one would place beside — containers, furniture, doors, activators,
/// NPCs, items — are listed with their positions; the rest is counted by its kind. And the lines a new reference is written
/// with, in the file it goes in.
/// </summary>
internal static class CellContents
{
    /// <summary>The most references listed; the rest are counted.</summary>
    private const int Listed = 40;

    /// <summary>The kinds of record a reference is listed by, in this order: what a new object is placed beside.</summary>
    private static readonly string[] Shown = ["Container", "Furniture", "Door", "Activator", "Npc", "TalkingActivator", "Flora",
        "Book", "Weapon", "Armor", "Ingestible", "MiscItem", "Ammunition", "Ingredient", "Key", "SoulGem", "Scroll"];

    public static JsonObject? Of(IReadOnlyList<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> contexts,
        RecordIndex index, string? cellFile)
    {
        if (contexts.Count == 0 || contexts[0].Record is not ICellGetter winner) return null;
        var seen = new HashSet<FormKey>();
        var placed = new List<(IPlacedGetter Reference, bool Persistent)>();
        foreach (var context in contexts)
        {
            if (context.Record is not ICellGetter cell) continue;
            foreach (var reference in cell.Persistent) if (seen.Add(reference.FormKey)) placed.Add((reference, true));
            foreach (var reference in cell.Temporary) if (seen.Add(reference.FormKey)) placed.Add((reference, false));
        }

        var rows = placed.Select(p => (p.Reference, p.Persistent, Base: BaseOf(p.Reference), Placement: PlacementOf(p.Reference)))
            .Select(r => (r.Reference, r.Persistent, r.Placement,
                Base: r.Base is { } key && index.ByFormKey.TryGetValue(key, out var entry) ? entry : null))
            .ToList();
        var byKind = rows.GroupBy(r => r.Base?.Type ?? "unknown").OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
        var listed = rows.Where(r => r.Base is not null && Array.IndexOf(Shown, r.Base.Type) >= 0 && r.Placement is not null)
            .OrderBy(r => Array.IndexOf(Shown, r.Base!.Type)).Take(Listed).ToList();

        var references = new JsonArray(listed.Select(r => (JsonNode)new JsonObject
        {
            ["formId"] = Session.FormKeyText(r.Reference.FormKey),
            ["editorId"] = r.Reference.EditorID,
            ["base"] = $"{r.Base!.EditorId ?? Session.FormKeyText(r.Base.FormKey)}{(r.Base.Name is { Length: > 0 } name ? $" \"{name}\"" : "")}, " +
                $"{RecordFiles.Article(r.Base.Type)} ({r.Base.Signature})",
            ["position"] = Triple(r.Placement!.Position),
            ["rotation"] = Triple(r.Placement.Rotation),
            ["persistent"] = r.Persistent
        }).ToArray());

        // A new reference beside the first object listed, moved a little along x — its rotation kept, so it faces the same way.
        var near = listed.FirstOrDefault();
        var position = near.Placement is { } at ? Triple(new P3Float(at.Position.X + 64, at.Position.Y, at.Position.Z)) : "0, 0, 0";
        var rotation = near.Placement is { } turned ? Triple(turned.Rotation) : "0, 0, 0";
        var folder = cellFile is null ? null : cellFile[..cellFile.LastIndexOf('/')];
        var name = winner.EditorID ?? "MyCell";
        return new JsonObject
        {
            ["count"] = placed.Count,
            ["byKind"] = new JsonObject(byKind.Select(k => KeyValuePair.Create(k.Key, (JsonNode?)k.Value))),
            ["references"] = references,
            ["newReference"] = new JsonObject
            {
                ["pathInPluginFolder"] = $"{folder ?? $"Cells/<block>/<sub-block>/{name}"}/Temporary/<EditorID>.yaml",
                ["text"] = $"MutagenObjectType: PlacedObject\nEditorID: <a name of its own>\nBase: <the record placed: its editor ID or FormID>\n" +
                    $"Placement:\n  Position: {position}\n  Rotation: {rotation}\n",
                ["note"] = "A reference placed in a cell is a file of its own in the cell's Temporary folder — its kind on its first line, " +
                    "PlacedObject for an object, PlacedNpc for an NPC — beside the copy of the cell addPatch makes, which keeps " +
                    $"{name}'s own fields; the cell's file holds none of them. Position is in game units, x and y across the floor, " +
                    "z up, rotation in radians about each axis" +
                    (near.Base is { } by ? $"; the position above is {by.EditorId ?? Session.FormKeyText(by.FormKey)}'s, 64 units along x" : "") +
                    ". A Persistent reference — in the Persistent folder — is one a quest or script names; an object placed for the player is Temporary."
            }
        };
    }

    private static FormKey? BaseOf(IPlacedGetter reference) => reference switch
    {
        IPlacedObjectGetter o => o.Base.FormKeyNullable,
        IPlacedNpcGetter n => n.Base.FormKeyNullable,
        _ => null
    };

    private static IPlacementGetter? PlacementOf(IPlacedGetter reference) => reference switch
    {
        IPlacedObjectGetter o => o.Placement,
        IPlacedNpcGetter n => n.Placement,
        _ => null
    };

    /// <summary>x, y, z as Spriggit writes a position; a negative zero, which rounding leaves, written 0.</summary>
    private static string Triple(P3Float p) => string.Join(", ", new[] { p.X, p.Y, p.Z }.Select(v =>
        (Math.Round((double)v, 4) + 0.0).ToString("0.####", CultureInfo.InvariantCulture)));
}
