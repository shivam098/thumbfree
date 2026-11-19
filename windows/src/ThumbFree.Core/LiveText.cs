using System.Text.RegularExpressions;

namespace ThumbFree.Core;

/// <summary>
/// Text that arrives while you speak (live typing). A streaming recognizer gives each phrase as it ends (finished)
/// plus the words of the phrase in progress (partial). Same rules as the Android app.
/// </summary>
public static partial class LiveText
{
    /// <summary>
    /// Takes the punctuation from <paramref name="punctuated"/> (the punctuation model's output) and keeps the
    /// recognizer's own words and capitals (<paramref name="original"/>); the model re-cases words badly ("YOUTUBE").
    /// Where the model puts no mark, a mark the recognizer wrote itself is kept. A word never ends in two marks
    /// ("build.." or "logs.,"). Sentence starts are capitalized. If the two don't have the same words,
    /// <paramref name="original"/> is returned unchanged.
    /// </summary>
    public static string MergePunctuation(string original, string punctuated)
    {
        var words = Words(original);
        var marked = Words(punctuated);
        if (words.Length == 0 || words.Length != marked.Length)
        {
            return original.Trim();
        }

        var sb = new System.Text.StringBuilder();
        var sentenceStart = true;
        for (var i = 0; i < words.Length; i++)
        {
            var bare = TrailingMarkRegex().Replace(words[i], string.Empty);
            if (!string.Equals(bare, TrailingMarkRegex().Replace(marked[i], string.Empty), StringComparison.OrdinalIgnoreCase))
            {
                return original.Trim();
            }

            var mark = TrailingMarkRegex().Match(marked[i]).Value;
            mark = Single(mark.Length > 0 ? mark : TrailingMarkRegex().Match(words[i]).Value);
            var word = sentenceStart && bare.Length > 0 && char.IsLower(bare[0]) ? char.ToUpperInvariant(bare[0]) + bare[1..] : bare;
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(word).Append(mark);
            sentenceStart = mark.Length > 0 && mark[^1] is '.' or '?' or '!';
        }

        return sb.ToString();
    }

    /// <summary>One mark for a run of them: the one that ends a sentence wins.</summary>
    private static string Single(string mark) =>
        mark.Length <= 1 ? mark
        : mark.Contains('?') ? "?"
        : mark.Contains('!') ? "!"
        : mark.Contains('.') ? "."
        : mark[..1];

    /// <summary>The text so far: finished phrases, then the phrase in progress.</summary>
    public static string Join(IEnumerable<string> finished, string partial) =>
        string.Join(' ', finished.Append(partial.Trim()).Where(p => p.Length > 0));

    /// <summary>
    /// The end of a dictation is the end of a sentence: a phrase cut at a pause may end in a comma (or nothing), which
    /// becomes a full stop. Text already ending in . ? ! (or a symbol) is left alone.
    /// </summary>
    public static string EndSentence(string text)
    {
        var t = text.TrimEnd().TrimEnd(',', ';', ':').TrimEnd();
        return t.Length > 0 && char.IsLetterOrDigit(t[^1]) ? t + "." : t;
    }

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"[.,?!;:]+$")]
    private static partial Regex TrailingMarkRegex();
}
