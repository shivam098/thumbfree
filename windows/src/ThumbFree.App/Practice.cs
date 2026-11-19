namespace ThumbFree.App;

/// <summary>Where practice text goes, for smart formatting: the same three facts as ThumbFree.Core's layout context.</summary>
/// <param name="MultiLine">The box takes line breaks (lists and emails need them).</param>
/// <param name="AtStart">The dictation starts the text (needed for email layout).</param>
/// <param name="Email">An email body: a greeting or a sign-off alone is enough for email layout.</param>
internal readonly record struct PracticeField(bool MultiLine, bool AtStart, bool Email);

/// <summary>
/// Smart formatting (emails, lists, corrections) for the guide's practice boxes. The integrator points it at
/// ThumbFree.Core's layout; until then the guide shows the expected results from tests/vectors/layout.tsv.
/// </summary>
internal delegate string LayoutText(string text, PracticeField field);

/// <summary>A practice box in the first-run guide that takes dictation itself.</summary>
internal interface IPracticeTarget
{
    PracticeField Field { get; }

    /// <summary>The words so far while speaking; null clears them. Any thread.</summary>
    void ShowLive(string? text);

    /// <summary>
    /// The finished dictation after the formatter (fillers, spoken punctuation, dictionary, word list). The box lays it
    /// out itself, so text the host already laid out comes through unchanged. Any thread.
    /// </summary>
    void Insert(string text);
}

/// <summary>
/// ThumbFree never types into its own windows, so the first-run guide's practice boxes are reached through this hook.
/// When a dictation starts, the host reads <see cref="Active"/>: if a practice box has the keyboard focus, that
/// dictation goes to it (live words to <see cref="IPracticeTarget.ShowLive"/>, the result to
/// <see cref="IPracticeTarget.Insert"/>) and to nothing else, even if the focus moves meanwhile.
/// </summary>
internal sealed class PracticeHost
{
    private volatile IPracticeTarget? _active;

    /// <summary>Raised on the UI thread when <see cref="Active"/> changes.</summary>
    public event Action? ActiveChanged;

    /// <summary>The practice box with the keyboard focus in the guide, or null. Safe to read from any thread.</summary>
    public IPracticeTarget? Active => _active;

    internal void SetActive(IPracticeTarget? target)
    {
        if (!ReferenceEquals(_active, target))
        {
            _active = target;
            ActiveChanged?.Invoke();
        }
    }
}

/// <summary>The guide's practice sentences and how ThumbFree lays them out (rows of tests/vectors/layout.tsv; a test keeps them in step).</summary>
internal static class PracticeTexts
{
    /// <summary>What to say, as shown in the guide.</summary>
    public const string EmailToSay = "Hey Tim, would love to connect. Are you available on Friday at 3 pm… actually 4 pm? Thanks, Ben.";

    /// <summary>The same sentence as a transcript (the input of the vector).</summary>
    public const string EmailHeard = "Hey Tim, would love to connect. Are you available on Friday at 3 PM... actually 4 PM? Thanks, Ben.";

    /// <summary>layout.tsv, flags "mse": a multi-line email body that the dictation starts.</summary>
    public const string EmailLaidOut = "Hey Tim,\n\nWould love to connect. Are you available on Friday at 4 PM?\n\nThanks,\nBen";

    public const string ListToSay = "Things to do today: order groceries, go on a walk, and call mom.";

    public const string ListHeard = ListToSay;

    /// <summary>layout.tsv, flags "ms": a multi-line box that the dictation starts.</summary>
    public const string ListLaidOut = "Things to do today:\n- Order groceries.\n- Go on a walk.\n- Call mom.";
}
