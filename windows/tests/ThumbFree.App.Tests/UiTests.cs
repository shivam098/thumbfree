using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ThumbFree.App.UI;
using ThumbFree.Core;

namespace ThumbFree.App.Tests;

/// <summary>
/// Builds every page and guide step offscreen with fakes (no window is shown, nothing on the PC is touched), checks what
/// they show and do, and draws them (saved as PNGs when THUMBFREE_UI_SHOTS is set).
/// </summary>
public sealed class UiTests : IDisposable
{
    private const double WindowWidth = 1060;
    private const double WindowHeight = 740;
    private const double GuideWidth = 860;
    private const double GuideHeight = 740;

    private readonly TempFolder _folder = new();
    private readonly FakeEnvironment _environment = new();
    private readonly FakeModelManager _models = new();
    private DateTimeOffset _now = DateTimeOffset.Now;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Home_shows_ready_the_model_and_the_last_dictation() => UiThread.Run(() =>
    {
        _models.Installed.Add(ModelCatalog.EnglishLive.Id);
        var services = Services();
        Seed(services);
        var view = new MainView(services);

        Render.Save(view, "window-home", WindowWidth, WindowHeight);
        var texts = Render.Texts(view);
        Assert.Contains("Ready", texts);
        Assert.Contains("Tap Ctrl+Win to dictate, and tap it again to finish.", texts);
        Assert.Contains(texts, t => t.StartsWith("Speech model: Live · English, live (types as you speak) · 79 MB, on this PC", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Hey Tim,", StringComparison.Ordinal));
        Assert.Contains("Outlook · 2 minutes ago", texts);

        Render.Click(Render.ButtonWith(view, "Copy"));
        Assert.Equal(PracticeTexts.EmailLaidOut, _environment.Copied.Single());
        Render.Click(Render.ButtonWith(view, "Pause"));
    });

    [Fact]
    public void Home_says_what_stops_dictation() => UiThread.Run(() =>
    {
        var paused = false;
        var services = Services();
        services.SetPaused = p => paused = p;
        var home = new HomePage(services);

        Render.Save(home, "home-model-needed", WindowWidth - 236, WindowHeight);
        Assert.Contains("Speech model needed", Render.Texts(home));
        Render.Click(Render.ButtonWith(home, "Download (79 MB)"));
        _models.Report(ModelCatalog.EnglishLive, 0.42);
        UiThread.DoEvents();
        Render.Save(home, "home-downloading", WindowWidth - 236, WindowHeight);
        Assert.Contains("Downloading the speech model… 42%", Render.Texts(home));

        _models.Finish(ModelCatalog.EnglishLive);
        WaitUntil(() => services.Models.State(ModelCatalog.EnglishLive).Status == ModelStatus.Installed);
        services.State.SetPaused(true);
        services.State.SetMicrophoneSilent(true);
        services.State.Report(new StatusUpdate(StatusKind.Warning, "That's a password field — I won't type there."));
        Render.Save(home, "home-paused-warnings", WindowWidth - 236, WindowHeight);
        var texts = Render.Texts(home);
        Assert.Contains("Paused", texts);
        Assert.Contains(texts, t => t.Contains("No sound from the microphone", StringComparison.Ordinal) && t.Contains("password field", StringComparison.Ordinal));
        Render.Click(Render.ButtonWith(home, "Resume"));
        Assert.False(paused);

        services.State.SetPaused(false);
        services.State.SetActivity(DictationLabels.Listening);
        Render.Draw(home, WindowWidth - 236, WindowHeight);
        Assert.Contains("Listening…", Render.Texts(home));
    });

    [Fact]
    public void History_copies_deletes_searches_and_clears() => UiThread.Run(() =>
    {
        var services = Services();
        Seed(services);
        var view = new MainView(services);
        view.ShowPage(AppPage.History);

        Render.Save(view, "window-history", WindowWidth, WindowHeight);
        Render.Save(new HistoryPage(services), "history-full", WindowWidth - 236);
        var cards = Cards(view.History);
        Assert.Equal(6, cards.Count);
        Assert.Contains("Search", Render.Texts(view.History));
        Assert.Contains("Outlook · 2 minutes ago", Render.Texts(cards[0]));

        Render.Click(cards[0]);
        Assert.Equal(PracticeTexts.EmailLaidOut, _environment.Copied.Single());
        Assert.Equal(Visibility.Visible, Render.All<TextBlock>(cards[0]).Single(t => t.Text == "✓ Copied").Visibility);
        Render.Save(view, "window-history-copied", WindowWidth, WindowHeight);

        ((MenuItem)cards[1].ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        UiThread.DoEvents();
        view.History.Render();
        Assert.Equal(5, services.History.Entries().Count);

        var search = Render.Named<TextBox>(view.History, "Search history");
        search.Text = "MEETING cancel";
        Render.Draw(view.History, WindowWidth - 236, WindowHeight);
        Assert.Single(Cards(view.History));

        _environment.Answer = false;
        Render.Click(Render.ButtonWith(view.History, "Clear history"));
        Assert.Equal("Clear history?", _environment.Questions.Single().Title);
        Assert.Equal(5, services.History.Entries().Count);
        _environment.Answer = true;
        Render.Click(Render.ButtonWith(view.History, "Clear history"));
        UiThread.DoEvents();
        view.History.Render();
        search.Text = string.Empty;
        Render.Save(view.History, "history-empty", WindowWidth - 236, 400);
        Assert.Contains(Render.Texts(view.History), t => t.StartsWith("Nothing yet.", StringComparison.Ordinal));
    });

    [Fact]
    public void History_off_says_so_and_offers_to_turn_it_on() => UiThread.Run(() =>
    {
        var services = Services(new AppSettings { KeepHistory = false });
        var page = new HistoryPage(services);

        Render.Save(page, "history-off", WindowWidth - 236, 400);
        Assert.Contains("History is off, so new dictations aren't kept.", Render.Texts(page));
        Render.Click(Render.ButtonWith(page, "Turn history on"));
        Assert.True(services.Current.KeepHistory);
    });

    [Fact]
    public void Home_and_Settings_say_what_is_wrong_in_settings_json() => UiThread.Run(() =>
    {
        _models.Installed.Add(ModelCatalog.EnglishLive.Id);
        var path = _folder.File("settings.json");
        File.WriteAllText(path, """{ "threads": "four", "keepHistory": false }""");
        var services = Services(AppSettings.Load(path));
        var view = new MainView(services);
        const string problem = "In settings.json, “threads” should be a whole number, so ThumbFree uses its default for it. Fix it in the file, then restart ThumbFree.";

        Render.Save(view, "window-home-settings-problem", WindowWidth, WindowHeight);
        Assert.Contains("Your settings need a look", Render.Texts(view));
        Assert.Contains(problem, Render.Texts(view));
        Assert.False(services.Current.KeepHistory);
        Render.Click(Render.ButtonWith(view, "Open settings file"));
        Assert.Equal("notepad.exe", _environment.Opened.Single());

        view.ShowPage(AppPage.Settings);
        Render.Draw(view, WindowWidth, WindowHeight);
        Assert.Contains(problem, Render.Texts(view.Settings));
    });

    [Fact]
    public void Settings_show_every_section_and_save_each_change() => UiThread.Run(() =>
    {
        _models.Installed.Add(ModelCatalog.EnglishLive.Id);
        _models.Installed.Add(ModelCatalog.Hinglish.Id);
        var services = Services(new AppSettings { SaveSamples = true, Profession = "Developer", Vocabulary = "GitHub\nKubernetes" });
        _ = services.Models.Start(ModelCatalog.EnglishFast);
        _models.Report(ModelCatalog.EnglishFast, 0.42);
        services.Samples.Save(new float[16000], "hello", "Hello.", ModelCatalog.EnglishLive, passwordField: false, null);
        services.Samples.Flush(TimeSpan.FromSeconds(5));
        var view = new MainView(services);
        view.ShowPage(AppPage.Settings);

        Render.Save(view, "window-settings", WindowWidth, WindowHeight);
        Render.Save(new SettingsPage(services), "settings-full", WindowWidth - 236);
        var texts = Render.Texts(view.Settings);
        foreach (var section in new[] { "Language and speech model", "Writing", "Words I use often", "Personal dictionary", "General", "History and privacy", "Keys", "Advanced" })
        {
            Assert.Contains(section, texts);
        }

        Assert.Contains("✓ 79 MB, on this PC", texts);
        Assert.Contains("42% of 136 MB", texts);
        Assert.Contains("661 MB", texts);
        Assert.Contains(texts, t => t.StartsWith("1 dictation kept on this PC (33 KB", StringComparison.Ordinal));
        Assert.Contains("2 of 40", texts);
        Assert.Contains(texts, t => t.Contains("Never from password fields or web browsers (a browser can't say whether a window is private).", StringComparison.Ordinal));

        Render.Click(Render.Named<CheckBox>(view.Settings, "Smart formatting"));
        Assert.False(services.Current.SmartFormatting);
        Assert.False(ReadSettings()["smartFormatting"]!.GetValue<bool>());

        var dictionary = Render.Named<TextBox>(view.Settings, "Personal dictionary");
        dictionary.Text = "journals = genres\nthumb free ThumbFree";
        WaitUntil(() => Render.Texts(view.Settings).Any(t => t.StartsWith("Line 2 needs", StringComparison.Ordinal)));
        Assert.True(services.Current.Replacements.ContainsKey("git hub"));
        dictionary.Text = "journals = genres\nthumb free = ThumbFree";
        WaitUntil(() => services.Current.Replacements.Count == 2);
        Assert.Equal("ThumbFree", ReadSettings()["replacements"]!["thumb free"]!.GetValue<string>());

        var roles = Render.Named<ComboBox>(view.Settings, "What do you do?");
        roles.SelectedItem = "Data analysis";
        Assert.Equal("Data analysis", services.Current.Profession);
        Assert.Equal("GitHub\nKubernetes\nSQL\nPower BI\nTableau\npandas\nJupyter", services.Current.Vocabulary);
        Assert.Contains("Added 5 words for Data analysis.", Render.Texts(view.Settings));
    });

    [Fact]
    public void Settings_ask_before_big_downloads_and_before_deleting_history() => UiThread.Run(() =>
    {
        _models.Installed.Add(ModelCatalog.EnglishLive.Id);
        _environment.TotalMemoryBytes = 4L << 30;
        _environment.Metered = true;
        var services = Services();
        Seed(services);
        var settings = new SettingsPage(services);
        Render.Draw(settings, WindowWidth - 236);
        var accurate = Render.Named<RadioButton>(settings, "English, Accurate: " + ModelCatalog.EnglishAccurate.Label);

        _environment.Answer = false;
        Render.Click(accurate);
        var question = _environment.Questions.Single();
        Assert.Equal("Download “English, accurate”?", question.Title);
        Assert.Contains("This PC has 4 GB of memory. That may not be enough", question.Message, StringComparison.Ordinal);
        Assert.Contains("metered connection", question.Message, StringComparison.Ordinal);
        Assert.Equal(Tier.Live, services.Current.EnglishTier);
        Assert.False(accurate.IsChecked);

        _environment.Answer = true;
        Render.Click(accurate);
        Assert.Equal(Tier.Accurate, services.Current.EnglishTier);
        Assert.Equal(ModelStatus.Downloading, services.Models.State(ModelCatalog.EnglishAccurate).Status);
        Render.Save(ConfirmDialog.View(question, _ => { }), "dialog-big-model", 488);

        var keep = Render.Named<CheckBox>(settings, "Keep history");
        _environment.Answer = false;
        Render.Click(keep);
        Assert.True(keep.IsChecked);
        Assert.True(services.Current.KeepHistory);
        Assert.Equal(6, services.History.Entries().Count);
        _environment.Answer = true;
        Render.Click(keep);
        Assert.False(services.Current.KeepHistory);
        Assert.Empty(services.History.Entries());
        Assert.Equal("Stop keeping history?", _environment.Questions[^1].Title);
        Render.Save(ConfirmDialog.View(_environment.Questions[^1], _ => { }), "dialog-stop-history", 488);
    });

    [Fact]
    public void Help_explains_and_filters() => UiThread.Run(() =>
    {
        var services = Services();
        var view = new MainView(services);
        view.ShowPage(AppPage.Help);

        Render.Save(view, "window-help", WindowWidth, WindowHeight);
        Render.Save(new HelpPage(services), "help-full", WindowWidth - 236);
        var texts = Render.Texts(view.Help);
        Assert.Contains("Licences and thanks", texts);
        Assert.Contains("•  Kroko ASR by Banafo (English, Live): CC BY-SA 4.0", texts);
        Assert.Contains(PracticeTexts.ListLaidOut, texts);

        Render.Named<TextBox>(view.Help, "Search help").Text = "comma";
        Render.Draw(view.Help, WindowWidth - 236, WindowHeight);
        texts = Render.Texts(view.Help);
        Assert.Contains("Spoken punctuation", texts);
        Assert.DoesNotContain("Licences and thanks", texts);
        Assert.DoesNotContain("Hindi + English", texts);
    });

    [Fact]
    public void The_guide_draws_every_step_and_finishing_marks_it_done() => UiThread.Run(() =>
    {
        var services = Services();
        services.Notifier = new Notifier(new FakeSurface(), services.State, _ => { });
        var finished = false;
        var guide = new GuideView(services, () => finished = true);

        Render.Save(guide, "guide-1-welcome", GuideWidth, GuideHeight);
        Render.Click(Render.ButtonWith(guide, "Get started"));
        Assert.Equal(GuideStep.Microphone, guide.Step);
        Render.Save(guide, "guide-2-microphone", GuideWidth, GuideHeight);
        _environment.Microphone = MicrophoneAccess.BlockedForDesktopApps;
        guide.Go(GuideStep.Microphone);
        Render.Save(guide, "guide-2-microphone-blocked", GuideWidth, GuideHeight);
        Render.Click(Render.ButtonWith(guide, "Open microphone settings"));
        Assert.Equal("ms-settings:privacy-microphone", _environment.Opened.Single());

        guide.Go(GuideStep.Model);
        Render.Save(guide, "guide-3-model", GuideWidth, GuideHeight);
        Render.Click(Render.ButtonWith(guide, "Download (79 MB)"));
        _models.Report(ModelCatalog.EnglishLive, 0.3);
        UiThread.DoEvents();
        Render.Save(guide, "guide-3-model-downloading", GuideWidth, GuideHeight);
        Assert.Contains("You can carry on while it downloads.", Render.Texts(guide));
        _models.Finish(ModelCatalog.EnglishLive);
        WaitUntil(() => Render.Texts(guide).Contains("✓  “English, live” is ready."));

        guide.Go(GuideStep.TryIt);
        guide.PracticeBoxes.Single().Simulate("Hello, this is my first dictation with ThumbFree.");
        Render.Save(guide, "guide-4-try-it", GuideWidth, GuideHeight);
        Assert.Contains("✓ That's it. It works the same way in any app.", Render.Texts(guide));

        guide.Go(GuideStep.Email);
        guide.PracticeBoxes.Single().Simulate(PracticeTexts.EmailLaidOut);
        Render.Save(guide, "guide-5-email", GuideWidth, GuideHeight);
        Assert.Contains("✓ Well done. ThumbFree laid out your email and kept only your correction.", Render.Texts(guide));

        guide.Go(GuideStep.List);
        guide.PracticeBoxes.Single().Simulate(PracticeTexts.ListLaidOut);
        Render.Save(guide, "guide-6-list", GuideWidth, GuideHeight);
        Assert.Contains("✓ Great. ThumbFree made the list for you.", Render.Texts(guide));

        _environment.Notifications = false;
        guide.Go(GuideStep.Notifications);
        Render.Save(guide, "guide-7-notifications", GuideWidth, GuideHeight);

        guide.Go(GuideStep.Profession);
        Render.Click(Render.ButtonWith(guide, "Developer"));
        Render.Save(guide, "guide-8-profession", GuideWidth, GuideHeight);
        Assert.StartsWith("GitHub\nGitHub Copilot\npull request", services.Current.Vocabulary, StringComparison.Ordinal);

        guide.Go(GuideStep.Data);
        Render.Click(Render.ButtonWith(guide, "Help improve ThumbFree"));
        Render.Save(guide, "guide-9-data", GuideWidth, GuideHeight);
        Assert.True(services.Current.SaveSamples);
        Assert.Contains(Render.Texts(guide), t => t.Contains("Never from password fields or web browsers (a browser can't say whether a window is private).", StringComparison.Ordinal));
        Render.Click(Render.ButtonWith(guide, "Finish"));
        Assert.True(finished);
        Assert.True(services.Current.GuideDone);
        Assert.True(ReadSettings()["guideDone"]!.GetValue<bool>());
    });

    [Fact]
    public void Practice_boxes_take_dictation_from_the_host_thread_and_lay_it_out() => UiThread.Run(() =>
    {
        var services = Services();
        PracticeField? asked = null;
        services.Layout = (text, field) =>
        {
            asked = field;
            return text == "Hey Tim. Thanks, Ben." ? "Hey Tim.\n\nThanks,\nBen" : text;
        };
        var guide = new GuideView(services, () => { });
        guide.Go(GuideStep.Email);
        var box = guide.PracticeBoxes.Single();

        services.Practice.SetActive(box);
        Assert.Same(box, services.Practice.Active);
        Task.Run(() =>
        {
            var target = services.Practice.Active!;
            target.ShowLive("hey tim");
            target.Insert("Hey Tim. Thanks, Ben.");
        }).Wait();
        UiThread.DoEvents();

        Assert.Equal("Hey Tim.\n\nThanks,\nBen", box.Text);
        Assert.Equal(new PracticeField(MultiLine: true, AtStart: true, Email: true), asked);
        Assert.False(box.Field.AtStart);

        guide.Go(GuideStep.List);
        Assert.Null(services.Practice.Active);
    });

    [Fact]
    public void The_tray_menu_lists_the_last_five_dictations_to_copy() => UiThread.Run(() =>
    {
        var services = Services();
        Seed(services);
        var notifier = new Notifier(new FakeSurface(), services.State, _ => { });
        services.Notifier = notifier;
        notifier.ShowUntyped("Call me at 4 PM.");
        var opened = new List<AppPage>();
        using var menu = new TrayMenu(services, new TrayCommands(opened.Add, () => { }, () => { }), () => false, () => { }, () => false);
        menu.ShowStatus("Ready — tap Ctrl+Win to dictate", paused: false);

        menu.Prepare();
        var items = menu.Strip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Where(i => i.Available).Select(i => i.Text).ToList();
        Assert.Equal("Open ThumbFree", items[0]);
        Assert.Equal(
            new[] { "Hey Tim, Would love to connect. Are you available…", "Notes from the design review. The guide needs a…", "Kal ki meeting cancel ho gayi hai, please sabko…", "Things to do today: - Order groceries. - Go on a…", "Can you send me the build logs? Thanks!" },
            items.Skip(3).Take(5));
        Assert.Contains("History…", items);
        Assert.Contains("Copy the text that wasn't typed", items);
        Assert.Equal("Exit", items[^1]);

        menu.Strip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().First(i => i.Text?.StartsWith("Can you send", StringComparison.Ordinal) == true).PerformClick();
        Assert.Equal("Can you send me the build logs? Thanks!", _environment.Copied.Single());
        menu.Strip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().First(i => i.Text == "History…").PerformClick();
        Assert.Equal(AppPage.History, opened.Single());

        // Opening again rebuilds the list instead of adding to it.
        menu.Prepare();
        Assert.Equal(items.Count, menu.Strip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Count(i => i.Available));

        if (Render.Folder is { } folder)
        {
            menu.Strip.PerformLayout();
            var size = menu.Strip.GetPreferredSize(System.Drawing.Size.Empty);
            menu.Strip.Size = size;
            using var bitmap = new System.Drawing.Bitmap(size.Width, size.Height);
            menu.Strip.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size));
            Directory.CreateDirectory(folder);
            bitmap.Save(Path.Combine(folder, "tray-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    });

    private AppServices Services(AppSettings? settings = null) =>
        TestServices.Create(_folder, _environment, _models, settings, () => _now);

    private JsonObject ReadSettings() =>
        JsonNode.Parse(File.ReadAllText(_folder.File("settings.json")), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();

    /// <summary>Six dictations, newest first: notes, an email, Hinglish, a list, a question and a long one.</summary>
    private void Seed(AppServices services)
    {
        var real = DateTimeOffset.Now;
        foreach (var (ago, app, text) in new (TimeSpan Ago, string App, string Text)[]
        {
            (TimeSpan.FromDays(3), "WINWORD", "Our Q3 plan focuses on three things: faster onboarding, fewer support tickets and a cleaner settings page. Priya owns onboarding, Sam owns support, and I'll take settings."),
            (TimeSpan.FromHours(26), "slack", "Can you send me the build logs? Thanks!"),
            (TimeSpan.FromHours(5), "chrome", PracticeTexts.ListLaidOut),
            (TimeSpan.FromMinutes(40), "ms-teams", "Kal ki meeting cancel ho gayi hai, please sabko bata dena."),
            (TimeSpan.FromMinutes(9), "notepad", "Notes from the design review. The guide needs a shorter welcome, the settings page should save as you go, and History should open from the tray. We agreed to keep everything on the device: no uploads, no accounts. Next steps: render every page offscreen, check the layout at 100% and 150% scaling, and write the release notes. Follow up with the team on Friday about the icons and the Hindi + English model sizes."),
                       (TimeSpan.FromMinutes(2), "OUTLOOK", PracticeTexts.EmailLaidOut),
        })
        {
            _now = real - ago;
            services.History.Add(text, app, passwordField: false);
        }

        _now = real;
        services.History.Flush(TimeSpan.FromSeconds(5));
    }

    private static List<Button> Cards(HistoryPage page) =>
        Render.All<Button>(page).Where(b => b.ContextMenu is not null).ToList();

    private static void WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the UI.");
            Thread.Sleep(20);
            UiThread.DoEvents();
        }
    }
}
