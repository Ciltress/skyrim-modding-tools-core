using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// A cell whose navmesh no longer fits what the project made of it, as a warning beside the build with the call that makes it
/// anew (generateNavmesh): an object big enough to change where NPCs walk placed, moved or removed — 192 units or more across,
/// a wagon, a wall, a house, not a chest or a chair — a load door added, the ground changed, or a room of the mod's own with no
/// navmesh at all. A navmesh the project made after those changes fits them: its file is newer than theirs.
/// </summary>
internal static class NavmeshNotes
{
    /// <summary>How wide an object is, at the least, before NPCs have to walk around it: about two and a half metres.</summary>
    private const float Big = 192;

    public static IEnumerable<string> Stale(ISkyrimModGetter mod, ILinkCache lookup, string pluginDir)
    {
        var project = ToolCalls.ProjectOf(pluginDir);
        var files = RecordFiles.CellRecords(pluginDir).Where(c => c.Type is not null && c.Type != typeof(Cell))
            .Select(c => (c.File, Identity: RecordFiles.Identity(File.ReadAllText(c.File)))).ToList();
        var own = mod.EnumerateMajorRecords().GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
        IMajorRecordGetter? BaseOf(IPlacedObjectGetter reference) =>
            own.TryGetValue(reference.Base.FormKey, out var mine) ? mine
            : lookup.TryResolve(reference.Base.FormKey, typeof(IMajorRecordGetter), out var found) ? found : null;
        float Across(IPlacedObjectGetter reference) => BaseOf(reference) is IObjectBoundedGetter { ObjectBounds: { } bounds }
            ? Math.Max(Math.Abs(bounds.Second.X - bounds.First.X), Math.Abs(bounds.Second.Y - bounds.First.Y)) * (reference.Scale ?? 1f) : 0;
        string? FileOf(IPlacedObjectGetter reference) => files.FirstOrDefault(f => f.Identity.FormKey == reference.FormKey
            || reference.EditorID is { } id && string.Equals(f.Identity.EditorId, id, StringComparison.OrdinalIgnoreCase)).File;

        foreach (var cell in mod.EnumerateMajorRecords<ICellGetter>())
        {
            var mine = cell.FormKey.ModKey == mod.ModKey;
            var placed = cell.Persistent.Concat(cell.Temporary).OfType<IPlacedObjectGetter>().ToList();
            var doors = placed.Where(r => r.TeleportDestination is not null).ToList();
            var big = placed.Where(r => r.TeleportDestination is null && Across(r) >= Big).ToList();
            // A game cell's copy holds a landscape only where the ground was changed: addPatch copies none.
            var ground = !mine && cell.Landscape?.VertexHeightMap is not null;
            var hasNavmesh = cell.NavigationMeshes.Count > 0;
            if (mine ? placed.Count == 0 : big.Count == 0 && doors.Count == 0 && !ground) continue;

            var cellName = cell.EditorID ?? Session.FormKeyText(cell.FormKey);
            if (hasNavmesh)
            {
                // Made after what changed: every navmesh file of the cell newer than the newest change.
                var folder = Session.CellFolderIn(pluginDir, cell.FormKey);
                var navmeshes = folder is null || !Directory.Exists(Path.Combine(pluginDir, folder, "NavigationMeshes")) ? []
                    : Directory.GetFiles(Path.Combine(pluginDir, folder, "NavigationMeshes"), "*.yaml").Select(File.GetLastWriteTimeUtc).ToList();
                var changes = big.Concat(doors).Select(FileOf).Where(f => f is not null).Select(f => File.GetLastWriteTimeUtc(f!)).ToList();
                if (ground && folder is not null) changes.Add(File.GetLastWriteTimeUtc(Path.Combine(pluginDir, folder, "RecordData.yaml")));
                if (navmeshes.Count > 0 && (changes.Count == 0 || navmeshes.Min() >= changes.Max().AddSeconds(-2))) continue;
            }

            var call = $"generateNavmesh {{\"project\": {ToolCalls.Quoted(project)}, \"cell\": {ToolCalls.Quoted(cellName)}}}";
            string Names(List<IPlacedObjectGetter> references) => string.Join(", ", references.Take(3).Select(r => r.EditorID ?? Session.FormKeyText(r.FormKey)))
                + (references.Count > 3 ? $" and {references.Count - 3} more" : "");
            if (mine && !hasNavmesh)
            {
                yield return $"{cellName}: a room of the mod's own with no navmesh — followers do not come in, and NPCs placed there stand where " +
                    $"they are. One call makes it from what stands there: {call}; then build again.";
                continue;
            }
            var what = new List<string>();
            if (big.Count > 0) what.Add($"the mod places or moves {big.Count} object(s) big enough to change where NPCs walk — {Names(big)}");
            if (doors.Count > 0) what.Add($"{doors.Count} load door(s) of the mod's — {Names(doors)}");
            if (ground) what.Add("its ground is changed");
            yield return $"{cellName}: {string.Join("; ", what)} — and its navmesh {(hasNavmesh ? "was made before that" : "is still the game's")}: NPCs and " +
                $"followers walk the old one, into what stands there now{(doors.Count > 0 ? ", and no path leads through the new door" : "")}. One call makes " +
                $"it anew from what stands there now: {call}; then build again.";
        }
    }
}
