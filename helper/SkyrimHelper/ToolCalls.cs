namespace SkyrimHelper;

/// <summary>
/// The tool calls the helper's refusals and notes write out whole, for a model to make as written: the renamed copy's
/// warning wrote its editRecord call, and e4b made it, 3 times of 3 (eval rounds 65–66). Paths are the plugin folder's,
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
}
