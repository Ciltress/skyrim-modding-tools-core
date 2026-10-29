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

    /// <summary>deleteRecord: a record file of the plugin folder.</summary>
    public static string Delete(string pluginDir, string record) =>
        $"deleteRecord {{\"project\": {Quoted(ProjectOf(pluginDir))}, \"record\": {Quoted(record.Replace('\\', '/'))}}}";

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
}
