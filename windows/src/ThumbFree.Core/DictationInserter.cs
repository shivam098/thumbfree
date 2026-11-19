namespace ThumbFree.Core;

/// <summary>A top-level window identity.</summary>
public sealed record WindowInfo(nint Handle, int ProcessId, string ProcessName, string Title);

public enum FieldCheck
{
    /// <summary>Positively confirmed not to be a password field.</summary>
    Safe,
    Password,

    /// <summary>The foreground window runs as administrator; Windows silently drops synthesized input to it.</summary>
    Elevated,

    /// <summary>Accessibility query failed or timed out.</summary>
    Unknown,
}

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record StatusUpdate(StatusKind Kind, string Message, string? Heard = null);

/// <summary>Operating-system side effects needed to insert text. Implemented with Win32/UI Automation; faked in tests.</summary>
public interface IDesktop
{
    WindowInfo? GetForegroundWindow();

    FieldCheck CheckFocusedField();

    /// <summary>True if the focused text field already ends in a non-space character (so a leading space is needed); null if unknown.</summary>
    bool? FocusedTextEndsWithNonWhitespace();

    void TypeText(string text);

    void PressKeys(string chord);
}

/// <summary>
/// Formats dictated speech and inserts it where the cursor is. The key press that started dictation is the user's
/// consent, so only password fields and administrator windows are refused (see docs/specs in windows-voice-assistant).
/// Spacing, continuing a sentence and the field rules are the ones every platform shares (<see cref="Insertion"/>,
/// <see cref="LiveTyping"/>); typing, line breaks as each app's new-line keys and the refusals are Windows' own.
/// </summary>
public sealed class DictationInserter
{
    public const string DefaultNewLineKeys = "Shift+Enter";

    private const string PasswordRefusal = "That's a password field — I won't type there.";

    private readonly IDesktop _desktop;
    private readonly DictationFormatter _formatter;
    private readonly IReadOnlyDictionary<string, string> _newLineKeys;

    /// <param name="newLineKeysByProcess">
    /// Keys for a line break per process name (editors: Enter). Others get Shift+Enter, which chat apps treat as a new
    /// line instead of “send”.
    /// </param>
    public DictationInserter(IDesktop desktop, DictationFormatter formatter, IReadOnlyDictionary<string, string>? newLineKeysByProcess = null)
    {
        _desktop = desktop;
        _formatter = formatter;
        _newLineKeys = new Dictionary<string, string>(newLineKeysByProcess ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    public event Action<StatusUpdate>? Status;

    /// <summary>
    /// The last dictation, for “paste last dictation”, without the space that joined it to the text before. Kept only in
    /// memory. After a dictation typed live, set it to <see cref="LiveTyping.Text"/>.
    /// </summary>
    public string? LastText { get; set; }

    /// <summary>Formats the transcript and inserts it at the cursor. Returns false if nothing was inserted.</summary>
    /// <param name="transcript">What was said (word list already applied).</param>
    /// <param name="context">
    /// The field and the text before the cursor, as found when dictation started. With it the whole shared pipeline runs,
    /// exactly as <see cref="LiveTyping"/> finishes a dictation that wasn't typed live: continuing the sentence, smart
    /// formatting, the field's rules (no line breaks in single-line boxes, no full stop in search boxes) and spacing.
    /// Without it only the formatter runs and the field is treated as multi-line.
    /// </param>
    public bool Insert(string transcript, LiveTypingContext? context = null)
    {
        var heard = TranscriptText.Clean(transcript);
        if (context?.Field.Password == true)
        {
            Report(StatusKind.Warning, PasswordRefusal, heard);
            return false;
        }

        string text;
        string remembered;
        if (context is null)
        {
            text = remembered = _formatter.Format(transcript);
        }
        else
        {
            var typing = new LiveTyping(_formatter, context with { Live = false });
            text = typing.Finish([transcript]);
            remembered = typing.Text;
        }

        if (text.Length == 0)
        {
            Report(StatusKind.Info, "I didn't catch anything to type.");
            return false;
        }

        if (!Type(text, addSpace: context?.TextBefore is null, heard))
        {
            return false;
        }

        LastText = remembered;
        Report(StatusKind.Success, "Typed.", heard);
        return true;
    }

    /// <summary>
    /// Types text that is already final, such as a piece from <see cref="LiveTyping"/> (formatted and spaced): line breaks
    /// become the app's new-line keys. The same refusals as <see cref="Insert"/>, also when <paramref name="field"/> says
    /// it's a password field, but only problems are reported, so the pill isn't flooded while speaking. Returns false if
    /// nothing was typed.
    /// </summary>
    public bool TypePrepared(string text, FieldInfo? field = null)
    {
        if (field?.Password == true)
        {
            Report(StatusKind.Warning, PasswordRefusal);
            return false;
        }

        return text.Length > 0 && Type(text, addSpace: false, heard: null);
    }

    /// <summary>Inserts the last dictated text again.</summary>
    public bool RepeatLast()
    {
        if (string.IsNullOrEmpty(LastText))
        {
            Report(StatusKind.Info, "There's no previous dictation yet.");
            return false;
        }

        if (!Type(LastText, addSpace: true, heard: null))
        {
            return false;
        }

        Report(StatusKind.Success, "Typed.");
        return true;
    }

    private bool Type(string text, bool addSpace, string? heard)
    {
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window to type into.", heard);
            return false;
        }

        var refusal = _desktop.CheckFocusedField() switch
        {
            FieldCheck.Password => PasswordRefusal,
            FieldCheck.Elevated => "That window runs as administrator; Windows won't let me type into it.",
            _ => null,
        };
        if (refusal is not null)
        {
            Report(StatusKind.Warning, refusal, heard);
            return false;
        }

        // Only whether the field ends in a word is known here: a space goes before anything that doesn't attach to it.
        if (addSpace && _desktop.FocusedTextEndsWithNonWhitespace() == true && !Insertion.JoinsDirectly(text))
        {
            text = " " + text;
        }

        var newLine = _newLineKeys.GetValueOrDefault(window.ProcessName, DefaultNewLineKeys);
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                _desktop.PressKeys(newLine);
            }

            if (parts[i].Length > 0)
            {
                _desktop.TypeText(parts[i]);
            }
        }

        return true;
    }

    private void Report(StatusKind kind, string message, string? heard = null) => Status?.Invoke(new StatusUpdate(kind, message, heard));
}
