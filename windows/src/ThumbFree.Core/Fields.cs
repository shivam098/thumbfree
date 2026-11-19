using System.Text.RegularExpressions;

namespace ThumbFree.Core;

/// <summary>
/// The text field a dictation goes into, read once when dictation starts. Windows fills it from UI Automation; Android
/// from the field's input type. The defaults describe a plain single-line box.
/// </summary>
/// <param name="Password">A password or other secure field: never typed into (live typing holds everything, and <see cref="DictationInserter"/> refuses).</param>
/// <param name="Search">A search or address box: no trailing full stop, and no smart formatting.</param>
/// <param name="MultiLine">
/// Accepts line breaks. Elsewhere a spoken "new line" becomes a space, since Enter would send or search.
/// </param>
/// <param name="Private">An incognito / InPrivate window: no voice samples are kept from it.</param>
/// <param name="Email">
/// An email app or a webmail compose box: a greeting or a sign-off alone is enough for email layout, so live typing holds
/// a dictation that starts with a greeting.
/// </param>
public sealed record FieldInfo(bool Password = false, bool Search = false, bool MultiLine = false, bool Private = false, bool Email = false);

/// <summary>How dictated text joins what is already in the field. Same rules as the Android app.</summary>
public static partial class Insertion
{
    // Text starting with one of these attaches to the word before it (", right?").
    private const string AttachesToPrevious = ",.;:!?)\n";

    // After one of these the next word follows directly ("(see", "@mail", "#tag", "/path").
    private const string AttachesToNext = "([{\"'“‘/@#";

    /// <summary>The text before the cursor doesn't end a sentence (so "And then…" continues as "and then…").</summary>
    public static bool ContinuesSentence(string? before)
    {
        if (string.IsNullOrEmpty(before))
        {
            return false;
        }

        var trimmed = before.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] is not ('.' or '!' or '?') && before[^1] != '\n';
    }

    /// <summary>The text to type: with a leading space when it would otherwise run into the previous word.</summary>
    public static string WithSpacing(string? before, string text)
    {
        if (text.Length == 0 || string.IsNullOrEmpty(before))
        {
            return text;
        }

        var last = before[^1];
        var needsSpace = !char.IsWhiteSpace(last) && !AttachesToPrevious.Contains(text[0]) && !AttachesToNext.Contains(last);
        return needsSpace ? " " + text : text;
    }

    /// <summary>Line breaks typed into a single-line box would trigger its action (send, search): spaces there.</summary>
    public static string ForField(string text, FieldInfo field) => field.MultiLine ? text : LineBreaksRegex().Replace(text, " ");

    /// <summary>
    /// The text starts with punctuation or a line break, so no space goes before it whatever the previous character is
    /// (for callers that only know whether the field ends in a word).
    /// </summary>
    internal static bool StartsAttached(string text) => text.Length > 0 && AttachesToPrevious.Contains(text[0]);

    [GeneratedRegex(" *\n+ *")]
    private static partial Regex LineBreaksRegex();
}

/// <summary>Whisper's well-known outputs on silence or noise, dropped for short audio (under 2.5 s).</summary>
public static class Hallucinations
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { "you", "thanks for watching", "thank you for watching", "please subscribe" };

    public static bool IsLikely(string text, int audioSamples) =>
        audioSamples < Decoding.SampleRate * 5 / 2 && Known.Contains(TranscriptText.Normalize(text));
}

/// <summary>
/// The personal dictionary as typed in settings: one "heard = typed" per line, for words the recognizer mishears
/// completely ("journals = genres"); <c>\n</c> in "typed" is a line break. Becomes <see cref="DictationOptions.Replacements"/>.
/// </summary>
public static class PersonalDictionary
{
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (text ?? string.Empty).Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var heard = line[..eq].Trim();
            var typed = line[(eq + 1)..].Trim().Replace("\\n", "\n", StringComparison.Ordinal);
            if (heard.Length > 0 && typed.Length > 0)
            {
                map[heard] = typed;
            }
        }

        return map;
    }
}
