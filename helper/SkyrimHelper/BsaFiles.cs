using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SkyrimHelper;

/// <summary>
/// Which of the game's archives — BSAs — hold some files, for a crash log that names a mesh or a texture
/// no mod has loose. The archives come in the order the game loads them; a file several hold is the
/// last one's, as the game takes it. Which archives there are, and their order, is the server's
/// (src/backend/skyrim/crash/attribution.ts); this only reads their file lists, through Mutagen.
/// </summary>
public static class BsaFiles
{
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
                var reader = Archive.CreateReader(release, archive);
                foreach (var file in reader.Files)
                {
                    if (wanted.TryGetValue(Key(file.Path), out var asked)) found[asked] = archive;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or NotImplementedException or UnauthorizedAccessException or EndOfStreamException)
            {
                errors.Add(new JsonObject { ["archive"] = archive, ["error"] = e.Message });
            }
        }
        return new JsonObject { ["found"] = found, ["errors"] = errors };
    }

    /// <summary>A path as the game compares it: any case, backslashes, no leading separator.</summary>
    private static string Key(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
}
