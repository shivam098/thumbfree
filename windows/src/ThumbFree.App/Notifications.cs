namespace ThumbFree.App;

/// <summary>Where ThumbFree's notifications appear: the tray icon's balloon, which Windows shows as a notification.</summary>
internal interface INotificationSurface
{
    /// <summary>Raised when the person clicks a notification, with the tag it was shown with.</summary>
    event Action<object?>? Clicked;

    void Show(string title, string text, bool warning, object? tag);

    /// <summary>Takes the notification shown last off the screen and out of the notification centre.</summary>
    void Hide();
}

/// <summary>
/// "Never speak into the void, never lose a dictation" (PLAN 3.6) on Windows. The dictation host calls these from any
/// thread: text that couldn't be typed is kept (and never typed anywhere else) until the person copies it from the
/// notification or the tray, and a microphone that sends only silence is reported until sound comes back.
/// </summary>
internal sealed class Notifier
{
    private static readonly object UntypedTag = new();
    private static readonly object SilentTag = new();

    private readonly INotificationSurface _surface;
    private readonly DictationState _state;
    private readonly Action<string> _copy;
    private readonly SynchronizationContext? _context;
    private object? _shown;

    /// <param name="copy">Puts text on the clipboard (on the UI thread).</param>
    public Notifier(INotificationSurface surface, DictationState state, Action<string> copy)
    {
        _surface = surface;
        _state = state;
        _copy = copy;
        _context = SynchronizationContext.Current;
        surface.Clicked += tag =>
        {
            if (ReferenceEquals(tag, UntypedTag))
            {
                CopyUntyped();
            }
        };
    }

    /// <summary>The latest dictation that couldn't be typed, until it's copied (the tray offers it too).</summary>
    public string? UntypedText { get; private set; }

    /// <summary>
    /// A dictation couldn't be typed: the text box closed before the text was ready, or the app didn't take it. The text
    /// stays here and in a notification; clicking the notification copies it.
    /// </summary>
    /// <param name="reason">One sentence; by default "The text box closed before your text was ready."</param>
    public void ShowUntyped(string text, string? reason = null) => Post(() =>
    {
        UntypedText = text;
        Show("Your dictation is safe", $"{reason ?? "The text box closed before your text was ready."} Click to copy it.", warning: true, UntypedTag);
    });

    /// <summary>The microphone sends exact silence (a call or another app took it). Shown until <see cref="ClearMicrophoneSilent"/>.</summary>
    public void ShowMicrophoneSilent() => Post(() =>
    {
        if (!_state.MicrophoneSilent)
        {
            _state.SetMicrophoneSilent(true);
            Show("ThumbFree can't hear you", "No sound from the microphone. Is another app or a call using it?", warning: true, SilentTag);
        }
    });

    /// <summary>Sound came back (or the dictation ended): the warning goes, from the screen and the notification centre.</summary>
    public void ClearMicrophoneSilent() => Post(() =>
    {
        if (!_state.MicrophoneSilent)
        {
            return;
        }

        _state.SetMicrophoneSilent(false);
        if (ReferenceEquals(_shown, SilentTag))
        {
            _surface.Hide();
            _shown = null;
        }
    });

    /// <summary>The system took the microphone mid-dictation: what was said so far is being written.</summary>
    public void ShowInterrupted() => Post(() =>
        Show("Dictation stopped", "Another app took the microphone. What you said so far is being written.", warning: true, null));

    /// <summary>An example from the first-run guide, so people see where these appear (only when they ask).</summary>
    public void ShowExample() => Post(() =>
        Show("This is how ThumbFree tells you", "If the microphone goes quiet or your words can't be typed, you'll see it here.", warning: false, null));

    /// <summary>Copies <see cref="UntypedText"/> (from the notification or the tray). False when there's nothing to copy.</summary>
    public bool CopyUntyped()
    {
        if (UntypedText is not { } text)
        {
            return false;
        }

        _copy(text);
        UntypedText = null;
        Show("Copied", "Paste your dictation with Ctrl+V.", warning: false, null);
        return true;
    }

    private void Show(string title, string text, bool warning, object? tag)
    {
        _shown = tag;
        _surface.Show(title, text, warning, tag);
    }

    private void Post(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
