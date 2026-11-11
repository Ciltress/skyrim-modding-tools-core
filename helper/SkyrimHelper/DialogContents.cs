using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// Dialogue: what a topic's responses say and who says them, for readRecord; what a project's dialogue does in game, for the
/// build — a line every NPC would say, a topic the player is never offered, a quest nothing starts; and the sequence file
/// (SEQ) a plugin's start-game-enabled quests need for their dialogue to be offered.
/// </summary>
internal static class DialogContents
{
    /// <summary>The most responses readRecord lists for one topic: the game's shared topics have hundreds.</summary>
    public const int MaxResponses = 30;

    /// <summary>
    /// A topic's responses, from every plugin's version of it, each as its winning version says it: who it is for, what is said.
    /// A topic's own file holds none of them — Spriggit writes each response as a file in the topic's Responses/ folder — and
    /// readRecord of a topic answered its name and branch alone (2026-11-07). Null for a record that is no topic.
    /// </summary>
    public static JsonObject? Of(IReadOnlyList<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> contexts,
        RecordIndex index, Func<FormKey, List<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>> contextsOf)
    {
        if (contexts.Count == 0 || contexts[0].Record is not IDialogTopicGetter) return null;
        var keys = contexts.Select(c => c.Record).OfType<IDialogTopicGetter>().SelectMany(t => t.Responses.Select(r => r.FormKey))
            .Distinct().ToList();
        var rows = new JsonArray();
        foreach (var key in keys.Take(MaxResponses))
        {
            var versions = contextsOf(key);
            if (versions.Count == 0 || versions[0].Record is not IDialogResponsesGetter info) continue;
            var row = new JsonObject { ["formId"] = Session.FormKeyText(key) };
            if (info.EditorID is { Length: > 0 } id) row["editorId"] = id;
            row["says"] = new JsonArray(info.Responses.Select(r => (JsonNode)(r.Text?.String ?? "")).ToArray());
            if (info.Conditions.Count > 0) row["conditions"] = new JsonArray(info.Conditions.Select(c => (JsonNode)ConditionText(c, index)).ToArray());
            if (versions.Count > 1 || versions[0].ModKey != key.ModKey) row["winner"] = versions[0].ModKey.FileName.String;
            rows.Add(row);
        }
        return new JsonObject
        {
            ["count"] = keys.Count,
            ["responses"] = rows,
            ["note"] = (keys.Count > MaxResponses ? $"The first {MaxResponses} of {keys.Count}. " : "") +
                "Each response is a record of its own, in the topic's folder's Responses/: readRecord of its formId shows its whole text — " +
                "its lines, and the conditions that say who says it and when. A new response of a project goes in its topic's Responses/ folder."
        };
    }

    /// <summary>One condition as the Creation Kit lists it: <c>GetIsID(Hulda) == 1</c>, its OR and whom it runs on.</summary>
    public static string ConditionText(IConditionGetter condition, RecordIndex index)
    {
        var data = condition.Data;
        var function = data.GetType().Name.Replace("BinaryOverlay", "").Replace("ConditionData", "");
        var args = data.EnumerateFormLinks().Where(l => !l.IsNull)
            .Select(l => index.ByFormKey.TryGetValue(l.FormKey, out var entry) && entry.EditorId is { } id ? id : Session.FormKeyText(l.FormKey));
        var on = data.RunOnType == Condition.RunOnType.Subject ? "" : $"{data.RunOnType}.";
        var op = condition.CompareOperator switch
        {
            CompareOperator.EqualTo => "==",
            CompareOperator.NotEqualTo => "!=",
            CompareOperator.GreaterThan => ">",
            CompareOperator.GreaterThanOrEqualTo => ">=",
            CompareOperator.LessThan => "<",
            CompareOperator.LessThanOrEqualTo => "<=",
            _ => condition.CompareOperator.ToString()
        };
        var value = condition is IConditionFloatGetter f ? f.ComparisonValue.ToString(CultureInfo.InvariantCulture)
            : condition is IConditionGlobalGetter g && index.ByFormKey.TryGetValue(g.ComparisonValue.FormKey, out var global) ? global.EditorId ?? "a global"
            : "?";
        return $"{on}{function}({string.Join(", ", args)}) {op} {value}{(condition.Flags.HasFlag(Condition.Flag.OR) ? " OR" : "")}";
    }

    /// <summary>
    /// The project's record files by the FormKey each writes: its groups' and its dialogue topics' and their responses'.
    /// </summary>
    private static Dictionary<FormKey, string> Files(string pluginDir) => RecordFiles.FlatRecords(pluginDir)
        .Concat(RecordFiles.DialogRecords(pluginDir))
        .Select(f => (f.Relative, RecordFiles.Identity(File.ReadAllText(f.File)).FormKey))
        .Where(f => f.FormKey is not null)
        .GroupBy(f => f.FormKey!.Value).ToDictionary(g => g.Key, g => g.First().Relative);

    /// <summary>
    /// What the project's dialogue does in game that is likely not what was meant — each with the change that makes it so:
    /// a player's topic in no branch, which the player is never offered; a branch that is no top-level one and that nothing
    /// links to; a new response with no condition at a topic every NPC offers, which every NPC then says; a topic of no quest;
    /// a quest of the project whose dialogue nothing starts.
    /// </summary>
    public static List<string> Notes(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var notes = new List<string>();
        var files = Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        string Named(FormKey key) => lookup.TryResolve<IMajorRecordGetter>(key, out var record) && record.EditorID is { } id ? id : Session.FormKeyText(key);
        var branches = mod.DialogBranches.ToDictionary(b => b.FormKey);
        IDialogBranchGetter? BranchOf(IDialogTopicGetter topic) =>
            topic.Branch.IsNull ? null : branches.GetValueOrDefault(topic.Branch.FormKey)
                ?? (lookup.TryResolve<IDialogBranchGetter>(topic.Branch.FormKey, out var found) ? found : null);
        // The topics a response of the load order or of the project links to: a branch's later topics are reached that way.
        var linked = mod.DialogTopics.SelectMany(t => t.Responses).SelectMany(r => r.LinkTo.Select(l => l.FormKey)).ToHashSet();

        foreach (var topic in mod.DialogTopics)
        {
            var file = files.GetValueOrDefault(topic.FormKey) ?? $"DialogTopics/{topic.EditorID}/RecordData.yaml";
            var name = topic.EditorID ?? Session.FormKeyText(topic.FormKey);
            var own = topic.FormKey.ModKey == mod.ModKey;
            var branch = BranchOf(topic);
            if (own && topic.Quest.IsNull)
                notes.Add($"{file}: the topic {name} is in no quest, and the game offers only a running quest's dialogue — write the quest it " +
                    $"belongs to: {ToolCalls.Edit(project, file, "{\"path\": \"Quest\", \"value\": \"<the quest's editor ID>\"}", build: true)}");
            if (own && topic.Category == DialogTopic.CategoryEnum.Topic && branch is null && !linked.Contains(topic.FormKey))
                notes.Add($"{file}: the topic {name} is a line the player says, and it is in no branch, so the player is never offered it: a " +
                    "player's topic is a DialogBranch's StartingTopic — a file in DialogBranches/ with `Quest:` the topic's quest, `Category: Player`, " +
                    $"`Flags: [TopLevel]` and `StartingTopic: {name}` — and the topic names that branch in its `Branch:`");
            else if (own && branch is not null && branch.Quest.FormKey != topic.Quest.FormKey && !topic.Quest.IsNull && !branch.Quest.IsNull)
                notes.Add($"{file}: the topic {name} is in the quest {Named(topic.Quest.FormKey)}, and its branch {branch.EditorID} in " +
                    $"{Named(branch.Quest.FormKey)}: a branch and its topics are one quest's — write the branch's quest in both");

            // Where every NPC the quest's dialogue reaches offers the topic: a top-level branch's first topic, or a greeting —
            // a topic of no branch that the player does not say.
            var everyone = branch is not null && (branch.Flags ?? 0).HasFlag(DialogBranch.Flag.TopLevel) && branch.StartingTopic.FormKey == topic.FormKey
                || branch is null && topic.Category != DialogTopic.CategoryEnum.Topic;
            if (!everyone) continue;
            foreach (var info in topic.Responses.Where(r => r.FormKey.ModKey == mod.ModKey && r.Conditions.Count == 0))
            {
                var infoFile = files.GetValueOrDefault(info.FormKey) ?? $"DialogTopics/…/Responses/{info.EditorID}.yaml";
                var said = info.Responses.FirstOrDefault()?.Text?.String is { Length: > 0 } text ? $" ('{(text.Length > 60 ? text[..57] + "…" : text)}')" : "";
                notes.Add($"{infoFile}: the response {info.EditorID ?? Session.FormKeyText(info.FormKey)}{said} has no condition, so every NPC " +
                    $"who speaks says it{(topic.Category == DialogTopic.CategoryEnum.Topic ? $" — and offers the player {name}" : "")}. To give it to " +
                    "one NPC, write the condition the game writes for that, below its Responses, the NPC's editor ID in it:\n" +
                    "Conditions:\n- MutagenObjectType: ConditionFloat\n  Data:\n    MutagenObjectType: GetIsIDConditionData\n    Object: <the NPC's editor ID>\n" +
                    "  ComparisonValue: 1\n(the lines end here) Where every NPC is meant to say it, nothing to change, and this warning stays");
            }
        }

        foreach (var branch in mod.DialogBranches.Where(b => b.FormKey.ModKey == mod.ModKey))
        {
            var file = files.GetValueOrDefault(branch.FormKey) ?? $"DialogBranches/{branch.EditorID}.yaml";
            if (branch.StartingTopic.IsNull)
                notes.Add($"{file}: the branch {branch.EditorID} has no StartingTopic, the topic the player is offered first: name the topic — " +
                    ToolCalls.Edit(project, file, "{\"path\": \"StartingTopic\", \"value\": \"<the topic's editor ID>\"}", build: true));
            else if (!(branch.Flags ?? 0).HasFlag(DialogBranch.Flag.TopLevel) && branch.Category.ToString() == "Player"
                && !linked.Contains(branch.StartingTopic.FormKey))
                notes.Add($"{file}: the branch {branch.EditorID} is no top-level branch, and no response links to its topic " +
                    $"{Named(branch.StartingTopic.FormKey)}, so the player is never offered it — a branch the player starts is flagged TopLevel: " +
                    ToolCalls.Edit(project, file, "{\"path\": \"Flags\", \"value\": [\"TopLevel\"]}", build: true));
        }

        // A quest of the project with dialogue, which runs only once started: StartGameEnabled starts it with the game; a script, the
        // story manager or another quest may start it too — none of which the project's own sources name.
        var scripts = Path.GetFullPath(Path.Combine(pluginDir, "..", "scripts"));
        var sources = Directory.Exists(scripts)
            ? string.Join('\n', Directory.EnumerateFiles(scripts, "*.psc", SearchOption.AllDirectories).Select(File.ReadAllText)) : "";
        var started = mod.StoryManagerQuestNodes.SelectMany(n => n.Quests.Select(q => q.Quest.FormKey)).ToHashSet();
        foreach (var quest in mod.Quests.Where(q => q.FormKey.ModKey == mod.ModKey && !q.Flags.HasFlag(Quest.Flag.StartGameEnabled)))
        {
            if (!mod.DialogTopics.Any(t => t.Quest.FormKey == quest.FormKey) || started.Contains(quest.FormKey)) continue;
            if (quest.EditorID is { } id && Regex.IsMatch(sources, $@"\b{Regex.Escape(id)}\b", RegexOptions.IgnoreCase)) continue;
            var file = files.GetValueOrDefault(quest.FormKey) ?? $"Quests/{quest.EditorID}.yaml";
            notes.Add($"{file}: the quest {quest.EditorID ?? Session.FormKeyText(quest.FormKey)} has the project's dialogue, which the game offers " +
                "only while the quest runs, and nothing starts it: not StartGameEnabled, and no script or story manager node of the project names " +
                "it. Where its dialogue is to be there from the start, the game's way is the flag: write `- StartGameEnabled` into its Flags " +
                "and build again — the build writes the SEQ file such a quest needs; where something else starts it, nothing to change, and this " +
                "warning stays");
        }
        return notes;
    }

    /// <summary>
    /// A quest of the project that a story manager node of the project starts, with no Event: the story manager starts a node's
    /// quests on the event each quest names, and this one on none, so never. Answered with the code of the event the node is
    /// under, read from the game's event node above it. Eval round 144's storymanager runs built 7 such quests without a word,
    /// and one answered its node "linked to the Change Location event".
    /// </summary>
    public static List<string> StoryNotes(ISkyrimModGetter mod, ILinkCache lookup, string pluginDir)
    {
        var notes = new List<string>();
        var files = Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        IAStoryManagerNodeGetter? Node(FormKey key) =>
            mod.StoryManagerQuestNodes.FirstOrDefault(n => n.FormKey == key) as IAStoryManagerNodeGetter
            ?? mod.StoryManagerBranchNodes.FirstOrDefault(n => n.FormKey == key)
            ?? (lookup.TryResolve<IAStoryManagerNodeGetter>(key, out var found) ? found : null);
        foreach (var node in mod.StoryManagerQuestNodes.Where(n => n.FormKey.ModKey == mod.ModKey))
        {
            var code = EventOf(node, mod, lookup);
            var nodeName = node.EditorID ?? Session.FormKeyText(node.FormKey);
            // Under a quest node, which holds quests and no nodes: the game's DA09ChangeLocationSharesEvent, the node the quests skill
            // has a model mirror, was named as its Parent by 4 of eval round 144's 5 detailed storymanager runs.
            if (!node.Parent.IsNull && Node(node.Parent.FormKey) is IStoryManagerQuestNodeGetter above && !above.Parent.IsNull)
            {
                var file = files.GetValueOrDefault(node.FormKey) ?? $"StoryManagerQuestNodes/{node.EditorID}.yaml";
                var aboveName = above.EditorID ?? Session.FormKeyText(above.FormKey);
                var branch = Node(above.Parent.FormKey) is { EditorID: { } id } ? id : Session.FormKeyText(above.Parent.FormKey);
                notes.Add($"{file}: the node {nodeName} is under {aboveName}, a quest node, which holds quests and no nodes: the story manager " +
                    $"never reaches it. Its Parent is the node {aboveName} is under, {branch}: " +
                    ToolCalls.Edit(project, file, $"{{\"path\": \"Parent\", \"value\": \"{branch}\"}}", build: true));
            }
            foreach (var link in node.Quests.Select(q => q.Quest).Where(l => !l.IsNull).DistinctBy(l => l.FormKey))
            {
                if (mod.Quests.FirstOrDefault(q => q.FormKey == link.FormKey) is not { } quest || quest.FormKey.ModKey != mod.ModKey
                    || quest.Event is { } had && had.TypeInt != 0)
                    continue;
                var file = files.GetValueOrDefault(quest.FormKey) ?? $"Quests/{quest.EditorID}.yaml";
                notes.Add($"{file}: the story manager node {nodeName} starts the quest " +
                    $"{quest.EditorID ?? Session.FormKeyText(quest.FormKey)}, which names no Event: the story manager starts a node's quests on the " +
                    $"event each quest names, so never this one. Write {(code is null ? "the code of the node's event" : $"`Event: {code}`, the node's event,")} " +
                    "at the margin of the quest's file: " +
                    ToolCalls.Edit(project, file, $"{{\"path\": \"Event\", \"value\": \"{code ?? "<the event's code, as CLOC>"}\"}}", build: true));
            }
        }
        return notes;
    }

    /// <summary>The four-letter code of the event a story manager node is under — CLOC — from the event node above it; null for none.</summary>
    private static string? EventOf(IAStoryManagerNodeGetter node, ISkyrimModGetter mod, ILinkCache lookup)
    {
        IAStoryManagerNodeGetter? at = node;
        for (var depth = 0; depth < 16 && at is not null; depth++)
        {
            if (at is IStoryManagerEventNodeGetter top) return top.Type is { } type ? new RecordType(Convert.ToInt32(type)).Type : null;
            if (at.Parent.IsNull) return null;
            var parent = at.Parent.FormKey;
            at = mod.StoryManagerBranchNodes.FirstOrDefault(b => b.FormKey == parent) as IAStoryManagerNodeGetter
                ?? (lookup.TryResolve<IAStoryManagerNodeGetter>(parent, out var found) ? found : null);
        }
        return null;
    }

    /// <summary>
    /// Each topic subtype's code as the game's topics carry it — CUST for a player's topic, HELO for a greeting — the first
    /// topic of the load order of each subtype giving it.
    /// </summary>
    public static Dictionary<DialogTopic.SubtypeEnum, RecordType> SubtypeCodes(IEnumerable<ISkyrimModGetter> mods)
    {
        var codes = new Dictionary<DialogTopic.SubtypeEnum, RecordType>();
        foreach (var topic in mods.SelectMany(m => m.DialogTopics))
            if (topic.SubtypeName.TypeInt != 0) codes.TryAdd(topic.Subtype, topic.SubtypeName);
        return codes;
    }

    /// <summary>
    /// A new topic's subtype code, where its file has none: every topic of the game carries the code of its subtype beside it —
    /// `SubtypeName: CUST` — and a topic written without the line was built with none (a hand-made topic, 2026-11-07).
    /// </summary>
    public static void FillSubtypeNames(ISkyrimMod mod, IReadOnlyDictionary<DialogTopic.SubtypeEnum, RecordType> codes)
    {
        foreach (var topic in mod.DialogTopics.Where(t => t.FormKey.ModKey == mod.ModKey))
            if (topic.SubtypeName.TypeInt == 0 && codes.TryGetValue(topic.Subtype, out var code)) topic.SubtypeName = code;
    }

    /// <summary>
    /// The FormIDs a plugin's sequence file holds — the quests that start with the game which are the plugin's own, or which it
    /// makes start so where its master's did not — as the plugin itself writes them: its master's index in the high byte. The
    /// game reads them at Data\Seq\&lt;plugin&gt;.seq, four bytes each, and offers a start-game-enabled quest's dialogue only when its
    /// quest is there (the Creation Kit's and xEdit's Create SEQ File, xEdit's GenerateSEQFileForFile).
    /// </summary>
    public static List<uint> SeqFormIds(ISkyrimModGetter written, ILinkCache lookup)
    {
        var masters = written.ModHeader.MasterReferences.Select(m => m.Master).ToList();
        var ids = new List<uint>();
        foreach (var quest in written.Quests.Where(q => q.Flags.HasFlag(Quest.Flag.StartGameEnabled)))
        {
            var own = quest.FormKey.ModKey == written.ModKey;
            if (!own && lookup.TryResolve<IQuestGetter>(quest.FormKey, out var master) && master.Flags.HasFlag(Quest.Flag.StartGameEnabled)) continue;
            var at = own ? masters.Count : masters.IndexOf(quest.FormKey.ModKey);
            if (at < 0) continue;
            ids.Add(((uint)at << 24) | quest.FormKey.ID);
        }
        return ids;
    }

    /// <summary>Writes the plugin's sequence file beside it, in Seq/, where the deploy takes it into the mod; none where it needs none.</summary>
    public static string? WriteSeq(ISkyrimModGetter written, ILinkCache lookup, string pluginPath)
    {
        var ids = SeqFormIds(written, lookup);
        if (ids.Count == 0) return null;
        var folder = Path.Combine(Path.GetDirectoryName(pluginPath)!, "Seq");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Path.ChangeExtension(Path.GetFileName(pluginPath), ".seq"));
        using var stream = File.Create(file);
        using var writer = new BinaryWriter(stream);
        foreach (var id in ids) writer.Write(id);
        return file;
    }
}
