using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Serialization.Yaml;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// The helper's state between calls: the load order the server handed over, the plugins opened as
/// read-only overlays, a link cache over them, and a lazily built index of their records.
/// </summary>
public sealed class Session : IDisposable
{
    public const string Version = "0.1.0";
    private const int DefaultLimit = 25;
    internal static readonly ModKey SkyrimMaster = ModKey.FromFileName("Skyrim.esm");

    private SkyrimRelease release = SkyrimRelease.SkyrimSE;
    private readonly List<LoadedPlugin> plugins = [];
    /// <summary>Where the load order places each record (PlacedIndex), built on first use and dropped with the load order.</summary>
    private PlacedIndex? placedIndex;
    private ILinkCache<ISkyrimMod, ISkyrimModGetter>? cache;
    /// <summary>Started as soon as a load order arrives, so the first search rarely waits for it.</summary>
    private Task<RecordIndex>? indexing;

    private RecordIndex Index => (indexing ?? throw new HelperException("noLoadOrder", "No load order set: call setLoadOrder first."))
        .GetAwaiter().GetResult();

    private sealed record LoadedPlugin(ModKey Key, string Path, ISkyrimModDisposableGetter Mod);

    public async Task<JsonNode?> Dispatch(string method, JsonObject p)
    {
        // Overlays are read by one thread at a time: whatever reads plugins waits for the background index.
        // Downloaded files and plugin headers are read from files of their own, and need not wait; record
        // files do, since a FormKey of another plugin's is checked against the record it names.
        if (method is not ("hello" or "gameVersion" or "setLoadOrder" or "readArchive" or "extractArchive" or "pluginHeaders" or "bsaFiles" or "extractFiles")
            && indexing is { } running)
        {
            try { await running; } catch { /* surfaced by the call that needs the index */ }
        }
        return await Run(method, p);
    }

    private async Task<JsonNode?> Run(string method, JsonObject p) => method switch
    {
        "hello" => new JsonObject { ["helper"] = Version, ["mutagen"] = MutagenVersion(), ["protocol"] = 1 },
        "gameVersion" => GameVersion(p),
        "setLoadOrder" => SetLoadOrder(p),
        "findRecords" => FindRecords(p),
        "readRecord" => await ReadRecord(p),
        "addOverrides" => await AddOverrides(p),
        "build" => await Build(p),
        "serialize" => await Serialize(p),
        "readArchive" => Archives.Read(p),
        "extractArchive" => Archives.Extract(p),
        "pluginHeaders" => PluginHeaders(p),
        "checkRecords" => await CheckRecords(Required(p, "projectPluginDir")),
        // With the load order's records where one is set: a path below a link is refused naming the record linked there.
        "editRecord" => RecordEdits.Edit(Required(p, "projectPluginDir"), Required(p, "record"), p["set"] as JsonObject, p["remove"] as JsonArray,
            indexing is null ? null : Index),
        // The fields a call sent as a text beside a copy — addPatch's `input` — written into the copy as editRecord writes them.
        "editInput" => RecordEdits.Input(Required(p, "projectPluginDir"), Required(p, "record"), Required(p, "input"),
            indexing is null ? null : Index),
        // What an editRecord call refused for want of its file would still change in the file written since; nothing is written.
        "pendingEdit" => RecordEdits.Pending(Required(p, "projectPluginDir"), Required(p, "record"), p["set"] as JsonObject,
            p["remove"] as JsonArray, p["among"] as JsonArray, indexing is null ? null : Index),
        "sksePlugins" => SksePlugins.Read(p),
        "bsaFiles" => BsaFiles.Find(p),
        "cellScene" => CellSceneOf(p),
        "extractFiles" => CellScene.Extract(p, release.ToGameRelease()),
        _ => throw new HelperException("unknownMethod", $"No method '{method}'.")
    };

    // ---------------------------------------------------------------- environment

    private static string MutagenVersion() =>
        typeof(SkyrimMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

    private static JsonNode GameVersion(JsonObject p)
    {
        var exe = Required(p, "exePath");
        if (!File.Exists(exe)) throw new HelperException("notFound", $"No game executable at '{exe}'.");
        return new JsonObject { ["version"] = PeVersion.Read(exe) };
    }

    private JsonNode SetLoadOrder(JsonObject p)
    {
        release = (p["release"]?.GetValue<string>() ?? "SkyrimSE") switch
        {
            "SkyrimSE" => SkyrimRelease.SkyrimSE,
            "SkyrimSEGog" => SkyrimRelease.SkyrimSEGog,
            var other => throw new HelperException("badRequest", $"Unsupported release '{other}': use SkyrimSE or SkyrimSEGog.")
        };

        DisposePlugins();
        var missing = new JsonArray();
        foreach (var entry in p["plugins"]?.AsArray() ?? [])
        {
            var name = entry?["name"]?.GetValue<string>() ?? throw new HelperException("badRequest", "Every plugin needs a 'name'.");
            var path = entry?["path"]?.GetValue<string>();
            if (path is null || !File.Exists(path)) { missing.Add(name); continue; }
            var key = ModKey.FromFileName(name);
            // A plugin someone may edit — from a mod folder or overwrite — is read into memory, so this
            // process never holds it open: an open handle stops the Creation Kit, xEdit and a deploy from
            // saving over it. The game's own masters are large and never saved over; they stay on disk.
            ISkyrimModDisposableGetter mod;
            if (entry?["inMemory"]?.GetValue<bool>() == true)
            {
                var bytes = File.ReadAllBytes(path);
                mod = SkyrimMod.Create(release).FromStreamFactory(() => new MemoryStream(bytes, writable: false), key).Construct();
            }
            else
            {
                mod = SkyrimMod.Create(release).FromPath(new ModPath(key, path)).Construct();
            }
            plugins.Add(new LoadedPlugin(mod.ModKey, path, mod));
        }
        cache = plugins.Select(x => (ISkyrimModGetter)x.Mod).ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
        var snapshot = plugins.Select(x => (x.Key, (ISkyrimModGetter)x.Mod)).ToList();
        indexing = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            var built = RecordIndex.Build(snapshot);
            Console.Error.WriteLine($"[helper] indexed {built.ByFormKey.Count} records of {snapshot.Count} plugins in {clock.ElapsedMilliseconds} ms");
            return built;
        });
        return new JsonObject { ["plugins"] = plugins.Count, ["missing"] = missing };
    }

    private ILinkCache<ISkyrimMod, ISkyrimModGetter> Cache =>
        cache ?? throw new HelperException("noLoadOrder", "No load order set: call setLoadOrder first.");

    // ---------------------------------------------------------------- finding

    private JsonNode FindRecords(JsonObject p)
    {
        var idx = Index;
        var limit = p["limit"]?.GetValue<int>() ?? DefaultLimit;
        var types = p["types"]?.AsArray().Select(t => t!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plugin = p["plugin"]?.GetValue<string>();
        // A type by the game's word for what it holds — a note, a potion — read as that record type, and said so: refused with
        // "Did you mean BOOK (Book)?", three runs of eval round 114 spent a turn on it (note+detailed #2–#4; 7 runs in rounds 87–114).
        var readAs = new List<string>();
        foreach (var type in types?.ToList() ?? [])
        {
            if (RecordIndex.Meant(type) is not { } kept) continue;
            var (meant, signature) = kept;
            types!.Remove(type);
            types.Add(meant);
            readAs.Add($"'{type}' read as {signature} ({meant}), the record type Skyrim keeps it as");
        }
        // A misspelled type would match nothing, and "no records" sends a model looking elsewhere.
        foreach (var type in types ?? [])
        {
            if (RecordIndex.IsRecordType(type)) continue;
            var closest = RecordIndex.Closest(type).ToList();
            throw new HelperException("badRequest",
                $"'{type}' is not a record type." + (closest.Count > 0 ? $" Did you mean {string.Join(", ", closest)}?" : "") +
                " Use a signature like WEAP or a type name like Weapon.");
        }

        IEnumerable<RecordIndex.Entry> matches;
        string? searchedAs = null;
        var notFound = new JsonArray();
        if (p["records"] is JsonArray wanted)
        {
            var found = new List<RecordIndex.Entry>();
            foreach (var raw in wanted.Select(r => r!.GetValue<string>()))
            {
                var key = FormIdShape.IsMatch(raw) ? Named(raw) ?? ParseFormKey(raw)
                    : idx.ByEditorId.TryGetValue(raw.Trim(), out var byId) ? byId
                    : EditorIdWithPlugin(raw, idx) ?? (raw.Contains(':') ? ParseFormKey(raw) : (FormKey?)null);
                if (key is { } k && idx.ByFormKey.TryGetValue(k, out var entry)) found.Add(entry);
                else notFound.Add(raw);
            }
            matches = found;
        }
        else
        {
            var text = p["text"]?.GetValue<string>()?.Trim() ?? "";
            matches = ByText(text);
            // A name said in the plural finds nothing: "Steel Arrows" found no record and "Arrows" two others, and eval round 129's
            // world+detailed #4 filled its chest with Iron Arrows. Each word made singular, the first that finds a record answers.
            if (text.Length > 0 && !Chosen(matches).Any())
                foreach (var singular in Singulars(text))
                    if (ByText(singular) is var found && Chosen(found).Any())
                    {
                        matches = found;
                        searchedAs = $"no record is named '{text}': these are '{singular}'";
                        break;
                    }
        }
        IEnumerable<RecordIndex.Entry> ByText(string text)
        {
            var word = new Regex($@"(?<![A-Za-z0-9]){Regex.Escape(text)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
            return idx.ByFormKey.Values
                .Where(e => text.Length == 0
                    || e.EditorId?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
                    || e.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                // Exact matches first, then the in-game names people speak in — "steel" finds the Steel
                // Sword before 1stPersonSteelSword, a static only the editor ID mentions — and the named records
                // whose editor IDs begin with the text, which name the thing itself: "shock" finds
                // ShockDamageFFAimed, Lightning Bolt's effect, before EnchShockDamageFFContact, a weapon's, which
                // three of three spells copied (eval probe 9 after round 11); SteelBootsAA, nameless, stays back.
                .OrderBy(e => string.Equals(e.EditorId, text, StringComparison.OrdinalIgnoreCase) ? 0
                    : string.Equals(e.Name, text, StringComparison.OrdinalIgnoreCase) ? 1
                    : e.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
                        || e.Name is { Length: > 0 } && e.EditorId?.StartsWith(text.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) == true ? 2 : 3)
                // Among equals, the editor ID beginning with the text, then the one spelling it out: SteelSword
                // before the quest's FFRiften16HarraldSword, both named "Steel Sword".
                .ThenBy(e => e.EditorId?.StartsWith(text.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
                // Then a name with the text as a word of its own: "sword" finds the Daedric Sword before the Daedric
                // Greatsword, a two-handed one.
                .ThenBy(e => e.Name is { } name && word.IsMatch(name) ? 0 : 1)
                .ThenBy(e => e.EditorId?.Contains(text.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
                // Then what the player can have: "sword" answered the Bound Sword first, NonPlayable and a bound weapon,
                // and 10 of the new swords of eval rounds 31–44 were copied from it so (round 44's sword #1 and #3).
                .ThenBy(e => e.Unplayable ? 1 : 0)
                // Then, with a plugin named, the records it adds before those it changes: put Skyrim.esm's first, the
                // search answered Dawnguard's armor with the Falmer armor Dawnguard changes, which the model gave as Dawnguard's
                // (eval round 42's filter). Else Skyrim.esm's own, as a copy's example is chosen: among the NPCs named
                // Nord, the Creation Club's dead one came first by its editor ID, and the model copied it, twice (rounds 37, 41).
                .ThenBy(e => plugin is not null ? e.FormKey.ModKey.FileName.String.Equals(plugin, StringComparison.OrdinalIgnoreCase) ? 0 : 1
                    : e.FormKey.ModKey == SkyrimMaster ? 0 : 1)
                .ThenBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase);
        }
        IEnumerable<RecordIndex.Entry> InPlugin(IEnumerable<RecordIndex.Entry> found) => plugin is null ? found
            : found.Where(e => e.Plugins.Any(k => k.FileName.String.Equals(plugin, StringComparison.OrdinalIgnoreCase)));
        IEnumerable<RecordIndex.Entry> Chosen(IEnumerable<RecordIndex.Entry> found) => types is { Count: > 0 }
            ? InPlugin(found).Where(e => types.Contains(e.Signature) || types.Contains(e.Type)) : InPlugin(found);
        matches = InPlugin(matches);
        var untyped = matches;
        if (types is { Count: > 0 }) matches = matches.Where(e => types.Contains(e.Signature) || types.Contains(e.Type));

        var all = matches.ToList();
        var rows = new JsonArray();
        foreach (var e in all.Take(limit)) rows.Add(e.ToJson());
        var answer = new JsonObject { ["total"] = all.Count, ["records"] = rows };
        if (readAs.Count > 0) answer["typesRead"] = string.Join("; ", readAs);
        if (searchedAs is not null) answer["searchedAs"] = searchedAs;
        if (notFound.Count > 0)
        {
            answer["notFound"] = notFound;
            // "Steel Sword" looked up as an editor ID found nothing, and a model gave up (eval round 11's probes).
            answer["nextStep"] = "records takes exact FormIDs and editor IDs only; to search in-game names and parts of " +
                $"editor IDs, send text instead: {{\"text\": \"{notFound[0]!.GetValue<string>().Replace("\"", "")}\"}}";
        }
        // Maven's note searched as a MISC record found nothing, and the model asked the user for its FormID (eval round 46's
        // note+detailed #3; round 39's failed so too): what the search finds of other types, named with them.
        else if (all.Count == 0 && types is { Count: > 0 } && untyped.ToList() is { Count: > 0 } other)
        {
            var shown = other.Take(3).ToList();
            answer["nextStep"] = $"No record of the types asked matches; {other.Count} of other types {(other.Count == 1 ? "does" : "do")}: " +
                string.Join("; ", shown.Select(e => $"{e.EditorId ?? FormKeyText(e.FormKey)}" + (e.Name is { Length: > 0 } name ? $" \"{name}\"" : "") +
                    $", {RecordFiles.Article(e.Type)} ({e.Signature})")) +
                $" — send types [{string.Join(", ", shown.Select(e => $"\"{e.Signature}\"").Distinct())}] for {(shown.Count == 1 ? "it" : "those")}, or no types";
        }
        return answer;
    }

    /// <summary>
    /// A search's text with a word made singular — the last first, one at a time, then every one: "Steel Arrows" as "Steel
    /// Arrow", "Iron Daggers" as "Iron Dagger", "Torches" as "Torch", "Wolves" as "Wolf".
    /// </summary>
    private static IEnumerable<string> Singulars(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        static List<string> Of(string word)
        {
            bool Ends(string end) => word.EndsWith(end, StringComparison.OrdinalIgnoreCase);
            if (word.Length < 4 || !Ends("s") || Ends("ss") || Ends("us") || Ends("is")) return [];
            if (Ends("ies")) return [word[..^3] + "y"];
            if (Ends("ves")) return [word[..^3] + "f", word[..^3] + "fe", word[..^1]];
            if (Ends("es")) return [word[..^2], word[..^1]];
            return [word[..^1]];
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { text };
        for (var i = words.Length - 1; i >= 0; i--)
            foreach (var singular in Of(words[i]))
                if (string.Join(' ', words.Select((w, at) => at == i ? singular : w)) is var said && seen.Add(said)) yield return said;
        if (words.Select(w => Of(w).FirstOrDefault() ?? w).ToArray() is var all && string.Join(' ', all) is var every && seen.Add(every))
            yield return every;
    }

    // ---------------------------------------------------------------- reading

    private async Task<JsonNode> ReadRecord(JsonObject p)
    {
        var formKey = Resolve(Required(p, "record"), byName: true);
        var plugin = PluginNamed(p["plugin"]?.GetValue<string>());
        var all = p["allVersions"]?.GetValue<bool>() ?? false;

        var contexts = Contexts(formKey);
        if (contexts.Count == 0) throw new HelperException("notFound", $"No record {formKey} in the load order.");

        var chosen = plugin is not null
            ? contexts.Where(c => c.ModKey.FileName.String.Equals(plugin, StringComparison.OrdinalIgnoreCase)).ToList()
            : all ? contexts : contexts.Take(1).ToList();
        if (chosen.Count == 0)
            throw new HelperException("notFound",
                $"{plugin} does not contain {formKey}. It is in: {string.Join(", ", contexts.Select(c => c.ModKey.FileName))}.");

        var versions = new JsonArray();
        foreach (var context in chosen)
        {
            var files = new JsonArray();
            foreach (var (path, text) in await Render([context], context.ModKey))
            {
                // An exterior cell's worldspace is its frame: its own file stands without its text — Tamriel's is 92 KB, the
                // most of it OffsetData — which filled a small model's prompts to 60k tokens (the outdoor task's first
                // mini-round). addPatch of the cell copies it as it is.
                if (context.Record is ICellGetter && path.Split('/') is ["Worldspaces", _, "RecordData.yaml"])
                {
                    files.Add(new JsonObject
                    {
                        ["path"] = path,
                        ["text"] = $"# The worldspace's own record, {text.Length / 1024} KB, left out: the frame of its cells, which addPatch " +
                            "of a cell copies as it is — nothing in it is the cell's.\n"
                    });
                    continue;
                }
                files.Add(new JsonObject { ["path"] = path, ["text"] = Annotated(text) });
            }
            versions.Add(new JsonObject
            {
                ["plugin"] = context.ModKey.FileName.String,
                ["winning"] = context.ModKey == contexts[0].ModKey,
                ["files"] = files
            });
        }

        var winner = contexts[0].Record;
        var answer = new JsonObject
        {
            ["formId"] = FormKeyText(formKey),
            ["editorId"] = winner.EditorID,
            ["type"] = RecordIndex.TypeName(winner),
            ["signature"] = RecordIndex.Signature(winner),
            ["definedIn"] = formKey.ModKey.FileName.String,
            ["winner"] = contexts[0].ModKey.FileName.String,
            ["plugins"] = new JsonArray(contexts.AsEnumerable().Reverse().Select(c => (JsonNode)c.ModKey.FileName.String).ToArray()),
            ["versions"] = versions
        };
        // A cell with what is placed in it, from every plugin's version, and the lines of a new reference (CellContents).
        var cellFile = versions.OfType<JsonObject>().SelectMany(v => v["files"]!.AsArray().OfType<JsonObject>())
            .Select(f => f["path"]!.GetValue<string>()).FirstOrDefault(f => RecordFiles.IsCellFile(f.Split('/')));
        if (CellContents.Of(contexts, Index, cellFile, Contexts) is { } placed) answer["placed"] = placed;
        // A thing that can be placed: where the load order places it (PlacedIndex).
        else if (winner is IPlaceableObjectGetter or INpcGetter)
        {
            placedIndex ??= PlacedIndex.Build(plugins.Select(x => (ISkyrimModGetter)x.Mod).ToList());
            if (placedIndex.Of(formKey, p["in"]?.GetValue<string>()) is { } placedIn) answer["placedIn"] = placedIn;
        }
        return answer;
    }

    private static readonly Regex LinkLine = new(@"^(?!FormKey:)([ \t]*(?:- )?(?:\w+:[ \t]*)?)([0-9A-Fa-f]{6}:\S+\.es[mpl])[ \t]*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// A record's text to read, with the editor ID of each record it links to beside the FormID, as a
    /// YAML comment the reader skips: <c>BaseEffect: 012F03:Skyrim.esm  # FireDamageFFAimed</c>. Bare
    /// FormIDs said nothing: a model copied Firebolt's fire effects into its shock spell, and wrote an
    /// invented race after reading Lydia's (eval round 11's probes).
    /// </summary>
    private string Annotated(string text) => LinkLine.Replace(text, line =>
        FormKey.TryFactory(line.Groups[2].Value, out var key) && Index.ByFormKey.TryGetValue(key, out var entry) && entry.EditorId is { } id
            ? $"{line.Groups[1].Value}{line.Groups[2].Value}  # {id}"
            : line.Value);

    /// <summary>
    /// The given versions, as overrides in a plugin named <paramref name="asPlugin"/>, in project text:
    /// relative path and contents of every file except the plugin header.
    /// </summary>
    /// <remarks>
    /// The overrides go through a real plugin file first, so the text is exactly what a build and a
    /// pull produce — one language instead of every translation a localized master carries, and the
    /// normalisations a round trip applies — and a project never changes on its first pull.
    /// </remarks>
    private async Task<List<(string Path, string Text)>> Render(
        IEnumerable<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> contexts, ModKey asPlugin)
    {
        var temp = new SkyrimMod(asPlugin, release);
        foreach (var context in contexts) context.GetOrAddAsOverride(temp);

        var dir = Directory.CreateTempSubdirectory("skyrimhelper-").FullName;
        try
        {
            var plugin = Path.Combine(dir, asPlugin.FileName);
            await temp.BeginWrite.ToPath(plugin).WithLoadOrder(LoadedMods(except: asPlugin)).WriteAsync();
            using var written = SkyrimMod.Create(release).FromPath(plugin).Construct();
            var yaml = Path.Combine(dir, "yaml");
            await MutagenYamlConverter.Instance.Serialize(written, yaml);
            return Directory.GetFiles(yaml, "*.yaml", SearchOption.AllDirectories)
                .Select(f => (Path: Path.GetRelativePath(yaml, f).Replace('\\', '/'), Text: TextBlocks.Literal(Lf(File.ReadAllText(f)))))
                .Where(f => f.Path != "RecordData.yaml")
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToList();
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A load-order record's file text as readRecord answers it — its winning version's first file — by editor ID; null for none.</summary>
    private string? ExampleText(string editorId)
    {
        if (!Index.ByEditorId.TryGetValue(editorId, out var key) || Contexts(key) is not { Count: > 0 } contexts) return null;
        var files = Render([contexts[0]], contexts[0].ModKey).GetAwaiter().GetResult();
        return files.Count > 0 ? Annotated(files[0].Text) : null;
    }

    private List<ISkyrimModGetter> LoadedMods(ModKey except) =>
        plugins.Where(x => x.Key != except).Select(x => (ISkyrimModGetter)x.Mod).ToList();

    /// <summary>Project text uses LF: fewer bytes in every answer, and no line-ending churn in git.</summary>
    public static string Lf(string text) => text.Replace("\r\n", "\n");

    /// <summary>
    /// A plugin named where its file name goes, as given — or, given as a FormKey, that FormKey's plugin: addPatch's `from`
    /// sent as the record's own FormID was answered "013989:Skyrim.esm does not contain 013989:Skyrim.esm" (eval round 97's
    /// build #1).
    /// </summary>
    private static string? PluginNamed(string? given) =>
        given is not null && FormKey.TryFactory(given.Trim(), out var formKey) ? formKey.ModKey.FileName.String : given;

    // ---------------------------------------------------------------- patching

    private async Task<JsonNode> AddOverrides(JsonObject p)
    {
        var pluginName = ModKey.FromFileName(Required(p, "plugin"));
        var projectDir = Required(p, "projectPluginDir");
        var from = PluginNamed(p["from"]?.GetValue<string>());
        var requested = p["records"]?.AsArray().Select(r => r!.GetValue<string>()).ToList()
            ?? throw new HelperException("badRequest", "'records' is required: FormIDs like 012EB7:Skyrim.esm, or editor IDs.");

        var added = new JsonArray();
        var already = new JsonArray();
        // A record named twice — the model's ["012EB7:Skyrim.esm", "IronSword"] — is copied once, not reported in the project as well.
        foreach (var formKey in requested.Select(raw => Resolve(raw)).Distinct())
        {
            if (ProjectFileFor(projectDir, formKey) is { } existing)
            {
                already.Add(new JsonObject { ["formId"] = FormKeyText(formKey), ["file"] = existing });
                continue;
            }

            // The project's own deployed plugin is never the source: its content is the project itself.
            var contexts = Contexts(formKey).Where(c => c.ModKey != pluginName).ToList();
            if (contexts.Count == 0) throw new HelperException("notFound", $"No record {formKey} in the load order.");
            var source = from is null
                ? contexts[0]
                : contexts.FirstOrDefault(c => c.ModKey.FileName.String.Equals(from, StringComparison.OrdinalIgnoreCase))
                  ?? throw new HelperException("notFound",
                      $"{from} does not contain {formKey}. It is in: {string.Join(", ", contexts.Select(c => c.ModKey.FileName))}.");

            var files = new JsonArray();
            foreach (var (path, text) in await Render([source], pluginName))
            {
                var target = Path.Combine(projectDir, path);
                if (File.Exists(target)) continue; // e.g. the parent cell's header, already brought in
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // As readRecord shows it: a line copied from there into an edit, its link's editor ID beside it, matched
                // nothing in a copy that held the FormID bare — seven times the same edit (eval round 30). A pull, whose
                // text has none, leaves a file that differs only in them as it is.
                await File.WriteAllTextAsync(target, Annotated(text));
                files.Add(path);
            }
            added.Add(new JsonObject
            {
                ["formId"] = FormKeyText(formKey),
                ["editorId"] = source.Record.EditorID,
                ["type"] = RecordIndex.TypeName(source.Record),
                ["from"] = source.ModKey.FileName.String,
                ["files"] = files
            });
        }
        return new JsonObject { ["added"] = added, ["alreadyInProject"] = already };
    }

    /// <summary>A cell as exportCell lays it out in Blender (CellScene): its references, the project's built plugin over the load order's.</summary>
    private JsonNode CellSceneOf(JsonObject p)
    {
        var formKey = Resolve(Required(p, "cell"));
        var built = p["projectPlugin"]?.GetValue<string>();
        using var project = built is not null && File.Exists(built) ? SkyrimMod.Create(release).FromPath(built).Construct() : null;
        return CellScene.Of(Contexts(formKey), key => Contexts(key).FirstOrDefault()?.Record, Index, project, Contexts);
    }

    private static string? ProjectFileFor(string projectDir, FormKey formKey)
    {
        if (!Directory.Exists(projectDir)) return null;
        var suffix = $"{formKey.ID:X6}_{formKey.ModKey.FileName}.yaml";
        return Directory.EnumerateFiles(projectDir, "*.yaml", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) is { } hit
            ? Path.GetRelativePath(projectDir, hit).Replace('\\', '/')
            : null;
    }

    // ---------------------------------------------------------------- build and pull

    /// <summary>
    /// Everything a build would refuse in the record files, said before the server gives new records
    /// their FormIDs — so every file is still under the name its author wrote it under.
    /// </summary>
    private async Task<JsonNode> CheckRecords(string dir)
    {
        // A fix with one right answer is written first, and named in a refusal that comes after it too: the model
        // knows the file as it wrote it, and an edit of the line taken out finds nothing.
        // A file that is no one YAML record — JSON, or two records apart by `---` — before any correction: the EditorID one wrote a
        // line above a JSON record's `{`, and the repeated-line one deleted 11 lines of a second record (refusal probes of
        // 2026-10-31, G13, F20, G19).
        if (RecordFiles.NotOneRecord(dir) is { Count: > 0 } unfit)
            throw new HelperException("badRecord", $"{unfit.Count} record file(s) cannot be built as written: {string.Join("; ", unfit)}");
        // A group folder in other letters before all: no file in it is read until it has its name.
        var corrected = RecordFiles.FolderCase(dir);
        // Then tabs, before any correction reads a file as YAML: the reader takes none in a line's indent.
        corrected.AddRange(RecordFiles.Untab(dir));
        // And a quote mark alone on a line, which keeps the reader from the rest of the file.
        corrected.AddRange(RecordFiles.DropLoneQuotes(dir));
        // An editor ID first: a copied FormKey is taken out by the EditorID beside it.
        corrected.AddRange(RecordFiles.CleanEditorIds(dir, Index));
        corrected.AddRange(RecordFiles.DropCopiedFormKeys(dir, Index));
        // And an unchanged copy in a file named for none of it: the new record its file names.
        corrected.AddRange(await UnchangedCopiesMadeNew(dir));
        corrected.AddRange(RecordFiles.DropSharedFormKeys(dir, release));
        corrected.AddRange(RecordFiles.DropEmptyStamps(dir));
        // A line written twice first: the YAML reader the moves use refuses the file until it is gone.
        corrected.AddRange(RecordFiles.DropRepeatedLines(dir));
        // A part's field at the margin below the part's empty line goes back under it, before Dedent reads it as the record's.
        corrected.AddRange(MisplacedFields.IntoEmptyPart(dir));
        // So too a record's fields indented below one that has a value: the reader stops there.
        corrected.AddRange(MisplacedFields.Dedent(dir));
        // And a part's fields indented below an item of a list of names, which holds none.
        corrected.AddRange(MisplacedFields.OutFromUnderItems(dir));
        // A record's text written as one value, once nothing is indented below it that is no part of it.
        corrected.AddRange(RecordFiles.TextsInTwoLines(dir));
        // Before the links are checked: a link may name the record by its file's name.
        corrected.AddRange(RecordFiles.NameFromFiles(dir, Index));
        var unneeded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        corrected.AddRange(RecordFiles.RenameTakenEditorIds(dir, Index, unneeded));
        // And one of two new records of different types under one editor ID, which a link of the other names.
        corrected.AddRange(RecordFiles.RenameSharedEditorIds(dir, Index));
        // Before the moves: what is left of another record type may break the reader they use.
        corrected.AddRange(RecordFiles.DropOtherTypesFields(dir));
        // A part's field written among its Flags, and a flag written as a field, before the moves read the part's fields.
        corrected.AddRange(FlagLines.OutOfFlags(dir));
        corrected.AddRange(FlagLines.IntoFlags(dir));
        // A weapon's field at the margin takes the place of its part's, where that one is the weapon's it was written after.
        corrected.AddRange(MisplacedFields.Move(dir, (type, text, part, key, kept, written) =>
            KeptFromSource.CopiedFrom(Cache, Index, type, text, part, key, kept, written)));
        corrected.AddRange(MisplacedFields.MoveEffectData(dir));
        // Once each field is in its part: a model's texture hashes are found by the part they are under.
        corrected.AddRange(RecordFiles.DropBrokenModelData(dir));
        // And a number written in hex, or bytes written as 0, by the type of the part each is in.
        corrected.AddRange(RecordFiles.MendNumbersAndBytes(dir));
        // And an NPC's aggression or responsibility written as none is, by the part it is in too.
        corrected.AddRange(RecordFiles.MendAIData(dir));
        // And a name written below `Level:`, then its level written as a number, once a level at the margin is under Configuration.
        corrected.AddRange(RecordFiles.NameOutOfLevel(dir));
        corrected.AddRange(RecordFiles.MendLevel(dir));
        // And the level its bounds bound, where it has bounds and no level.
        corrected.AddRange(RecordFiles.LevelForBounds(dir));
        // And a part's kind as its first field, once each field is in its part: the reader takes the part by it.
        corrected.AddRange(MisplacedFields.KindFirst(dir));
        // A spell's or enchantment's lines from its effects, read where each is: its cast, and an effect's flags in the spell's.
        corrected.AddRange(CasterLines.DropEffectFlags(dir, Index, Cache));
        corrected.AddRange(CasterLines.CastAsEffects(dir, Index, Cache));
        int records;
        List<(string File, string Copy)> leftOut;
        try
        {
            (records, leftOut) = RecordFiles.Require(dir, Index, unneeded, ExampleText);
            RecordLinks.Resolve(dir, Index, LinkPins.Read(dir), out _, Cache);
        }
        catch (HelperException e) when (corrected.Count > 0)
        {
            throw new HelperException(e.Code,
                $"{e.Message} Corrected in the files already: {string.Join("; ", corrected.Select(c => $"{c.File}: {c.Change}"))}.");
        }
        // Each note's fix beside the notes, as the build gives its own.
        var fixes = ToolCalls.CollectFixes();
        var notes = ProjectNotes.Of(dir, Index, Cache).Select(n => (JsonNode)n).ToArray();
        return new JsonObject
        {
            ["records"] = records,
            ["leftOut"] = new JsonArray(leftOut.Select(l => (JsonNode)new JsonObject { ["file"] = l.File, ["copy"] = l.Copy }).ToArray()),
            ["notes"] = new JsonArray(notes),
            ["fixes"] = new JsonArray(fixes.Select(x => (JsonNode)x).ToArray()),
            ["corrected"] = new JsonArray(corrected.Select(c => (JsonNode)new JsonObject { ["file"] = c.File, ["change"] = c.Change }).ToArray())
        };
    }

    private async Task<JsonNode> Build(JsonObject p)
    {
        var dir = Required(p, "projectPluginDir");
        var output = Required(p, "outputPath");
        var headerVersion = p["headerVersion"]?.GetValue<float>() ?? 1.71f;
        if (!File.Exists(Path.Combine(dir, "RecordData.yaml")))
            throw new HelperException("notFound", $"No RecordData.yaml in '{dir}': this is not a project's plugin folder.");

        // What the reader would skip or fail on without saying why, named first (RecordFiles); then the
        // links written by editor ID, made FormKeys the reader takes (RecordLinks) — in its copy alone.
        RecordFiles.Require(dir, Index, exampleText: ExampleText);
        var pins = LinkPins.Read(dir);
        var resolvedLinks = RecordLinks.Resolve(dir, Index, pins, out var rewrites, Cache);
        IModGetter deserialized;
        var readable = Readable(dir, rewrites);
        try
        {
            deserialized = await MutagenYamlConverter.Instance.Deserialize(readable);
        }
        catch (Exception e) when (e is not HelperException)
        {
            // The copy has the folder's layout, so the file is named as the project has it.
            throw RecordFiles.Unreadable(e, readable);
        }
        finally
        {
            if (readable != dir) Directory.Delete(readable, recursive: true);
        }
        if (deserialized is not ISkyrimMod mod) throw new HelperException("badRequest", "The project is not a Skyrim plugin.");
        mod.ModHeader.Stats.Version = headerVersion;
        // xEdit's Check for Errors requires an author in the header; the Creation Kit writes this one.
        if (string.IsNullOrWhiteSpace(mod.ModHeader.Author)) mod.ModHeader.Author = "DEFAULT";

        // Everything the plugin points at must exist — in the plugin itself, or in the load order without it.
        var others = LoadedMods(except: mod.ModKey);
        var lookup = others.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
        var own = mod.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();
        var dangling = new List<(string Record, FormKey Target)>();
        foreach (var record in mod.EnumerateMajorRecords())
        {
            foreach (var link in record.EnumerateFormLinks())
            {
                // Skyrim.esm's numbers below 000800 are the engine's own: the player's reference, 000014,
                // is in no plugin, and every Creation Club plugin points at it.
                if (link.IsNull || (link.FormKey.ModKey == SkyrimMaster && link.FormKey.ID < 0x800)) continue;
                var found = link.FormKey.ModKey == mod.ModKey
                    ? own.Contains(link.FormKey)
                    : lookup.TryResolve(link.FormKey, link.Type, out _);
                if (!found) dangling.Add((record.EditorID ?? FormKeyText(record.FormKey), link.FormKey));
            }
        }
        if (dangling.Count > 0)
        {
            var named = DanglingLinks(dir, mod.ModKey, dangling.Distinct().ToList());
            throw new HelperException("danglingLinks",
                $"{dangling.Count} link(s) point at records that exist neither in the project nor in the load order: " +
                string.Join("; ", named.Take(20)) + (named.Count > 20 ? "; …" : ""));
        }

        // A new spell's effects cast as it is, or the spell does nothing in game (EffectCasting).
        var miscast = EffectCasting.Mismatches(mod, lookup, Index, dir);
        if (miscast.Count > 0)
            throw new HelperException("badRecord",
                $"{miscast.Count} effect(s) would do nothing in game: {string.Join("; ", miscast.Take(12))}" + (miscast.Count > 12 ? "; …" : ""));
        // And a new NPC of no race, which has no body in game (RecordNeeds).
        var raceless = RecordNeeds.Raceless(mod, lookup, Index, dir);
        if (raceless.Count > 0)
            throw new HelperException("badRecord", $"{raceless.Count} NPC(s) would have no body in game: {string.Join("; ", raceless)}");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            await mod.BeginWrite.ToPath(output).WithLoadOrder(others).WriteAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The disk, not the project: a full drive, a file another program holds open.
            throw new HelperException("ioFailed", $"Could not write '{output}': {e.Message}");
        }
        catch (Exception e) when (e is not HelperException)
        {
            throw new HelperException("writeFailed",
                $"The plugin cannot be written from the project's text: {e.Message} Compare the records named " +
                "there with a vanilla record of the same type (readRecord) and fix their fields.");
        }

        // What the links named, kept for the next build, once a plugin was written from them.
        LinkPins.Add(dir, pins, resolvedLinks, mod.ModKey);
        using var written = SkyrimMod.Create(release).FromPath(output).Construct();
        // Each note's fix, the one call it gives, beside the notes: the core gives those of one file as one call.
        var fixes = ToolCalls.CollectFixes();
        var notes = (await OverrideNotes(dir, mod.ModKey)).Concat(EffectCasting.Notes(mod, lookup, Index, dir))
            .Concat(KeptFromSource.Notes(mod, lookup, Index, dir, Examples(p))).Concat(LevelBounds.Notes(mod, dir))
            .Concat(WeaponEffects.Notes(mod, lookup, Index, dir)).Concat(KindParts.Notes(mod, lookup, Index, dir))
            .Concat(RecordNeeds.ListNotes(mod, dir)).Concat(BookTexts.Notes(mod, lookup, Index, dir))
            .Concat(EffectCasting.GivenBackNotes(mod, lookup, Index, dir)).Concat(MadeNotes.Shouts(mod, lookup, Index, dir))
            .Concat(MadeNotes.CopiedNames(mod, Index, dir)).Concat(MadeNotes.RecipePerks(mod, lookup, dir)).Concat(MadeNotes.PlayerCopy(mod, dir))
            .Concat(MadeNotes.NpcItems(mod, lookup, dir)).Concat(MadeNotes.ListEntries(mod, lookup, dir)).Concat(MadeNotes.Words(mod, dir))
            .Concat(MadeNotes.Unplayable(mod, dir))
            .Concat(CellContents.PlacedNotes(mod, Contexts, Index, dir))
            .Select(n => (JsonNode)n).ToArray();
        return new JsonObject
        {
            ["output"] = output,
            ["records"] = written.EnumerateMajorRecords().Count(),
            ["masters"] = new JsonArray(written.ModHeader.MasterReferences.Select(m => (JsonNode)m.Master.FileName.String).ToArray()),
            ["headerVersion"] = written.ModHeader.Stats.Version,
            ["resolvedLinks"] = resolvedLinks,
            ["built"] = new JsonArray(BuiltRecords.Lines(written, lookup).Select(l => (JsonNode)l).ToArray()),
            ["models"] = ModelFiles.Unknown(mod, lookup, Index, dir),
            ["scripts"] = AttachedScripts.List(mod, dir),
            ["notes"] = new JsonArray(notes),
            ["fixes"] = new JsonArray(fixes.Select(f => (JsonNode)f).ToArray())
        };
    }

    /// <summary>The skills' example weapons the core sends with a build, each with its BasicStats; none where it sends none.</summary>
    private static List<KeptFromSource.Example> Examples(JsonObject p) =>
        (p["examples"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => e["editorId"] is JsonValue && e["skill"] is JsonValue && e["value"] is JsonValue && e["weight"] is JsonValue && e["damage"] is JsonValue)
            .Select(e => new KeptFromSource.Example(e["editorId"]!.GetValue<string>(), e["skill"]!.GetValue<string>(),
                e["value"]!.GetValue<float>(), e["weight"]!.GetValue<float>(), e["damage"]!.GetValue<float>()))
            .ToList();

    private static readonly Regex LinkComment = new(@"(?<=[0-9A-Fa-f]{6}:\S+\.es[mpl])[ \t]+#[^\n]*", RegexOptions.IgnoreCase);

    /// <summary>
    /// The project's overrides that change nothing: each file reads, its link comments aside, as addPatch writes the
    /// record from the load order without the project. The model's "new note" was Maven's own, copied and built as it was
    /// (eval round 45's note+detailed #1); 9 records of rounds 30–45 were copied so and built, untouched, in 8 runs, and 2
    /// of them failed — the 6 that passed each left the Iron Sword or a fire enchantment in the plugin beside the new sword.
    /// And a changed enchantment's copy no record of the project names (ProjectNotes.UnusedEnchantment).
    /// </summary>
    private async Task<List<string>> OverrideNotes(string dir, ModKey own)
    {
        var records = RecordFiles.FlatRecords(dir).Select(f => (f.Relative, f.Type, Text: File.ReadAllText(f.File))).ToList();
        // The project's new records: told the three ways out, the model answered with its new sword and its new note built and
        // the copy each was made from left beside it (eval round 46's sword #4 and note+detailed #2) — named, the copy is left over.
        var made = records.Select(f => (f.Relative, f.Type, f.Text, Identity: RecordFiles.Identity(f.Text)))
            .Where(f => f.Identity.EditorId is not null && (f.Identity.FormKey is not { } key || key.ModKey == own))
            .Select(f => (f.Relative, f.Type, f.Text, Id: f.Identity.EditorId!)).ToList();
        var notes = new List<string>();
        foreach (var (relative, type, text, key, unchanged) in await Copies(records, own))
        {
            if (!unchanged)
            {
                if (type == typeof(ObjectEffect) && ProjectNotes.UnusedEnchantment(dir, records, relative, text, Index, own) is { } unused)
                    notes.Add(unused);
                continue;
            }
            var id = Index.ByFormKey.TryGetValue(key, out var entry) && entry.EditorId is { } editorId ? editorId : FormKeyText(key);
            var said = $"{relative}: overrides {id} of {key.ModKey.FileName} and changes nothing in it, every field as the load order " +
                "has it — in game it only undoes what mods loaded before this one change there. ";
            // A new record of another type that names the copied one has the load order's without the copy: told the three ways
            // out, the model answered that the copy of Lightning Bolt's effect, which its new spell named, gave the spell its
            // magnitude (eval round 116's spell+detailed #1; the 4 runs of rounds 87–116 that ended on this warning failed).
            var naming = new Regex($@"^[ \t]*(?:-[ \t]+)?(?!EditorID:)(?:[\w.\[\]]+:[ \t]*)?(?:{Regex.Escape(id)}|{Regex.Escape(FormKeyText(key))})[ \t]*(?:#.*)?$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            notes.Add(made.FirstOrDefault(m => m.Type == type).Id is { } mine
                ? said + $"{mine} is the project's new {type.Name}: where it is what was asked for, this copy is left over — delete " +
                    $"it, {ToolCalls.Delete(dir, relative, build: true)}; where {id} was to change too, write the change into this file"
                : made.FirstOrDefault(m => naming.IsMatch(Lf(m.Text))) is { Id: not null } namer
                    ? said + $"{namer.Id} ({namer.Relative}), the project's new {namer.Type.Name}, names {id}: it has the load order's {id} " +
                        $"without this copy, which is left over — delete it, {ToolCalls.Delete(dir, relative, build: true)}; where {id} itself was to " +
                        "change, write the change into this file"
                : said + $"A new record like it is this file without its FormKey line, with an EditorID of its own; a change to {id} is " +
                    $"written into this file; a copy that is neither is deleted: {ToolCalls.Delete(dir, relative, build: true)}");
        }
        return notes;
    }

    /// <summary>
    /// A record file's text as addPatch's is compared with: line ends, link comments and a text's block style aside — a copy
    /// addPatch wrote before v0.2.45 has its texts folded.
    /// </summary>
    private static string Bare(string text) =>
        string.Join('\n', LinkComment.Replace(TextBlocks.Literal(Lf(text)), "").Split('\n').Select(l => l.TrimEnd())).TrimEnd();

    /// <summary>
    /// The project's copies of another plugin's records — files whose FormKey is another plugin's — each with whether it reads,
    /// its link comments aside, as addPatch writes the record from the load order without the project. A copy of a record the
    /// load order has not is left out.
    /// </summary>
    private async Task<List<(string Relative, Type Type, string Text, FormKey Key, bool Unchanged)>> Copies(
        IReadOnlyList<(string Relative, Type Type, string Text)> records, ModKey own)
    {
        var copies = records.Where(f => RecordFiles.Identity(f.Text).FormKey is { } key && key.ModKey != own).ToList();
        var contexts = copies.Select(c => Contexts(RecordFiles.Identity(c.Text).FormKey!.Value).FirstOrDefault(x => x.ModKey != own))
            .OfType<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>().ToList();
        if (contexts.Count == 0) return [];
        var asLoaded = (await Render(contexts, own))
            .Where(r => RecordFiles.Identity(r.Text).FormKey is not null)
            .ToDictionary(r => RecordFiles.Identity(r.Text).FormKey!.Value, r => Bare(r.Text));
        return copies.Select(c => (c.Relative, c.Type, c.Text, Key: RecordFiles.Identity(c.Text).FormKey!.Value))
            .Where(c => asLoaded.ContainsKey(c.Key))
            .Select(c => (c.Relative, c.Type, c.Text, c.Key, asLoaded[c.Key] == Bare(c.Text))).ToList();
    }

    /// <summary>
    /// An unchanged copy of another plugin's record in a file named for none of it — neither its EditorID nor addPatch's
    /// `EditorID - FormID_plugin.yaml` — made the new record its file names: the FormKey line out, the file's name its
    /// EditorID. Maven's note, copied whole into Books/MavenWarning.yaml, was built as an override that changes nothing, and
    /// the model answered that warning as "expected" (eval round 111's note #2; round 97's sword+detailed #2). An override
    /// that changes nothing only undoes what mods loaded before it change, so such a file is never meant as one; a name
    /// the load order or the project has already, or none to make of the file's, leaves it to the build's warning.
    /// </summary>
    private async Task<List<(string File, string Change)>> UnchangedCopiesMadeNew(string dir)
    {
        if (RecordFiles.OwnPlugin(dir) is not { } own) return [];
        var records = RecordFiles.FlatRecords(dir).Select(f => (f.Relative, f.Type, Text: File.ReadAllText(f.File))).ToList();
        var taken = records.Select(r => RecordFiles.Identity(r.Text).EditorId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<(string File, string Change)>();
        foreach (var (relative, _, _, key, unchanged) in await Copies(records, own))
        {
            if (!unchanged || Path.GetFileName(relative).EndsWith($"{key.ID:X6}_{key.ModKey.FileName}.yaml", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Regex.Replace(Path.GetFileNameWithoutExtension(relative), "[^A-Za-z0-9_]", "");
            var theirs = Index.ByFormKey.TryGetValue(key, out var entry) && entry.EditorId is { } editorId ? editorId : FormKeyText(key);
            if (id.Length == 0 || char.IsDigit(id[0]) || id.Equals(theirs, StringComparison.OrdinalIgnoreCase) || taken.Contains(id)
                || Index.ByEditorId.ContainsKey(id)) continue;
            var file = Path.Combine(dir, relative);
            var written = File.ReadAllText(file);
            var lines = Lf(written).Split('\n').ToList();
            var formKeyAt = lines.FindIndex(l => l.StartsWith("FormKey:", StringComparison.Ordinal));
            if (formKeyAt < 0) continue;
            var formKeyLine = lines[formKeyAt].Trim();
            lines.RemoveAt(formKeyAt);
            var editorIdAt = lines.FindIndex(l => l.StartsWith("EditorID:", StringComparison.Ordinal));
            if (editorIdAt >= 0) lines[editorIdAt] = $"EditorID: {id}";
            else lines.Insert(formKeyAt, $"EditorID: {id}");
            var text = string.Join('\n', lines);
            File.WriteAllText(file, written.Contains("\r\n") ? text.Replace("\n", "\r\n") : text);
            taken.Add(id);
            changes.Add((relative, $"made a new record, {id}, of a copy of {theirs} of {key.ModKey.FileName} that changed nothing in it: removed " +
                $"`{formKeyLine}` and wrote `EditorID: {id}`, its file's name — an override that changes nothing only undoes what mods " +
                $"loaded before it change. A change to {theirs} itself is a copy with its FormKey line and the change written in"));
        }
        return changes;
    }

    private async Task<JsonNode> Serialize(JsonObject p)
    {
        var pluginPath = Required(p, "pluginPath");
        var outputDir = Required(p, "outputDir");
        if (!File.Exists(pluginPath)) throw new HelperException("notFound", $"No plugin at '{pluginPath}'.");
        using var mod = SkyrimMod.Create(release).FromPath(pluginPath).Construct();
        await MutagenYamlConverter.Instance.Serialize(mod, outputDir);
        var files = Directory.GetFiles(outputDir, "*.yaml", SearchOption.AllDirectories);
        // As readRecord and addPatch write them, so that a pull changes no file of theirs that the plugin holds as it is.
        foreach (var file in files) File.WriteAllText(file, TextBlocks.Literal(Lf(File.ReadAllText(file))));
        return new JsonObject { ["files"] = files.Length };
    }

    // ---------------------------------------------------------------- installing

    /// <summary>
    /// The header of each plugin a mod brings: its masters, and whether it is master- or light-flagged —
    /// what the server needs to place new plugins in the load order and to name missing masters. A file
    /// that is not a plugin answers its error instead.
    /// </summary>
    private JsonNode PluginHeaders(JsonObject p)
    {
        var answers = new JsonArray();
        foreach (var path in p["paths"]?.AsArray().Select(x => x!.GetValue<string>())
            ?? throw new HelperException("badRequest", "'paths' is required: the plugins' full paths."))
        {
            try
            {
                using var mod = SkyrimMod.Create(release).FromPath(new ModPath(ModKey.FromFileName(Path.GetFileName(path)), path)).Construct();
                var flags = mod.ModHeader.Flags;
                answers.Add(new JsonObject
                {
                    ["path"] = path,
                    ["masters"] = new JsonArray(mod.ModHeader.MasterReferences.Select(m => (JsonNode)m.Master.FileName.String).ToArray()),
                    ["master"] = flags.HasFlag(SkyrimModHeader.HeaderFlag.Master),
                    ["light"] = flags.HasFlag(SkyrimModHeader.HeaderFlag.Small)
                });
            }
            catch (Exception e)
            {
                answers.Add(new JsonObject { ["path"] = path, ["error"] = e.Message });
            }
        }
        return new JsonObject { ["plugins"] = answers };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The plugin folder as the reader can take it: itself, or a copy — without the record files written empty to
    /// drop their records (RecordFiles.Check), as the reader fails on an empty file, and with each link written by
    /// editor ID as the FormID it names (<paramref name="rewrites"/>), as the reader takes FormIDs alone.
    /// The project's own files are never renamed, moved or rewritten.
    /// </summary>
    private static string Readable(string dir, List<(string Relative, string Text)> rewrites)
    {
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        var empty = files.Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) && RecordFiles.Empty(File.ReadAllText(f))).ToHashSet();
        if (empty.Count == 0 && rewrites.Count == 0) return dir;
        var copy = Path.Combine(Path.GetTempPath(), $"skyrim-helper-build-{Guid.NewGuid():N}");
        foreach (var file in files)
        {
            if (empty.Contains(file)) continue;
            var target = Path.Combine(copy, Path.GetRelativePath(dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        foreach (var (relative, text) in rewrites) File.WriteAllText(Path.Combine(copy, relative), text);
        return copy;
    }

    /// <summary>
    /// Every version of a record, winning first. Resolved by its own type when the index knows it:
    /// resolving as "any record" makes the link cache index every record type there is.
    /// </summary>
    private List<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> Contexts(FormKey formKey)
    {
        var type = Index.ByFormKey.TryGetValue(formKey, out var entry) ? entry.GetterType : typeof(IMajorRecordGetter);
        return Cache.ResolveAllContexts(formKey, type).ToList();
    }

    /// <param name="byName">
    /// Whether an in-game name that one record alone has finds that record — for reading. The model asked for "Note
    /// From Maven", was told its editor ID, and never read it (eval round 18). Copying keeps to exact names.
    /// </param>
    private FormKey Resolve(string raw, bool byName = false)
    {
        // A FormID is hex digits before its colon; "Spell Tome: Flames" is a name.
        if (FormIdShape.IsMatch(raw)) return Named(raw) ?? ParseFormKey(raw);
        if (Index.ByEditorId.TryGetValue(raw.Trim(), out var key)) return key;
        if (EditorIdWithPlugin(raw, Index) is { } withPlugin) return withPlugin;
        // An in-game name, sent as an editor ID: "Lydia", whose editor ID is HousecarlWhiterun (eval probe 15).
        var withName = Index.ByFormKey.Values.Where(e => e.EditorId is not null && string.Equals(e.Name, raw.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName && withName.Count == 1) return withName[0].FormKey;
        var named = withName.OrderBy(e => e.FormKey.ModKey == SkyrimMaster ? 0 : 1).Take(5)
            .Select(e => $"{e.EditorId} ({e.Type}, {FormKeyText(e.FormKey)})").ToList();
        // A colon that is no record's name is a FormID written wrong: Skyrim.esm:012EB7.
        if (named.Count == 0 && raw.Contains(':')) return ParseFormKey(raw);
        var near = named.Count > 0 ? new List<string>() : RecordLinks.Closest(raw.Trim(), Index);
        throw new HelperException("notFound", $"No record with editor ID '{raw}' in the load order" +
            (named.Count > 0 ? $" — the records named '{raw.Trim()}' in game: {string.Join(", ", named)}; read one by its editor ID"
                : near.Count > 0 ? $" — the records closest to it: {string.Join(", ", near)}" : "") + ". Search with findRecords.");
    }

    private static readonly Regex FormIdShape = new(@"^\s*[0-9A-Fa-f]{1,8}\s*:", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>
    /// An editor ID with the plugin that defines it after a colon, written the way a FormID is —
    /// SteelSword:Skyrim.esm (eval round 21) — names that record. With a plugin that does not define it, the
    /// refusal gives the FormID it has.
    /// </summary>
    private static FormKey? EditorIdWithPlugin(string raw, RecordIndex index)
    {
        var colon = raw.LastIndexOf(':');
        var editorId = colon > 0 ? raw[..colon].Trim() : "";
        if (editorId.Length == 0 || !index.ByEditorId.TryGetValue(editorId, out var key)) return null;
        return key.ModKey.FileName.String.Equals(raw[(colon + 1)..].Trim(), StringComparison.OrdinalIgnoreCase)
            ? key
            : throw new HelperException("badRequest",
                $"'{raw.Trim()}' is not a FormID: {editorId} is {FormKeyText(key)}, defined in {key.ModKey.FileName} — write that, or {editorId} alone.");
    }

    // The FormID inside what was sent, as `013989:Skyrim.esm#SteelSword` holds one: told to write it as 012EB7:Skyrim.esm, the
    // Iron Sword's, whatever was asked, a model sent the next call by hand five times in eval rounds 87–105.
    private static readonly Regex FormIdIn = new(@"\b[0-9A-Fa-f]{6,8}:[^\s#:,;'""]+?\.es[mpl]\b", RegexOptions.IgnoreCase);

    /// <summary>A FormID with the record's name after it: `013989:Skyrim.esm#SteelSword`, `013989:Skyrim.esm:SteelSword`, `… # SteelSword`.</summary>
    private static readonly Regex FormIdNamed = new(@"^\s*(?<id>[0-9A-Fa-f]{1,8}:[^\s#:]+?\.es[mpl])\s*(?:#|:)\s*(?<name>[A-Za-z_][\w]*)\s*$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    /// <summary>
    /// The record a FormID with its editor ID after it names, where the two name one record: five calls of eval rounds 87–105
    /// sent `013989:Skyrim.esm#SteelSword`, each refused and sent again by hand. Where the name is another record's, both are
    /// named; where it is none, the refusal names the FormID inside.
    /// </summary>
    private FormKey? Named(string raw)
    {
        if (FormIdNamed.Match(raw) is not { Success: true } m || !FormKey.TryFactory(m.Groups["id"].Value, out var key)
            || !Index.ByFormKey.TryGetValue(key, out var entry)) return null;
        var name = m.Groups["name"].Value;
        if (string.Equals(entry.EditorId, name, StringComparison.OrdinalIgnoreCase)) return key;
        return Index.ByEditorId.TryGetValue(name, out var other)
            ? throw new HelperException("badRequest", $"'{raw.Trim()}' names two records: {FormKeyText(key)} is {entry.EditorId ?? "a record with no editor ID"}, " +
                $"and {name} is {FormKeyText(other)} — send the one meant alone, by its FormID or its editor ID.")
            : null;
    }

    private static FormKey ParseFormKey(string raw) =>
        FormKey.TryFactory(raw.Trim(), out var key)
            ? key
            : throw new HelperException("badRequest", $"'{raw}' is not a FormID: write it as " +
                (FormIdIn.Match(raw) is { Success: true } inside ? $"{inside.Value}, the FormID alone." : "012EB7:Skyrim.esm, its number and its plugin."));

    public static string FormKeyText(FormKey key) => $"{key.ID:X6}:{key.ModKey.FileName}";

    /// <summary>
    /// Each link to a record that exists nowhere, as the build refuses it: the file and the field it is written in, what it
    /// points at, and the call that deletes it. Told only "Hilda -> 000000:HildaTheNord.esp", the model set Hilda's class and
    /// outfit six times and wrote her file anew three times before it found the inventory line `- Item: 000000:HildaTheNord.esp
    /// # Placeholder`, and the run ended at the turn limit with no answer (eval round 89's npc+detailed #3).
    /// </summary>
    private static List<string> DanglingLinks(string dir, ModKey plugin, List<(string Record, FormKey Target)> dangling)
    {
        var written = RecordFiles.FlatRecords(dir)
            .Select(f => (f.Relative, Links: RecordLinks.LinksIn(File.ReadAllText(f.File), f.Type))).ToList();
        var project = ToolCalls.ProjectOf(dir);
        var named = new List<string>();
        foreach (var (record, target) in dangling)
        {
            var why = target.ID == 0 ? "a placeholder no record has"
                : target.ModKey == plugin ? "a FormID no record of the project has"
                : $"a FormID no record of {target.ModKey.FileName} in the load order has";
            var places = written.SelectMany(w => w.Links
                    .Where(l => FormKey.TryFactory(l.Value, out var key) && key == target)
                    .Select(l => (w.Relative, l.Path)))
                .ToList();
            if (places.Count == 0)
            {
                named.Add($"{record} -> {FormKeyText(target)}, {why}");
                continue;
            }
            foreach (var (relative, path) in places)
            {
                // An item of a list goes whole — the inventory's `Items[0]`, not its `Items[0].Item.Item` alone.
                var item = Regex.Match(path, @"^(.*\[\d+\])\..+$") is { Success: true } inList ? inList.Groups[1].Value : path;
                named.Add($"{relative.Replace('\\', '/')}: {path} -> {FormKeyText(target)}, {why} — point it at a record that " +
                    "exists, by its editor ID or FormID, or delete it: " + ToolCalls.Edit(project, relative, "", ToolCalls.Quoted(item), build: true));
            }
        }
        return named;
    }

    public static string Required(JsonObject p, string name) =>
        p[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new HelperException("badRequest", $"'{name}' is required.");

    private void DisposePlugins()
    {
        // The index reads the plugins in the background; let it finish before closing them.
        try { indexing?.Wait(); } catch { /* a failed index is rebuilt with the next load order */ }
        foreach (var plugin in plugins) plugin.Mod.Dispose();
        plugins.Clear();
        cache = null;
        indexing = null;
        placedIndex = null;
    }

    public void Dispose() => DisposePlugins();
}
