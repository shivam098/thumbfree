using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ThumbFree.Core;
using DictationLanguage = ThumbFree.Core.Language;

namespace ThumbFree.App.UI;

/// <summary>Home: can I dictate right now (and if not, why), in which language and model, and the last dictation.</summary>
internal sealed class HomePage : UserControl
{
    private readonly AppServices _services;
    private readonly SolidColorBrush _dotColour = Motion.Live(Theme.ReadyColour);
    private readonly TextBlock _status = new() { FontSize = 22, FontFamily = Theme.DisplayFont, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = Theme.Label(string.Empty, 13, Theme.Muted);
    private readonly ProgressBar _progress = Theme.Progress();
    private readonly TextBlock _warning = Theme.Label(string.Empty, 13, Theme.Danger);
    private readonly Button _action;
    private readonly Button _english;
    private readonly Button _hinglish;
    private readonly TextBlock _model = Theme.Label(string.Empty, 13, Theme.Muted);
    private readonly TextBlock _lastText = new() { FontSize = 14, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 20, MaxHeight = 80 };
    private readonly TextBlock _lastCaption = Theme.Label(string.Empty, 12, Theme.Muted);
    private readonly Button _copy;
    private readonly Button _allHistory;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _copied = new() { Interval = TimeSpan.FromSeconds(2) };
    private Action? _onAction;
    private HistoryEntry? _last;

    public HomePage(AppServices services)
    {
        _services = services;
        var stack = new StackPanel();
        stack.Children.Add(Theme.Heading("ThumbFree"));
        var tagline = Theme.Label("Type with your voice, not your thumbs. 100% offline.", 14, Theme.Muted);
        tagline.Margin = new Thickness(0, 0, 0, 20);
        stack.Children.Add(tagline);

        // Can I dictate now?
        _action = Theme.Button(string.Empty, (_, _) => _onAction?.Invoke(), primary: true);
        _action.Margin = new Thickness(16, 0, 0, 0);
        var statusRow = new DockPanel();
        DockPanel.SetDock(_action, Dock.Right);
        statusRow.Children.Add(_action);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = _dotColour, Margin = new Thickness(2, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(_status);
        statusRow.Children.Add(title);
        var status = new StackPanel();
        status.Children.Add(statusRow);
        _hint.Margin = new Thickness(28, 6, 0, 0);
        status.Children.Add(_hint);
        _progress.Margin = new Thickness(28, 12, 0, 2);
        status.Children.Add(_progress);
        _warning.Margin = new Thickness(28, 10, 0, 0);
        status.Children.Add(_warning);
        stack.Children.Add(Theme.CardOf(status, padding: new Thickness(20, 18, 20, 18)));

        // Language and model.
        _english = Theme.Choice("English", (_, _) => SetLanguage(DictationLanguage.English));
        _hinglish = Theme.Choice("Hindi + English", (_, _) => SetLanguage(DictationLanguage.Hinglish));
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10) };
        choices.Children.Add(_english);
        choices.Children.Add(_hinglish);
        var language = new StackPanel();
        var languageHeader = new DockPanel();
        var change = Theme.Link("Change model", (_, _) => _services.OpenPage(AppPage.Settings));
        DockPanel.SetDock(change, Dock.Right);
        languageHeader.Children.Add(change);
        languageHeader.Children.Add(Theme.Title("Language"));
        language.Children.Add(languageHeader);
        language.Children.Add(choices);
        language.Children.Add(_model);
        var shortcut = Theme.Label($"{services.Current.DictationLanguageHotkey} switches between them. Hindi is typed in Roman letters (“kal meeting hai”).", 12, Theme.Muted);
        shortcut.Margin = new Thickness(0, 4, 0, 0);
        language.Children.Add(shortcut);
        stack.Children.Add(Theme.CardOf(language));

        // The last dictation.
        _copy = Theme.Button("Copy", (_, _) => CopyLast());
        _copy.Margin = new Thickness(16, 0, 0, 0);
        var lastHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_copy, Dock.Right);
        lastHeader.Children.Add(_copy);
        lastHeader.Children.Add(Theme.Title("Last dictation"));
        var last = new StackPanel();
        last.Children.Add(lastHeader);
        last.Children.Add(_lastText);
        var lastFooter = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _allHistory = Theme.Link("See all in History", (_, _) => _services.OpenPage(AppPage.History));
        DockPanel.SetDock(_allHistory, Dock.Right);
        lastFooter.Children.Add(_allHistory);
        _lastCaption.VerticalAlignment = VerticalAlignment.Center;
        lastFooter.Children.Add(_lastCaption);
        last.Children.Add(lastFooter);
        stack.Children.Add(Theme.CardOf(last));

        // Everything else.
        var more = new WrapPanel();
        more.Children.Add(Theme.Link("History", (_, _) => _services.OpenPage(AppPage.History)));
        more.Children.Add(Theme.Link("Settings", (_, _) => _services.OpenPage(AppPage.Settings)));
        more.Children.Add(Theme.Link("Help", (_, _) => _services.OpenPage(AppPage.Help)));
        more.Children.Add(Theme.Link("Show the guide again", (_, _) => _services.ShowGuide()));
        var morePanel = new StackPanel();
        morePanel.Children.Add(Theme.Title("More"));
        morePanel.Children.Add(more);
        stack.Children.Add(Theme.CardOf(morePanel));

        var scroll = new ScrollViewer { Content = Theme.Column(stack, 820, new Thickness(32, 28, 32, 24)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Motion.SmoothScroll(scroll);
        Content = scroll;
        Background = Theme.Background;

        services.State.Changed += RefreshStatus;
        services.Models.Changed += _ =>
        {
            RefreshStatus();
            RefreshLanguage();
        };
        services.Settings.Changed += _ =>
        {
            RefreshStatus();
            RefreshLanguage();
            RefreshLast();
        };
        services.History.Changed += RefreshLast;
        _clock.Tick += (_, _) => RefreshLast();
        _copied.Tick += (_, _) =>
        {
            _copied.Stop();
            _copy.Content = "Copy";
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                RefreshLast();
                _clock.Start();
            }
            else
            {
                _clock.Stop();
            }
        };
        RefreshStatus();
        RefreshLanguage();
        RefreshLast();
    }

    /// <summary>The big status, its button and any warning.</summary>
    private void RefreshStatus()
    {
        var state = _services.State;
        var spec = _services.Current.SelectedModel();
        var model = _services.Models.State(spec);
        var size = ModelConsent.Mb(spec.Bytes);
        var name = ModelConsent.ShortName(spec);
        string status, hint;
        Color colour;
        (string Label, bool Primary, Action Run)? action = null;
        if (state.Paused)
        {
            (status, hint, colour) = ("Paused", "Ctrl+Win does nothing until you resume.", Theme.PausedColour);
            action = ("Resume", true, () => _services.SetPaused(false));
        }
        else if (state.Listening)
        {
            (status, hint, colour) = ("Listening…", "Tap Ctrl+Win when you're done, or press Esc to cancel.", Theme.ListeningColour);
        }
        else if (state.Transcribing)
        {
            (status, hint, colour) = ("Transcribing…", "Your words appear where your cursor is in a moment.", Theme.BusyColour);
        }
        else if (model.Status == ModelStatus.Downloading)
        {
            status = $"Downloading the speech model… {(int)(model.Progress * 100)}%";
            hint = $"“{name}”, {size}. It downloads once and then works offline. You can keep working meanwhile.";
            colour = Theme.BusyColour;
            action = ("Cancel", false, () => _services.Models.Cancel(spec));
        }
        else if (model.Status == ModelStatus.NotInstalled)
        {
            status = "Speech model needed";
            hint = model.Error ?? $"Download “{name}” ({size}) to start dictating. It downloads once, then works offline.";
            colour = Theme.BusyColour;
            action = ($"Download ({size})", true, () => ModelActions.Download(_services, spec, Window.GetWindow(this)));
        }
        else if (state.Activity is { } activity)
        {
            (status, hint, colour) = (activity, "One moment…", Theme.BusyColour);
        }
        else
        {
            (status, hint, colour) = ("Ready", "Tap Ctrl+Win to dictate, and tap it again to finish.", Theme.ReadyColour);
            action = ("Pause", false, () => _services.SetPaused(true));
        }

        _status.Text = status;
        _hint.Text = hint;
        _hint.Foreground = model is { Status: ModelStatus.NotInstalled, Error: not null } && !state.Paused ? Theme.Danger : Theme.Muted;
        Motion.ToColor(_dotColour, colour, 220);
        _progress.Visibility = model.Status == ModelStatus.Downloading && !state.Paused ? Visibility.Visible : Visibility.Collapsed;
        Motion.ToValue(_progress, model.Progress);
        _onAction = action?.Run;
        _action.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        if (action is { } a)
        {
            _action.Content = a.Label;
            Theme.SetRestColor(_action, a.Primary ? Theme.Color("#5B4BDB") : Theme.Color("#FFFFFF"));
            _action.Foreground = a.Primary ? Brushes.White : Theme.Text;
            _action.BorderBrush = a.Primary ? Theme.Accent : Theme.Border;
        }

        var warnings = new List<string>();
        if (state.MicrophoneSilent)
        {
            warnings.Add("No sound from the microphone. Is another app or a call using it?");
        }

        if (state.Problem is { } problem)
        {
            warnings.Add($"{state.ProblemTime.LocalDateTime:HH:mm} · {problem.Message}");
        }

        _warning.Text = string.Join("\n", warnings);
        _warning.Foreground = state.MicrophoneSilent || state.Problem?.Kind == StatusKind.Error ? Theme.Danger : Theme.Warning;
        _warning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshLanguage()
    {
        var settings = _services.Current;
        Theme.SetChosen(_english, settings.Language == DictationLanguage.English);
        Theme.SetChosen(_hinglish, settings.Language == DictationLanguage.Hinglish);
        var spec = settings.SelectedModel();
        var where = _services.Models.State(spec).Status == ModelStatus.Installed ? "on this PC" : "not downloaded yet";
        _model.Text = $"Speech model: {ModelActions.TierName(settings.TierFor(settings.Language))} · {spec.Label} · {ModelConsent.Mb(spec.Bytes)}, {where}";
    }

    private void RefreshLast()
    {
        var keep = _services.Current.KeepHistory;
        _last = keep ? _services.History.Entries().FirstOrDefault() : null;
        _copy.Visibility = _last is null ? Visibility.Collapsed : Visibility.Visible;
        _allHistory.Visibility = keep ? Visibility.Visible : Visibility.Collapsed;
        if (_last is { } entry)
        {
            _lastText.Text = Theme.FirstLines(entry.Text, 4);
            _lastText.Foreground = Theme.Text;
            _lastCaption.Text = RelativeTime.Caption(entry, DateTimeOffset.Now);
        }
        else
        {
            _lastText.Text = keep
                ? "Nothing yet. Click in any text box, tap Ctrl+Win and start talking."
                : $"History is off, so dictations aren't kept here. {_services.Current.PasteLastHotkey} still pastes the last one.";
            _lastText.Foreground = Theme.Muted;
            _lastCaption.Text = string.Empty;
        }
    }

    private void CopyLast()
    {
        if (_last is not { } entry)
        {
            return;
        }

        _services.Environment.CopyText(entry.Text);
        _copy.Content = "✓ Copied";
        _copied.Stop();
        _copied.Start();
    }

    /// <summary>Switches the language; a small model that isn't on this PC yet starts downloading (big ones ask from the status card).</summary>
    private void SetLanguage(DictationLanguage language)
    {
        _services.Settings.Update(s => s.Language = language);
        ModelActions.DownloadIfSmall(_services, _services.Current.SelectedModel());
    }
}
