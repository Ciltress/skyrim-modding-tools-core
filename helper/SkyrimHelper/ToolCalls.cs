using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkyrimHelper;

/// <summary>
/// The tool calls the helper's refusals and notes write out whole, for a model to make as written: the renamed copy's
/// warning wrote its editRecord call, and the model made it, 3 times of 3 (eval rounds 65–66). Paths are the plugin folder's,
/// with forward slashes: a backslash reached the model escaped once per layer of JSON (round 65).
/// </summary>
internal static class ToolCalls
{
    /// <summary>The project a plugin folder belongs to: the folder above it.</summary>
    public static string ProjectOf(string pluginDir) =>
        Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDir)))) ?? "";

    public static string Quoted(string text) => $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>moveRecord: a record file of the plugin folder to another place in it.</summary>
    public static string Move(string pluginDir, string from, string to) =>
        $"moveRecord {{\"project\": {Quoted(ProjectOf(pluginDir))}, \"from\": {Quoted(from.Replace('\\', '/'))}, " +
        $"\"to\": {Quoted(to.Replace('\\', '/'))}}}";

    /// <summary>deleteRecord: a record file of the plugin folder; with `build`, the build after it in the same call.</summary>
    public static string Delete(string pluginDir, string record, bool build = false) =>
        $"deleteRecord {{\"project\": {Quoted(ProjectOf(pluginDir))}, \"record\": {Quoted(record.Replace('\\', '/'))}" +
        (build ? ", \"build\": true" : "") + "}";

    /// <summary>
    /// editRecord: fields of a record file, `set` as its items' JSON — `{"path": "Voice", "value": "FemaleNord"}`, empty for
    /// a call that only removes — and `remove` as its paths' JSON. With `build`, the call builds the project after the change
    /// and answers that build: told to make the rename warning's call and build again, the model made the call and answered
    /// with no build since, twice in eval round 75 — so a warning whose fix is one call writes it with the build.
    /// </summary>
    public static string Edit(string project, string record, string set, string? remove = null, bool build = false) =>
        $"editRecord {{\"project\": {Quoted(project)}, \"record\": {Quoted(record.Replace('\\', '/'))}" +
        (set.Length == 0 ? "" : $", \"set\": [{set}]") + (remove is null ? "" : $", \"remove\": [{remove}]") +
        (build ? ", \"build\": true" : "") + "}";

    /// <summary>
    /// A note's fix: the one call it gives, with the build, that it means made unless the user asked otherwise — written as
    /// Edit writes it, and kept for the build that runs, which answers them beside its notes. The core gives the fixes of one
    /// file as one call: given four warnings on its sword, three with a call, a model made none of them and answered with the
    /// four as a list (eval round 122's sword #1). A note whose call is one of two ways, or "if the user asked for others",
    /// writes Edit, not this.
    /// </summary>
    public static string Fix(string project, string record, string set, string? remove = null)
    {
        if (Collected.Value is { } fixes)
        {
            try
            {
                fixes.Add(new JsonObject
                {
                    ["file"] = record.Replace('\\', '/'),
                    ["set"] = JsonNode.Parse($"[{set}]"),
                    ["remove"] = JsonNode.Parse($"[{remove ?? ""}]")
                });
            }
            catch (JsonException) { }
        }
        return Edit(project, record, set, remove, build: true);
    }

    /// <summary>The fixes the notes of the build that runs give, gathered from here on in its flow.</summary>
    public static List<JsonObject> CollectFixes()
    {
        var fixes = new List<JsonObject>();
        Collected.Value = fixes;
        return fixes;
    }

    private static readonly AsyncLocal<List<JsonObject>?> Collected = new();
}
