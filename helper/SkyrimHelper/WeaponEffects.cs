using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// A new weapon whose enchantment casts a magic effect no weapon of the load order casts — one made for a trap, a
/// creature's attack, an explosion — as a note beside a build that succeeds. Searching "FireDamage", a model took a Dark
/// Brotherhood trap's enchantment named so for its Flame Blade: its Heavy Fire Damage is Painless, shows no burn, says
/// nothing on the item card and spends no charge, and its duration of 2 s, kept, burned 10 a second where 10 in all was
/// asked (eval round 103's sword+detailed #5). The note names the effect the load order's weapons cast most of those that
/// change the same actor value against the same resistance, with the one call that casts it instead, at the duration
/// those weapons give it.
/// </summary>
internal static class WeaponEffects
{
    /// <summary>A magic effect as the load order's weapons cast it: how many do, the durations they give it, the enchantments.</summary>
    private sealed class Cast
    {
        public int Weapons;
        public readonly Dictionary<int, int> Durations = [];
        public readonly Dictionary<FormKey, int> Enchantments = [];
    }

    public static List<string> Notes(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var notes = new List<string>();
        var own = mod.ModKey;
        var weapons = mod.Weapons.Where(w => w.FormKey.ModKey == own && !w.ObjectEffect.IsNull).ToList();
        if (weapons.Count == 0) return notes;

        var enchantments = new Dictionary<FormKey, IObjectEffectGetter?>();
        IObjectEffectGetter? EnchantmentOf(FormKey key) => enchantments.TryGetValue(key, out var known) ? known
            : enchantments[key] = mod.ObjectEffects.FirstOrDefault(e => e.FormKey == key)
                ?? (lookup.TryResolve<IObjectEffectGetter>(key, out var found) ? found : null);
        // What each project weapon's enchantment casts that is the load order's: a magic effect of the project's is
        // EffectCasting's to note.
        var foreign = weapons
            .Select(w => (Weapon: w, Enchantment: EnchantmentOf(w.ObjectEffect.FormKey)))
            .Where(c => c.Enchantment is not null)
            .SelectMany(c => c.Enchantment!.Effects.Select((e, i) => (c.Weapon, Enchantment: c.Enchantment!, Item: i, Effect: e)))
            .Where(c => !c.Effect.BaseEffect.IsNull && c.Effect.BaseEffect.FormKey.ModKey != own)
            .ToList();
        if (foreign.Count == 0) return notes;

        var casts = new Dictionary<FormKey, Cast>();
        foreach (var entry in index.ByFormKey.Values.Where(e => e.Type == "Weapon" && e.FormKey.ModKey != own))
        {
            if (!lookup.TryResolve<IWeaponGetter>(entry.FormKey, out var weapon) || weapon.ObjectEffect.IsNull
                || EnchantmentOf(weapon.ObjectEffect.FormKey) is not { } enchantment) continue;
            foreach (var effect in enchantment.Effects.Where(e => !e.BaseEffect.IsNull))
            {
                var cast = casts.TryGetValue(effect.BaseEffect.FormKey, out var known) ? known : casts[effect.BaseEffect.FormKey] = new Cast();
                cast.Weapons++;
                var duration = effect.Data?.Duration ?? 0;
                cast.Durations[duration] = cast.Durations.GetValueOrDefault(duration) + 1;
                cast.Enchantments[enchantment.FormKey] = cast.Enchantments.GetValueOrDefault(enchantment.FormKey) + 1;
            }
        }

        // The one the load order's weapons cast most of those that change the same actor value against the same resistance.
        var castEffects = casts.Select(c => (Key: c.Key, Cast: c.Value, Effect: lookup.TryResolve<IMagicEffectGetter>(c.Key, out var e) ? e : null))
            .Where(c => c.Effect is not null).ToList();
        (FormKey Key, Cast Cast, IMagicEffectGetter? Effect) Like(IMagicEffectGetter named) => castEffects
            .Where(c => c.Effect!.ResistValue == named.ResistValue && c.Effect.Archetype.ActorValue == named.Archetype.ActorValue
                && c.Effect.Archetype.Type == named.Archetype.Type)
            .OrderByDescending(c => c.Cast.Weapons).FirstOrDefault();
        string Name(IMajorRecordGetter record) => record.EditorID ?? Session.FormKeyText(record.FormKey);

        var files = EffectCasting.Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        // One note a record: the project's enchantment, with a call for its items; a weapon carrying the load order's, with one
        // for the weapon. A trap's enchantment casts two fire effects, and two notes gave the same call.
        foreach (var group in foreign.Where(c => !casts.ContainsKey(c.Effect.BaseEffect.FormKey))
                     .GroupBy(c => c.Enchantment.FormKey.ModKey == own ? c.Enchantment.FormKey : c.Weapon.FormKey))
        {
            var (weapon, enchantment, _, _) = group.First();
            var ownEnchantment = enchantment.FormKey.ModKey == own;
            if (files.GetValueOrDefault(group.Key) is not { } relative) continue;
            var items = group.DistinctBy(c => c.Item)
                .Select(c => (c.Item, c.Effect, Named: lookup.TryResolve<IMagicEffectGetter>(c.Effect.BaseEffect.FormKey, out var e) ? e : null))
                .Where(i => i.Named is not null)
                .Select(i => (i.Item, i.Effect, Named: i.Named!, Like: Like(i.Named!))).ToList();
            if (items.Count == 0) continue;
            var shown = string.Join(" and ", items.Select(i => i.Named.Name?.String is { Length: > 0 } words ? $"{Name(i.Named)} ('{words}')" : Name(i.Named)));
            var said = $"{relative}: " + (ownEnchantment ? $"{Name(enchantment)} casts {shown}"
                    : $"{weapon.EditorID ?? relative} carries {Name(enchantment)}, and it casts {shown}") +
                $", which no weapon of the load order casts — an effect made for something else, as a trap or a creature's attack, can " +
                "lack the burn, the sounds, the item card's text and the charge a weapon's enchantment spends";
            var found = items.Where(i => i.Like.Effect is not null).ToList();
            if (found.Count == 0)
            {
                notes.Add(said + "; the game's weapons cast no effect like it: an effect meant so stays");
                continue;
            }
            var games = found.Select(i => i.Like).DistinctBy(l => l.Key).ToList();
            var cast = string.Join(" and ", games.Select(l => $"{Name(l.Effect!)} for that, {l.Cast.Weapons} of them, as " +
                $"{Name(EnchantmentOf(Example(l.Cast))!)} does"));
            string call;
            if (ownEnchantment)
            {
                // Each kind the game's weapons cast once, at the duration they give it most; a second item of a kind goes.
                var set = new List<string>();
                var remove = new List<int>();
                var given = new HashSet<FormKey>();
                var durations = false;
                foreach (var (item, effect, _, like) in found)
                {
                    if (!given.Add(like.Key)) { remove.Add(item); continue; }
                    set.Add($"{{\"path\": \"Effects[{item}].BaseEffect\", \"value\": {ToolCalls.Quoted(Name(like.Effect!))}}}");
                    var duration = like.Cast.Durations.OrderByDescending(d => d.Value).First().Key;
                    if ((effect.Data?.Duration ?? 0) == duration) continue;
                    set.Add($"{{\"path\": \"Effects[{item}].Data.Duration\", \"value\": {duration}}}");
                    durations = true;
                }
                call = $"one call casts {(games.Count == 1 ? "it" : "them")} instead" + (durations ? ", for as long as they do" : "") +
                    (remove.Count > 0 ? $", without a second of its kind" : "") + ", and builds again: " +
                    ToolCalls.Edit(project, relative, string.Join(", ", set),
                        remove.Count == 0 ? null : string.Join(", ", remove.OrderDescending().Select(i => ToolCalls.Quoted($"Effects[{i}]"))), build: true);
            }
            else
            {
                var carried = Name(EnchantmentOf(Example(games[0].Cast))!);
                call = $"one call carries {carried}, which casts {Name(games[0].Effect!)}, instead and builds again: " +
                    ToolCalls.Edit(project, relative, $"{{\"path\": \"ObjectEffect\", \"value\": {ToolCalls.Quoted(carried)}}}", build: true);
            }
            notes.Add(said + $". The game's weapons cast {cast}; {call} — an effect meant so stays");
        }
        return notes;
    }

    /// <summary>The enchantment most of the weapons that cast an effect carry.</summary>
    private static FormKey Example(Cast cast) => cast.Enchantments.OrderByDescending(e => e.Value).First().Key;
}
