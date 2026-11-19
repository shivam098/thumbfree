using ThumbFree.App.UI;

namespace ThumbFree.App;

/// <summary>
/// Everything the app window, the guide and the tray work with, made once by the app (and with fakes by tests). The
/// dictation host gets what it needs from here too: <see cref="History"/>, <see cref="Samples"/>,
/// <see cref="Notifier"/>, <see cref="Practice"/> and <see cref="Models"/>.
/// </summary>
internal sealed class AppServices
{
    public required SettingsStore Settings { get; init; }

    public required HistoryStore History { get; init; }

    public required SampleStore Samples { get; init; }

    public required ModelDownloads Models { get; init; }

    public required DictationState State { get; init; }

    public required IAppEnvironment Environment { get; init; }

    /// <summary>Notifications through the tray icon; null until the tray exists (and in tests).</summary>
    public Notifier? Notifier { get; set; }

    /// <summary>The guide's practice boxes, which take dictation directly.</summary>
    public PracticeHost Practice { get; } = new();

    /// <summary>
    /// Smart formatting for the guide's practice boxes; the integrator sets it to ThumbFree.Core's layout. While null the
    /// guide shows the expected results from tests/vectors/layout.tsv.
    /// </summary>
    public LayoutText? Layout { get; set; }

    /// <summary>Pauses or resumes the Ctrl+Win keys.</summary>
    public Action<bool> SetPaused { get; set; } = _ => { };

    public Action ShowGuide { get; set; } = () => { };

    public Action<AppPage> OpenPage { get; set; } = _ => { };

    public string AppVersion { get; init; } = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0";

    public AppSettings Current => Settings.Current;
}
