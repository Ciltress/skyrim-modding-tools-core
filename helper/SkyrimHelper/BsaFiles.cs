using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SkyrimHelper;

/// <summary>
/// Which of the game's archives — BSAs — hold some files, for a crash log that names a mesh or a texture
/// no mod has loose. The archives come in the order the game loads them; a file several hold is the
/// last one's, as the game takes it. Which archives there are, and their order, is the server's
/// (src/backend/skyrim/crash/attribution.ts); this only reads their file lists, through Mutagen. A build looks up a model
/// no record of the load order is on too (ModelFiles), so the lists are kept while an archive's size and time stay.
/// </summary>
public static class BsaFiles
{
    private static readonly Dictionary<string, (long Length, DateTime Written, HashSet<string> Files)> Read = new(StringComparer.OrdinalIgnoreCase);

    public static JsonNode Find(JsonObject p)
    {
        var archives = p["archives"]?.AsArray().Select(a => a!.GetValue<string>()).ToList()
            ?? throw new HelperException("badRequest", "bsaFiles needs 'archives', the archive paths in load order.");
        var wanted = (p["files"]?.AsArray().Select(f => f!.GetValue<string>()).ToList()
            ?? throw new HelperException("badRequest", "bsaFiles needs 'files', the files to look for, relative to Data."))
            .ToDictionary(Key, f => f);
        var release = p["release"]?.GetValue<string>() == "SkyrimSEGog" ? GameRelease.SkyrimSEGog : GameRelease.SkyrimSE;

        var found = new JsonObject();
        var errors = new JsonArray();
        foreach (var archive in archives)
        {
            try
            {
                foreach (var file in FilesOf(release, archive))
                {
                    if (wanted.TryGetValue(file, out var asked)) found[asked] = archive;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or NotImplementedException or UnauthorizedAccessException or EndOfStreamException)
            {
                errors.Add(new JsonObject { ["archive"] = archive, ["error"] = e.Message });
            }
        }
        return new JsonObject { ["found"] = found, ["errors"] = errors };
    }

    /// <summary>
    /// Every file each archive holds, as <see cref="Key"/> gives them: for the file conflicts between mods' archives, and between
    /// an archive and a mod's loose files (src/backend/skyrim/fileConflicts.ts).
    /// </summary>
    public static JsonNode List(JsonObject p)
    {
        var archives = p["archives"]?.AsArray().Select(a => a!.GetValue<string>()).ToList()
            ?? throw new HelperException("badRequest", "archiveFiles needs 'archives', the archive paths.");
        var release = p["release"]?.GetValue<string>() == "SkyrimSEGog" ? GameRelease.SkyrimSEGog : GameRelease.SkyrimSE;
        var files = new JsonObject();
        var errors = new JsonArray();
        foreach (var archive in archives)
        {
            try
            {
                files[archive] = new JsonArray(FilesOf(release, archive).Order(StringComparer.Ordinal).Select(f => (JsonNode)f).ToArray());
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or NotImplementedException or UnauthorizedAccessException or EndOfStreamException)
            {
                errors.Add(new JsonObject { ["archive"] = archive, ["error"] = e.Message });
            }
        }
        return new JsonObject { ["files"] = files, ["errors"] = errors };
    }

    /// <summary>An archive's files as <see cref="Key"/> gives them, read again only once the archive changed.</summary>
    private static HashSet<string> FilesOf(GameRelease release, string archive)
    {
        var info = new FileInfo(archive);
        if (!info.Exists) throw new IOException($"No archive at '{archive}'.");
        lock (Read)
        {
            if (Read.TryGetValue(archive, out var kept) && kept.Length == info.Length && kept.Written == info.LastWriteTimeUtc) return kept.Files;
        }
        var files = Archive.CreateReader(release, archive).Files.Select(f => Key(f.Path)).ToHashSet();
        lock (Read) Read[archive] = (info.Length, info.LastWriteTimeUtc, files);
        return files;
    }

    /// <summary>A path as the game compares it: any case, backslashes, no leading separator.</summary>
    private static string Key(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
}
