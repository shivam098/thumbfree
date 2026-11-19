using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThumbFree.Core;
using DictationLanguage = ThumbFree.Core.Language;

namespace ThumbFree.App.UI;

internal enum GuideStep
{
    Welcome,
    Microphone,
    Model,
    TryIt,
    Email,
    List,
    Notifications,
    Profession,
    Data,
}

/// <summary>
/// The first-run guide (PLAN 3.7, for Windows): why ThumbFree is different, the microphone, the speech model, a first
/// dictation, an email and a list to practise, notifications, "What do you do?" and the data choice. Every step can be
/// skipped and changed later in Settings. It opens until it's been finished, skipped or closed once; Home, Settings and
/// the tray offer it again.
/// </summary>
internal sealed class GuideWindow : Window
{
    /// <param name="finished">Runs after Finish or Skip (the app opens its window then).</param>
    /// <param name="workArea">The screen's work area to fit in (tests); by default the screen's own.</param>
    public GuideWindow(AppServices services, Action finished, Rect? workArea = null)
    {
        Title = "Welcome to ThumbFree";
        WindowFit.Apply(this, new Size(860, 740), new Size(700, 600), workArea);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Card;
        FontFamily = Theme.Font;
        Icon = Theme.AppIcon();
        UseLayoutRounding = true;
        View = new GuideView(services, () =>
        {
            Close();
            finished();
        });
        Content = View;

        // Back from Windows' microphone or notification settings: show what changed.
        Activated += (_, _) => View.Recheck();
        Closed += (_, _) =>
        {
            View.Detach();
            if (!services.Current.GuideDone)
            {
                services.Settings.Update(s => s.GuideDone = true);
            }
        };
    }

    public GuideView View { get; }
}

/// <summary>The guide's content (its own control so tests can draw every step offscreen).</summary>
internal sealed class GuideView : UserControl
{
    private static readonly GuideStep[] Steps = Enum.GetValues<GuideStep>();

    private readonly AppServices _services;
    private readonly Action _done;
    private readonly Grid _progress = new() { Height = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _content = new() { MaxWidth = 680, Margin = new Thickness(48, 8, 48, 24) };
    private readonly ScrollViewer _scroll;
    private readonly Button _back;
    private readonly Button _secondary;
    private readonly Button _primary;
    private readonly List<PracticeBox> _boxes = [];
    private Action? _onPrimary;
    private Action? _onSecondary;
    private ProgressBar? _downloadBar;
    private TextBlock? _downloadText;
    private TextBlock? _practiceStatus;
    private ModelSpec? _shownSpec;
    private ModelStatus? _shownModelStatus;
    private string _addedWords = string.Empty;

    public GuideView(AppServices services, Action done)
    {
        _services = services;
        _done = done;

        for (var i = 0; i < Steps.Length; i++)
        {
            _progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var skip = Theme.Link("Skip", (_, _) => Finish());
        skip.Margin = new Thickness(16, 0, 0, 0);
        skip.Foreground = Theme.Muted;
        var top = new DockPanel { Margin = new Thickness(48, 22, 40, 8) };
        DockPanel.SetDock(skip, Dock.Right);
        top.Children.Add(skip);
        top.Children.Add(_progress);

        _back = Theme.Button("Back", (_, _) => Go(Steps[Math.Max(0, Array.IndexOf(Steps, Step) - 1)]));
        _secondary = Theme.Button(string.Empty, (_, _) => _onSecondary?.Invoke());
        _primary = Theme.Button(string.Empty, (_, _) => _onPrimary?.Invoke(), primary: true);
        _primary.Padding = new Thickness(22, 8, 22, 8);
        _primary.Margin = new Thickness(0);
        _primary.IsDefault = true;
        _secondary.Padding = new Thickness(18, 8, 18, 8);
        _back.Padding = new Thickness(18, 8, 18, 8);
        var buttons = new DockPanel { Margin = new Thickness(48, 12, 48, 24), LastChildFill = false };
        DockPanel.SetDock(_back, Dock.Left);
        DockPanel.SetDock(_primary, Dock.Right);
        DockPanel.SetDock(_secondary, Dock.Right);
        buttons.Children.Add(_back);
        buttons.Children.Add(_primary);
        buttons.Children.Add(_secondary);

        _scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Motion.SmoothScroll(_scroll);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(_scroll);
        Content = root;
        Background = Theme.Card;

        services.Models.Changed += OnModelsChanged;
        services.State.Changed += OnStateChanged;
        Render();
    }

    public GuideStep Step { get; private set; } = GuideStep.Welcome;

    /// <summary>The practice boxes on the current step (tests simulate dictation into them).</summary>
    internal IReadOnlyList<PracticeBox> PracticeBoxes => _boxes;

    public void Go(GuideStep step)
    {
        Step = step;
        Render();
        _scroll.ScrollToTop();
        Motion.FadeIn(_content, 180, rise: 8);
    }

    /// <summary>Re-reads what Windows says (microphone, notifications) when the person comes back to the guide.</summary>
    public void Recheck()
    {
        if (Step is GuideStep.Microphone or GuideStep.Notifications)
        {
            Render();
        }
    }

    /// <summary>Stops listening to the app (the window closed).</summary>
    public void Detach()
    {
        _services.Models.Changed -= OnModelsChanged;
        _services.State.Changed -= OnStateChanged;
        ReleaseBoxes();
    }

    private AppSettings Current => _services.Current;

    private void Next()
    {
        var i = Array.IndexOf(Steps, Step);
        if (i + 1 < Steps.Length)
        {
            Go(Steps[i + 1]);
        }
        else
        {
            Finish();
        }
    }

    private void Finish()
    {
        _services.Settings.Update(s => s.GuideDone = true);
        Detach();
        _done();
    }

    private void Render()
    {
        ReleaseBoxes();
        _downloadBar = null;
        _downloadText = null;
        _practiceStatus = null;
        _shownSpec = null;
        _shownModelStatus = null;
        var at = Array.IndexOf(Steps, Step);
        _progress.Children.Clear();
        for (var i = 0; i < Steps.Length; i++)
        {
            var segment = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = i <= at ? Theme.Accent : Theme.Track,
                Margin = new Thickness(0, 0, i < Steps.Length - 1 ? 4 : 0, 0),
            };
            Grid.SetColumn(segment, i);
            _progress.Children.Add(segment);
        }

        _content.Children.Clear();
        _back.Visibility = at > 0 ? Visibility.Visible : Visibility.Hidden;
        Secondary(null, null);
        switch (Step)
        {
            case GuideStep.Welcome:
                Welcome();
                break;
            case GuideStep.Microphone:
                Microphone();
                break;
            case GuideStep.Model:
                Model();
                break;
            case GuideStep.TryIt:
                TryIt();
                break;
            case GuideStep.Email:
                Practice(
                    "Now try writing an email",
                    PracticeTexts.EmailToSay,
                    PracticeTexts.EmailHeard,
                    PracticeTexts.EmailLaidOut,
                    email: true,
                    success: t => t.Contains("\n\n", StringComparison.Ordinal) && t.Contains('4'),
                    well: "Well done. ThumbFree laid out your email and kept only your correction.");
                break;
            case GuideStep.List:
                Practice(
                    "Now make a list",
                    PracticeTexts.ListToSay,
                    PracticeTexts.ListHeard,
                    PracticeTexts.ListLaidOut,
                    email: false,
                    success: t => t.Contains("\n- ", StringComparison.Ordinal),
                    well: "Great. ThumbFree made the list for you.");
                break;
            case GuideStep.Notifications:
                Notifications();
                break;
            case GuideStep.Profession:
                Profession();
                break;
            case GuideStep.Data:
                Data();
                break;
        }
    }

    // ---- steps ----

    private void Welcome()
    {
        _content.Children.Add(new Image { Source = Theme.Asset("logo-256.png"), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) });
        Title("Type with your voice.\nNo internet needed.");
        Line("ThumbFree turns speech into clean, punctuated text in any app, entirely on this PC.");
        Point("\uE72E", "Your voice never leaves this PC", "Speech is recognised right here. No account, no cloud, no recordings sent anywhere.");
        Point("\uE709", "Works offline", "On a plane or with no Wi-Fi: it's just as fast.");
        Point("\uE765", "Tap, talk, tap", "Tap Ctrl+Win and speak, watching your words appear. Tap it again and they're typed. Nothing to hold.");
        Point("\uE70F", "Writes like you meant to", "No ums or “thik hai na”, punctuation added, emails and lists laid out for you.");
        Point("\uE774", "English and Hinglish", "Hindi + English, typed in Roman letters.");
        Primary("Get started", Next);
    }

    private void Microphone()
    {
        Title("Let ThumbFree hear you");
        Line("The microphone is on only while you dictate: from one tap of Ctrl+Win to the next. Audio stays in memory and is turned into text on this PC.");
        var access = _services.Environment.CheckMicrophone();
        if (access == MicrophoneAccess.Allowed)
        {
            Done("Windows lets ThumbFree use the microphone.");
            Primary("Next", Next);
            return;
        }

        var (problem, fix) = access switch
        {
            MicrophoneAccess.NoMicrophone => ("No microphone found.", "Plug one in or turn it on, then check again."),
            MicrophoneAccess.BlockedForAllApps => ("Microphone access is off in Windows.", "Turn on “Microphone access” and “Let desktop apps access your microphone”."),
            _ => ("Windows is blocking desktop apps from the microphone.", "Turn on “Let desktop apps access your microphone”."),
        };
        Problem(problem);
        Line(fix, muted: true);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (access != MicrophoneAccess.NoMicrophone)
        {
            actions.Children.Add(Theme.Button("Open microphone settings", (_, _) => _services.Environment.Open("ms-settings:privacy-microphone"), primary: true));
        }

        actions.Children.Add(Theme.Button("Check again", (_, _) => Render()));
        _content.Children.Add(actions);
        Primary("Next", Next);
        Secondary("Not now", Next);
    }

    private void Model()
    {
        Title("Download the speech model");
        Line("This is what makes ThumbFree work offline. It downloads once, then never needs the internet again.");
        var english = Theme.Choice("English", (_, _) => ChooseLanguage(DictationLanguage.English));
        var hinglish = Theme.Choice("Hindi + English", (_, _) => ChooseLanguage(DictationLanguage.Hinglish));
        Theme.SetChosen(english, Current.Language == DictationLanguage.English);
        Theme.SetChosen(hinglish, Current.Language == DictationLanguage.Hinglish);
        var languages = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 10) };
        languages.Children.Add(english);
        languages.Children.Add(hinglish);
        _content.Children.Add(languages);
        if (Current.Language == DictationLanguage.English)
        {
            var live = Theme.Choice("Live", (_, _) => ChooseEnglishTier(Tier.Live));
            var standard = Theme.Choice("Standard", (_, _) => ChooseEnglishTier(Tier.Standard));
            Theme.SetChosen(live, Current.EnglishTier == Tier.Live);
            Theme.SetChosen(standard, Current.EnglishTier == Tier.Standard);
            var tiers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            tiers.Children.Add(live);
            tiers.Children.Add(standard);
            _content.Children.Add(tiers);
            Line(
                Current.EnglishTier switch
                {
                    Tier.Live => "Words appear as you speak.",
                    Tier.Standard => "Types when you finish; a little more accurate.",
                    _ => "Accurate is chosen in Settings: the fewest mistakes, but slower.",
                },
                muted: true);
        }
        else
        {
            Line("Hindi words are typed in Roman letters, English words stay English. A more accurate (much bigger) model is in Settings.", muted: true);
        }

        var spec = Current.SelectedModel();
        _shownSpec = spec;
        var state = _services.Models.State(spec);
        _shownModelStatus = state.Status;
        var size = ModelConsent.Mb(spec.Bytes);
        switch (state.Status)
        {
            case ModelStatus.Installed:
                Done($"“{ModelConsent.ShortName(spec)}” is ready.");
                Primary("Next", Next);
                break;
            case ModelStatus.Downloading:
                _downloadText = Line(string.Empty);
                _downloadBar = Theme.Progress();
                _downloadBar.Margin = new Thickness(0, 8, 0, 8);
                _content.Children.Add(_downloadBar);
                Line("You can carry on while it downloads.", muted: true);
                UpdateDownload(spec);
                Primary("Continue", Next);
                break;
            default:
                var note = $"{size}. Wi-Fi or an unmetered connection is best.";
                if (_services.Environment.IsMeteredConnection())
                {
                    note += " You're on a metered connection now.";
                }

                Line(note, muted: true).Margin = new Thickness(0, 12, 0, 0);
                if (state.Error is { } error)
                {
                    Problem(error);
                }

                Primary($"Download ({size})", () => ModelActions.Download(_services, spec, Window.GetWindow(this)));
                Secondary("Later", Next);
                break;
        }
    }

    private void TryIt()
    {
        Title("Try it");
        Line("Click in the box below. Tap Ctrl+Win, say anything you like, then tap Ctrl+Win again.");
        var result = Theme.Label(string.Empty, 14, Theme.Ok);
        result.FontWeight = FontWeights.SemiBold;
        result.Margin = new Thickness(0, 10, 0, 0);
        PracticeBox? box = null;
        box = new PracticeBox(_services, email: false, "Your words appear here", lines: 4, () =>
            result.Text = box!.Text.Trim().Length > 0 ? "✓ That's it. It works the same way in any app." : string.Empty);
        box.View.Margin = new Thickness(0, 16, 0, 0);
        _content.Children.Add(box.View);
        _boxes.Add(box);
        _content.Children.Add(result);
        PracticeStatus();
        Primary("Next", Next);
        _content.Dispatcher.BeginInvoke(box.Focus, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// A practice round: say <paramref name="toSay"/> into a box that asks for a layout, and compare with what ThumbFree
    /// writes (from <see cref="AppServices.Layout"/> when wired, else the vector's expected text).
    /// </summary>
    private void Practice(string heading, string toSay, string heard, string expected, bool email, Func<string, bool> success, string well)
    {
        Title(heading);
        Line("Click in the box, tap Ctrl+Win, say this, then tap Ctrl+Win again:", muted: true);
        var quote = Line($"“{toSay}”");
        quote.FontStyle = FontStyles.Italic;
        quote.FontSize = 16;

        var result = Theme.Label(string.Empty, 14, Theme.Ok);
        result.FontWeight = FontWeights.SemiBold;
        result.Margin = new Thickness(0, 10, 0, 0);
        PracticeBox? box = null;
        box = new PracticeBox(_services, email, email ? "Email" : "Notes", lines: 7, () =>
            result.Text = success(box!.Text.Replace("\r\n", "\n", StringComparison.Ordinal)) ? "✓ " + well : string.Empty);
        _boxes.Add(box);

        var preview = Theme.Label(Expected(heard, expected, email), 14);
        preview.LineHeight = 20;
        var previewCard = new Border
        {
            Background = Theme.Hex("#F5F3FF"),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 12),
            Child = preview,
        };

        var columns = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var yours = Captioned(email ? "Your email" : "Your notes", box.View);
        columns.Children.Add(yours);
        var theirs = Captioned("ThumbFree writes", previewCard);
        Grid.SetColumn(theirs, 2);
        columns.Children.Add(theirs);
        _content.Children.Add(columns);
        _content.Children.Add(result);
        if (!Current.SmartFormatting)
        {
            var off = Problem("Smart formatting is off in Settings, so emails and lists aren't laid out.");
            off.Foreground = Theme.Warning;
            var turnOn = Theme.Link("Turn it on", (_, _) =>
            {
                _services.Settings.Update(s => s.SmartFormatting = true);
                Render();
            });
            turnOn.HorizontalAlignment = HorizontalAlignment.Left;
            _content.Children.Add(turnOn);
        }

        PracticeStatus();
        Primary("Next", Next);
        _content.Dispatcher.BeginInvoke(box.Focus, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Notifications()
    {
        Title("Don't speak into the void");
        Line(
            "If a call or another app takes the microphone while you're talking, ThumbFree tells you straight away. And if a " +
            "text box closes before your words are typed, they're kept in a notification for you to copy, so nothing you said is lost.");
        if (_services.Environment.NotificationsEnabled())
        {
            Done("Windows notifications are on.");
        }
        else
        {
            Problem("Windows notifications are off, so you'd miss these.");
            var open = Theme.Button("Open notification settings", (_, _) => _services.Environment.Open("ms-settings:notifications"), primary: true);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Margin = new Thickness(0, 12, 0, 0);
            _content.Children.Add(open);
        }

        if (_services.Notifier is { } notifier)
        {
            var example = Theme.Link("Show me an example", (_, _) => notifier.ShowExample());
            example.HorizontalAlignment = HorizontalAlignment.Left;
            example.Margin = new Thickness(-6, 12, 0, 0);
            _content.Children.Add(example);
        }

        Primary("Next", Next);
        Secondary("Skip", Next);
    }

    private void Profession()
    {
        Title("What do you do?");
        Line(
            "Optional. ThumbFree adds your field's names and jargon to “Words I use often”, so they're spelled right from the " +
            "start. This stays on your PC; you can edit the list in Settings.",
            muted: true);
        var roles = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (role, _) in Professions.All)
        {
            var chip = Theme.Choice(role, (_, _) => ChooseProfession(role));
            chip.Margin = new Thickness(0, 0, 8, 8);
            Theme.SetChosen(chip, Current.Profession == role);
            roles.Children.Add(chip);
        }

        _content.Children.Add(roles);
        if (_addedWords.Length > 0)
        {
            Done(_addedWords);
        }

        Primary("Next", Next);
        Secondary("Skip", Next);
    }

    private void Data()
    {
        Title("Your voice stays yours");
        Line("Choose how ThumbFree handles your recordings.", muted: true);
        _content.Children.Add(ChoiceCard(
            "Keep everything private 🔒",
            "Recordings are never kept. Nothing is ever sent anywhere.",
            !Current.SaveSamples,
            () => _services.Settings.Update(s => s.SaveSamples = false)));
        _content.Children.Add(ChoiceCard(
            "Help improve ThumbFree",
            "Keep my recordings, what was recognised and the typed text on this PC, so I can choose to send them to the " +
            "ThumbFree team. Never from password fields or web browsers (a browser can't say whether a window is private). " +
            "Nothing is sent automatically, and I can delete them any time in Settings.",
            Current.SaveSamples,
            () => _services.Settings.Update(s => s.SaveSamples = true)));
        Primary("Finish", Finish);
    }

    // ---- actions ----

    private void ChooseLanguage(DictationLanguage language)
    {
        _services.Settings.Update(s => s.Language = language);
        Render();
    }

    private void ChooseEnglishTier(Tier tier)
    {
        _services.Settings.Update(s => s.EnglishTier = tier);
        Render();
    }

    private void ChooseProfession(string role)
    {
        var before = Current.Vocabulary.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var after = Professions.AddTo(before, role);
        var existing = before.Length == 0 ? 0 : before.Split('\n').Length;
        var added = after.Split('\n').Skip(existing).Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
        _services.Settings.Update(s =>
        {
            s.Vocabulary = after;
            s.Profession = role;
        });
        _addedWords = added.Count switch
        {
            0 => role == "Other" ? string.Empty : "Those words are already in your list.",
            <= 6 => "Added " + string.Join(", ", added) + ".",
            _ => "Added " + string.Join(", ", added.Take(6)) + $" and {added.Count - 6} more.",
        };
        Render();
    }

    private void OnModelsChanged(ModelSpec spec)
    {
        if (Step == GuideStep.Model)
        {
            if (spec != _shownSpec)
            {
                return;
            }

            if (_services.Models.State(spec).Status != _shownModelStatus)
            {
                Render();
            }
            else
            {
                UpdateDownload(spec);
            }
        }
        else if (Step is GuideStep.TryIt or GuideStep.Email or GuideStep.List)
        {
            UpdatePracticeStatus();
        }
    }

    private void OnStateChanged()
    {
        if (Step is GuideStep.TryIt or GuideStep.Email or GuideStep.List)
        {
            UpdatePracticeStatus();
        }
    }

    private void UpdateDownload(ModelSpec spec)
    {
        var state = _services.Models.State(spec);
        if (_downloadText is not null)
        {
            _downloadText.Text = $"Downloading “{ModelConsent.ShortName(spec)}”: {ModelConsent.Mb((long)(spec.Bytes * state.Progress))} of {ModelConsent.Mb(spec.Bytes)}…";
        }

        if (_downloadBar is not null)
        {
            Motion.ToValue(_downloadBar, state.Progress);
        }
    }

    /// <summary>A line under the practice box that says whether dictation can work yet.</summary>
    private void PracticeStatus()
    {
        _practiceStatus = Theme.Label(string.Empty, 13, Theme.Muted);
        _practiceStatus.Margin = new Thickness(0, 10, 0, 0);
        _content.Children.Add(_practiceStatus);
        UpdatePracticeStatus();
    }

    private void UpdatePracticeStatus()
    {
        if (_practiceStatus is null)
        {
            return;
        }

        var state = _services.State;
        var model = _services.Models.State(Current.SelectedModel());
        (_practiceStatus.Text, _practiceStatus.Foreground) = state switch
        {
            { Paused: true } => ("Ctrl+Win is paused. Resume it from the tray icon or Home.", Theme.Warning),
            { Listening: true } => ("Listening… tap Ctrl+Win when you're done.", Theme.Danger),
            { Transcribing: true } => ("Transcribing…", Theme.Muted),
            _ when model.Status == ModelStatus.Downloading => ($"The speech model is downloading ({(int)(model.Progress * 100)}%). Try this once it's ready.", Theme.Warning),
            _ when model.Status == ModelStatus.NotInstalled => ("The speech model isn't downloaded yet, so this can't work yet. Go back a few steps to download it.", Theme.Warning),
            _ => (string.Empty, Theme.Muted),
        };
        _practiceStatus.Visibility = _practiceStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The laid-out text for the preview: Layout on the formatted sentence when it's wired, else the vector's expected result.</summary>
    private string Expected(string heard, string expected, bool email)
    {
        if (_services.Layout is not { } layout)
        {
            return expected;
        }

        var formatted = new DictationFormatter().Format(heard);
        return layout(formatted, new PracticeField(MultiLine: true, AtStart: true, Email: email));
    }

    private void ReleaseBoxes()
    {
        foreach (var box in _boxes)
        {
            box.Release();
        }

        _boxes.Clear();
    }

    // ---- pieces ----

    private void Primary(string label, Action action)
    {
        _primary.Content = label;
        _onPrimary = action;
    }

    private void Secondary(string? label, Action? action)
    {
        _secondary.Content = label;
        _onSecondary = action;
        _secondary.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Title(string text) => _content.Children.Add(new TextBlock
    {
        Text = text,
        FontFamily = Theme.TitleFont,
        FontSize = 32,
        Foreground = Theme.Text,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 40,
        Margin = new Thickness(0, 20, 0, 12),
    });

    private TextBlock Line(string text, bool muted = false)
    {
        var line = Theme.Label(text, 15, muted ? Theme.Muted : Theme.Text);
        line.LineHeight = 22;
        line.Margin = new Thickness(0, 4, 0, 4);
        _content.Children.Add(line);
        return line;
    }

    private void Point(string glyph, string head, string body)
    {
        var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = Theme.AccentSoft,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 16, Foreground = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        row.Children.Add(icon);
        var texts = new StackPanel();
        var title = Theme.Label(head, 16);
        title.FontWeight = FontWeights.SemiBold;
        texts.Children.Add(title);
        var detail = Theme.Label(body, 14, Theme.Muted);
        detail.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(detail);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        _content.Children.Add(row);
    }

    private void Done(string text)
    {
        var line = Line("✓  " + text);
        line.Foreground = Theme.Ok;
        line.FontWeight = FontWeights.SemiBold;
        line.Margin = new Thickness(0, 14, 0, 4);
    }

    private TextBlock Problem(string text)
    {
        var line = Line(text);
        line.Foreground = Theme.Danger;
        line.FontWeight = FontWeights.SemiBold;
        line.Margin = new Thickness(0, 14, 0, 4);
        return line;
    }

    private static StackPanel Captioned(string caption, UIElement content)
    {
        var label = Theme.Label(caption, 11, Theme.Muted);
        label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(2, 0, 0, 6);
        var panel = new StackPanel();
        panel.Children.Add(label);
        panel.Children.Add(content);
        return panel;
    }

    private Button ChoiceCard(string head, string body, bool chosen, Action choose)
    {
        var panel = new StackPanel();
        var title = Theme.Label(chosen ? "✓  " + head : head, 16);
        title.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(title);
        var detail = Theme.Label(body, 14, Theme.Muted);
        detail.Margin = new Thickness(0, 4, 0, 0);
        detail.LineHeight = 20;
        panel.Children.Add(detail);
        var card = new Button
        {
            Content = panel,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 14, 0, 0),
            BorderThickness = new Thickness(chosen ? 2 : 1),
            BorderBrush = chosen ? Theme.Accent : Theme.Border,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        Theme.Animate(card, chosen ? Theme.Color("#ECEAFD") : Colors.White, Theme.Color("#F3F1FE"), Theme.Color("#E6E2FC"));
        card.Click += (_, _) =>
        {
            choose();
            Render();
        };
        return card;
    }
}
