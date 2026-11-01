using System.Text.Json.Nodes;
using Loqui;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// The project's records on a model no record of their type in the load order is on — a path the model made up, or a mesh
/// the project or a mod brings — for the server to look up where the game finds meshes (SkyrimInstanceBackend.buildProject):
/// the game draws nothing for a record whose mesh it does not have. Eval round 105's sword #1, #3 and #5 built a Flame Blade
/// on `Weapons\FlameBlade.nif`, `Weapons\GenericOneHandSword.nif` and `Weapons\Iron\Sword.nif`, no file of the game, each
/// build answering success; 21 sword runs of rounds 87–105 passed so. A model the load order's records of its type are on is
/// the game's, and not looked up; an override on the model of the record it overrides is left alone.
/// Each comes with the models of the records of its type most like it, for the server to offer the first the game has.
/// So too a new record with no model at all, of a type whose records the game draws — 9 in 10 of the load order's have a
/// model — given as the model "": round 93's note #1 and round 94's sword #5 built with none and passed.
/// </summary>
internal static class ModelFiles
{
    /// <summary>The most models offered for one record: the server takes the first the game has.</summary>
    private const int Offered = 5;

    public static JsonArray Unknown(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var unknown = new JsonArray();
        var drawn = mod.EnumerateMajorRecords().Where(r => r is IModeledGetter)
            .Select(record => (Record: record, Model: Path(((IModeledGetter)record).Model?.File.GivenPath)))
            .ToList();
        var modeled = drawn.Where(r => r.Model is not null).ToList();
        var bare = drawn.Where(r => r.Model is null && r.Record.FormKey.ModKey == mod.ModKey).Select(r => r.Record).ToList();
        if (drawn.Count == 0) return unknown;
        var files = EffectCasting.Files(pluginDir);
        var loaded = new Dictionary<string, List<(IMajorRecordGetter Record, string Model)>>();

        foreach (var (record, model) in modeled)
        {
            if (record.FormKey.ModKey != mod.ModKey
                && lookup.TryResolve(record.FormKey, ((ILoquiObject)record).Registration.GetterType, out var original)
                && string.Equals(Path((original as IModeledGetter)?.Model?.File.GivenPath), model, StringComparison.OrdinalIgnoreCase))
                continue;
            var type = RecordIndex.TypeName(record);
            var ofType = loaded.TryGetValue(type, out var list) ? list : loaded[type] = OfType(type);
            if (ofType.Any(r => string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase))) continue;
            if (files.GetValueOrDefault(record.FormKey) is not { } relative) continue;
            unknown.Add(Entry(record, relative, model, ofType));
        }
        foreach (var record in bare)
        {
            var type = RecordIndex.TypeName(record);
            var ofType = loaded.TryGetValue(type, out var list) ? list : loaded[type] = OfType(type);
            var all = index.ByFormKey.Values.Count(e => e.Type == type && e.FormKey.ModKey != mod.ModKey && !e.Unplayable);
            if (all < 5 || ofType.Count < 0.9 * all || files.GetValueOrDefault(record.FormKey) is not { } relative) continue;
            unknown.Add(Entry(record, relative, "", ofType));
        }
        return unknown;

        JsonObject Entry(IMajorRecordGetter record, string relative, string model, List<(IMajorRecordGetter Record, string Model)> ofType)
        {
            // Most like it: of its kind — a one-handed sword's, not a staff's — then the most links shared — the keywords, sounds
            // and impact sets a copy keeps — then the model the most records are on, the kind's common one, then load order. Round
            // 105's sword #3 wrote no keyword, and the type's most common model was a staff's. Named by a plain record: the Iron
            // Sword, not an enchanted one of its look.
            var links = record.EnumerateFormLinks().Select(l => l.FormKey).ToHashSet();
            var kind = Kind(record);
            var onModel = ofType.GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var plain = ofType.Where(r => Plain(r.Record)).ToList();
            var like = (plain.Count > 0 ? plain : ofType)
                .Select((r, at) => (r.Record, r.Model, At: at, Shared: r.Record.EnumerateFormLinks().Select(l => l.FormKey).Distinct().Count(links.Contains)))
                .OrderByDescending(r => kind is not null && Kind(r.Record) == kind).ThenByDescending(r => r.Shared)
                .ThenByDescending(r => onModel[r.Model]).ThenBy(r => r.At)
                .DistinctBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                .Take(Offered)
                .Select(r => (JsonNode)new JsonObject { ["editorId"] = r.Record.EditorID ?? Session.FormKeyText(r.Record.FormKey), ["model"] = r.Model })
                .ToArray();
            return new JsonObject
            {
                ["file"] = relative.Replace('\\', '/'),
                ["editorId"] = record.EditorID ?? Session.FormKeyText(record.FormKey),
                ["model"] = model,
                ["like"] = new JsonArray(like)
            };
        }

        // The load order's records of the type the player can have, on a model, in load order.
        List<(IMajorRecordGetter Record, string Model)> OfType(string type)
        {
            var records = new List<(IMajorRecordGetter, string)>();
            foreach (var entry in index.ByFormKey.Values.Where(e => e.Type == type && e.FormKey.ModKey != mod.ModKey && !e.Unplayable))
                if (lookup.TryResolve(entry.FormKey, entry.GetterType, out var record) && Path((record as IModeledGetter)?.Model?.File.GivenPath) is { } file)
                    records.Add((record, file));
            return records;
        }
    }

    /// <summary>What a record is among its type, where its type tells: a weapon's animation, a sword's or a staff's.</summary>
    private static string? Kind(IMajorRecordGetter record) => record switch
    {
        IWeaponGetter weapon => weapon.Data?.AnimationType.ToString(),
        _ => null
    };

    /// <summary>A record of its own, not the copy of another: a weapon with no template and no enchantment.</summary>
    private static bool Plain(IMajorRecordGetter record) => record switch
    {
        IWeaponGetter weapon => weapon.Template.IsNull && weapon.ObjectEffect.IsNull,
        _ => true
    };

    /// <summary>A model's path as the record gives it, below the Meshes folder, with backslashes.</summary>
    internal static string? Path(string? given)
    {
        if (string.IsNullOrWhiteSpace(given)) return null;
        var file = given.Trim().Replace('/', '\\');
        return file.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? file[7..] : file;
    }
}
