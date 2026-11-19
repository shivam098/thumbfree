using System.Text.RegularExpressions;

namespace ThumbFree.Core;

/// <summary>Where a dictation is typed, decided once when it starts (PLAN 3.10, rule 1).</summary>
/// <param name="TextBefore">
/// The text before the cursor when dictation started (its end is enough), or null if it couldn't be read: the dictation
/// then starts a sentence, gets no joining space and isn't taken as the start of the text (so no email layout). Line ends
/// count as \n however the app reports them (\r\n, \r, \v, \u2028, \u2029).
/// </param>
/// <param name="Field">The field it goes into.</param>
/// <param name="Live">Type finished phrases while speaking (the Live typing switch); false types everything at the end.</param>
/// <param name="SmartFormatting">Emails, lists and corrections (<see cref="Layout"/>).</param>
public sealed record LiveTypingContext(string? TextBefore, FieldInfo Field, bool Live, bool SmartFormatting);

/// <summary>What an update while speaking asks for.</summary>
/// <param name="Type">Text to type now, after everything typed before (often empty).</param>
/// <param name="Pending">Said but not typed yet, formatted: shown in the pill.</param>
public readonly record struct LiveStep(string Type, string Pending);

/// <summary>
/// Live typing where words can't sit in the text box as underlined composing text, as in any app on Windows (PLAN 3.10):
/// finished phrases are typed at pauses, and the phrase still being spoken is shown in the pill. Text typed into another
/// app can't be taken back (Backspace could destroy the person's own text), so only final text is typed: the finished
/// phrases before the last one, once the text with all of them still begins with them, both laid out as it would be at
/// the end and with more words after it ("… actually 4 pm", "scratch that", a repeated phrase, a sign-off or a Hindi tag
/// can still change them); not while a correction waits for its new value ("at 6, sorry,"); while the speech marks its
/// clauses with written punctuation, only up to where a "scratch that" said next would stop (it drops the sentence or
/// clause it follows), so a clause waits for the phrase after the one that ends it; and with a space, line break or comma
/// at the end of a piece held for the next one, since what's said next can still take it back ("new line" then "comma").
/// A clause that runs on over two pauses without a mark, like speech without written punctuation (often Hindi + English),
/// is typed one pause late as any phrase is, so there a "scratch that" said later can't take back typed text. While
/// nothing is typed, a dictation that may become a list, or an email in an email field, is held and typed whole at the
/// end. One with nothing typed live ends exactly like a dictation typed at the end. Pure logic: the app does the typing.
/// </summary>
public sealed partial class LiveTyping
{
    // Stands for whatever is said next: an ordinary word that no formatting rule treats specially.
    private const string NextWord = "zebra";

    private readonly DictationFormatter _formatter;
    private readonly LiveTypingContext _context;
    private readonly string? _textBefore;
    private readonly bool _smart;
    private readonly bool _holdAll;
    private bool _layoutHold;
    private bool _joinedWithSpace;
    private string _heldBreak = string.Empty;
    private string[] _finished = [];
    private string[] _typedPhrases = [];

    public LiveTyping(DictationFormatter formatter, LiveTypingContext context)
    {
        _formatter = formatter;
        _context = context;
        _textBefore = Insertion.NormalizeLineEnds(context.TextBefore);

        // As on Android, search and address boxes get no smart formatting.
        _smart = context.SmartFormatting && !context.Field.Search;
        _holdAll = !context.Live || context.Field.Password;
    }

    /// <summary>Everything typed so far (all that <see cref="Update"/> and <see cref="Finish"/> returned), including the joining space.</summary>
    public string Typed { get; private set; } = string.Empty;

    /// <summary>How many phrases, from the first, <see cref="Typed"/> holds.</summary>
    public int TypedPhrases => _typedPhrases.Length;

    /// <summary>
    /// Nothing is typed until <see cref="Finish"/>: live typing is off, it's a password field, or (while nothing is typed)
    /// a list or an email may be coming.
    /// </summary>
    public bool HoldsAll => _holdAll || _layoutHold;

    /// <summary>
    /// <see cref="Finish"/> couldn't type the final text as a whole, because its layout or a correction would have changed
    /// text already typed: the phrases not typed yet were formatted on their own and typed after it. Worth a log line.
    /// </summary>
    public bool FinishedWithoutLayout { get; private set; }

    /// <summary>
    /// <see cref="Finish"/> got phrases that don't begin with the ones already typed, against the contract of
    /// <see cref="Update"/>, so nothing more was typed: offer <see cref="FinalText"/> to copy instead.
    /// </summary>
    public bool PhrasesChanged { get; private set; }

    /// <summary>
    /// The whole dictation as it would be typed at the end (formatted and laid out, without the joining space), set by
    /// <see cref="Finish"/>. Empty for a password field.
    /// </summary>
    public string FinalText { get; private set; } = string.Empty;

    /// <summary>The dictation as typed, without the space that joined it to the text before (for History and "paste last").</summary>
    public string Text => _joinedWithSpace ? Typed[1..] : Typed;

    /// <summary>The text before what's still to be typed, with a space, line break or comma held back from the last piece.</summary>
    private string? Before => Typed.Length == 0 ? _textBefore : _textBefore + Typed + _heldBreak;

    /// <summary>
    /// The recognizer's progress: <paramref name="finishedPhrases"/>, the raw text of each phrase that ended at a pause (word
    /// list already applied; a phrase never changes once given), and <paramref name="partial"/>, the phrase being spoken.
    /// Returns what to type now and what to show in the pill (nothing for a password field).
    /// </summary>
    public LiveStep Update(IReadOnlyList<string> finishedPhrases, string partial)
    {
        var type = string.Empty;
        if (!finishedPhrases.SequenceEqual(_finished))
        {
            _finished = [.. finishedPhrases];
            type = TypeFinished();
        }

        var pending = _context.Field.Password ? string.Empty : Shape(LiveText.Join(_finished.Skip(TypedPhrases), partial), layout: false, Before);
        return new LiveStep(type, pending);
    }

    /// <summary>
    /// The dictation is over: <paramref name="allPhrases"/> are all its phrases (those given to <see cref="Update"/> and the
    /// last one), so the final text comes from the same phrase texts as what was typed. Returns the text still to type:
    /// the rest of the final text (laid out) after <see cref="Typed"/>, or, if that would change typed text, the phrases
    /// not typed yet formatted on their own (<see cref="FinishedWithoutLayout"/>). Nothing for a password field.
    /// </summary>
    public string Finish(IReadOnlyList<string> allPhrases)
    {
        if (_context.Field.Password)
        {
            return string.Empty;
        }

        var final = Prepare(LiveText.Join(allPhrases, string.Empty), layout: true, out var joined);
        FinalText = joined ? final[1..] : final;
        string rest;
        if (final.StartsWith(Typed, StringComparison.Ordinal))
        {
            rest = final[Typed.Length..];
            _joinedWithSpace |= Typed.Length == 0 && joined;
        }
        else
        {
            FinishedWithoutLayout = true;
            if (!allPhrases.Take(TypedPhrases).SequenceEqual(_typedPhrases))
            {
                PhrasesChanged = true;
                return string.Empty;
            }

            // Typed text stays as it is; a list in the rest is still laid out, on lines of its own.
            var before = Before;
            rest = _heldBreak + Insertion.WithSpacing(before, Shape(LiveText.Join(allPhrases.Skip(TypedPhrases), string.Empty), layout: true, before));
        }

        Typed += rest;
        _heldBreak = string.Empty;
        _typedPhrases = [.. allPhrases];
        return rest;
    }

    /// <summary>Types the longest run of finished phrases, before the last one, that is final.</summary>
    private string TypeFinished()
    {
        if (_holdAll)
        {
            return string.Empty;
        }

        var said = LiveText.Join(_finished, string.Empty);
        if (Typed.Length == 0)
        {
            // Judged again at each pause until something is typed: a list or an email is only laid out at the end.
            var start = _formatter.Format(said, Insertion.ContinuesSentence(_textBefore), _context.Field.Search);
            _layoutHold = start.Length > 0 && _context.Field.MultiLine && _smart
                && Layout.MayLayOut(start, new LayoutContext(true, AtStart(_textBefore), _context.Field.Email));
            if (start.Length == 0 || _layoutHold)
            {
                return string.Empty;
            }
        }

        var last = LastSpoken();
        if (last <= TypedPhrases)
        {
            return string.Empty;
        }

        var final = Prepare(said, layout: true, out _);
        if (!final.StartsWith(Typed, StringComparison.Ordinal) || (_smart && SelfCorrection.AwaitsValue(final)))
        {
            return string.Empty;
        }

        // What a "scratch that" said next would keep (while the speech marks its clauses), and what more words would leave
        // unchanged (a Hindi tag, a run of fillers or a filler-only sentence is formatted by what follows it).
        var kept = MarksItsClauses(said, last) ? Prepare(said + " scratch that", layout: false, out _) : final;
        var continued = Prepare(said + " " + NextWord, layout: false, out _);
        if (kept.Length <= Typed.Length || !kept.StartsWith(Typed, StringComparison.Ordinal) || !continued.StartsWith(Typed, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // The longest run of phrases that is final: all before the last phrase, or one or two fewer. Trying only these keeps
        // a pause's cost in proportion to the text, however long the dictation (a run can't be judged by its length, as a
        // "scratch that" in a longer one shortens it).
        for (var k = last; k > TypedPhrases && k > last - 3; k--)
        {
            // A space, line break or comma at the end goes out with the next piece: what's said next can still take it back
            // ("new line" then "comma"), and a comma that ends up last is dropped. The text without it is where the text
            // with it begins, so it passes whatever that passes.
            var full = Prepare(LiveText.Join(_finished.Take(k), string.Empty), layout: false, out var joined);
            var end = full.Length;
            while (end > 0 && (char.IsWhiteSpace(full[end - 1]) || full[end - 1] == ','))
            {
                end--;
            }

            var candidate = full[..end];
            if (candidate.Length > Typed.Length && candidate.StartsWith(Typed, StringComparison.Ordinal) && final.StartsWith(candidate, StringComparison.Ordinal)
                && kept.StartsWith(candidate, StringComparison.Ordinal) && continued.StartsWith(candidate, StringComparison.Ordinal))
            {
                var type = candidate[Typed.Length..];
                _joinedWithSpace |= Typed.Length == 0 && joined;
                Typed = candidate;
                _heldBreak = full[candidate.Length..];
                _typedPhrases = _finished[..k];
                return type;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The speech marks its clauses with written punctuation here: what follows the last written mark lies within the last
    /// finished phrase. Only then is the clause a "scratch that" would drop held back; one that has run on over two pauses
    /// without a mark is typed like unpunctuated speech, or live typing would stop for the rest of the dictation. Marks are
    /// read in all that was said as "scratch that" sees it, after filler removal ("Um," leaves none).
    /// </summary>
    private bool MarksItsClauses(string said, int last)
    {
        var text = _formatter.BeforeScratch(said);
        var mark = LastClauseMarkRegex().Match(text);
        return mark.Success && text.AsSpan(mark.Index + 1).TrimStart().Length <= _formatter.BeforeScratch(_finished[last]).Length;
    }

    /// <summary>The last finished phrase with words in it (blank or filler-only phrases don't count), or -1.</summary>
    private int LastSpoken()
    {
        for (var i = _finished.Length - 1; i >= 0; i--)
        {
            if (_formatter.Format(_finished[i]).Length > 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The dictation from its start, as typed after the text before the cursor.</summary>
    private string Prepare(string said, bool layout, out bool joined)
    {
        var text = Shape(said, layout, _textBefore);
        var spaced = Insertion.WithSpacing(_textBefore, text);
        joined = spaced.Length > text.Length;
        return spaced;
    }

    /// <summary>
    /// The shared pipeline for text that follows <paramref name="before"/>: the formatter, then layout or corrections only
    /// (smart formatting), then the field's rules. No joining space.
    /// </summary>
    private string Shape(string said, bool layout, string? before)
    {
        var atStart = AtStart(before);
        var text = _formatter.Format(said, Insertion.ContinuesSentence(before), _context.Field.Search);
        if (text.Length > 0 && _smart)
        {
            var corrected = SelfCorrection.Apply(text);
            var laid = layout && _context.Field.MultiLine ? Layout.Apply(text, new LayoutContext(true, atStart, _context.Field.Email)) : corrected;

            // A laid-out list or email starts on a line of its own when the cursor isn't at the start of one (PLAN 3.3).
            text = laid != corrected && !atStart && before is { Length: > 0 } && before[^1] != '\n' ? "\n" + laid : laid;
        }

        return Insertion.ForField(text, _context.Field);
    }

    /// <summary>Only whitespace before the cursor. Unknown text isn't the start.</summary>
    private static bool AtStart(string? before) => before is not null && string.IsNullOrWhiteSpace(before);

    // The last written mark that ends a sentence or clause (not the point in "2.5" or the colon in "3:30"): "scratch that"
    // drops back to it. Spoken "comma" or "full stop" become marks only after that, so they don't count.
    [GeneratedRegex(@"[.!?,;:](?=\s|$)", RegexOptions.RightToLeft)]
    private static partial Regex LastClauseMarkRegex();
}
