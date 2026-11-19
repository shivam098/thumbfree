namespace ThumbFree.Core;

/// <summary>Where a dictation is typed, decided once when it starts (PLAN 3.10, rule 1).</summary>
/// <param name="TextBefore">
/// The text before the cursor when dictation started (its end is enough), or null if it couldn't be read: the dictation
/// then starts a sentence, gets no joining space and isn't taken as the start of the text (so no email layout).
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
/// app can't be taken back (Backspace could destroy the person's own text), so only final text is typed. A phrase is
/// typed one pause late, and only if the text with the next phrase, laid out as it would be at the end, still begins
/// with it: "… actually 4 pm", "scratch that", a repeated phrase or a sign-off can still change it. A dictation that may
/// become a list, or an email in an email field, judged by how it starts, is held and typed whole at the end. One with
/// nothing typed live ends exactly like a dictation typed at the end. Pure logic: the app does the typing.
/// </summary>
public sealed class LiveTyping
{
    private readonly DictationFormatter _formatter;
    private readonly LiveTypingContext _context;
    private readonly bool _smart;
    private bool? _holdAll;
    private bool _joinedWithSpace;
    private string[] _finished = [];

    public LiveTyping(DictationFormatter formatter, LiveTypingContext context)
    {
        _formatter = formatter;
        _context = context;

        // As on Android, search and address boxes get no smart formatting.
        _smart = context.SmartFormatting && !context.Field.Search;
        if (!context.Live || context.Field.Password)
        {
            _holdAll = true;
        }
    }

    /// <summary>Everything typed so far (all that <see cref="Update"/> and <see cref="Finish"/> returned), including the joining space.</summary>
    public string Typed { get; private set; } = string.Empty;

    /// <summary>How many phrases, from the first, <see cref="Typed"/> holds.</summary>
    public int TypedPhrases { get; private set; }

    /// <summary>Nothing is typed until <see cref="Finish"/>: live typing is off, it's a password field, or an email or a list may be coming.</summary>
    public bool HoldsAll => _holdAll == true;

    /// <summary>
    /// <see cref="Finish"/> couldn't type the final text as a whole, because its layout or a correction would have changed
    /// text already typed: the phrases not typed yet were formatted on their own and typed after it. Worth a log line.
    /// </summary>
    public bool FinishedWithoutLayout { get; private set; }

    /// <summary>The dictation as typed, without the space that joined it to the text before (for History and "paste last").</summary>
    public string Text => _joinedWithSpace ? Typed[1..] : Typed;

    private string? Before => Typed.Length == 0 ? _context.TextBefore : _context.TextBefore + Typed;

    /// <summary>
    /// The recognizer's progress: <paramref name="finishedPhrases"/>, the raw text of each phrase that ended at a pause (word
    /// list already applied; a phrase never changes once given), and <paramref name="partial"/>, the phrase being spoken.
    /// Returns what to type now and what to show in the pill.
    /// </summary>
    public LiveStep Update(IReadOnlyList<string> finishedPhrases, string partial)
    {
        var type = string.Empty;
        if (!finishedPhrases.SequenceEqual(_finished))
        {
            _finished = [.. finishedPhrases];
            type = TypeFinished();
        }

        return new LiveStep(type, Shape(LiveText.Join(_finished.Skip(TypedPhrases), partial), layout: false, Before));
    }

    /// <summary>
    /// The dictation is over: <paramref name="allPhrases"/> are all its phrases (those given to <see cref="Update"/> and the
    /// last one), so the final text comes from the same phrase texts as what was typed. Returns the text still to type:
    /// the rest of the final text (laid out) after <see cref="Typed"/>, or, if that would change typed text, the phrases
    /// not typed yet formatted on their own (<see cref="FinishedWithoutLayout"/>).
    /// </summary>
    public string Finish(IReadOnlyList<string> allPhrases)
    {
        var final = Prepare(LiveText.Join(allPhrases, string.Empty), layout: true, out var joined);
        string rest;
        if (final.StartsWith(Typed, StringComparison.Ordinal))
        {
            rest = final[Typed.Length..];
            _joinedWithSpace |= Typed.Length == 0 && joined;
        }
        else
        {
            // Typed text stays as it is; a list in the rest is still laid out, on lines of its own.
            FinishedWithoutLayout = true;
            var before = Before;
            rest = Insertion.WithSpacing(before, Shape(LiveText.Join(allPhrases.Skip(TypedPhrases), string.Empty), layout: true, before));
        }

        Typed += rest;
        TypedPhrases = Math.Max(TypedPhrases, allPhrases.Count);
        return rest;
    }

    /// <summary>Types the finished phrases before the last one, once they're final.</summary>
    private string TypeFinished()
    {
        var said = LiveText.Join(_finished, string.Empty);
        if (_holdAll is null)
        {
            // Decided once, from how the dictation starts: an email or a list can only be laid out when it's finished.
            var start = _formatter.Format(said, Insertion.ContinuesSentence(_context.TextBefore), _context.Field.Search);
            if (start.Length == 0)
            {
                return string.Empty;
            }

            _holdAll = _context.Field.MultiLine && _smart && Layout.MayLayOut(start, new LayoutContext(true, AtStart(_context.TextBefore), _context.Field.Email));
        }

        if (_holdAll == true)
        {
            return string.Empty;
        }

        var last = LastSpoken();
        if (last <= TypedPhrases)
        {
            return string.Empty;
        }

        var candidate = Prepare(LiveText.Join(_finished.Take(last), string.Empty), layout: false, out var joined);
        if (candidate.Length <= Typed.Length || !candidate.StartsWith(Typed, StringComparison.Ordinal)
            || !Prepare(said, layout: true, out _).StartsWith(candidate, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var type = candidate[Typed.Length..];
        _joinedWithSpace |= Typed.Length == 0 && joined;
        Typed = candidate;
        TypedPhrases = last;
        return type;
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
        var text = Shape(said, layout, _context.TextBefore);
        var spaced = Insertion.WithSpacing(_context.TextBefore, text);
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
}
