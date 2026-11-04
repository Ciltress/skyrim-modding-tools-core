using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimHelper;

/// <summary>
/// A new book whose text is a game book's but for a few lines, as a note beside a build that succeeds. Asked for the text
/// of "Note From Maven" word for word, a model wrote it with Maven's signature as the note's new name, "Maven's Warning"
/// (eval round 123's note+detailed #1); others added a `&lt;/font&gt;` or lost the note's font (rounds 89, 104 and 122) —
/// each build answered `success`. The note names the lines that differ beside the game book's and gives the call that
/// writes that book's text; where the user asked for those changes, it stays. Only the words outside the markup count as
/// shared: a new spell tome is a game tome's markup with its own spell's name, and no copy of it.
/// </summary>
internal static class BookTexts
{
    /// <summary>The words two texts share, at least, and the part of each they are.</summary>
    private const int LeastShared = 80;
    private const double SharedPart = 0.8;
    /// <summary>The longest text the note's call writes whole; a longer one, readRecord gives.</summary>
    private const int LongestCall = 4000;
    private const int ShownLines = 3;

    private static readonly Regex Markup = new("<[^>]*>");
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static List<string> Notes(ISkyrimModGetter mod, ILinkCache lookup, RecordIndex index, string pluginDir)
    {
        var notes = new List<string>();
        var own = mod.ModKey;
        var books = mod.Books.Where(b => b.FormKey.ModKey == own && b.BookText?.String is { Length: > 0 }).ToList();
        if (books.Count == 0) return notes;

        var game = index.ByFormKey.Values.Where(e => e.Type == "Book" && e.FormKey.ModKey != own)
            .Select(e => lookup.TryResolve<IBookGetter>(e.FormKey, out var book) ? book : null)
            .Where(b => b?.BookText?.String is { Length: > 0 })
            .Select(b => (Book: b!, Text: Normal(b!.BookText!.String!)))
            .ToList();
        var files = EffectCasting.Files(pluginDir);
        var project = ToolCalls.ProjectOf(pluginDir);
        foreach (var book in books)
        {
            if (files.GetValueOrDefault(book.FormKey) is not { } relative) continue;
            var text = Normal(book.BookText!.String!);
            if (game.Any(g => g.Text == text)) continue;
            var lines = text.Split('\n');
            var words = lines.Select(Visible).Where(l => l.Length >= 12).ToHashSet();
            if (words.Count == 0) continue;

            // The game book that shares the most words with it, of those that share a line of words at all.
            var closest = game.Where(g => g.Text.Split('\n').Any(l => words.Contains(Visible(l))))
                .Select(g => (g.Book, g.Text, Diff: Diff(g.Text.Split('\n'), lines)))
                .Where(g => g.Diff.Shared >= LeastShared && g.Diff.Shared >= SharedPart * g.Diff.FromWords
                    && g.Diff.Shared >= SharedPart * g.Diff.ToWords)
                .OrderByDescending(g => g.Diff.Shared).ThenBy(g => g.Diff.Changes.Count)
                .FirstOrDefault();
            if (closest.Book is null) continue;

            // Blank lines alone say nothing: copied from readRecord's folded text (TextBlocks writes it literal since), a
            // note's two blank lines between its parts were written as one in 170 of the 357 copies of rounds 87–123 —
            // runs that passed.
            var changes = closest.Diff.Changes.Where(c => !Blank(c.From) || !Blank(c.To)).ToList();
            if (changes.Count == 0) continue;
            var shown = string.Join("; ", changes.Take(ShownLines).Select(Said)) +
                (changes.Count > ShownLines ? $"; and {changes.Count - ShownLines} more" : "");
            var name = closest.Book.Name?.String is { Length: > 0 } title ? $" (\"{title}\", {closest.Book.FormKey.ModKey.FileName})"
                : $" ({closest.Book.FormKey.ModKey.FileName})";
            var gameId = closest.Book.EditorID ?? Session.FormKeyText(closest.Book.FormKey);
            var write = closest.Text.Length <= LongestCall
                ? "one call writes it and builds again: " + ToolCalls.Edit(project, relative,
                    $"{{\"path\": \"BookText.Value\", \"value\": {JsonSerializer.Serialize(closest.Text, Relaxed)}}}", build: true)
                : $"readRecord {{\"record\": {ToolCalls.Quoted(gameId)}}} gives it, to write as the file's BookText, then build again";
            notes.Add($"{relative}: {book.EditorID ?? Session.FormKeyText(book.FormKey)}'s text is {gameId}'s{name} but for " +
                (changes.Count == 1 ? "one place" : $"{changes.Count} places") + $": {shown}. Where the user asked for that " +
                $"book's text, word for word, {write}; where the user asked for these changes, nothing to change, and this " +
                "warning stays");
        }
        return notes;
    }

    /// <summary>A text with its lines as the game shows them: Windows' line ends read as one.</summary>
    private static string Normal(string text) => text.Replace("\r\n", "\n");

    /// <summary>A line's words, its markup taken out.</summary>
    private static string Visible(string line) => Markup.Replace(line, "").Trim();

    /// <summary>One place two texts differ: the game book's lines there, and the new book's.</summary>
    private sealed record Change(List<string> From, List<string> To);

    /// <summary>
    /// The lines two texts share, in order — the longest such run, by the words on them — and the places they differ.
    /// Shared counts the words of the lines both have, FromWords and ToWords all of each text's.
    /// </summary>
    private static (int Shared, int FromWords, int ToWords, List<Change> Changes) Diff(string[] from, string[] to)
    {
        var n = from.Length;
        var m = to.Length;
        var best = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            best[i, j] = from[i] == to[j] ? best[i + 1, j + 1] + Visible(from[i]).Length + 1
                : Math.Max(best[i + 1, j], best[i, j + 1]);

        var changes = new List<Change>();
        Change? open = null;
        int a = 0, b = 0, shared = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && from[a] == to[b] && best[a, b] == best[a + 1, b + 1] + Visible(from[a]).Length + 1)
            {
                shared += Visible(from[a]).Length;
                open = null;
                a++;
                b++;
                continue;
            }
            if (open is null) changes.Add(open = new Change([], []));
            if (b < m && (a >= n || best[a, b + 1] >= best[a + 1, b])) open.To.Add(to[b++]);
            else open.From.Add(from[a++]);
        }
        return (shared, from.Sum(l => Visible(l).Length), to.Sum(l => Visible(l).Length), changes);
    }

    private static bool Blank(List<string> lines) => lines.All(l => l.Trim().Length == 0);

    /// <summary>A place two texts differ, as the note says it: its lines with words, the blank ones left out.</summary>
    private static string Said(Change change)
    {
        static string Lines(List<string> lines) => string.Join(" / ", lines.Where(l => l.Trim().Length > 0).Take(2)
            .Select(l => $"`{Clip(l.Trim())}`")) + (lines.Count(l => l.Trim().Length > 0) > 2 ? " …" : "");
        if (Blank(change.From)) return $"{Lines(change.To)} added";
        if (Blank(change.To)) return $"{Lines(change.From)} left out";
        return $"{Lines(change.To)} where that book has {Lines(change.From)}";
    }

    private static string Clip(string line) => line.Length <= 60 ? line : line[..57] + "…";
}
