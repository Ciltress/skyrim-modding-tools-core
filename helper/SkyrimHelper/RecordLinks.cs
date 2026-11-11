using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SkyrimHelper;

/// <summary>
/// Links written by editor ID — <c>BaseEffect: StormBoltDamage</c>, <c>ObjectEffect: FlameBladeEnch:FlameBlade.esp</c>
/// — replaced in their files by the FormKey of the record they name, before the build reads them. The
/// reader takes a link as a FormKey only, and a new record's FormKey exists only once the build assigned
/// it: models named the record they had just written by its editor ID, and the build failed with
/// "Malformed FormKey string" (eval round 10).
/// </summary>
/// <remarks>
/// A value is a link when the field it is written under is one in Mutagen's record class — so a
/// <c>Name</c> that happens to equal an editor ID stays as written. The project's own records are looked
/// up first, then the load order's; the record found must be of a type the field takes. A link written
/// as another plugin's FormKey is held to the same: a record of the load order, of a type the field takes.
/// Only records directly in a group's folder are read; cells, worldspaces and dialogue are left as written.
/// </remarks>
public static class RecordLinks
{
    private static readonly Regex ByEditorId = new(@"^(?<id>[A-Za-z0-9_]+)(?::(?<plugin>\S+\.es[mpl]))?$", RegexOptions.IgnoreCase);
    /// <summary>A FormID without its plugin, or with the load-order slot in front: 012EB7, 00012EB7, 00012EB7:Skyrim.esm.</summary>
    /// A plugin is a plugin's file name: F000123:CustomFireEnchantment is none (eval round 11's probes).
    private static readonly Regex LoadOrderFormId = new(@"^[0-9A-Fa-f]{0,2}(?<local>[0-9A-Fa-f]{6})(?::(?<plugin>\S+\.es[mpl]))?$", RegexOptions.IgnoreCase);

    /// <summary>A record a link may name; a new record of the project has no FormKey until the build assigns one.</summary>
    private sealed record Target(FormKey? FormKey, string EditorId, Type Type);
    private sealed record Replacement(int Start, int End, string Text);

    /// <summary>
    /// Finds the FormKey of the record each editor-ID link names, and answers what each named; <paramref name="rewrites"/>
    /// holds each file with them written in, for the reader's copy. The project's files keep the editor IDs: written
    /// into them as FormIDs, the model's next edits of those lines found nothing, 12 times in eval rounds 40–47 — Storm Bolt's
    /// `MenuDisplayObject: MAGINVShockSpellArt` in seven. A link to the load order keeps to its pin (LinkPins).
    /// Throws when a link names no record of a type its field takes. <paramref name="lookup"/> reads the load order's records
    /// a refusal names the lines of (LikeWeapon).
    /// </summary>
    public static JsonArray Resolve(string pluginDir, RecordIndex index, IReadOnlyDictionary<string, FormKey> pins,
        out List<(string Relative, string Text)> rewrites, ILinkCache? lookup = null)
    {
        var (own, unnamed) = OwnRecords(pluginDir);
        var ownPlugin = RecordFiles.OwnPlugin(pluginDir);
        var resolved = new JsonArray();
        var problems = new List<string>();
        var rewritten = new List<(string Relative, string Text)>();
        // The record a pin names, while the load order has it under that editor ID still.
        Target? Pinned(string editorId) =>
            pins.TryGetValue(editorId, out var pin) && index.ByFormKey.TryGetValue(pin, out var entry)
                && string.Equals(entry.EditorId, editorId, StringComparison.OrdinalIgnoreCase)
                ? new Target(pin, entry.EditorId!, entry.GetterType) : null;

        // An interior cell's own file and the references placed in it as well: a chest placed by its editor ID, `Base: HuntersChest`,
        // was refused by the reader as a malformed FormKey, naming the cell's file (the eval's hand-made world mods, 2026-11-04).
        // A dialogue topic's own file and its responses too (RecordFiles.DialogRecords).
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir).Concat(RecordFiles.NestedRecords(pluginDir)))
        {
            var text = File.ReadAllText(file);
            var yaml = new YamlStream();
            try
            {
                yaml.Load(new StringReader(text));
            }
            catch (YamlException e)
            {
                problems.Add($"{relative}: {YamlBreak(text, e, type, pluginDir, relative)}");
                continue;
            }
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root) continue;
            if (FieldInText(text, type, root) is { } swallowed) problems.Add($"{relative}: {swallowed}");
            if (TakenEditorId(text, index, ownPlugin) is { } taken) problems.Add($"{relative}: {taken}");
            // What every official weapon or armor of its kind carries itself, named with the links' problems.
            if (RecordFiles.Identity(text).FormKey is not { } written || written.ModKey == ownPlugin)
                if (RecordFiles.Uncarried(type, text, index) is { } uncarried) problems.Add($"{relative}: {uncarried}");

            var replacements = new List<Replacement>();
            // The weapon most like this file's, looked for once a file, and its keywords' call given once.
            (string EditorId, List<string> Keywords)? like = null;
            var (likeLooked, likeGiven) = (false, false);
            // The fields linking to no record that the record can go without, deleted by the one call the last of them gives; and
            // the load order's weapons this file was written after, looked for once a file. Told only to write the one meant, the
            // model kept two sounds it had made up beside IronSword's three, which IronSword has not, until its turns ran out
            // (eval round 112's sword+detailed #3).
            var unlinked = new List<string>();
            var lastUnlinked = -1;
            List<(string Name, IWeaponGetter Weapon)>? followed = null;
            // Where the refusal offers the records closest to a name, the deletion is offered only where the weapon followed has no
            // such field: a spell's HalfCostPerk written DestructionApprentice is DestructionApprentice25, not a line to drop.
            string Unlinked(string field, bool offered = false)
            {
                if (field.Contains('.') || field.Contains('[') || Kept.Contains(field)) return "";
                var (said, lacks) = ((string?)null, false);
                if (type == typeof(Weapon) && lookup is not null)
                {
                    followed ??= KeptFromSource.Followed(lookup, index, text);
                    if (followed.Count > 0 && followed.Select(f => KeptFromSource.LinkOn(f.Weapon, field, index)).Distinct().ToList() is [var held])
                        (said, lacks) = held is null
                            ? ($" — {followed[0].Name}, the weapon this file was written after, has no {field}", true)
                            : ($" — {followed[0].Name}, the weapon this file was written after, has `{field}: {held}`", false);
                }
                if (offered && !lacks) return said ?? "";
                unlinked.Add(field);
                lastUnlinked = problems.Count;
                return lacks ? $"{said}: delete the line" : $"{said}; or, if none is meant, delete the line";
            }
            // An enchantment's base named as the magic effect it casts, the line beside its Effects: offered another enchantment
            // to name, the model's went round its files until the turns ran out, naming the game's base on the last (eval round
            // 95's sword #1). A new enchantment does without one. Named by its FormID, it was told only to write the one meant,
            // and went on naming the magic effect (eval round 101's sword+detailed #4).
            string? BaseAsEffect(string field, string wanted, string kind, string effect) =>
                wanted == "ObjectEffect" && kind == "MagicEffect" && field == "BaseEnchantment"
                    ? $" — the enchantment the player learns by disenchanting this one, not a magic effect it casts (that is `- BaseEffect: " +
                        $"{effect}` among its Effects); the game's enchantments of one kind name their base there, as readRecord " +
                        $"{{\"record\": \"{RecordFiles.ExampleName("ObjectEffect")}\"}} shows, and a new enchantment does without one: one " +
                        "call deletes the line and builds again — " +
                        ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, "", "\"BaseEnchantment\"", build: true)
                    : null;
            // An effect item naming what casts magic effects — an enchantment, a spell — in place of the magic effect: told only to
            // write the one meant, the model named the game's EnchWeaponFireDamage01 there by its FormID, then by its editor ID,
            // then by its FormID again, and answered that the build was stuck (eval round 116's sword+detailed #1, which failed).
            // The load order's record says which magic effects it casts.
            string? CasterAsEffect(string field, string wanted, string editorId, FormKey? key)
            {
                if (wanted != "MagicEffect" || !field.EndsWith("].BaseEffect", StringComparison.Ordinal) || key is not { } caster
                    || lookup is null || !lookup.TryResolve<IMajorRecordGetter>(caster, out var record)) return null;
                var (effects, what) = record switch
                {
                    IObjectEffectGetter e => (e.Effects, "an enchantment"),
                    ISpellGetter s => (s.Effects, "a spell"),
                    IScrollGetter s => (s.Effects, "a scroll"),
                    IIngestibleGetter i => (i.Effects, "a potion"),
                    IIngredientGetter i => (i.Effects, "an ingredient"),
                    _ => ((IReadOnlyList<IEffectGetter>?)null, "")
                };
                var cast = (effects ?? []).Select(e => e.BaseEffect.FormKeyNullable).OfType<FormKey>().Distinct()
                    .Select(k => index.ByFormKey.TryGetValue(k, out var entry) && entry.Signature == "MGEF" && entry.EditorId is { } id
                        ? (EditorId: id, Key: k) : ((string EditorId, FormKey Key)?)null)
                    .OfType<(string EditorId, FormKey Key)>().ToList();
                if (cast.Count == 0) return null;
                var said = $" — {editorId} is {what}, which casts the magic effects its Effects name, not one of them: its Effects name ";
                return cast is [var one]
                    ? said + $"{one.EditorId} ({Session.FormKeyText(one.Key)}) — one call names it here and builds again: " +
                        ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative,
                            $"{{\"path\": \"{field}\", \"value\": {ToolCalls.Quoted(one.EditorId)}}}", build: true)
                    : said + $"{string.Join(", ", cast.Select(c => $"{c.EditorId} ({Session.FormKeyText(c.Key)})"))} — write the one meant " +
                        $"here, `BaseEffect: {cast[0].EditorId}` for the first";
            }
            // What to write for a link to a record of another type, the same however the link was written: by FormID the magic effect
            // named as a sword's enchantment got the short text, the model wrote the same effect by its editor ID, and was told only
            // then that an enchantment names the effects it casts (eval round 104's sword #4, which failed).
            // An item in a leveled NPC list: the list its NPCs carry it from named, and the copy's deletion. Asked for bandits
            // carrying a sword, eval round 128's 5 detailed runs copied LCharBanditMelee1H and added the sword there; told only "takes
            // a NpcSpawn", none found the item list, and the copy left kept each later build refused.
            string? ItemInNpcList(string wanted, string kind, string editorId)
            {
                if (wanted != "NpcSpawn" || !ItemKinds.Contains(kind)) return null;
                var lists = CarriedLists(text, index, lookup);
                var word = kind is "Weapon" ? "Weapon" : kind is "Armor" ? "Armor" : null;
                var named = lists.OrderByDescending(l => word is not null && l.Contains(word, StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
                return $" — {editorId} is {RecordFiles.Article(kind)}, and this is a leveled NPC list: its entries are NPCs. What its NPCs carry " +
                    "comes from the leveled item lists their Items name" +
                    (named.Count > 0 ? $" — {string.Join(", ", named)}" : " — readRecord one of its NPCs and its template to find them") +
                    $": addPatch the one meant and add {editorId} to its Entries there, every entry kept; and where this list was copied only " +
                    $"for it, delete the copy, which nothing else needs: {ToolCalls.Delete(pluginDir, relative)}";
            }
            // A recipe named where an item belongs: told only to write the one meant, eval round 129's world+detailed #5 put 500 of
            // RecipeIngotGold in its chest through three builds. The item the recipe makes, and the items named by a word of the
            // recipe's editor ID — Gold001 "Gold" — each with its call.
            string? RecipeAsItem(string field, string wanted, string kind, string editorId, FormKey? key)
            {
                if (kind != "ConstructibleObject" || !(wanted == "Item" || ItemKinds.Contains(wanted))) return null;
                var offered = new List<(string Id, string? Name, FormKey Key, bool Made)>();
                if (key is { } recipe && lookup is not null && lookup.TryResolve<IConstructibleObjectGetter>(recipe, out var cobj)
                    && cobj.CreatedObject.FormKeyNullable is { } made && index.ByFormKey.TryGetValue(made, out var madeEntry) && madeEntry.EditorId is { } madeId)
                    offered.Add((madeId, madeEntry.Name, made, true));
                var words = Regex.Split(editorId, "(?<=[a-z])(?=[A-Z])|[^A-Za-z]+")
                    .Where(w => w.Length > 2 && !w.Equals("Recipe", StringComparison.OrdinalIgnoreCase) && !w.Equals("Temper", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var entry in index.ByFormKey.Values
                    .Where(e => ItemKinds.Contains(e.Type) && e.Type != "LeveledItem" && !e.Unplayable && e.EditorId is not null
                        && e.Name is { } name && words.Any(w => name.Equals(w, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(e => e.FormKey.ModKey.FileName.String.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase).Take(2))
                    if (!offered.Any(o => o.Key == entry.FormKey)) offered.Add((entry.EditorId!, entry.Name, entry.FormKey, false));
                if (offered.Count == 0) return null;
                return $" — {editorId} is a recipe, the way a workbench makes an item, not an item: name the item itself, one call each naming it " +
                    "here and building again — " + string.Join("; or ", offered.Select(o =>
                        $"{o.Id}{(o.Name is { Length: > 0 } shown ? $" \"{shown}\"" : "")} ({Session.FormKeyText(o.Key)}){(o.Made ? ", the one it makes" : "")}: " +
                        ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, $"{{\"path\": \"{field}\", \"value\": {ToolCalls.Quoted(o.Id)}}}", build: true)));
            }
            string WrongType(string field, string wanted, string kind, string editorId, FormKey? key) =>
                BaseAsEffect(field, wanted, kind, editorId) is { } baseText ? baseText
                    : ItemInNpcList(wanted, kind, editorId) is { } npcListText ? npcListText
                    : RecipeAsItem(field, wanted, kind, editorId, key) is { } recipeText ? recipeText
                    : CasterAsEffect(field, wanted, editorId, key) is { } casterText ? casterText
                    // A recipe placed as a chest: eval round 131's world #2 made its chest of a copy of a Hearthfire recipe.
                    : field == "Base" && kind == "ConstructibleObject"
                        ? $" — {editorId} is a recipe, the way a workbench makes an item, and nothing the world holds: a reference places the " +
                          "object itself — a chest that holds items is a Container, in Containers/, written as readRecord of a game's chest " +
                          "shows one, its Items listed in it"
                    : wanted == "ObjectEffect" && kind == "MagicEffect"
                        ? $" — an enchantment, whose Effects name the magic effects it casts: name one, such as {RecordFiles.ExampleName("ObjectEffect")}, " +
                          $"or write one in ObjectEffects with `- BaseEffect: {editorId}` among its Effects, as readRecord {{\"record\": " +
                          $"\"{RecordFiles.ExampleName("ObjectEffect")}\"}} shows them, and name it here"
                        : " — write the editor ID of the one meant, or its FormID as findRecords answers it";
            void Link(YamlScalarNode value, Type takes, string field)
            {
                var raw = value.Value?.Trim() ?? "";
                if (raw.Length == 0 || raw is "~" || raw.Equals("null", StringComparison.OrdinalIgnoreCase)) return;
                var wanted = Kind(takes);
                if (FormKey.TryFactory(raw, out var linked))
                {
                    // None and Null are the reader's no record: DefaultOutfit: None was refused as missing (eval probe 8).
                    if (linked.IsNull) return;
                    // A FormID written from memory under the comment of the record meant: the model's Hilda had
                    // `Race: 02C65A:Skyrim.esm  # NordRace`, RedguardRaceChild's, and built a plain success (eval round 22).
                    if (CommentedOther(text, value, linked, takes, own, index) is { } other)
                    {
                        var leaf = field[(field.LastIndexOf('.') + 1)..];
                        string Line(string id) => leaf.EndsWith(']') ? $"- {id}" : $"{leaf}: {id}";
                        var was = other.Linked is { } linkedEditorId ? $"which is {linkedEditorId}" : "which no record has";
                        if (other.Meant is { } meant)
                            problems.Add($"{relative}: {field} is {raw}, {was}, but the comment after it names {meant.EditorId}" +
                                (meant.FormKey is { } meantKey ? $" ({Session.FormKeyText(meantKey)})" : ", a new record of this project") +
                                $" — the build reads the FormID and skips the comment. Write the one meant by its editor ID: `{Line(meant.EditorId)}`" +
                                (other.Linked is { } linkedId ? $" or `{Line(linkedId)}`" : ""));
                        else
                        {
                            var near = Near(other.Named, takes, own, index);
                            problems.Add($"{relative}: {field} is {raw}, {was}, but the comment after it names {other.Named}, which no record has " +
                                "— the build reads the FormID and skips the comment. Write the one meant by its editor ID" +
                                (near.Count > 0 ? $" — the {wanted} records closest to {other.Named}: {string.Join(", ", near)}" : $" — find the {wanted} with findRecords") +
                                (other.Linked is { } linkedId ? $"; or `{Line(linkedId)}`, the one the FormID names" : ""));
                        }
                        return;
                    }
                    // The project's own record by the FormID a build gave it, of a type the field does not take: eval round 131's
                    // world #2 placed its copy of a recipe as `Base: 000800:BreezehomeChest.esp` once `Base: BreezehomeChest` was
                    // refused as a ConstructibleObject, and built.
                    if (linked.ModKey == ownPlugin && own.Values.FirstOrDefault(t => t.FormKey == linked) is { } numberedOwn
                        && !takes.IsAssignableFrom(numberedOwn.Type))
                    {
                        problems.Add($"{relative}: {field} is {raw}, {numberedOwn.EditorId} of this project, which is " +
                            $"{RecordFiles.Article(Kind(numberedOwn.Type))}; {field} takes {RecordFiles.Article(wanted)}" +
                            WrongType(field, wanted, Kind(numberedOwn.Type), numberedOwn.EditorId, linked));
                        return;
                    }
                    // A FormID written from memory names another record, or none: Race: 011F7B:Skyrim.esm
                    // (eval round 11's probes). The project's own are numbered by the build, and checked after it.
                    if (linked.ModKey != ownPlugin && Mismatch(linked, takes, index) is { } mismatch)
                        problems.Add($"{relative}: {field} is {raw}, {mismatch}; {field} takes {RecordFiles.Article(wanted)}" +
                            (index.ByFormKey.TryGetValue(linked, out var linkedEntry)
                                ? WrongType(field, wanted, linkedEntry.Type, linkedEntry.EditorId ?? raw, linked)
                                : " — write the editor ID of the one meant, or its FormID as findRecords answers it" + Unlinked(field)));
                    return;
                }
                if (LoadOrderFormId.Match(raw) is { Success: true } numbered)
                {
                    // 00012EB7 as the console shows it: the load-order slot first, and no plugin.
                    problems.Add($"{relative}: {field} is {raw} — a FormID is six digits and the plugin that defines the record, " +
                        $"as findRecords answers it: {numbered.Groups["local"].Value}:" +
                        (numbered.Groups["plugin"].Success ? numbered.Groups["plugin"].Value : "Skyrim.esm for a record of Skyrim.esm"));
                    return;
                }
                // A file where a record goes: told "a link is a FormID … or the editor ID of a Static", the model wrote
                // `FirstPersonModel: 1stPersonWeapons\FlameBlade.nif` three times (eval probe 20).
                if (FilePath.IsMatch(raw))
                {
                    problems.Add($"{relative}: {field} is '{raw}', a file — {field} names {RecordFiles.Article(wanted)} record, the one " +
                        "that holds the file: write the line of the vanilla record you mirror, as readRecord shows it" +
                        (field == "FirstPersonModel" ? " — an iron sword's is `FirstPersonModel: 1stPersonIronSword`" : ""));
                    return;
                }
                if (ByEditorId.Match(raw) is not { Success: true } written)
                {
                    problems.Add($"{relative}: {field} is '{raw}' — " + (ByName(raw, takes, wanted, field, pluginDir, index, own)
                        ?? $"a link is a FormID like 012EB7:Skyrim.esm, or the editor ID of {RecordFiles.Article(wanted)}"));
                    return;
                }
                var editorId = written.Groups["id"].Value;
                var found = own.TryGetValue(editorId, out var mine) ? mine
                    : Pinned(editorId) ?? (index.ByEditorId.TryGetValue(editorId, out var key) && index.ByFormKey.TryGetValue(key, out var entry)
                        ? new Target(key, entry.EditorId ?? editorId, entry.GetterType)
                        : null);
                // A record of another project the model made, which no build of this one reads: told "no record has that editor
                // ID", the model tried addPatch from that project twice and found moveRecord four turns later (eval round 92's
                // sword+detailed #4, which failed).
                if (found is null && InOtherProject(pluginDir, editorId) is { } elsewhere)
                {
                    var group = Path.GetFileName(Path.GetDirectoryName(elsewhere)) ?? "";
                    problems.Add($"{relative}: {field} names {editorId}, which is a record of another project, {elsewhere} — a plugin holds its own " +
                        $"project's records alone: move the file into this project, " +
                        ToolCalls.Move(pluginDir, elsewhere, $"{group}/{Path.GetFileName(elsewhere)}"));
                    return;
                }
                if (found is null)
                {
                    // A weapon's keyword: the keywords of the weapon most like it, whole. Offered the closest names, the model
                    // took MagicFortifyOneHanded, a magic effect's keyword, for its sword's `OneHanded` (eval round 106's sword #4).
                    if (type == typeof(Weapon) && wanted == "Keyword" && field.StartsWith("Keywords[", StringComparison.Ordinal) && lookup is not null)
                    {
                        if (!likeLooked) (like, likeLooked) = (LikeWeapon(root, own, index, lookup, ownPlugin), true);
                        if (like is { } weapon)
                        {
                            var keywords = weapon.Keywords.Concat(KnownKeywords(root, own, index)).Distinct(StringComparer.OrdinalIgnoreCase);
                            problems.Add($"{relative}: {field} names {editorId}, and no record has that editor ID — " + (likeGiven
                                ? "the call above writes the weapon's keywords"
                                : $"a weapon carries the keywords of its kind: {weapon.EditorId}, the load order's weapon most like this " +
                                  $"one, carries {string.Join(", ", weapon.Keywords)}. One call writes them, with this weapon's own the load " +
                                  "order has, and builds again: " + ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative,
                                      $"{{\"path\": \"Keywords\", \"value\": [{string.Join(", ", keywords.Select(ToolCalls.Quoted))}]}}", build: true)));
                            likeGiven = true;
                            return;
                        }
                    }
                    var near = Near(editorId, takes, own, index);
                    // A record of the project written without an EditorID line has no name a link can use (eval round 11).
                    var nameless = unnamed.Where(u => takes.IsAssignableFrom(u.Type)).Select(u => u.Relative).ToList();
                    problems.Add($"{relative}: {field} names {editorId}, and no record has that editor ID — " +
                        (nameless.Count > 0 ? $"{string.Join(", ", nameless)} {(nameless.Count == 1 ? "has" : "have")} no EditorID line: " +
                            $"give the record meant the line EditorID: {editorId}" + (near.Count > 0 ? "; or " : "") : "") +
                        (near.Count > 0 ? $"the {wanted} records closest to it: {string.Join(", ", near)}"
                            : nameless.Count > 0 ? "" : $"find the {wanted} with findRecords") + Unlinked(field, near.Count > 0 || nameless.Count > 0));
                    return;
                }
                if (!takes.IsAssignableFrom(found.Type))
                {
                    // A weapon's enchantment named as the magic effect it was to cast: all 3 runs of eval rounds 40–53 that
                    // were refused so failed — round 53's sword+detailed #5 rewrote its magic effect until the turns ran out.
                    problems.Add($"{relative}: {field} names {found.EditorId}, which is {RecordFiles.Article(Kind(found.Type))}; {field} takes {RecordFiles.Article(wanted)}" +
                        WrongType(field, wanted, Kind(found.Type), found.EditorId, found.FormKey));
                    return;
                }
                if (found.FormKey is not { } target) return; // A new record, numbered after this check.
                var formKey = Session.FormKeyText(target);
                replacements.Add(new Replacement((int)value.Start.Index, (int)value.End.Index, formKey));
                resolved.Add(new JsonObject { ["file"] = relative, ["field"] = field, ["from"] = raw, ["to"] = formKey });
            }

            // The NPC's level is given whole once per file, however many of its fields are misplaced.
            var levelGiven = false;
            var before = problems.Count;
            Walk(root, type, "", Link, problem =>
            {
                if (problem.Contains(RecordFiles.NpcLevel))
                {
                    if (levelGiven) problem = problem.Replace(RecordFiles.NpcLevel, "the level's lines as above");
                    levelGiven = true;
                }
                problems.Add($"{relative}: {problem}");
            }, deleting: (field, set) => ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, set, ToolCalls.Quoted(field), build: true));
            // A new record nothing else names can be deleted, said first, after the file's name (RecordFiles.Droppable).
            if (problems.Count > before)
                problems[before] = problems[before].Insert(relative.Length + 2, RecordFiles.Droppable(type, relative, text,
                    RecordFiles.FlatRecords(pluginDir).Where(f => f.Relative != relative).Select(f => (f.Relative, File.ReadAllText(f.File))), pluginDir));
            if (lastUnlinked >= 0)
                problems[lastUnlinked] += $" — one call deletes {(unlinked.Count == 1 ? "it" : string.Join(" and ", unlinked))} and builds again: " +
                    ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, "", string.Join(", ", unlinked.Select(ToolCalls.Quoted)), build: true);
            if (replacements.Count == 0) continue;
            var withFormIds = text;
            foreach (var r in replacements.OrderByDescending(r => r.Start))
                withFormIds = withFormIds[..r.Start] + r.Text + withFormIds[r.End..];
            rewritten.Add((relative, withFormIds));
        }
        // A new enchantment no record names, beside a weapon or armor that carries none (RecordFiles.Unworn).
        var records = RecordFiles.FlatRecords(pluginDir).Select(f => (f.Relative, f.Type, Text: File.ReadAllText(f.File)))
            .Where(f => !RecordFiles.Empty(f.Text)).ToList();
        foreach (var (relative, enchantment, carriers) in RecordFiles.Unworn(records, ownPlugin).Where(u => u.Carriers.Count > 0))
            problems.Add(RecordFiles.UnwornProblem(relative, enchantment, carriers));

        if (problems.Count > 0)
        {
            throw new HelperException("badRecord",
                $"{problems.Count} field(s) cannot be built as written: {string.Join("; ", problems.Take(12))}" +
                (problems.Count > 12 ? "; …" : "") +
                ". Write each field the way readRecord shows a record of its type; a link is a record's FormID, or the exact " +
                "editor ID of a record of this project's or of the load order.");
        }
        rewrites = rewritten;
        return resolved;
    }

    /// <summary>A field whose value is a block text, <c>Value: &gt;-</c> or <c>Value: |</c>, as readRecord writes a long one.</summary>
    private static readonly Regex BlockTextHeader = new(@"^ *(?:- +)?(?<key>[A-Za-z0-9_]+): +[|>][-+1-9]* *(?:#.*)?$");

    /// <summary>
    /// Where and why a record file's YAML breaks, with the line it breaks on — named before the build
    /// numbers the file and renames it. A line that fell out of a block text is said as such: told to
    /// give every line a `Field: value`, the model rewrote a note five times with its paragraphs still at the
    /// margin, where they end the text and are read as fields (eval round 16).
    /// </summary>
    internal static string YamlBreak(string text, YamlException e, Type type, string pluginDir, string relative)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var at = (int)e.Start.Line;
        var where = at >= 1 && at <= lines.Length ? $"line {at}, `{lines[at - 1].Trim()}`" : $"line {at}";
        // A tab where YAML takes spaces: the parser named line 1, or the line before the tab, and the advice to indent by two
        // spaces never said the tab was the fault (refusal probes of 2026-10-31, G01, G16, F19).
        var tabbed = lines.Select((line, i) => (line, i)).Where(l => Regex.IsMatch(l.line, @"^ *\t")).Select(l => l.i + 1).ToList();
        if (tabbed.Count > 0)
            return $"the YAML breaks: {LineList(tabbed)} {(tabbed.Count == 1 ? "is" : "are")} indented with a tab, and YAML indents with " +
                $"spaces alone — write two spaces for each tab in front of {(tabbed.Count == 1 ? "it" : "them")}";
        if (TextLeft(lines, at) is { } block)
        {
            // Told only to indent the text, the model indented the fields after it too, then put both back at the margin,
            // four times (eval round 20): what stays as it is, said with what moves.
            var fields = RecordFiles.FieldsOf(type);
            var next = Array.FindIndex(lines, at, line => MarginField.Match(line) is { Success: true } m && fields.Contains(m.Groups["key"].Value));
            return $"the YAML breaks at {where}: the line is part of the text under `{block.Header}` (line {block.Line}), but a text " +
                $"ends at the first line indented less than its own first line, and the rest is read as fields. Indent every line of " +
                $"the text by {block.Indent} spaces, as readRecord shows a text — `{new string(' ', block.Indent)}{lines[at - 1].Trim()}`; " +
                "an empty line may stay empty" +
                (next >= 0 ? $"; the record's fields after the text, from `{lines[next].Trim()}` (line {next + 1}) on, stay at the margin" : "");
        }
        var reason = Regex.Replace(e.Message, @"^\(Line:[^)]*\)( - \(Line:[^)]*\))?:\s*", "").Trim();
        // Told to remove a FormKey line, the model wrote the line below it over it, and was then sent to fix its indentation
        // while it looked for the FormKey line again, until its turns ran out (eval round 21): both lines, named.
        if (Regex.Match(reason, @"^Duplicate key (?<key>[A-Za-z0-9_]+)") is { Success: true } duplicate && at >= 2 && at <= lines.Length)
        {
            var key = duplicate.Groups["key"].Value;
            var indent = lines[at - 1].Length - lines[at - 1].TrimStart().Length;
            var first = Array.FindLastIndex(lines, at - 2, line => line.Length - line.TrimStart().Length == indent && line.TrimStart().StartsWith($"{key}:"));
            // A text's second Value, a number: told to move a price under BasicStats, the model put it under Name, and was told
            // to keep `Value: Flame Blade` or `Value: 150` — both meant (eval rounds 97, 101 and 102, 2–4 turns each).
            var header = first >= 1 ? Array.FindLastIndex(lines, first - 1, line => line.Trim().Length > 0 && line.Length - line.TrimStart().Length < indent) : -1;
            if (first >= 0 && key == "Value" && header >= 0 && Regex.Match(lines[header], @"^\s*(?<text>[A-Za-z]+):\s*$") is { Success: true } part
                && type.GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p => p.Name == part.Groups["text"].Value) is { } textField && typeof(ITranslatedStringGetter).IsAssignableFrom(textField.PropertyType)
                && new[] { first, at - 1 }.Select(i => Regex.Match(lines[i], @"Value:\s*['""]?(?<n>-?\d+(?:\.\d+)?)['""]?\s*$")).ToList() is var numbers
                && numbers.Count(m => m.Success) == 1 && WorthPath(type) is { } worth)
            {
                var number = numbers.First(m => m.Success).Groups["n"].Value;
                var numberLine = numbers[0].Success ? first + 1 : at;
                var textName = part.Groups["text"].Value;
                return $"the YAML breaks at {where}: Value is written twice under `{textName}:` (line {header + 1}), on lines {first + 1} and {at} — " +
                    $"{textName}'s Value is its words, and {number} is no part of a text. If {number} is the price, {RecordFiles.Article(type.Name)}'s " +
                    $"price is {worth}: delete line {numberLine}, `{lines[numberLine - 1].Trim()}`, then set it there with one call that builds again: " +
                    ToolCalls.Edit(ToolCalls.ProjectOf(pluginDir), relative, $"{{\"path\": \"{worth}\", \"value\": {number}}}", build: true);
            }
            if (first >= 0)
                return $"the YAML breaks at {where}: {key} is written twice, on lines {first + 1} and {at}, and a field is written once — " +
                    (lines[first].Trim() == lines[at - 1].Trim()
                        ? "delete one of the two lines, they are the same"
                        : $"keep the one meant, `{lines[first].Trim()}` or `{lines[at - 1].Trim()}`, and delete the other");
        }
        static int Depth(string line) => line.Length - line.TrimStart(' ').Length;
        // A quote mark alone, where taking it out did not make the file read (RecordFiles.DropLoneQuotes): the parser's words
        // and the general advice sent the model to edit other lines (eval round 107's sword #1).
        if (at >= 1 && at <= lines.Length && RecordFiles.LoneQuote.IsMatch(lines[at - 1]))
            return $"the YAML breaks at {where}: a quote mark alone on a line opens a quoted text that runs on to the end of the file, " +
                "and writes no field — delete the line";
        // Deeper than an item of a list of names: YAML reads the line as more words of the item, and the value on it was blamed —
        // a level's `{MutagenObjectType: …}` told to be quoted (eval round 107's npc+detailed #3). Its fields are moved where that
        // makes the file read (MisplacedFields.OutFromUnderItems).
        if (at >= 2 && at <= lines.Length && MisplacedFields.UnderItem(lines, at - 1) is { } under)
            return $"the YAML breaks at {where}: it is {Depth(lines[at - 1])} spaces deep below line {under.Item + 1}, `{lines[under.Item].Trim()}`, " +
                $"an item of `{under.Key}:` (line {under.List + 1}) — an item of a list of names holds no lines below it, so YAML reads this " +
                $"line as more words of it. A field beside `{under.Key}:` is written as deep as it, with {Depth(lines[under.List])} spaces in front";
        // What the parser says of a value written in a way YAML reads otherwise, said as the value written right — each was
        // answered "indent a field's own fields", which fixes none of them (refusal probes of 2026-10-31, B10, B11, W31, G22).
        if (at >= 1 && at <= lines.Length && Written(lines[at - 1], reason) is { } written)
            return $"the YAML breaks at {where}: {written}";
        if (reason.Contains("did not find expected key") && at >= 2 && at <= lines.Length)
        {
            var blamed = lines[at - 1];
            var before = Array.FindLastIndex(lines, at - 2, line => line.Trim().Length > 0);
            // A field deeper than the fields beside it: `    Value: 25` among BasicStats' two-space fields was blamed on the line
            // after it, and indenting the lines named took three builds (refusal probes, W05, F06).
            if (before >= 0 && Depth(lines[before]) > Depth(blamed) && ValueLine.IsMatch(lines[before])
                && Array.FindLastIndex(lines, before - 1, line => line.Trim().Length > 0 && Depth(line) < Depth(lines[before])) is var part and >= 0
                && Depth(lines[part]) < Depth(blamed))
                return $"the YAML breaks at line {before + 1}, `{lines[before].Trim()}`: it is {Depth(lines[before])} spaces deep, and the fields " +
                    $"of `{lines[part].Trim()}` after it {Depth(blamed)} — the fields of one part are written equally deep: write it with " +
                    $"{Depth(blamed)} spaces in front, as `{lines[at - 1].Trim()}` is";
            // An item of a list without its dash, blamed on the next item (refusal probes, S21).
            if (Regex.Match(blamed, @"^(?<dash> *)- +(?<key>[A-Za-z0-9_]+):") is { Success: true } item)
            {
                var dash = item.Groups["dash"].Length;
                var header = Array.FindLastIndex(lines, at - 2, line => line.Trim().Length > 0 && Depth(line) <= dash && !line.TrimStart().StartsWith('-'));
                var undashed = header < 0 ? -1 : Array.FindIndex(lines, header + 1, at - 2 - header, line => Depth(line) == dash + 2
                    && Regex.IsMatch(line, $@"^ *{Regex.Escape(item.Groups["key"].Value)}:"));
                if (undashed >= 0)
                    return $"the YAML breaks at line {undashed + 1}, `{lines[undashed].Trim()}`: it starts an item of `{lines[header].Trim()}` without its " +
                        $"dash — write it `{new string(' ', dash)}- {lines[undashed].Trim()}`, the item's lines below it as they are";
            }
        }
        // Indented below a field that has a value of its own: a part's fields whose line was lost, under `EnchantmentAmount:
        // 500` (eval round 35's sword+detailed #3) — the line above, named; the record's own fields there are moved
        // (MisplacedFields.Dedent).
        if (at >= 2 && at <= lines.Length && Depth(lines[at - 1]) > 0
            && Array.FindLastIndex(lines, at - 2, line => line.Trim().Length > 0 && Depth(line) < Depth(lines[at - 1])) is var above and >= 0
            && ValueLine.IsMatch(lines[above]))
            return $"the YAML breaks at {where}: it is indented below line {above + 1}, `{lines[above].Trim()}`, which has a value of its " +
                "own, so no line goes below it. A field beside it is written as deep as it is; a part's fields go below a line of the " +
                "part's name alone, `Part:`";
        // Indented below a part written whole in braces on its line, `Level: {MutagenObjectType: PcLevelMult, LevelMult: 1}`, the
        // level's bounds below it: told to indent a field's own fields, which they were, the model sent editRecord the same call
        // three times (eval round 111's npc #4).
        if (at >= 2 && at <= lines.Length && Depth(lines[at - 1]) > 0
            && Array.FindLastIndex(lines, at - 2, line => line.Trim().Length > 0 && Depth(line) < Depth(lines[at - 1])) is var braced and >= 0
            && Regex.Match(lines[braced], @"^[ \t]*(?:- +)?(?<key>[A-Za-z0-9_]+):[ \t]*\{.*\}[ \t]*(?:#.*)?$") is { Success: true } whole)
        {
            var key = whole.Groups["key"].Value;
            return $"the YAML breaks at {where}: it is indented below line {braced + 1}, `{lines[braced].Trim()}`, a part written whole in its " +
                $"braces on that line, so no line goes below it. A field beside {key} is written as deep as `{key}:` is; a field of {key} goes " +
                $"inside its braces, or `{key}:` is written alone with its fields below it, two spaces deeper";
        }
        return $"the YAML breaks at {where}: {reason} Indent a field's own fields by two spaces below it, and give every other line a `Field: value`";
    }

    /// <summary>
    /// A line whose value YAML reads otherwise than it was meant, as the words that say how to write it — a value holding
    /// `: `, a path in double quotes, double quotes inside double quotes, no space after the colon — or null.
    /// </summary>
    private static string? Written(string line, string reason)
    {
        if (Regex.Match(line, @"^(?<indent> *(?:- +)?)(?<key>[A-Za-z][A-Za-z0-9_]*):[ \t]+(?<value>.*?)[ \t]*$") is { Success: true } field)
        {
            var (key, value) = (field.Groups["key"].Value, field.Groups["value"].Value);
            string Single(string words) => $"`{key}: '{words.Replace("'", "''")}'`";
            if (reason.Contains("unknown escape character") && value.StartsWith('"') && value.EndsWith('"') && value.Contains('\\'))
                return $"the value after `{key}:` is in double quotes, where a backslash starts an escape — write it without the quotes: " +
                    $"`{key}: {value[1..^1].Replace("\\\\", "\\")}`";
            if (value.StartsWith('"') && value.Count(c => c == '"') > 2 && !value.Contains("\\\""))
                return $"the value after `{key}:` is in double quotes and holds double quotes of its own, which end it early — write it in single " +
                    $"quotes, {Single(value[1..^(value.EndsWith('"') ? 1 : 0)])}, an apostrophe in it written twice";
            // A part written on one line in braces, or a list in brackets, holds `: ` that YAML reads as it should.
            if (reason.Contains("invalid mapping") || reason.Contains("mapping values are not allowed"))
                if (!value.StartsWith('\'') && !value.StartsWith('"') && !value.StartsWith('{') && !value.StartsWith('[') && value.Contains(": "))
                    return $"the value after `{key}:` holds `: `, which YAML reads as a field inside it — write the value in single quotes, " +
                        $"{Single(value)}, an apostrophe in it written twice, or write `{key}: >-` and the words on the lines below it, " +
                        "two spaces deeper";
        }
        // `Damage:7`: no space after the colon, and YAML reads one word, no field. A FormID's colon is its own.
        if (Regex.Match(line, @"^ *(?:- +)?(?<key>[A-Za-z][A-Za-z0-9_]*):(?<value>[^\s:][^:\s]*)[ \t]*$") is { Success: true } joined
            && !Regex.IsMatch(joined.Groups["value"].Value, @"\.es[mpl]$", RegexOptions.IgnoreCase))
            return $"`{line.Trim()}` has no space after its colon, so YAML reads one word there and no field — write " +
                $"`{joined.Groups["key"].Value}: {joined.Groups["value"].Value}`";
        return null;
    }

    /// <summary>Line numbers as words: "line 4", "lines 29, 30 and 31", the first eight of more.</summary>
    private static string LineList(List<int> numbers) => numbers.Count == 1 ? $"line {numbers[0]}"
        : $"lines {string.Join(", ", numbers.Take(Math.Min(numbers.Count, 8) - 1))} and {(numbers.Count > 8 ? $"{numbers[7]}, …" : numbers[^1].ToString())}";

    /// <summary>A field with a value of its own on its line, plain — no text block, quote or flow below it.</summary>
    private static readonly Regex ValueLine = new(@"^[ \t]*(?:- +)?[A-Za-z0-9_]+:[ \t]+[^\s'""|>\[{&*!#]", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>A field of the record at the margin, `Key: value` or `Key:`, as Spriggit writes one.</summary>
    private static readonly Regex MarginField = new(@"^(?<key>[A-Z][A-Za-z0-9]*):(?: |$)", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>A file's path: folders apart by a slash, or a file's extension at the end.</summary>
    private static readonly Regex FilePath = new(@"[\\/]|\.(nif|dds|tga|wav|xwm|fuz|hkx|pex|psc|swf)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    /// <summary>
    /// A line written as a field, in a field's own spelling, with a value only a field has: a link, a
    /// number, or none — fields of its own below it. A text's "By:" or "Value: 200 gold" is no field.
    /// </summary>
    private static readonly Regex FieldLine = new(
        @"^(?<indent> +)(?<key>[A-Z][A-Za-z0-9]*):(?:[ \t]+(?<value>[0-9A-Fa-f]{6}:\S+\.es[mpl]|-?\d+(?:\.\d+)?)(?:[ \t]+#.*)?)?[ \t]*$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    /// <summary>
    /// A field of the record indented into a block text above it, where it is a line of the text and the
    /// record silently has no such field. Told to indent a text's lines, the model indented the fields below it
    /// too, and a note built with its sound and keywords as words of its text (eval probe 18). Only a field
    /// the record has nowhere else counts: a letter's own "Name: …" line is text.
    /// </summary>
    private static string? FieldInText(string text, Type type, YamlMappingNode root)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var fields = RecordFiles.FieldsOf(type);
        var written = root.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value).ToHashSet(StringComparer.Ordinal);
        static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;
        for (var i = 0; i < lines.Length; i++)
        {
            if (BlockTextHeader.Match(lines[i]) is not { Success: true } header) continue;
            var column = header.Groups["key"].Index;
            for (var j = i + 1; j < lines.Length && (lines[j].Trim().Length == 0 || IndentOf(lines[j]) > column); j++)
            {
                if (FieldLine.Match(lines[j]) is not { Success: true } field) continue;
                var key = field.Groups["key"].Value;
                if (!fields.Contains(key) || written.Contains(key)) continue;
                // `Keywords:` with nothing after it is a field when its own lines follow: a list, or deeper ones.
                if (!field.Groups["value"].Success)
                {
                    var next = lines.Skip(j + 1).FirstOrDefault(line => line.Trim().Length > 0);
                    var ownLines = next is not null && (next.TrimStart().StartsWith("- ") || IndentOf(next) > field.Groups["indent"].Length);
                    if (!ownLines) continue;
                }
                return $"the text under `{lines[i].Trim()}` (line {i + 1}) runs on into `{lines[j].Trim()}` (line {j + 1}) — the " +
                    $"{Kind(type)}'s field {key}, indented into the text, so the record would have no {key} and the text would " +
                    $"end with it. The text ends before line {j + 1}: write that line and the field lines after it at the margin, " +
                    $"as readRecord shows them; the text's own lines, above line {j + 1}, keep their spaces";
            }
        }
        return null;
    }

    /// <summary>
    /// The block text the broken line (1-based) fell out of: going up from it through the text's lines —
    /// each indented deeper than the field that holds the text — to that field's line. Undefined when the
    /// line is indented as deep as the text, or follows no block text.
    /// </summary>
    private static (string Header, int Line, int Indent)? TextLeft(string[] lines, int at)
    {
        if (at < 2 || at > lines.Length) return null;
        static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;
        var broken = lines[at - 1];
        if (broken.Trim().Length == 0) return null;
        int? textIndent = null;
        var shallowest = int.MaxValue;
        for (var i = at - 2; i >= 0; i--)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (BlockTextHeader.Match(lines[i]) is { Success: true } header)
            {
                var fieldColumn = header.Groups["key"].Index;
                // The text's lines are indented as deep as its first; with none yet, two deeper than its field.
                var indent = textIndent ?? fieldColumn + 2;
                return shallowest > fieldColumn && IndentOf(broken) < indent ? (lines[i].Trim(), i + 1, indent) : null;
            }
            // A line at the margin can be no text's: the field holding one is at the margin or deeper.
            if (IndentOf(lines[i]) == 0) return null;
            textIndent = IndentOf(lines[i]);
            shallowest = Math.Min(shallowest, textIndent.Value);
        }
        return null;
    }

    /// <summary>Whether a mapping's part holds a field by that name: an NPC's Configuration with LevelMult in its Level.</summary>
    private static bool Holds(YamlMappingNode mapping, string part, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(part), out var node) && node is YamlMappingNode inner
        && inner.Children.ContainsKey(new YamlScalarNode(key));

    /// <summary>
    /// Follows a mapping through the record class's properties, calling <paramref name="link"/> on each
    /// link value, and <paramref name="problem"/> on what the reader would refuse or skip below the top
    /// level, which RecordFiles checks: a field the type does not have, a field of several kinds that
    /// does not name its kind, a text written as one value, a list where fields belong and the reverse,
    /// a value a field of names, numbers or truths does not take. <paramref name="parent"/> is the type of the
    /// mapping this one is a field of, where a field written a level too deep belongs; <paramref name="record"/>
    /// the record's, where one written in the wrong part does.
    /// </summary>
    /// <param name="deleting">The editRecord call that deletes a field of the file, by its path, and builds: told "BasicStats has no
    /// field VendorValue (delete it)", a model turned it into a second Value (eval round 101's sword+detailed #4); told an NPC's
    /// level is three lines, one whose Level held its LevelMult left the LevelMult beside it, two builds (round 103's npc+detailed #3).</param>
    /// <summary>The record file of another project, by its path from the projects folder, whose EditorID is this one; or null.</summary>
    private static string? InOtherProject(string pluginDir, string editorId)
    {
        var project = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDir)));
        var projects = project is null ? null : Path.GetDirectoryName(project);
        if (projects is null || !Directory.Exists(projects)) return null;
        foreach (var other in Directory.EnumerateDirectories(projects).Where(d => !string.Equals(d, project, StringComparison.OrdinalIgnoreCase)))
        {
            var plugin = Path.Combine(other, "plugin");
            if (!Directory.Exists(plugin)) continue;
            foreach (var (file, inPlugin, _) in RecordFiles.FlatRecords(plugin))
                if (string.Equals(RecordFiles.Identity(File.ReadAllText(file)).EditorId, editorId, StringComparison.OrdinalIgnoreCase))
                    return $"{Path.GetFileName(other)}/plugin/{inPlugin.Replace('\\', '/')}";
        }
        return null;
    }

    /// <summary>A field's name for what a thing costs in gold: VendorValue, Price, GoldValue, Value_Custom, ValueNumeric.</summary>
    private static readonly Regex PriceName = new(@"^(?:Vendor|Sell|Buy|Base|Gold|Item)?_?(?:Value|Price|Gold|Worth)(?:_?(?:Numeric|Custom|Amount|Gold))?$",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// A number under a price's name where the record has none — `VendorValue: 150` under BasicStats, `ValueNumeric: 150` in a
    /// Name — offered as the price, with the one call that sets it there and deletes the line; null otherwise. Told "delete it",
    /// a model deleted the 150 the user asked for, or wrote it as a second Value, and ran out of turns (eval round 101's
    /// sword+detailed #4, round 93's #3).
    /// </summary>
    private static string? Priced(Type record, string key, YamlNode value, string field, Func<string, string, string>? deleting)
    {
        if (deleting is null || !PriceName.IsMatch(key) || value is not YamlScalarNode { Value: { } said }
            || !double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return null;
        if (WorthPath(record) is not { } worth || worth == field) return null;
        return $"no such field: if {said} is the price, {RecordFiles.Article(record.Name)}'s price is {worth} — one call sets it there, deletes this " +
            $"line and builds again: {deleting(field, $"{{\"path\": \"{worth}\", \"value\": {said}}}")}; if it is not, delete it: {deleting(field, "")}";
    }

    /// <summary>Where a record keeps what it is worth in gold: a weapon's BasicStats.Value, an armor's or a book's Value; null for none.</summary>
    private static string? WorthPath(Type record) =>
        record.Name == "Weapon" ? "BasicStats.Value"
            : record.GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p => p.Name == "Value") is { } own && own.PropertyType.IsPrimitive ? "Value" : null;

    private static void Walk(YamlMappingNode mapping, Type type, string path, Action<YamlScalarNode, Type, string> link,
        Action<string> problem, Type? parent = null, Type? record = null, Func<string, string, string>? deleting = null)
    {
        record ??= type;
        // A field of several kinds names its kind, as a group of several does.
        var named = mapping.Children.TryGetValue(new YamlScalarNode("MutagenObjectType"), out var kindNode)
            && kindNode is YamlScalarNode { Value: { Length: > 0 } kindName } ? kindName : null;
        if (named is not null && RecordFiles.KindOf(type, named) is { } kind) type = kind;
        else if (path.Length > 0 && type.IsAbstract && KindsOf(type) is { Count: > 0 } kinds)
        {
            // Left unnamed, the reader fails with "Object reference not set to an instance of an object" (eval round 11). Where
            // its line goes, said: told "give it a first line `MutagenObjectType:`", the model wrote it at the margin above
            // `Archetype:`, twice (eval round 82's sword+detailed #5).
            var under = Regex.Replace(path.Split('.')[^1], @"\[\d+\]$", "");
            // A name's lines below `Level:`, where the NPC has a Name too: told the level's kinds alone, a model set
            // Configuration.Level.TargetLanguage (eval round 108's npc #1; with no Name, the build writes them as its Name).
            var nameKeys = type.Name == "ANpcLevel" && mapping.Children.Count > 0
                && mapping.Children.Keys.All(k => k is YamlScalarNode { Value: "TargetLanguage" or "Value" })
                ? mapping.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!).ToList() : null;
            problem($"{path} is a field of several kinds: its first line, below `{under}:` and two spaces deeper, is `MutagenObjectType:` " +
                $"naming one — {string.Join(", ", kinds)}" +
                (named is null ? "" : $"; {named} is none of them") +
                (nameKeys is null ? "" : $"; {string.Join(" and ", nameKeys)} below `{under}:` {(nameKeys.Count == 1 ? "is" : "are")} a name's — " +
                    "an NPC's name is its `Name:` at the margin, and its level another field: write the name there, and a level in its place") +
                (type.Name == "ANpcLevel" ? $"; {RecordFiles.NpcLevel}" : ""));
            return;
        }
        // The fields of the level above written here, named in one problem with what this part holds: told
        // "move it up" eight times, field by field, a model moved them all and back, build after build (round 13).
        var movedUp = new List<string>();
        foreach (var (keyNode, value) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key } || key == "MutagenObjectType") continue;
            // The most derived one, where a class redeclares a property of its base; an interface's own and those it inherits —
            // an armor's WorldModel is one, its Male and Female its getter's, and the lines below them went unread: `File:`
            // written straight under `Male:` built an armor with no male model (refusal probes of 2026-10-31, A02).
            var property = (type.IsInterface ? type.GetProperties().Concat(type.GetInterfaces().SelectMany(i => i.GetProperties()))
                    : type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Where(p => p.Name == key).OrderBy(p => p.DeclaringType == type ? 0 : 1).FirstOrDefault();
            var field = path.Length == 0 ? key : $"{path}.{key}";
            if (property is null)
            {
                // The top level is RecordFiles' to name; below it, the reader skips the field without a word.
                // A field of the level above, written below one of its fields: Configuration.Level.CalcMinLevel,
                // which a model moved to the top level next, a build later (eval probe 8 after round 11).
                if (path.Length > 0 && !type.IsInterface)
                {
                    // A field of another part goes there: a weapon's Speed, written under BasicStats, is Data's.
                    // Only one the record has nowhere is deleted; "has no field" alone reads as one missing (eval probe 9).
                    // An NPC's level whole, before "move it up": moved one field at a time, it went back and forth (round 12).
                    if (RecordFiles.LevelHint(type, key) is null && parent?.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.Name == key) == true)
                    {
                        movedUp.Add(key);
                        continue;
                    }
                    // A magic effect's Data, below one of its parts: found by name, the Data was a condition's, and the model
                    // moved Storm Bolt's magnitude into its Conditions (eval round 39's spell+detailed #4), as at the
                    // margin in round 24.
                    var deleted = deleting is null ? "" : $"; one call deletes it and builds again: {deleting(field, "")}";
                    // Another name of a field this part has, with its value: renamed in one call (RecordFiles.Synonyms) — told to
                    // delete a level's `Mult: 1`, a model held its NPC at level 5 (eval round 114's npc #4).
                    if (RecordFiles.SynonymFor(type, key) is { } meant && type.GetProperty(meant, BindingFlags.Public | BindingFlags.Instance) is not null
                        && value is YamlScalarNode { Value: { Length: > 0 } said })
                    {
                        var written = double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? said : ToolCalls.Quoted(said);
                        problem($"{path} has no field {key} (write it as `{meant}: {said}`: {RecordFiles.HintFor(type, key).Trim(' ', '(', ')')}" +
                            (deleting is null ? ")" : $"; one call does it and builds again: {deleting(field, $"{{\"path\": \"{path}.{meant}\", \"value\": {written}}}")})"));
                        continue;
                    }
                    problem($"{path} has no field {key}" + (RecordFiles.LevelHint(type, key) is { } level
                            ? (deleting is not null && Holds(mapping, "Level", key) ? $" (its Level holds {key} already: delete this one{deleted})" : $" ({level})")
                        : record.Name == "MagicEffect" && key == "Data" ? $" ({RecordFiles.EffectStrength})"
                        : RecordFiles.HintFor(type, key) is { Length: > 0 } hint ? hint
                        : record != type && RecordFiles.WhereIn(record, key) is { } at ? $" (move it: {RecordFiles.Article(record.Name)}'s {key} is {at})"
                        : Priced(record, key, value, field, deleting) is { } priced ? $" ({priced})"
                        : $" (delete it{deleted})"));
                }
                continue;
            }
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (LinkTarget(propertyType) is { } takes)
            {
                if (value is YamlScalarNode scalar) link(scalar, takes, field);
            }
            else if (typeof(ITranslatedStringGetter).IsAssignableFrom(propertyType))
                Text(value, field, record, problem, deleting);
            else if (Gendered(propertyType) is { } sexed)
            {
                // A part for each sex, `Male:` and `Female:`, each walked as the part it holds: an armor's WorldModel is one, an
                // enumerable the walk below passed over — `File:` written straight under `Male:` built an armor with no male
                // model, and a field no model has built with no word (refusal probes of 2026-10-31, A02, F12).
                // A text for each sex — a faction rank's Title — is checked as a text: walked as a part, the game's own
                // `Value:` was refused as no field of it, on ThievesGuildFaction copied unchanged too, and `Title: Initiate`
                // built a rank with no title (eval round 139's faction runs).
                var text = typeof(ITranslatedStringGetter).IsAssignableFrom(sexed);
                if (value is not YamlMappingNode sexes)
                {
                    if (text && value is YamlScalarNode { Value.Length: > 0 } said)
                        problem($"{field} is written as one value — it holds `Male:` and `Female:`, each a text with two lines indented " +
                            $"below it, `TargetLanguage: English` and `Value: {said.Value}`");
                    continue;
                }
                foreach (var (sexNode, part) in sexes.Children)
                {
                    if (sexNode is not YamlScalarNode { Value: { } sex }) continue;
                    if (sex is not ("Male" or "Female"))
                        problem($"{field} has no field {sex} — it holds `Male:` and `Female:`, each with its lines below it");
                    else if (text)
                        Text(part, $"{field}.{sex}", record, problem, deleting);
                    else if (part is YamlMappingNode inner)
                        Walk(inner, sexed, $"{field}.{sex}", link, problem, null, record, deleting);
                }
            }
            else if (Pairs(propertyType) is { } pairs)
            {
                // A dictionary written as `- Key:` and `Value:` items, an NPC's skills: `Key: OneHand` was refused in the reader's
                // words alone, "Requested value 'OneHand' was not found" (refusal probes of 2026-10-31, P19).
                if (value is not YamlSequenceNode entries) continue;
                var i = 0;
                foreach (var entry in entries.Children)
                {
                    var at = $"{field}[{i++}]";
                    if (entry is not YamlMappingNode written) continue;
                    foreach (var (part, partType) in new[] { ("Key", pairs.Key), ("Value", pairs.Value) })
                        if ((partType.IsEnum || partType.IsPrimitive) && written.Children.TryGetValue(new YamlScalarNode(part), out var node)
                            && node is YamlScalarNode { Value: { Length: > 0 } said } && Unfit(partType, said) is { } unfit)
                            problem($"{at}.{part} is {said} — {unfit}");
                    // A Value of fields of its own, a package's data, walked as the part it is: `Link: WhiterunBanneredMare` in a
                    // location's target was left for the reader, which refused it as a malformed FormKey (eval round 139's package #2).
                    if (!pairs.Value.IsEnum && !pairs.Value.IsPrimitive && pairs.Value != typeof(string)
                        && written.Children.TryGetValue(new YamlScalarNode("Value"), out var held) && held is YamlMappingNode fields)
                        Walk(fields, pairs.Value, $"{at}.Value", link, problem, null, record, deleting);
                    // A line beside Key and Value, which the reader refused as indented wrong: a class's TrainerLevel, written
                    // in its One-Handed weight (eval round 139's class+detailed #3).
                    foreach (var other in written.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).Where(k => k is not ("Key" or "Value" or null)))
                        problem($"{at}.{other}: an item of {field} holds `Key` and `Value` alone — " +
                            (record.Name == "Class" ? $"{RecordFiles.ClassTraining}; delete `{other}` here"
                                : RecordFiles.WhereIn(record, other!) is { } where ? $"{RecordFiles.Article(record.Name)}'s {other} is {where}"
                                : $"delete `{other}`"));
                }
            }
            else if (typeof(System.Collections.IList).IsAssignableFrom(propertyType) && ElementType(propertyType) is { } element)
            {
                if (value is YamlMappingNode)
                {
                    problem($"{field} takes a list: each item on a line of its own below it, starting `- `");
                    continue;
                }
                if (value is not YamlSequenceNode items) continue;
                var i = 0;
                foreach (var item in items.Children)
                {
                    var at = $"{field}[{i++}]";
                    if (LinkTarget(element) is { } itemTakes)
                    {
                        if (item is YamlScalarNode scalarItem) link(scalarItem, itemTakes, at);
                    }
                    else if (item is YamlMappingNode inner)
                    {
                        Walk(inner, element, at, link, problem, record: record, deleting: deleting);
                    }
                }
            }
            else if (propertyType.Name == "Percent")
            {
                // A fraction from 0 to 1, as readRecord writes LItemGems75's `ChanceNone: 0.25`; written as the Creation Kit shows
                // it, 50 or 50%, the reader answered "Could not parse string into Percent" or "Element out of range: 50", naming
                // neither field nor line (refusal probes of 2026-10-31, L10, L11, H03).
                if (value is YamlScalarNode { Value: { Length: > 0 } percent }
                    && !(double.TryParse(percent, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && fraction is >= 0 and <= 1))
                    problem($"{field} is {percent} — it takes a fraction from 0 to 1" + (key == "ChanceNone" ? ", the chance the list gives nothing" : "") +
                        (double.TryParse(percent.TrimEnd('%', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var whole) && whole is > 1 and <= 100
                            ? $": {whole.ToString(CultureInfo.InvariantCulture)}% is `{key}: {(whole / 100).ToString(CultureInfo.InvariantCulture)}`"
                            : $": write it as readRecord shows one, `{key}: 0.25` for a quarter"));
            }
            else if (propertyType.IsEnum || propertyType.IsPrimitive)
            {
                // The reader's own words name neither field nor line: "The input string '15.0' was not in a
                // correct format", "Requested value 'Passive' was not found" (eval round 11's probes).
                if (value is YamlScalarNode { Value: { Length: > 0 } one } && Unfit(propertyType, one, key) is { } expected)
                    problem($"{field} is {one} — {expected}");
                // Flags written as one value, which the reader refused in its own words, "Could not convert to
                // Mutagen.Bethesda.Skyrim.CriticalData+Flag: OnDeath" (refusal probes of 2026-10-31, W30, A16, P11).
                else if (value is YamlScalarNode { Value: { Length: > 0 } flagged } && propertyType.IsDefined(typeof(FlagsAttribute), false)
                    && !long.TryParse(flagged, out _))
                    problem($"{field} is written as one value, {flagged} — it takes a list: `{key}:` and below it each flag on a line of its own, " +
                        $"as deep as `{key}:` and starting `- `: " + string.Join(" ", flagged.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Select(f => $"`- {f}`")));
                else if (value is YamlSequenceNode many && propertyType.IsDefined(typeof(FlagsAttribute), false))
                {
                    // A magic effect's flags in a spell's, named where they go in one problem: the model copied ShockDamageFFAimed's
                    // Hostile, Detrimental and NoArea into its spell in 3 runs of eval rounds 35–43, all failed; told only the
                    // flags a spell takes, round 43's spell+detailed #5 took them out of its magic effect too.
                    var effects = new List<string>();
                    foreach (var item in many.Children.OfType<YamlScalarNode>())
                        if (item.Value is { Length: > 0 } flag && Unfit(propertyType, flag, key) is { } flagExpected)
                        {
                            if (path.Length == 0 && record != typeof(MagicEffect) && record.GetProperties().Any(p => p.Name == "Effects")
                                && Enum.GetNames<MagicEffect.Flag>().Contains(flag, StringComparer.Ordinal)) effects.Add(flag);
                            else problem($"{field} has {flag} — {flagExpected}");
                        }
                    var them = effects.Count > 1 ? "them" : "it";
                    if (effects.Count > 0)
                        problem($"{field} has {string.Join(", ", effects)} — {(effects.Count > 1 ? "magic effects' flags" : "a magic effect's flag")}, " +
                            $"which {RecordFiles.Article(record.Name)} takes from the MagicEffect each item of its Effects names: delete {them} here, " +
                            $"and keep {them} in that MagicEffect's Flags");
                }
            }
            else if (propertyType != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType))
            {
                if (value is YamlMappingNode nested) Walk(nested, propertyType, field, link, problem, type, record, deleting);
                else if (value is YamlSequenceNode) problem($"{field} takes fields of its own, indented below it — not a list");
                // A part of the record, not a file path or a struct written as one value, as Model's File and a bound's corner are.
                else if (value is YamlScalarNode { Value: { Length: > 0 } one } && one is not ("~" or "null" or "Null" or "NULL")
                    && typeof(Loqui.ILoquiObject).IsAssignableFrom(propertyType))
                    problem(PartAsValue(field, propertyType, one, record));
                // Left empty, a part the record cannot be without reads as none, and the reader failed with "Object reference not
                // set to an instance of an object": the model's Hilda, her `Level:` emptied once the build had moved CalcMinLevel and
                // CalcMaxLevel out of it up to Configuration (eval round 45's npc+detailed #3, two builds; round 35's #1).
                else if (value is YamlScalarNode { Value: null or "" or "~" or "null" or "Null" or "NULL" }
                    && typeof(Loqui.ILoquiObject).IsAssignableFrom(propertyType)
                    && new NullabilityInfoContext().Create(property).WriteState == NullabilityState.NotNull)
                    problem(PartAsValue(field, propertyType, null, record));
            }
        }
        if (movedUp.Count > 0)
        {
            var above = path.LastIndexOf('.') is var dot and >= 0 ? path[..dot] : "";
            var holds = movedUp.Count > 1
                ? $"; {path} holds only {string.Join(", ", type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name))}"
                : "";
            // Told "to the top level", the model wrote the lines back with their two spaces in front, under Data again,
            // until it gave up (eval round 19): the lines themselves, and how deep they go.
            var lines = string.Join(", ", movedUp.Select(key => mapping.Children[new YamlScalarNode(key)] is YamlScalarNode { Value: { Length: > 0 } one }
                ? $"`{key}: {one}`" : $"`{key}:` with its lines"));
            // Told to move them up, the model deleted both lines and built a sword its enchantment never reached (eval
            // round 22): that they stay, said in so many words, and the move as the edit it is.
            var single = movedUp.Count == 1;
            var keys = mapping.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value).ToList();
            var last = keys.Skip(keys.Count - movedUp.Count).ToHashSet();
            var there = above.Length == 0 ? "at the margin, with no spaces in front" : $"under {above}, as deep as its other fields";
            problem($"{path} has no field {string.Join(", ", movedUp)} (move {(single ? "it" : "them")} up, keeping " +
                $"{(single ? "the line" : movedUp.Count == 2 ? "both" : "all of them")}: " +
                (last.SetEquals(movedUp)
                    ? $"{(single ? "it is" : "they are")} {path}'s last, so take the two spaces out in front of {(single ? "it" : "them")} " +
                        $"and {(single ? "it is" : "they are")} {(above.Length == 0 ? "at the margin" : $"under {above}")}"
                    : $"delete {(single ? "it" : "them")} from {path} and write {(single ? "it" : "them")} again {there}, below {path}'s last line") +
                $" — {lines}{holds})");
        }
    }

    /// <summary>
    /// A link written as a record's in-game name, as the words that name the record: the project's own, the load order's of
    /// that name, or the editor IDs closest to the name with its spaces taken out — `Right Hand`'s RightHand. Told only that a
    /// link is a FormID or an editor ID, a probe's `ObjectEffect: Probe Fire` had the name of the project's own enchantment
    /// (refusal probes of 2026-10-31, N02, W18, A11). Null where nothing is near.
    /// </summary>
    private static string? ByName(string raw, Type takes, string wanted, string field, string pluginDir, RecordIndex index, Dictionary<string, Target> own)
    {
        var last = field.Split('.')[^1];
        var line = last.EndsWith(']') ? "- {0}" : $"{last}: {{0}}";
        var mine = RecordFiles.FlatRecords(pluginDir)
            .Where(f => takes.IsAssignableFrom(f.Type))
            .Select(f => (Text: File.ReadAllText(f.File), f.Type))
            .Where(f => string.Equals(RecordFiles.NameOf(f.Text), raw.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(f => RecordFiles.Identity(f.Text).EditorId).OfType<string>().ToList();
        if (mine.Count > 0)
            return $"that is the in-game name of {mine[0]}, new in this project — a link names a record by its editor ID: write `{string.Format(line, mine[0])}`";
        // The one whose editor ID is the name itself first — SteelSword before a quest's copy named "Steel Sword" — then the closest.
        var compact = Regex.Replace(raw, @"[\s'_-]+", "");
        var named = index.ByFormKey.Values.Where(e => e.EditorId is not null && takes.IsAssignableFrom(e.GetterType)
                && string.Equals(e.Name, raw.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => string.Equals(e.EditorId, compact, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => Closeness(compact, e.EditorId!)).ThenBy(e => e.FormKey.ID).ThenBy(e => e.EditorId!.Length).ToList();
        if (named.Count > 0)
            return $"the in-game name of {named[0].EditorId} ({Session.FormKeyText(named[0].FormKey)})" +
                (named.Count > 1 ? $" and of {named.Count - 1} other {wanted} record{(named.Count == 2 ? "" : "s")}" +
                    $" ({string.Join(", ", named.Skip(1).Take(4).Select(e => e.EditorId))}{(named.Count > 5 ? ", …" : "")})" : "") +
                $" — a link names a record by its editor ID: write the one meant, `{string.Format(line, named[0].EditorId)}`";
        var near = Near(compact, takes, own, index);
        return near.Count > 0 ? $"a link is a FormID like 012EB7:Skyrim.esm, or the editor ID of {RecordFiles.Article(wanted)} — the {wanted} records " +
            $"closest to it: {string.Join(", ", near)}" : null;
    }

    /// <summary>A dictionary's key and value types, or null for any other type.</summary>
    private static (Type Key, Type Value)? Pairs(Type type) => new[] { type }.Concat(type.GetInterfaces())
        .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() is var d && (d == typeof(IReadOnlyDictionary<,>) || d == typeof(IDictionary<,>)))
        .Select(i => (i.GetGenericArguments()[0], i.GetGenericArguments()[1])).Cast<(Type, Type)?>().FirstOrDefault();

    /// <summary>
    /// A text's lines, <c>TargetLanguage</c> and <c>Value</c>: <c>Name: Hilda</c>, which the reader refuses for want of its
    /// language, said with the two lines to write; any other line in it, which the reader skips without a word (eval round 53's
    /// sword #2), named with where it goes. One of the part's own fields is moved out of it before this (MisplacedFields).
    /// </summary>
    private static void Text(YamlNode value, string field, Type record, Action<string> problem, Func<string, string, string>? deleting)
    {
        if (value is YamlScalarNode { Value.Length: > 0 } or YamlSequenceNode)
            problem($"{field} is written as {(value is YamlSequenceNode ? "a list" : "one value")} — a text takes two lines indented " +
                $"below `{field}:`, `TargetLanguage: English` and `Value: {(value as YamlScalarNode)?.Value ?? "…"}`");
        else if (value is YamlMappingNode words)
            foreach (var other in words.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).Where(k => k is not null && !TextFields.Contains(k)))
                problem($"{field}.{other}: a text holds `TargetLanguage` and `Value` alone — " +
                    (RecordFiles.WhereIn(record, other!) is { } at ? $"{RecordFiles.Article(record.Name)}'s {other} is {at}"
                        : Priced(record, other!, words.Children[new YamlScalarNode(other!)], $"{field}.{other}", deleting) is { } priced ? priced
                        : $"delete `{other}`" + (deleting is null ? "" : $"; one call deletes it and builds again: {deleting($"{field}.{other}", "")}")));
    }

    /// <summary>What a part of one for each sex holds — an armor's WorldModel an ArmorModel — or null for any other type.</summary>
    private static Type? Gendered(Type type) => new[] { type }.Concat(type.GetInterfaces())
        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition().Name == "IGenderedItemGetter`1")?.GetGenericArguments()[0];

    /// <summary>
    /// The value of an NPC's AIData the official plugins' named NPCs have most often, by its type: told only the
    /// names, the model took the second, Aggressive, for an NPC the task gave no aggression (eval round 26). Counted over
    /// the 3,186 named NPCs the 80 official plugins define (2026-10-21).
    /// </summary>
    private static readonly Dictionary<Type, (string Value, string Of)> Usual = new()
    {
        [typeof(Aggression)] = ("Unaggressive", "1,838"),
        [typeof(Confidence)] = ("Foolhardy", "1,383"),
        [typeof(Responsibility)] = ("NoCrime", "2,371"),
        [typeof(Mood)] = ("Neutral", "2,901"),
        [typeof(Assistance)] = ("HelpsNobody", "1,353")
    };
    private const string NamedNpcs = "3,186";

    /// <summary>"the official plugins' named NPCs have Unaggressive most often: 1,838 of 3,186", for a type of AIData's; else null.</summary>
    internal static string? MostOften(Type type) =>
        Usual.TryGetValue(type, out var usual) ? $"the official plugins' named NPCs have {usual.Value} most often: {usual.Of} of {NamedNpcs}" : null;

    /// <summary>
    /// What a value of a field of names, a number or a truth is not, as words after it — "it takes a whole
    /// number, such as 15", "it takes one of Unaggressive, Aggressive…" — or null for one the reader takes.
    /// </summary>
    internal static string? Unfit(Type type, string raw, string? key = null)
    {
        var invariant = CultureInfo.InvariantCulture;
        if (type.IsEnum)
        {
            var names = Enum.GetNames(type);
            var written = type.IsDefined(typeof(FlagsAttribute), false)
                ? raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [raw.Trim()];
            foreach (var name in written)
            {
                // A value without a name is written as a number — 0x4 for an unnamed flag.
                if (names.Contains(name, StringComparer.Ordinal) || long.TryParse(name, NumberStyles.Integer, invariant, out _)
                    || name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(name[2..], NumberStyles.HexNumber, invariant, out _)) continue;
                // The quest's own flag written among a stage's: told only what a stage's take, a model replaced the quest's
                // StartGameEnabled with AllowRepeatedStages, and its quest never started (eval round 128's quest #1).
                if (type == typeof(QuestStage.Flag) && Enum.GetNames<Quest.Flag>().Contains(name, StringComparer.OrdinalIgnoreCase))
                    return $"{name} is the quest's own flag, among the Flags at the margin of the quest's file — keep it there, and delete it " +
                        $"here; a stage's Flags take {string.Join(", ", names)}";
                return names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } spelled
                    ? $"it is spelled {spelled}"
                    : Listed(type, name, key) is var listed && listed.Length > 0
                        ? $"it takes one of {string.Join(", ", listed.Take(16))}{(listed.Length > 16 ? ", …" : "")}" +
                        (MostOften(type) is { } usual ? $" ({usual})" : "") : null;
            }
            return null;
        }
        if (type == typeof(bool)) return bool.TryParse(raw, out _) ? null : "it takes True or False";
        if (type == typeof(float) || type == typeof(double))
            return double.TryParse(raw, NumberStyles.Float, invariant, out _) ? null : "it takes a number, such as 1.5";
        if (type == typeof(char)) return null;
        // An actor of a scene named where its alias's number goes: told only "ActorID takes a whole number" three times, eval round
        // 144's scene #4 wrote Hulda's editor ID again each time.
        if (!decimal.TryParse(raw, NumberStyles.Float, invariant, out _) && key?[(key.LastIndexOf('.') + 1)..] is "ActorID" or "HeadtrackActorID" or "AliasID" or "Alias")
            return $"it takes the number of the quest's alias that holds the actor, not the actor's name: the `ID:` of that alias under the quest's " +
                $"Aliases — `- ID: 1` with `UniqueActor: {raw.Trim()}` below it, as the game's quests fill an alias with one NPC";
        if (!decimal.TryParse(raw, NumberStyles.Integer, invariant, out var whole))
            return decimal.TryParse(raw, NumberStyles.Float, invariant, out var number)
                ? $"it takes a whole number, such as {decimal.Round(number)}" : "it takes a whole number";
        var (min, max) = (Convert.ToDecimal(type.GetField("MinValue")!.GetValue(null), invariant),
            Convert.ToDecimal(type.GetField("MaxValue")!.GetValue(null), invariant));
        return whole < min || whole > max ? $"it takes a whole number from {min} to {max}" : null;
    }

    /// <summary>Short names written for an actor value, and the one each means.</summary>
    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HP"] = "Health", ["HitPoints"] = "Health", ["Life"] = "Health", ["MP"] = "Magicka", ["Mana"] = "Magicka",
        ["SP"] = "Stamina", ["Fatigue"] = "Stamina"
    };

    /// <summary>The values a field of actor values takes, by its name: a magic effect's school, its resistance.</summary>
    private static readonly Dictionary<string, string[]> FieldValues = new(StringComparer.Ordinal)
    {
        ["MagicSkill"] = ["Alteration", "Conjuration", "Destruction", "Illusion", "Restoration"],
        ["ResistValue"] = ["ResistFire", "ResistFrost", "ResistShock", "ResistMagic", "PoisonResist", "ResistDisease"]
    };

    /// <summary>
    /// An enum's names in the order to offer them for a value written: the field's own values first (a school for MagicSkill),
    /// then the name a short one means, then the closest, then the rest in their order. Offered the first 16 of the actor
    /// values, Aggression to Sneak, `MagicSkill: Fire`, `ResistValue: FireResist` and `ActorValue: HP` were shown neither
    /// Destruction, ResistFire nor Health (refusal probes of 2026-10-31, S12, S13, S24).
    /// </summary>
    private static string[] Listed(Type type, string raw, string? key)
    {
        var names = Enum.GetNames(type);
        // A field of its own values offers those alone: a school is what MagicSkill holds, though it takes any actor value.
        if (key is not null && FieldValues.TryGetValue(key, out var values) && values.Where(names.Contains).ToArray() is { Length: > 0 } own)
            return own;
        var meant = ShortNames.TryGetValue(raw.Trim(), out var name) && names.Contains(name) ? [name] : Array.Empty<string>();
        var closest = names.Select(n => (Name: n, Score: Closeness(raw.Trim(), n))).Where(c => c.Score < int.MaxValue)
            .OrderBy(c => c.Score).Select(c => c.Name);
        // A value sharing one long word of a name of more words: `ActorValue: CarryCapacityIncrease` was offered the first 16
        // values, CarryWeight not among them, and the model took HeavyArmor (eval round 128's effect #1).
        var words = WordsOf(raw.Trim());
        var sharing = names.Where(n => SharedWords(words, WordsOf(n)).Any(word => word.Length >= 5));
        return meant.Concat(closest).Concat(sharing).Concat(names).Distinct().ToArray();
    }

    /// <summary>
    /// The link fields a refusal never offers to delete: an NPC's race, which it cannot go without (RecordNeeds), and a weapon's
    /// or armor's enchantment, written for its own sake — the Iron Sword a Flame Blade follows has none, and its enchantment
    /// named by a typo is to be named right (refusal probe W20); and a placed reference's Base, the record it places, without
    /// which it is no reference.
    /// </summary>
    private static readonly HashSet<string> Kept = new(["Race", "ObjectEffect", "Base"], StringComparer.Ordinal);

    /// <summary>The kinds of record an NPC carries: what a leveled item list holds, and an NPC list never.</summary>
    private static readonly HashSet<string> ItemKinds = new(["Weapon", "Armor", "Ammunition", "MiscItem", "Ingestible", "Ingredient", "Book",
        "Scroll", "SoulGem", "Key", "Light", "LeveledItem"], StringComparer.Ordinal);

    /// <summary>
    /// The leveled item lists the NPCs of a leveled NPC list's text carry from, by editor ID in their order: each NPC's Items, or
    /// its template's where it takes its inventory from one, through nested NPC lists a few levels down — for LCharBanditMelee1H,
    /// EncBandit01TemplateMelee's LItemBanditWeapon1H among them.
    /// </summary>
    private static List<string> CarriedLists(string text, RecordIndex index, ILinkCache? lookup)
    {
        var found = new List<string>();
        if (lookup is null) return found;
        var seen = new HashSet<FormKey>();
        var queue = new Queue<(FormKey Key, int Depth)>();
        foreach (Match reference in Regex.Matches(text, @"\bReference:[ \t]*([^\s#]+)"))
        {
            var raw = reference.Groups[1].Value;
            if (FormKey.TryFactory(raw, out var formKey)) queue.Enqueue((formKey, 0));
            else if (index.ByEditorId.TryGetValue(raw, out var byId)) queue.Enqueue((byId, 0));
        }
        while (queue.Count > 0 && found.Count < 8)
        {
            var (key, depth) = queue.Dequeue();
            if (depth > 3 || !seen.Add(key)) continue;
            if (lookup.TryResolve<INpcGetter>(key, out var npc))
            {
                if (!npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Inventory))
                    queue.Enqueue((npc.Template.FormKey, depth + 1));
                else
                    foreach (var item in npc.Items ?? [])
                        if (lookup.TryResolve<ILeveledItemGetter>(item.Item.Item.FormKey, out var list) && list.EditorID is { } id && !found.Contains(id))
                            found.Add(id);
            }
            else if (lookup.TryResolve<ILeveledNpcGetter>(key, out var npcs))
                foreach (var entry in npcs.Entries ?? [])
                    if (entry.Data?.Reference.FormKeyNullable is { } inner) queue.Enqueue((inner, depth + 1));
        }
        return found;
    }

    /// <summary>
    /// What is wrong with a FormKey a link field holds, as words after it — "which is IronSword, a Weapon",
    /// "which no record of the load order has" — or null when it names a record of a type the field takes.
    /// </summary>
    private static string? Mismatch(FormKey formKey, Type takes, RecordIndex index)
    {
        if (index.ByFormKey.TryGetValue(formKey, out var entry))
            return takes.IsAssignableFrom(entry.GetterType) ? null
                : $"which is {(entry.EditorId is { } id ? $"{id}, " : "")}{RecordFiles.Article(Kind(entry.GetterType))}";
        // Skyrim.esm's numbers below 000800 are the engine's own: the player's reference, 000014, is in no
        // plugin, and every Creation Club plugin points at it. References and navmeshes the index leaves out.
        if (formKey.ModKey == Session.SkyrimMaster && formKey.ID < 0x800 || RecordIndex.SkippedTypes.Any(takes.IsAssignableFrom)) return null;
        return "which no record of the load order has";
    }

    /// <summary>A text's own lines, as Spriggit writes one: its language and its words — or, in a localized plugin, each language's.</summary>
    internal static readonly HashSet<string> TextFields = new(["TargetLanguage", "Value", "Values"], StringComparer.Ordinal);

    /// <summary>A comment after a value that names one record — `# NordRace`, as readRecord writes one — not words.</summary>
    private static readonly Regex CommentName = new(@"^[ \t]+#[ \t]*(?<id>[A-Za-z0-9_]+)[ \t]*\r?$", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>An editor ID as a model makes one up — `FireDamageEnchantment`, `1stPersonFlameBlade`: a capital inside the word, or a digit.</summary>
    private static readonly Regex MadeUpEditorId = new(@"^(?=.*(?:[a-z0-9][A-Z]|[0-9]))[A-Za-z0-9_]{6,}$", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>
    /// The record the comment after a link names, when it is another than the one the link's FormID names —
    /// with that one's editor ID, null for no record — or null when the comment names that one, or one of a type
    /// the field does not take: such a comment is a word, and says nothing of the link. A comment written as an
    /// editor ID that no record has is answered with <c>Meant</c> null: `ObjectEffect: 091AE7:Skyrim.esm  #
    /// FireDamageEnchantment` built Mehrunes' Razor's enchantment into the model's Flame Blade (eval round 49's sword #2) —
    /// 15 such lines in rounds 32–49, each a made-up name, three of them built so.
    /// </summary>
    private static (string? Linked, Target? Meant, string Named)? CommentedOther(string text, YamlScalarNode value, FormKey linked, Type takes,
        Dictionary<string, Target> own, RecordIndex index)
    {
        var end = (int)value.End.Index;
        var lineEnd = text.IndexOf('\n', end);
        if (CommentName.Match(text[end..(lineEnd < 0 ? text.Length : lineEnd)]) is not { Success: true } comment) return null;
        var named = comment.Groups["id"].Value;
        var linkedId = index.ByFormKey.TryGetValue(linked, out var entry) ? entry.EditorId
            : own.Values.FirstOrDefault(t => t.FormKey == linked)?.EditorId;
        if (named.Equals(linkedId, StringComparison.OrdinalIgnoreCase)) return null;
        var meant = own.TryGetValue(named, out var mine) ? mine
            : index.ByEditorId.TryGetValue(named, out var key) && index.ByFormKey.TryGetValue(key, out var found)
                ? new Target(key, found.EditorId ?? named, found.GetterType)
                : null;
        // A record the index leaves out may be the one it names; a FormID of none is answered by the check after this.
        if (meant is null)
            return linkedId is not null && MadeUpEditorId.IsMatch(named) && !RecordIndex.SkippedTypes.Any(takes.IsAssignableFrom)
                ? (linkedId, null, named) : null;
        if (meant.FormKey == linked || !takes.IsAssignableFrom(meant.Type)) return null;
        return (linkedId, meant, named);
    }

    /// <summary>
    /// What a part of the record written as one value takes: `Archetype: ShockDamage`, which the reader answered
    /// with "Expected 'Scalar', got 'MappingEnd'" and a line and column, twice to the model (eval round 27). A value one of
    /// the part's fields takes is given as that field's line. One written empty (<paramref name="one"/> null) is said so.
    /// </summary>
    private static string PartAsValue(string field, Type type, string? one, Type record)
    {
        var leaf = field[(field.LastIndexOf('.') + 1)..];
        if (one is null && type.Name == "ANpcLevel") return $"{field} is empty — {RecordFiles.NpcLevel}";
        // `Level: 1` under Configuration: told only of the kinds and readRecord, the model moved the lines the build had moved
        // already, five edits refused (eval round 50's npc #5). The level's lines, its number in them.
        if (type.Name == "ANpcLevel")
            return $"{field} is written as one value, {one} — " + (short.TryParse(one, out var level)
                ? RecordFiles.NpcLevel.Replace("NpcLevel, Level: 10}", $"NpcLevel, Level: {level}}}") : RecordFiles.NpcLevel);
        var shapes = type.IsAbstract
            ? type.Assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface && type.IsAssignableFrom(t)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList()
            : [type];
        var fit = shapes.SelectMany(shape => shape.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).IsEnum)
                .Select(p => (Shape: shape, Field: p.Name,
                    Value: Enum.GetNames(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).FirstOrDefault(n => n.Equals(one, StringComparison.OrdinalIgnoreCase)))))
            .FirstOrDefault(f => f.Value is not null);
        var said = (one is null ? $"{field} is empty" : $"{field} is written as one value, {one}") +
            $" — it takes fields of its own, on lines indented below `{leaf}:`";
        if (fit.Value is not null)
            return said + $": write `{leaf}:` with " + (type.IsAbstract ? $"`MutagenObjectType: {fit.Shape.Name}` and " : "") +
                $"`{fit.Field}: {fit.Value}` below it, two spaces in";
        var example = RecordFiles.ExampleName(record.Name) is { } id ? $"readRecord {{\"record\": \"{id}\"}}" : "readRecord";
        return said + (type.IsAbstract
            ? $", the first `MutagenObjectType:` naming its kind — {string.Join(", ", KindsOf(type))} — and the kind's fields below it"
            : $" — {string.Join(", ", type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => p.Name))}") +
            $", as {example} shows them in a vanilla {record.Name}";
    }

    /// <summary>The kinds a field of several kinds can be — NpcLevel, PcLevelMult for an NPC's level.</summary>
    private static List<string> KindsOf(Type type) => type.Assembly.GetTypes()
        .Where(t => !t.IsAbstract && !t.IsInterface && type.IsAssignableFrom(t))
        .Select(t => t.Name).Order(StringComparer.Ordinal).Take(8).ToList();

    /// <summary>
    /// A record file's links as written — an editor ID or a FormID — each with its field's path as editRecord takes it:
    /// `ObjectEffect`, `Effects[0].BaseEffect`, `Keywords[2]`. None for a file that does not read as YAML.
    /// </summary>
    internal static List<(string Path, string Value)> LinksIn(string text, Type type) =>
        LinksTo(text, type).Select(l => (l.Path, l.Value)).ToList();

    /// <summary>
    /// A record file's links as <see cref="LinksIn"/> answers them, each with the record type its field takes —
    /// IMagicEffectGetter for an Effects item's BaseEffect.
    /// </summary>
    internal static List<(string Path, string Value, Type Takes)> LinksTo(string text, Type type)
    {
        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(text));
        }
        catch (YamlException)
        {
            return [];
        }
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root) return [];
        var links = new List<(string, string, Type)>();
        Walk(root, type, "", (value, takes, field) => { if (value.Value?.Trim() is { Length: > 0 } raw) links.Add((field, raw, takes)); }, _ => { });
        return links;
    }

    /// <summary>The record type a link field takes — IMagicEffectGetter for an effect's BaseEffect — or null for a field that is no link.</summary>
    /// A condition's link is a link or an alias's index (IFormLinkOrIndex), written as a FormKey where it names a record: an
    /// editor ID there went unresolved, and the reader refused a recipe's `Perk: SteelSmithing` as a malformed FormKey (the
    /// eval's hand-made recipe and perk mods, 2026-11-04).
    internal static Type? LinkTarget(Type type) =>
        typeof(IFormLinkGetter).IsAssignableFrom(type)
            ? type.GetInterfaces().Append(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkGetter<>))?.GetGenericArguments()[0]
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkOrIndexGetter<>))?.GetGenericArguments()[0];

    /// <summary>The item type of a list field, or null.</summary>
    internal static Type? ElementType(Type type) =>
        type == typeof(string) ? null
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];

    /// <summary>A record type as people say it: IMagicEffectGetter → MagicEffect.</summary>
    internal static string Kind(Type type)
    {
        var name = type.Name;
        if (type.IsInterface && name.StartsWith('I')) name = name[1..];
        return name.EndsWith("Getter", StringComparison.Ordinal) ? name[..^"Getter".Length] : name;
    }

    /// <summary>
    /// A new record — no FormKey line yet — written with the editor ID of a record of the load order: every link by it
    /// names the new one, and the game has two records of one editor ID. The model wrote `EditorID: ShockDamageFFAimed` with a
    /// few lines to change Skyrim.esm's, and its spell then cast that empty effect, refused until the turns ran out
    /// (eval rounds 31 and 51, spell+detailed #3) — 4 runs of rounds 30–51 wrote one. The checks give such a record an editor
    /// ID of its own first (RecordFiles.RenameTakenEditorIds); this is for one they found no name for.
    /// </summary>
    private static string? TakenEditorId(string text, RecordIndex index, ModKey? ownPlugin)
    {
        if (RecordFiles.Identity(text) is not { FormKey: null, EditorId: { } editorId }
            || !index.ByEditorId.TryGetValue(editorId, out var key) || key.ModKey == ownPlugin
            || !index.ByFormKey.TryGetValue(key, out var entry))
            return null;
        var formId = Session.FormKeyText(key);
        return $"a new record, with the editor ID of {key.ModKey.FileName}'s {Kind(entry.GetterType)} {entry.EditorId} ({formId}) — every link " +
            $"by {editorId} would name this one instead. To change {key.ModKey.FileName}'s, copy it in with addPatch {{\"records\": " +
            $"[\"{formId}\"]}} and edit the copy, and write this file empty; for a new record, give it an EditorID of its own";
    }

    /// <summary>
    /// The project's records by editor ID, with the FormKey each has — none yet for a new one; and the
    /// files of records without an editor ID, which no link can name.
    /// </summary>
    private static (Dictionary<string, Target> Named, List<(string Relative, Type Type)> Unnamed) OwnRecords(string pluginDir)
    {
        var own = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        var unnamed = new List<(string, Type)>();
        // A dialogue topic's and its responses as well: a branch's StartingTopic names its topic by editor ID.
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir).Concat(RecordFiles.DialogRecords(pluginDir)))
        {
            var (formKey, editorId) = RecordFiles.Identity(File.ReadAllText(file));
            if (editorId is null) unnamed.Add((relative, type));
            else own.TryAdd(editorId, new Target(formKey, editorId, type));
        }
        // A cell's own file and the references placed in it, by an editor ID they have: a load door's TeleportDestination
        // naming its twin in a cell of the mod's own was refused as no record of that editor ID (eval round 139's door runs).
        foreach (var (file, _, type) in RecordFiles.CellRecords(pluginDir))
            if (type is not null && RecordFiles.Identity(File.ReadAllText(file)) is (var formKey, { } editorId))
                own.TryAdd(editorId, new Target(formKey, editorId, type));
        return (own, unnamed);
    }

    /// <summary>
    /// The load order's weapon most like a weapon file, and its keywords' editor IDs: a plain one — no template, no enchantment —
    /// of the file's kind, its AnimationType, then the one sharing the most links with the file — the sounds, impact sets and
    /// keywords a copy keeps — then load order, as ModelFiles finds a model. Null where no weapon of the load order has keywords.
    /// </summary>
    private static (string EditorId, List<string> Keywords)? LikeWeapon(YamlMappingNode root, Dictionary<string, Target> own,
        RecordIndex index, ILinkCache lookup, ModKey? ownPlugin)
    {
        var kind = root.Children.TryGetValue(new YamlScalarNode("Data"), out var data) && data is YamlMappingNode fields
            && fields.Children.TryGetValue(new YamlScalarNode("AnimationType"), out var animation) ? (animation as YamlScalarNode)?.Value?.Trim() : null;
        var links = Scalars(root).Select(s => s.Value?.Trim() ?? "")
            .Select(v => FormKey.TryFactory(v, out var key) ? key : index.ByEditorId.TryGetValue(v, out var byId) ? byId : (FormKey?)null)
            .OfType<FormKey>().ToHashSet();
        var weapons = new List<(int At, IWeaponGetter Weapon)>();
        foreach (var entry in index.ByFormKey.Values.Where(e => e.Signature == "WEAP" && !e.Unplayable && e.FormKey.ModKey != ownPlugin))
            if (lookup.TryResolve(entry.FormKey, entry.GetterType, out var record) && record is IWeaponGetter { Keywords.Count: > 0 } weapon)
                weapons.Add((weapons.Count, weapon));
        var plain = weapons.Where(w => w.Weapon.Template.IsNull && w.Weapon.ObjectEffect.IsNull).ToList();
        var best = (plain.Count > 0 ? plain : weapons)
            .OrderByDescending(w => kind is not null && string.Equals(w.Weapon.Data?.AnimationType.ToString(), kind, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.Weapon.EnumerateFormLinks().Select(l => l.FormKey).Distinct().Count(links.Contains))
            .ThenBy(w => w.At)
            .Select(w => w.Weapon).FirstOrDefault();
        if (best is null) return null;
        var name = best.EditorID ?? Session.FormKeyText(best.FormKey);
        return (name, best.Keywords!.Select(k => index.ByFormKey.TryGetValue(k.FormKey, out var e) && e.EditorId is { } id
            ? id : Session.FormKeyText(k.FormKey)).ToList());
    }

    /// <summary>The keywords a weapon file lists that a record has — of the project or the load order — as written.</summary>
    private static IEnumerable<string> KnownKeywords(YamlMappingNode root, Dictionary<string, Target> own, RecordIndex index) =>
        root.Children.TryGetValue(new YamlScalarNode("Keywords"), out var listed) && listed is YamlSequenceNode items
            ? items.Children.OfType<YamlScalarNode>().Select(s => s.Value?.Trim() ?? "").Where(v =>
                own.ContainsKey(v) || index.ByEditorId.ContainsKey(v) || FormKey.TryFactory(v, out var key) && index.ByFormKey.ContainsKey(key))
            : [];

    /// <summary>Every scalar of a YAML node, its mappings' keys aside.</summary>
    private static IEnumerable<YamlScalarNode> Scalars(YamlNode node) => node switch
    {
        YamlScalarNode scalar => [scalar],
        YamlMappingNode mapping => mapping.Children.Values.SelectMany(Scalars),
        YamlSequenceNode sequence => sequence.Children.SelectMany(Scalars),
        _ => []
    };

    /// <summary>Up to five records a link may have meant: of a type the field takes, with an editor ID close to the one written.</summary>
    private static List<string> Near(string editorId, Type takes, Dictionary<string, Target> own, RecordIndex index)
    {
        var candidates = own.Values.Where(t => takes.IsAssignableFrom(t.Type)).Select(t => (t.EditorId, t.FormKey, t.Type, Name: (string?)null))
            .Concat(index.ByFormKey.Values.Where(e => e.EditorId is not null && takes.IsAssignableFrom(e.GetterType))
                .Select(e => (EditorId: e.EditorId!, FormKey: (FormKey?)e.FormKey, Type: e.GetterType, e.Name)));
        // Its in-game name with the spaces out first: `AncientNordSword` is DraugrSword, "Ancient Nord Sword", which the closest editor
        // IDs left out (eval round 128's recipe #1). Each with its kind: offered among the close names for SteelArrows, a leveled list,
        // BaseArrowSteel75, was taken for ten arrows (eval round 128's world+detailed #3).
        static string Bare(string text) => Regex.Replace(text, "[^A-Za-z0-9]", "");
        var bare = Bare(editorId);
        return candidates
            .Select(c => (c.EditorId, c.FormKey, c.Type, c.Name,
                Score: c.Name is { Length: > 0 } inGame && Bare(inGame).Equals(bare, StringComparison.OrdinalIgnoreCase) ? -1 : Closeness(editorId, c.EditorId)))
            .Where(c => c.Score < int.MaxValue)
            .OrderBy(c => c.Score).ThenByDescending(c => SharedStart(editorId, c.EditorId)).ThenBy(c => c.EditorId.Length)
            .ThenBy(c => c.EditorId, StringComparer.Ordinal)
            .Select(c => $"{c.EditorId} ({(c.Name is { Length: > 0 } shown ? $"\"{shown}\", " : "")}" +
                $"{(typeof(ILeveledItemGetter).IsAssignableFrom(c.Type) ? "a leveled list, one picked at random" : RecordFiles.Article(Kind(c.Type)))}, " +
                $"{(c.FormKey is { } key ? Session.FormKeyText(key) : "new in this project")})")
            .Distinct()
            .Take(5)
            .ToList();
    }

    /// <summary>
    /// Up to five records of any type an editor ID the load order lacks may have meant, the closest first,
    /// each with its type and FormID. Told only "search with findRecords", a model that guessed
    /// FireDamageEnchantment01 searched for any enchantment and read a Creation Club axe's, though
    /// EnchWeaponFireDamage01 shares three of its words (eval probe 9 after round 11).
    /// </summary>
    public static List<string> Closest(string editorId, RecordIndex index) => index.ByFormKey.Values
        .Where(e => e.EditorId is not null)
        .Select(e => (Entry: e, Score: Closeness(editorId, e.EditorId!)))
        .Where(c => c.Score < int.MaxValue)
        .OrderBy(c => c.Score).ThenByDescending(c => SharedStart(editorId, c.Entry.EditorId!)).ThenBy(c => c.Entry.EditorId!.Length)
        .ThenBy(c => c.Entry.EditorId, StringComparer.Ordinal)
        .Take(5)
        .Select(c => $"{c.Entry.EditorId} ({c.Entry.Type}, {Session.FormKeyText(c.Entry.FormKey)})")
        .ToList();

    /// <summary>
    /// How many letters two editor IDs begin with alike, any case, two at least: among names as close, the one sharing the most
    /// of the beginning first — one letter alike says nothing. StandardArrow shares one word with every arrow, and SteelArrow, the one meant, came sixth by length —
    /// left out of the five, the model took IronArrow for ten Steel Arrows (eval round 132's world+detailed #1).
    /// </summary>
    private static int SharedStart(string a, string b) =>
        a.Zip(b).TakeWhile(p => char.ToUpperInvariant(p.First) == char.ToUpperInvariant(p.Second)).Count() is var shared and >= 2 ? shared : 0;

    /// <summary>
    /// Lower is closer: one editor ID inside the other, at least half its length — not the quest T01 in
    /// FireDamageEnchantment01; else a few letters apart; else sharing two or more
    /// of its words — one of four letters or more, for a name of two words or fewer — most first, and
    /// among as many the ones beginning as it does, which names the kind
    /// (WPNz… for a weapon's impact set) — WeapTypeOneHanded has none a few letters away, but the WeapType
    /// keywords share two of its words (eval probe 6 after round 11). int.MaxValue for none of these.
    /// </summary>
    private static int Closeness(string written, string candidate)
    {
        if (2 * Math.Min(candidate.Length, written.Length) >= Math.Max(candidate.Length, written.Length)
            && (candidate.Contains(written, StringComparison.OrdinalIgnoreCase) || written.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
            return Math.Abs(candidate.Length - written.Length);
        if (Math.Abs(candidate.Length - written.Length) <= 2
            && RecordIndex.Distance(candidate.ToUpperInvariant(), written.ToUpperInvariant()) is var distance and <= 2)
            return 100 + distance;
        // A name of one or two words shares one: Warrior1F the WARRIOR1 of CombatWarrior1H (eval probe 7).
        var mine = WordsOf(written);
        var shared = SharedWords(mine, WordsOf(candidate));
        if (shared.Count < 2 && !(mine.Count <= 2 && shared.Any(word => word.Length >= 4))) return int.MaxValue;
        var prefix = written.Zip(candidate).TakeWhile(p => char.ToUpperInvariant(p.First) == char.ToUpperInvariant(p.Second)).Count();
        return 1000 - 2 * shared.Count - (prefix >= 3 ? 1 : 0);
    }

    /// <summary>
    /// The words of one editor ID another has too, or a word one begins the other with, of four letters or
    /// more: ENCH for ENCHANTMENT, DAMAGE for DAMAGE01 — the way Bethesda shortens and numbers its words.
    /// </summary>
    internal static List<string> SharedWords(HashSet<string> mine, HashSet<string> theirs) => mine
        .Where(word => theirs.Contains(word) || theirs.Any(other => Math.Min(word.Length, other.Length) >= 4
            && (word.StartsWith(other, StringComparison.Ordinal) || other.StartsWith(word, StringComparison.Ordinal))))
        .ToList();

    /// <summary>
    /// An editor ID's words, upper-cased: WeapTypeOneHanded -> WEAP, TYPE, ONE, HANDED; a run of capitals
    /// ends before a capitalised word, so WPNZFireImpactSet -> WPNZ, FIRE, IMPACT, SET.
    /// </summary>
    internal static HashSet<string> WordsOf(string editorId) =>
        Regex.Split(editorId, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|_").Where(w => w.Length > 0)
            .Select(w => w.ToUpperInvariant()).ToHashSet();
}
