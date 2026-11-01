using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using YamlDotNet.RepresentationModel;

namespace SkyrimHelper;

/// <summary>
/// A new spell's or enchantment's lines written from its effects, before the files are checked: the cast it lacks, where its
/// effects are all cast one way, and an effect's flags listed in the spell's own Flags, where its effects have them. Each
/// effect is read where its record is: the project's file, or the load order.
/// </summary>
internal static class CasterLines
{
    /// <summary>What a record's effect is, as far as the lines need it: how it is cast, and its flags.</summary>
    private sealed record Effect(string Name, CastType? Cast, TargetType? Target, HashSet<string> Flags);

    private static readonly Regex CastLine = new(@"^CastType[ \t]*:[ \t]*(?<value>\w+)", RegexOptions.Multiline);
    private static readonly Regex TargetLine = new(@"^TargetType[ \t]*:[ \t]*(?<value>\w+)", RegexOptions.Multiline);
    private static readonly Regex EffectsLine = new(@"^Effects[ \t]*:", RegexOptions.Multiline);

    /// <summary>
    /// A spell or enchantment written without its CastType, its TargetType or both, where every effect it names is cast one way:
    /// the lines it lacks written as its effects are cast, above its Effects. Without them it is cast ConstantEffect on Self, and the
    /// build refused a weapon's enchantment whose effect is cast FireAndForget on Touch (eval rounds 92 and 97, a sword run each). A
    /// line written otherwise than its effects are cast, and effects cast several ways, are left to the refusal.
    /// </summary>
    public static List<(string File, string Change)> CastAsEffects(string pluginDir, RecordIndex index, ILinkCache lookup)
    {
        var changes = new List<(string File, string Change)>();
        var own = OwnEffects(pluginDir);
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir))
        {
            if (type != typeof(ObjectEffect) && type != typeof(Spell)) continue;
            var written = File.ReadAllText(file);
            var text = written.Replace("\r\n", "\n");
            var (cast, target) = (CastLine.Match(text), TargetLine.Match(text));
            if (cast.Success && target.Success || EffectsOf(text, own, index, lookup) is not { Count: > 0 } effects) continue;
            if (effects.Any(e => e.Cast is null || e.Target is null)
                || effects.Select(e => (e.Cast, e.Target)).Distinct().Count() > 1) continue;
            var (effectCast, effectTarget) = (effects[0].Cast!.Value, effects[0].Target!.Value);
            if (cast.Success && !string.Equals(cast.Groups["value"].Value, effectCast.ToString(), StringComparison.OrdinalIgnoreCase)
                || target.Success && !string.Equals(target.Groups["value"].Value, effectTarget.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            // Missing, the record is cast ConstantEffect on Self: nothing to write where its effects are cast so.
            if (!cast.Success && effectCast == CastType.ConstantEffect && !target.Success && effectTarget == TargetType.Self) continue;
            var lines = new List<string>();
            if (!cast.Success) lines.Add($"CastType: {effectCast}");
            if (!target.Success) lines.Add($"TargetType: {effectTarget}");
            var all = text.Split('\n').ToList();
            var at = EffectsLine.Match(text) is { Success: true } effectsLine ? text[..effectsLine.Index].Count(c => c == '\n') : all.Count;
            all.InsertRange(at, lines);
            var made = string.Join('\n', all);
            File.WriteAllText(file, written.Contains("\r\n") ? made.Replace("\n", "\r\n") : made);
            var name = RecordFiles.Identity(text).EditorId ?? Path.GetFileNameWithoutExtension(relative);
            changes.Add((relative, $"wrote {string.Join(" and ", lines.Select(l => $"`{l}`"))} above Effects, line {at + 1} — {name} had " +
                $"{(lines.Count == 2 ? "neither" : $"no {lines[0].Split(':')[0]}")}, so it was cast {(cast.Success ? cast.Groups["value"].Value : "ConstantEffect")} " +
                $"on {(target.Success ? target.Groups["value"].Value : "Self")}, and {Effects(effects)} {(effects.Count == 1 ? "is" : "are")} cast {effectCast} " +
                $"on {effectTarget}: an effect does " +
                $"nothing in {RecordFiles.Article(type.Name)} cast otherwise"));
        }
        return changes;
    }

    /// <summary>
    /// A magic effect's flags listed in a spell's Flags — `- Hostile`, `- Detrimental`, copied from the effect's file — taken out
    /// where every effect the spell names has it: a Spell's Flags hold none of them, and the build refused the spell (eval round
    /// 95's spell+detailed #3, which ran out of turns). A flag an effect lacks is left to the refusal: the spell may mean it.
    /// </summary>
    public static List<(string File, string Change)> DropEffectFlags(string pluginDir, RecordIndex index, ILinkCache lookup)
    {
        var changes = new List<(string File, string Change)>();
        var spellFlags = RecordFiles.FlagsOf(typeof(Spell));
        var effectFlags = RecordFiles.FlagsOf(typeof(MagicEffect));
        if (spellFlags is null || effectFlags is null) return changes;
        var own = OwnEffects(pluginDir);
        foreach (var (file, relative, type) in RecordFiles.FlatRecords(pluginDir))
        {
            if (type != typeof(Spell)) continue;
            var written = File.ReadAllText(file);
            var text = written.Replace("\r\n", "\n");
            if (MisplacedFields.Root(text) is not { } root || !root.Children.TryGetValue(new YamlScalarNode("Flags"), out var flagsNode)
                || flagsNode is not YamlSequenceNode { Style: not YamlDotNet.Core.Events.SequenceStyle.Flow } flags) continue;
            var candidates = flags.Children.OfType<YamlScalarNode>()
                .Where(i => i.Value is { } name && RecordFiles.FlagNamed(spellFlags, name) is null && RecordFiles.FlagNamed(effectFlags, name) is not null).ToList();
            if (candidates.Count == 0 || EffectsOf(text, own, index, lookup) is not { Count: > 0 } effects) continue;
            var theirs = candidates.Where(i => effects.All(e => e.Flags.Contains(RecordFiles.FlagNamed(effectFlags, i.Value!)!))).ToList();
            if (theirs.Count == 0) continue;
            var all = text.Split('\n').ToList();
            var gone = theirs.Select(i => (int)i.Start.Line - 1).ToList();
            // All of them: the Flags line goes too, as a list with no item reads as none.
            if (gone.Count == flags.Children.Count) gone.Add((int)root.Children.Keys.OfType<YamlScalarNode>().First(k => k.Value == "Flags").Start.Line - 1);
            foreach (var line in gone.Distinct().OrderByDescending(n => n)) all.RemoveAt(line);
            var made = string.Join('\n', all);
            File.WriteAllText(file, written.Contains("\r\n") ? made.Replace("\n", "\r\n") : made);
            var names = theirs.Select(i => $"`{i.Value}`").ToList();
            changes.Add((relative, $"took {string.Join(", ", names)} out of Flags — {(names.Count == 1 ? "it is a MagicEffect's flag" : "they are a MagicEffect's flags")}, " +
                $"which a Spell's Flags hold none of, and {Effects(effects)} {(effects.Count == 1 ? "has" : "have")} {(names.Count == 1 ? "it" : "each")}" +
                (theirs.Count < candidates.Count ? $"; {string.Join(", ", candidates.Except(theirs).Select(i => $"`{i.Value}`"))} left, which " +
                    $"{(effects.Count == 1 ? "it lacks" : "not every one has")}" : "")));
        }
        return changes;
    }

    private static string Effects(List<Effect> effects) =>
        effects.Count == 1 ? $"its effect {effects[0].Name}" : $"its effects {string.Join(", ", effects.Select(e => e.Name).Distinct())}";

    /// <summary>The project's magic effect files' texts, by their EditorID and by the FormKey they are written with.</summary>
    private static Dictionary<string, string> OwnEffects(string pluginDir)
    {
        var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, _, type) in RecordFiles.FlatRecords(pluginDir))
        {
            if (type != typeof(MagicEffect)) continue;
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            var (key, editorId) = RecordFiles.Identity(text);
            if (editorId is not null) own.TryAdd(editorId, text);
            if (key is { } formKey) own.TryAdd(Session.FormKeyText(formKey), text);
        }
        return own;
    }

    /// <summary>The effects a spell's or enchantment's Effects name, each read; null where one is not found or the list reads as none.</summary>
    private static List<Effect>? EffectsOf(string text, Dictionary<string, string> own, RecordIndex index, ILinkCache lookup)
    {
        if (MisplacedFields.Root(text) is not { } root || !root.Children.TryGetValue(new YamlScalarNode("Effects"), out var list)
            || list is not YamlSequenceNode items) return null;
        var effects = new List<Effect>();
        foreach (var item in items.Children)
        {
            if (item is not YamlMappingNode fields || !fields.Children.TryGetValue(new YamlScalarNode("BaseEffect"), out var named)
                || named is not YamlScalarNode { Value: { Length: > 0 } written } || Read(written.Trim(), own, index, lookup) is not { } effect) return null;
            effects.Add(effect);
        }
        return effects;
    }

    /// <summary>The effect a link names — the project's own first, by editor ID or FormKey, then the load order's — or null.</summary>
    private static Effect? Read(string written, Dictionary<string, string> own, RecordIndex index, ILinkCache lookup)
    {
        if (own.TryGetValue(written, out var text))
        {
            var root = MisplacedFields.Root(text);
            var flags = root is not null && root.Children.TryGetValue(new YamlScalarNode("Flags"), out var listed) && listed is YamlSequenceNode items
                ? items.Children.OfType<YamlScalarNode>().Select(i => i.Value ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new Effect(RecordFiles.Identity(text).EditorId ?? written,
                CastLine.Match(text) is { Success: true } c && Enum.TryParse<CastType>(c.Groups["value"].Value, true, out var cast) ? cast : null,
                TargetLine.Match(text) is { Success: true } t && Enum.TryParse<TargetType>(t.Groups["value"].Value, true, out var target) ? target : null,
                flags);
        }
        FormKey key;
        if (FormKey.TryFactory(written, out var parsed)) key = parsed;
        else if (index.ByEditorId.TryGetValue(written, out var byId)) key = byId;
        else return null;
        if (!lookup.TryResolve<IMagicEffectGetter>(key, out var effect)) return null;
        return new Effect(effect.EditorID ?? written, effect.CastType, effect.TargetType,
            Enum.GetValues<MagicEffect.Flag>().Where(f => f != 0 && effect.Flags.HasFlag(f)).Select(f => f.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}
