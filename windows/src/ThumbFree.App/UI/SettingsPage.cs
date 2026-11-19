using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ThumbFree.Core;
using DictationLanguage = ThumbFree.Core.Language;

namespace ThumbFree.App.UI;

/// <summary>
/// Settings: language and speech models, live typing, writing options, the word list and personal dictionary, sounds and
/// sign-in, History and samples, and the keys. Every change is saved at once (only that field is written into
/// settings.json; see <see cref="SettingsStore"/>) and applies without a restart.
/// </summary>
internal sealed class SettingsPage : UserControl
{
    private readonly AppServices _services;
    private readonly List<Action> _refreshers = [];
    private readonly List<ModelRow> _modelRows = [];
    private readonly TextBlock _error = Theme.Label(string.Empty, 13, Theme.Danger);
    private readonly TextBlock _samplesNote = Theme.Label(string.Empty, 12, Theme.Muted);
    private readonly Button _saveZip;
    private readonly Button _deleteSamples;
    private readonly Button _showZip;
    private string? _savedZip;
    private bool _refreshing;

    public SettingsPage(AppServices services)
    {
        _services = services;
        var stack = new StackPanel();
        stack.Children.Add(Theme.Heading("Settings"));
        var intro = Theme.Label("Changes are saved as you make them and work straight away.", 13, Theme.Muted);
        intro.Margin = new Thickness(0, 0, 0, 16);
        stack.Children.Add(intro);
        _error.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(_error);

        _saveZip = Theme.Button("Save samples as a zip…", async (_, _) => await SaveZipAsync());
        _deleteSamples = Theme.Button("Delete all", async (_, _) => await DeleteSamplesAsync(), danger: true);
        _showZip = Theme.Link("Show in folder", (_, _) =>
        {
            if (_savedZip is not null)
            {
                _services.Environment.ShowInFolder(_savedZip);
            }
        });

        stack.Children.Add(Section("Language and speech model", LanguageSection()));
        stack.Children.Add(Section("Writing", WritingSection()));
        stack.Children.Add(Section("Words I use often", WordsSection()));
        stack.Children.Add(Section("Personal dictionary", DictionarySection()));
        stack.Children.Add(Section("General", GeneralSection()));
        stack.Children.Add(Section("History and privacy", PrivacySection()));
        stack.Children.Add(Section("Keys", KeysSection()));
        stack.Children.Add(Section("Advanced", AdvancedSection()));

        var scroll = new ScrollViewer { Content = Theme.Column(stack, 780, new Thickness(32, 28, 32, 24)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Motion.SmoothScroll(scroll);
        Content = scroll;
        Background = Theme.Background;

        services.Settings.Changed += _ => Refresh();
        services.Models.Changed += _ => RefreshModels();
        services.Samples.Changed += RefreshSamples;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Refresh();
                RefreshSamples();
            }
        };
        Refresh();
        RefreshSamples();
    }

    private AppSettings Current => _services.Current;

    private Window? Owner => Window.GetWindow(this);

    private static Border Section(string title, params UIElement[] rows)
    {
        var panel = new StackPanel();
        var heading = Theme.Title(title, 16);
        heading.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(heading);
        foreach (var row in rows)
        {
            panel.Children.Add(row);
        }

        return Theme.CardOf(panel, padding: new Thickness(20, 18, 20, 18));
    }

    private UIElement[] LanguageSection()
    {
        var english = Theme.Choice("English", (_, _) => ChooseLanguage(DictationLanguage.English));
        var hinglish = Theme.Choice("Hindi + English", (_, _) => ChooseLanguage(DictationLanguage.Hinglish));
        _refreshers.Add(() =>
        {
            Theme.SetChosen(english, Current.Language == DictationLanguage.English);
            Theme.SetChosen(hinglish, Current.Language == DictationLanguage.Hinglish);
        });
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        choices.Children.Add(english);
        choices.Children.Add(hinglish);
        var hint = Theme.Label($"{Current.DictationLanguageHotkey} switches between them. Hindi is typed in Roman letters.", 12, Theme.Muted);
        hint.Margin = new Thickness(0, 8, 0, 0);

        var englishModels = ModelGroup("English model", DictationLanguage.English, [Tier.Live, Tier.Standard, Tier.Accurate]);
        var hinglishModels = ModelGroup("Hindi + English model", DictationLanguage.Hinglish, [Tier.Standard, Tier.Accurate]);
        var note = Theme.Label("Models download once and then work offline. Live types as you speak; the accurate models type when you finish.", 12, Theme.Muted);
        note.Margin = new Thickness(0, 4, 0, 0);

        return
        [
            choices,
            hint,
            Theme.Divider(),
            englishModels,
            Theme.Divider(),
            hinglishModels,
            note,
            Theme.Divider(),
            Switch(
                "Live typing",
                "Your words are typed as you speak, a phrase at each pause, and the pill shows the phrase you're saying. Off: everything is typed when you finish (uses less power).",
                s => s.LiveTyping,
                (s, v) => s.LiveTyping = v),
        ];
    }

    private StackPanel ModelGroup(string title, DictationLanguage language, Tier[] tiers)
    {
        var panel = new StackPanel();
        var heading = Theme.Label(title, 14);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(heading);
        foreach (var tier in tiers)
        {
            var row = new ModelRow(this, language, tier);
            _modelRows.Add(row);
            panel.Children.Add(row.View);
        }

        return panel;
    }

    private UIElement[] WritingSection() =>
    [
        Switch("Smart formatting", "Emails laid out, spoken lists turned into lists, and “3 pm… actually 4 pm” becomes “4 pm”.", s => s.SmartFormatting, (s, v) => s.SmartFormatting = v),
        Theme.Divider(),
        Switch("Remove filler words", "Drops “um”, “uh”, “you know”, “thik hai na” and repeated words.", s => s.RemoveFillerWords, (s, v) => s.RemoveFillerWords = v),
        Theme.Divider(),
        Switch("Spoken punctuation", "Say “comma”, “full stop”, “question mark” or “new line” to type them.", s => s.SpokenPunctuation, (s, v) => s.SpokenPunctuation = v),
    ];

    private UIElement[] WordsSection()
    {
        var about = Theme.Label(
            "Names and jargon, one per line, so they're written your way (“git hub co-pilot” → “GitHub Copilot”). Small " +
            "misspellings of longer words are fixed too. For words it mishears completely, use the personal dictionary.",
            13,
            Theme.Muted);
        about.Margin = new Thickness(0, 0, 0, 10);
        var box = Theme.TextBox(Current.Vocabulary, multiLine: true, minHeight: 110);
        box.MaxHeight = 220;
        System.Windows.Automation.AutomationProperties.SetName(box, "Words I use often");
        var count = Theme.Label(string.Empty, 12, Theme.Muted);
        count.Margin = new Thickness(0, 6, 0, 0);
        void Count()
        {
            var entries = box.Text.Split('\n', ',').Count(e => e.Trim().Length > 0);
            count.Text = entries > Vocabulary.MaxEntries
                ? $"{entries} words: only the first {Vocabulary.MaxEntries} are used."
                : $"{entries} of {Vocabulary.MaxEntries}";
            count.Foreground = entries > Vocabulary.MaxEntries ? Theme.Warning : Theme.Muted;
        }

        AutoSave(box, () => Save(s => s.Vocabulary = Tidy(box.Text)));
        box.TextChanged += (_, _) => Count();
        _refreshers.Add(() => ShowText(box, Current.Vocabulary));
        Count();

        // "What do you do?": adds that field's names and jargon (nothing leaves this PC).
        var roles = new ComboBox { Width = 220, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (role, _) in Professions.All)
        {
            roles.Items.Add(role);
        }

        var added = Theme.Label(string.Empty, 12, Theme.Muted);
        added.Margin = new Thickness(0, 6, 0, 0);
        roles.SelectionChanged += (_, _) =>
        {
            if (_refreshing || roles.SelectedItem is not string role)
            {
                return;
            }

            var before = Tidy(box.Text);
            var after = Professions.AddTo(before, role);
            var lines = after.Split('\n').Length - (before.Trim().Length == 0 ? 0 : before.Split('\n').Length);
            Save(s =>
            {
                s.Vocabulary = after;
                s.Profession = role;
            });
            box.Text = after;
            added.Text = lines > 0 ? $"Added {lines} {(lines == 1 ? "word" : "words")} for {role}." : "Those words are already in your list.";
        };
        _refreshers.Add(() => roles.SelectedItem = Current.Profession is { } p && roles.Items.Contains(p) ? p : null);
        System.Windows.Automation.AutomationProperties.SetName(roles, "What do you do?");
        var professionRow = Theme.Row("What do you do?", "Adds your field's names and acronyms to the list. Stays on this PC.", roles);
        professionRow.Margin = new Thickness(0, 14, 0, 0);
        return [about, Theme.WithPlaceholder(box, "GitHub Copilot\nKubernetes\nPriyanka"), count, professionRow, added];
    }

    private UIElement[] DictionarySection()
    {
        var about = Theme.Label("One per line: what it hears = what to type. For example: journals = genres", 13, Theme.Muted);
        about.Margin = new Thickness(0, 0, 0, 10);
        var box = Theme.TextBox(PersonalDictionary.Format(Current.Replacements), multiLine: true, minHeight: 110);
        box.MaxHeight = 220;
        System.Windows.Automation.AutomationProperties.SetName(box, "Personal dictionary");
        var problem = Theme.Label(string.Empty, 12, Theme.Danger);
        problem.Margin = new Thickness(0, 6, 0, 0);
        problem.Visibility = Visibility.Collapsed;
        AutoSave(box, () =>
        {
            if (PersonalDictionary.TryParse(box.Text, out var entries, out var error))
            {
                problem.Visibility = Visibility.Collapsed;
                Save(s => s.Replacements = entries);
            }
            else
            {
                problem.Text = error + " Nothing here is saved until it's fixed.";
                problem.Visibility = Visibility.Visible;
            }
        });
        _refreshers.Add(() =>
        {
            if (PersonalDictionary.TryParse(box.Text, out var shown, out _) && !SameEntries(shown, Current.Replacements))
            {
                ShowText(box, PersonalDictionary.Format(Current.Replacements));
            }
        });
        return [about, Theme.WithPlaceholder(box, "thumb free = ThumbFree"), problem];
    }

    private UIElement[] GeneralSection()
    {
        var signIn = Theme.Switch(SafeStartAtSignIn(), "Start when I sign in");
        signIn.Click += (_, _) =>
        {
            try
            {
                _services.Environment.StartAtSignIn = signIn.IsChecked == true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                ShowError("Couldn't change “Start when I sign in”: " + ex.Message);
            }

            signIn.IsChecked = SafeStartAtSignIn();
        };
        _refreshers.Add(() => signIn.IsChecked = SafeStartAtSignIn());

        return
        [
            Switch("Sounds", "A short tone when recording starts and when it stops.", s => s.SoundFeedback, (s, v) => s.SoundFeedback = v),
            Theme.Divider(),
            Theme.Row("Start when I sign in", "ThumbFree waits in the tray, ready for Ctrl+Win.", signIn),
            Theme.Divider(),
            Theme.Row("First-run guide", "Practise an email and a list again, or change the choices you made.", Theme.Button("Show the guide again", (_, _) => _services.ShowGuide())),
        ];
    }

    private UIElement[] PrivacySection()
    {
        var keep = Theme.Switch(Current.KeepHistory, "Keep history");
        keep.Click += (_, _) => SetKeepHistory(keep);
        _refreshers.Add(() => keep.IsChecked = Current.KeepHistory);
        var history = new StackPanel();
        history.Children.Add(Theme.Row(
            "Keep history",
            "Your dictations from the last 30 days, on this PC only, so you can copy one again. Never from password fields. Turning this off deletes History.",
            keep));
        var open = Theme.Link("Open History", (_, _) => _services.OpenPage(AppPage.History));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(-6, 6, 0, 0);
        history.Children.Add(open);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_saveZip);
        buttons.Children.Add(_deleteSamples);
        buttons.Children.Add(_showZip);
        _samplesNote.Margin = new Thickness(0, 10, 0, 0);
        return
        [
            history,
            Theme.Divider(),
            Switch(
                "Help improve ThumbFree",
                "Keep each dictation's recording and text on this PC so you can choose to send them to the ThumbFree team. " +
                "Never from password fields or private browser windows. Nothing is sent automatically: you save a zip and share it yourself.",
                s => s.SaveSamples,
                (s, v) => s.SaveSamples = v),
            _samplesNote,
            buttons,
        ];
    }

    private UIElement[] KeysSection()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = new (string Keys, string What)[]
        {
            ("Ctrl+Win", "Start dictating; tap again to finish (Ctrl+Win+Space works too)"),
            ("Esc", "Cancel while dictating"),
            (Current.DictationLanguageHotkey, "Switch between English and Hindi + English"),
            (Current.PasteLastHotkey, "Paste the last dictation again"),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = Theme.Keys(rows[i].Keys);
            keys.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(keys, i);
            grid.Children.Add(keys);
            var what = Theme.Label(rows[i].What, 13);
            what.VerticalAlignment = VerticalAlignment.Center;
            what.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 1);
            grid.Children.Add(what);
        }

        var note = Theme.Label("Tap the keys: there's nothing to hold. The last two can be changed in the settings file (then restart ThumbFree).", 12, Theme.Muted);
        return [grid, note];
    }

    private UIElement[] AdvancedSection()
    {
        var buttons = new WrapPanel();
        buttons.Children.Add(Theme.Button("Open settings file", (_, _) => OpenSettingsFile()));
        buttons.Children.Add(Theme.Button("Open logs folder", (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            _services.Environment.Open(AppPaths.LogsDir);
        }));
        var note = Theme.Label(
            "Snippets, new-line keys for each app, the microphone and how text is inserted are in the settings file; restart " +
            "ThumbFree after changing them there. Logs hold timings and error names, never your words.",
            12,
            Theme.Muted);
        note.Margin = new Thickness(0, 10, 0, 0);
        return [buttons, note];
    }

    private UIElement Switch(string title, string detail, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        var toggle = Theme.Switch(get(Current), title);
        toggle.Click += (_, _) => Save(s => set(s, toggle.IsChecked == true));
        _refreshers.Add(() => toggle.IsChecked = get(Current));
        return Theme.Row(title, detail, toggle);
    }

    /// <summary>Saves a text box shortly after typing stops, and when it loses focus.</summary>
    private void AutoSave(TextBox box, Action save)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            save();
        };
        box.TextChanged += (_, _) =>
        {
            if (!_refreshing)
            {
                timer.Stop();
                timer.Start();
            }
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (timer.IsEnabled)
            {
                timer.Stop();
                save();
            }
        };
    }

    /// <summary>Shows a value changed elsewhere (the guide, the file), unless the box is being typed in.</summary>
    private static void ShowText(TextBox box, string text)
    {
        if (!box.IsKeyboardFocusWithin && Tidy(box.Text) != Tidy(text))
        {
            box.Text = text;
        }
    }

    private static string Tidy(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    /// <summary>"340 KB" below a megabyte, else "12 MB".</summary>
    private static string Size(long bytes) => bytes < 1_000_000 ? $"{Math.Max(1, (bytes + 999) / 1000)} KB" : ModelConsent.Mb(bytes);

    private static bool SameEntries(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var v) && v == e.Value);

    private void Save(Action<AppSettings> change)
    {
        if (!_refreshing)
        {
            _services.Settings.Update(change);
            ShowError(_services.Settings.LastError);
        }
    }

    private void ShowError(string? error)
    {
        _error.Text = error ?? string.Empty;
        _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            foreach (var refresh in _refreshers)
            {
                refresh();
            }

            RefreshModels();
        }
        finally
        {
            _refreshing = false;
        }

        ShowError(_services.Settings.LastError);
    }

    private void RefreshModels()
    {
        foreach (var row in _modelRows)
        {
            row.Refresh();
        }
    }

    private void RefreshSamples()
    {
        var summary = _services.Samples.Summary();
        if (_savedZip is null || summary.Count == 0)
        {
            _samplesNote.Text = summary.Count > 0
                ? $"{summary.Count} {(summary.Count == 1 ? "dictation" : "dictations")} kept on this PC ({Size(summary.Bytes)})."
                : Current.SaveSamples ? "None kept yet: your next dictations will appear here." : "Nothing kept.";
        }

        _saveZip.IsEnabled = summary.Count > 0;
        _deleteSamples.IsEnabled = summary.Count > 0;
        _showZip.Visibility = _savedZip is not null && File.Exists(_savedZip) ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool SafeStartAtSignIn()
    {
        try
        {
            return _services.Environment.StartAtSignIn;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Turning History off deletes it, so it asks first (when there's anything to delete).</summary>
    private void SetKeepHistory(CheckBox toggle)
    {
        if (toggle.IsChecked == true)
        {
            Save(s => s.KeepHistory = true);
            return;
        }

        if (_services.History.Entries().Count > 0 && !_services.Environment.Confirm(Owner, new Confirmation(
            "Stop keeping history?",
            "Everything in History will be deleted from this PC, and new dictations won't be kept.",
            "Turn off and delete",
            Danger: true)))
        {
            toggle.IsChecked = true;
            return;
        }

        Save(s => s.KeepHistory = false);
        _services.History.Clear();
    }

    private async Task SaveZipAsync()
    {
        var path = _services.Environment.AskSavePath(Owner, "Save samples as a zip", $"thumbfree-samples-{DateTime.Now:yyyyMMdd}.zip", "Zip files (*.zip)|*.zip");
        if (path is null)
        {
            return;
        }

        _saveZip.IsEnabled = false;
        _samplesNote.Text = "Saving…";
        try
        {
            var count = await _services.Samples.ExportZipAsync(path);
            _savedZip = path;
            _samplesNote.Text = $"Saved {count} {(count == 1 ? "dictation" : "dictations")} in {Path.GetFileName(path)}. Share it however you like.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _savedZip = null;
            _samplesNote.Text = "Couldn't save the zip: " + ex.Message;
        }

        RefreshSamples();
    }

    private async Task DeleteSamplesAsync()
    {
        if (!_services.Environment.Confirm(Owner, new Confirmation(
            "Delete all kept dictations?",
            "Their recordings and text are removed from this PC. Zips you've saved stay where you put them.",
            "Delete all",
            Danger: true)))
        {
            return;
        }

        try
        {
            await _services.Samples.DeleteAllAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't delete every sample: " + ex.Message);
        }

        _savedZip = null;
        RefreshSamples();
    }

    private void OpenSettingsFile()
    {
        var path = _services.Settings.FilePath;
        if (!File.Exists(path))
        {
            Current.Save(path);
        }

        _services.Environment.Open("notepad.exe", $"\"{path}\"");
    }

    /// <summary>One speech model: choose it, see its size and state, and download, cancel or delete it.</summary>
    private sealed class ModelRow
    {
        private readonly SettingsPage _page;
        private readonly DictationLanguage _language;
        private readonly Tier _tier;
        private readonly ModelSpec _spec;
        private readonly RadioButton _radio;
        private readonly Border _inUse;
        private readonly TextBlock _state = Theme.Label(string.Empty, 12, Theme.Muted, wrap: false);
        private readonly Button _action;
        private readonly ProgressBar _progress = Theme.Progress();
        private readonly TextBlock _error = Theme.Label(string.Empty, 12, Theme.Danger);

        public ModelRow(SettingsPage page, DictationLanguage language, Tier tier)
        {
            _page = page;
            _language = language;
            _tier = tier;
            _spec = ModelCatalog.ForLanguage(language, tier);

            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = ModelActions.TierName(tier), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text });
            _inUse = new Border
            {
                Background = Theme.AccentSoft,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 0, 6, 1),
                Margin = new Thickness(8, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "In use", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.Accent },
            };
            title.Children.Add(_inUse);
            var texts = new StackPanel();
            texts.Children.Add(title);
            texts.Children.Add(Theme.Label(_spec.Label, 12, Theme.Muted));
            _radio = Theme.Radio($"model-{language}", texts);
            _radio.Click += (_, _) => _page.ChooseTier(_language, _tier);
            System.Windows.Automation.AutomationProperties.SetName(_radio, $"{ModelActions.LanguageName(language)}, {tier}: {_spec.Label}");

            _action = Theme.Button(string.Empty, (_, _) => Act());
            _action.Margin = new Thickness(12, 0, 0, 0);
            _action.MinWidth = 88;
            _state.VerticalAlignment = VerticalAlignment.Center;
            _state.Margin = new Thickness(12, 0, 0, 0);

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(_radio);
            Grid.SetColumn(_state, 1);
            grid.Children.Add(_state);
            Grid.SetColumn(_action, 2);
            grid.Children.Add(_action);
            var below = new StackPanel { Margin = new Thickness(32, 6, 0, 0) };
            below.Children.Add(_progress);
            below.Children.Add(_error);
            Grid.SetRow(below, 1);
            Grid.SetColumnSpan(below, 3);
            grid.Children.Add(below);
            View = grid;
        }

        public FrameworkElement View { get; }

        public void Refresh()
        {
            var settings = _page.Current;
            var chosen = settings.TierFor(_language) == _tier;
            _radio.IsChecked = chosen;
            _inUse.Visibility = chosen && settings.Language == _language ? Visibility.Visible : Visibility.Collapsed;
            var state = _page._services.Models.State(_spec);
            var size = ModelConsent.Mb(_spec.Bytes);
            (_state.Text, _state.Foreground, _action.Content) = state.Status switch
            {
                ModelStatus.Installed => ($"✓ {size}, on this PC", Theme.Ok, "Delete"),
                ModelStatus.Downloading => ($"{(int)(state.Progress * 100)}% of {size}", Theme.Muted, "Cancel"),
                _ => (size, Theme.Muted, "Download"),
            };
            _progress.Visibility = state.Status == ModelStatus.Downloading ? Visibility.Visible : Visibility.Collapsed;
            Motion.ToValue(_progress, state.Progress);
            _error.Text = state.Error ?? string.Empty;
            _error.Visibility = state.Error is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Act()
        {
            var services = _page._services;
            switch (services.Models.State(_spec).Status)
            {
                case ModelStatus.Installed:
                    ModelActions.Delete(services, _spec, _page.Owner);
                    break;
                case ModelStatus.Downloading:
                    services.Models.Cancel(_spec);
                    break;
                default:
                    ModelActions.Download(services, _spec, _page.Owner);
                    break;
            }

            Refresh();
        }
    }

    /// <summary>
    /// Chooses a model tier. A small model that isn't here yet starts downloading if its language is in use; a big one
    /// asks first, and saying no keeps the previous choice.
    /// </summary>
    private void ChooseTier(DictationLanguage language, Tier tier)
    {
        var spec = ModelCatalog.ForLanguage(language, tier);
        if (_services.Models.State(spec).Status == ModelStatus.NotInstalled)
        {
            if (ModelConsent.Needed(spec))
            {
                if (!ModelActions.Download(_services, spec, Owner))
                {
                    Refresh();
                    return;
                }
            }
            else if (language == Current.Language)
            {
                _ = _services.Models.Start(spec);
            }
        }

        Save(s =>
        {
            if (language == DictationLanguage.Hinglish)
            {
                s.HinglishTier = tier;
            }
            else
            {
                s.EnglishTier = tier;
            }
        });
    }

    /// <summary>Switches the language; a small model that isn't on this PC yet starts downloading.</summary>
    private void ChooseLanguage(DictationLanguage language)
    {
        Save(s => s.Language = language);
        ModelActions.DownloadIfSmall(_services, Current.SelectedModel());
    }
}
