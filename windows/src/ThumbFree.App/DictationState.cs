using ThumbFree.Core;

namespace ThumbFree.App;

/// <summary>
/// What the app window and the tray show about dictation right now: paused, listening, transcribing or loading, the
/// microphone gone silent, and the latest problem. The app feeds it from the dictation host's events, on the UI thread.
/// </summary>
internal sealed class DictationState
{
    /// <summary>Raised on the UI thread after any change.</summary>
    public event Action? Changed;

    /// <summary>Ctrl+Win is paused (from the tray or Home).</summary>
    public bool Paused { get; private set; }

    /// <summary>
    /// <see cref="DictationLabels.Listening"/>, <see cref="DictationLabels.Transcribing"/>, or another short label
    /// such as "Loading speech model…"; null while idle.
    /// </summary>
    public string? Activity { get; private set; }

    /// <summary>The microphone sends only silence: a call or another app has it (see <see cref="Notifier"/>).</summary>
    public bool MicrophoneSilent { get; private set; }

    /// <summary>The latest warning or error, until a dictation works again.</summary>
    public StatusUpdate? Problem { get; private set; }

    /// <summary>When <see cref="Problem"/> happened.</summary>
    public DateTimeOffset ProblemTime { get; private set; }

    public bool Listening => Activity == DictationLabels.Listening;

    public bool Transcribing => Activity == DictationLabels.Transcribing;

    public void SetPaused(bool paused)
    {
        if (Paused != paused)
        {
            Paused = paused;
            Changed?.Invoke();
        }
    }

    public void SetActivity(string? activity)
    {
        if (Activity != activity)
        {
            Activity = activity;
            Changed?.Invoke();
        }
    }

    public void SetMicrophoneSilent(bool silent)
    {
        if (MicrophoneSilent != silent)
        {
            MicrophoneSilent = silent;
            Changed?.Invoke();
        }
    }

    /// <summary>A status message from the host: warnings and errors are kept until a later success.</summary>
    public void Report(StatusUpdate update)
    {
        switch (update.Kind)
        {
            case StatusKind.Warning or StatusKind.Error:
                Problem = update;
                ProblemTime = DateTimeOffset.Now;
                Changed?.Invoke();
                break;
            case StatusKind.Success when Problem is not null:
                Problem = null;
                Changed?.Invoke();
                break;
        }
    }
}
