using System.IO.Compression;
using System.Text.Json.Nodes;
using ThumbFree.Core;

namespace ThumbFree.App.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 18, 0, 0, TimeSpan.FromHours(5.5));
    private readonly TempFolder _folder = new();
    private DateTimeOffset _now = Start;
    private bool _keep = true;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Keeps_dictations_newest_first_in_the_shared_format()
    {
        var store = NewStore();
        store.Add("Hey Tim,\n\nWould love to connect.", "OUTLOOK", passwordField: false);
        _now += TimeSpan.FromMinutes(2);
        store.Add("Buy milk\tand eggs", "notepad", passwordField: false);
        Assert.True(store.Flush(TimeSpan.FromSeconds(5)));

        var entries = store.Entries();
        Assert.Equal(new[] { "Buy milk\tand eggs", "Hey Tim,\n\nWould love to connect." }, entries.Select(e => e.Text));
        Assert.Equal("notepad", entries[0].App);
        var lines = File.ReadAllLines(_folder.File("history.txt"));
        Assert.Equal(2, lines.Length);
        Assert.Equal(entries, lines.Select(History.Decode).OfType<HistoryEntry>());
        Assert.Contains(@"Hey Tim,\n\nWould", lines[1], StringComparison.Ordinal);

        // A new session reads what the last one wrote.
        Assert.Equal(entries, NewStore().Entries());
    }

    [Fact]
    public void Never_keeps_password_fields_blank_text_or_anything_while_off()
    {
        var store = NewStore();
        store.Add("hunter2", "chrome", passwordField: true);
        store.Add("   ", "chrome", passwordField: false);
        _keep = false;
        store.Add("Kept while off?", "chrome", passwordField: false);
        store.Flush(TimeSpan.FromSeconds(5));

        Assert.Empty(store.Entries());
        Assert.False(File.Exists(_folder.File("history.txt")));
    }

    [Fact]
    public void Drops_entries_older_than_30_days_and_keeps_the_newest_500()
    {
        var now = Start.ToUnixTimeMilliseconds();
        var old = Enumerable.Range(0, 5).Select(i => new HistoryEntry(now - History.MaxAgeMs - 1000 - i, "app", $"old {i}"));
        var recent = Enumerable.Range(0, 510).Select(i => new HistoryEntry(now - (i * 1000L), "app", $"recent {i}"));
        File.WriteAllLines(_folder.File("history.txt"), old.Concat(recent).Select(History.Encode).Append("damaged line").Append("1\tonly two"));
        var store = NewStore();

        var entries = store.Entries();
        store.Flush(TimeSpan.FromSeconds(5));

        Assert.Equal(History.MaxEntries, entries.Count);
        Assert.Equal("recent 0", entries[0].Text);
        Assert.DoesNotContain(entries, e => e.Text.StartsWith("old", StringComparison.Ordinal));
        Assert.Equal(History.MaxEntries, File.ReadAllLines(_folder.File("history.txt")).Length);
    }

    [Fact]
    public void Deletes_one_or_all()
    {
        var store = NewStore();
        var changes = 0;
        store.Changed += () => changes++;
        foreach (var text in new[] { "one", "two", "three" })
        {
            _now += TimeSpan.FromSeconds(1);
            store.Add(text, "app", passwordField: false);
        }

        store.Delete(store.Entries()[1]);
        Assert.Equal(new[] { "three", "one" }, store.Entries().Select(e => e.Text));

        store.Clear();
        store.Flush(TimeSpan.FromSeconds(5));
        Assert.Empty(store.Entries());
        Assert.False(File.Exists(_folder.File("history.txt")));
        Assert.Equal(5, changes);
    }

    [Fact]
    public void Adds_from_many_threads_are_all_kept()
    {
        var store = new HistoryStore(_folder.File("history.txt"), () => true);
        Parallel.For(0, 200, i => store.Add($"dictation {i}", "app", passwordField: false));
        store.Flush(TimeSpan.FromSeconds(5));

        Assert.Equal(200, store.Entries().Count);
        Assert.Equal(200, new HistoryStore(_folder.File("history.txt"), () => true).Entries().Count);
    }

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(150, "2 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(5 * 3600, "5 hours ago")]
    [InlineData(26 * 3600, "yesterday")]
    [InlineData(3 * 86400, "3 days ago")]
    public void Says_when_in_words(int secondsAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Describe(Start - TimeSpan.FromSeconds(secondsAgo), Start));

    [Fact]
    public void Captions_name_the_app()
    {
        var time = Start.ToUnixTimeMilliseconds() - 120_000;
        Assert.Equal("Outlook · 2 minutes ago", RelativeTime.Caption(new HistoryEntry(time, "OUTLOOK", "x"), Start));
        Assert.Equal("Slack · 2 minutes ago", RelativeTime.Caption(new HistoryEntry(time, "slack", "x"), Start));
        Assert.Equal("Obscureapp · 2 minutes ago", RelativeTime.Caption(new HistoryEntry(time, "obscureapp", "x"), Start));
        Assert.Equal("2 minutes ago", RelativeTime.Caption(new HistoryEntry(time, string.Empty, "x"), Start));
    }

    private HistoryStore NewStore() => new(_folder.File("history.txt"), () => _keep, () => _now);
}

public sealed class SampleStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private DateTimeOffset _now = new(2026, 11, 19, 18, 0, 0, TimeSpan.FromHours(5.5));
    private bool _enabled = true;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Keeps_the_audio_and_a_note_when_switched_on()
    {
        var store = NewStore();
        var audio = Enumerable.Range(0, 16000).Select(i => MathF.Sin(i / 10f) * 0.5f).ToArray();

        Assert.True(store.Save(audio, "hey tim would love to connect", "Hey Tim, would love to connect.", ModelCatalog.EnglishLive, passwordField: false, "Inbox - Outlook"));
        store.Flush(TimeSpan.FromSeconds(5));

        var sample = Assert.Single(Directory.GetDirectories(store.Folder));
        Assert.Equal("20261119-180000-000", Path.GetFileName(sample));
        var wav = File.ReadAllBytes(Path.Combine(sample, "audio.wav"));
        Assert.Equal(44 + (audio.Length * 2), wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal(audio, ThumbFree.Speech.WavFile.ReadPcm16Mono16k(Path.Combine(sample, "audio.wav")), new Close());

        var note = JsonNode.Parse(File.ReadAllText(Path.Combine(sample, "meta.json")))!;
        Assert.Equal("en-kroko-live", note["model"]!.GetValue<string>());
        Assert.Equal("hey tim would love to connect", note["transcript"]!.GetValue<string>());
        Assert.Equal("Hey Tim, would love to connect.", note["typed"]!.GetValue<string>());
        Assert.Equal("0.8.0", note["app_version"]!.GetValue<string>());
        Assert.Equal(1.0, note["seconds"]!.GetValue<double>());
        Assert.StartsWith("2026-11-19T18:00:00", note["time"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(new SampleSummary(1, wav.Length + new FileInfo(Path.Combine(sample, "meta.json")).Length), store.Summary());
    }

    [Theory]
    [InlineData(false, false, "Inbox - Outlook")]
    [InlineData(true, true, "Inbox - Outlook")]
    [InlineData(true, false, "New Incognito Tab - Google Chrome (Incognito)")]
    [InlineData(true, false, "Bank - Personal - Microsoft​ Edge [InPrivate]")]
    [InlineData(true, false, "Mozilla Firefox Private Browsing")]
    public void Keeps_nothing_when_off_from_password_fields_or_private_windows(bool enabled, bool passwordField, string title)
    {
        _enabled = enabled;
        var store = NewStore();

        Assert.False(store.Save([0.1f, 0.2f], "secret", "secret", ModelCatalog.EnglishFast, passwordField, title));
        store.Flush(TimeSpan.FromSeconds(5));

        Assert.Equal(0, store.Summary().Count);
    }

    [Theory]
    [InlineData("New InPrivate tab - Microsoft​ Edge", true)]
    [InlineData("New Tab - Google Chrome (Incognito)", true)]
    [InlineData("Example Domain — Mozilla Firefox Private Browsing", true)]
    [InlineData("Private browsing - Opera", true)]
    [InlineData("New Private Tab - Brave (Private)", true)]
    [InlineData("Private Window - Vivaldi", true)]
    [InlineData("Inbox (3) - Gmail - Google Chrome", false)]
    [InlineData("Quarterly plan.docx - Word", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognises_private_browser_windows(string? title, bool isPrivate) => Assert.Equal(isPrivate, PrivateWindows.IsPrivate(title));

    [Fact]
    public void Keeps_only_the_newest_samples()
    {
        var store = new SampleStore(_folder.File("samples"), () => true, "0.8.0", () => _now, maxSamples: 10);
        for (var i = 0; i < 15; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            store.Save([0.1f], "a", "A.", ModelCatalog.EnglishLive, passwordField: false, null);
        }

        store.Flush(TimeSpan.FromSeconds(10));

        var names = Directory.GetDirectories(store.Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(10, names.Count);
        Assert.Equal("20261119-180006-000", names[0]);
        Assert.Equal("20261119-180015-000", names[^1]);
    }

    [Fact]
    public void Deletes_the_oldest_beyond_the_size_limit()
    {
        var store = new SampleStore(_folder.File("samples"), () => true, "0.8.0", () => _now, maxBytes: 100_000);
        var second = new float[16000];
        for (var i = 0; i < 5; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            store.Save(second, "a", "A.", ModelCatalog.EnglishLive, passwordField: false, null);
        }

        store.Flush(TimeSpan.FromSeconds(5));

        Assert.Equal(3, store.Summary().Count);
        Assert.True(store.Summary().Bytes <= 100_000);
    }

    [Fact]
    public async Task Saves_everything_in_one_zip_and_deletes_all()
    {
        var store = NewStore();
        for (var i = 0; i < 2; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            store.Save([0.1f, -0.1f], "a", "A.", ModelCatalog.Hinglish, passwordField: false, null);
        }

        var zipPath = _folder.File("out.zip");
        Assert.Equal(2, await store.ExportZipAsync(zipPath));
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.Equal(
                new[] { "20261119-180001-000/audio.wav", "20261119-180001-000/meta.json", "20261119-180002-000/audio.wav", "20261119-180002-000/meta.json" },
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        await store.DeleteAllAsync();
        Assert.Equal(0, store.Summary().Count);
        Assert.True(File.Exists(zipPath));
    }

    private SampleStore NewStore() => new(_folder.File("samples"), () => _enabled, "0.8.0", () => _now);

    private sealed class Close : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => Math.Abs(x - y) < 1e-4;

        public int GetHashCode(float obj) => 0;
    }
}

public sealed class NotifierTests
{
    private readonly FakeSurface _surface = new();
    private readonly DictationState _state = new();
    private readonly List<string> _copied = [];

    [Fact]
    public void Text_that_couldnt_be_typed_is_kept_and_a_click_copies_it()
    {
        var notifier = new Notifier(_surface, _state, _copied.Add);

        notifier.ShowUntyped("Hey Tim, would love to connect.");
        Assert.Equal("Hey Tim, would love to connect.", notifier.UntypedText);
        Assert.Contains("The text box closed before your text was ready. Click to copy it.", _surface.Shown[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tim", _surface.Shown[0].Text, StringComparison.Ordinal);

        _surface.Click();
        Assert.Equal(new[] { "Hey Tim, would love to connect." }, _copied);
        Assert.Null(notifier.UntypedText);
        Assert.Equal("Copied", _surface.Shown[^1].Title);

        // A click on the "Copied" confirmation does nothing more.
        _surface.Click();
        Assert.Single(_copied);
    }

    [Fact]
    public void The_microphone_warning_shows_once_and_goes_when_sound_returns()
    {
        var notifier = new Notifier(_surface, _state, _copied.Add);

        notifier.ShowMicrophoneSilent();
        notifier.ShowMicrophoneSilent();
        Assert.True(_state.MicrophoneSilent);
        Assert.Single(_surface.Shown);
        Assert.Equal("No sound from the microphone. Is another app or a call using it?", _surface.Shown[0].Text);

        notifier.ClearMicrophoneSilent();
        Assert.False(_state.MicrophoneSilent);
        Assert.Equal(1, _surface.Hidden);
    }

    [Fact]
    public void Clearing_the_warning_never_takes_back_kept_text()
    {
        var notifier = new Notifier(_surface, _state, _copied.Add);

        notifier.ShowMicrophoneSilent();
        notifier.ShowUntyped("Call me at 4 PM.", "That app didn't accept the text.");
        notifier.ClearMicrophoneSilent();

        Assert.Equal(0, _surface.Hidden);
        Assert.StartsWith("That app didn't accept the text.", _surface.Shown[^1].Text, StringComparison.Ordinal);
        Assert.True(notifier.CopyUntyped());
        Assert.Equal(new[] { "Call me at 4 PM." }, _copied);
    }
}

public sealed class ModelTests
{
    [Fact]
    public async Task A_download_shows_progress_then_the_model_is_installed()
    {
        var models = new FakeModelManager();
        var downloads = new ModelDownloads(models);
        var spec = ModelCatalog.EnglishLive;

        var task = downloads.Start(spec);
        Assert.Same(task, downloads.Start(spec));
        Assert.Equal(ModelStatus.Downloading, downloads.State(spec).Status);
        models.Report(spec, 0.5);
        Assert.Equal(0.5, downloads.State(spec).Progress);

        models.Finish(spec);
        await task;
        Assert.Equal(ModelStatus.Installed, downloads.State(spec).Status);
    }

    [Fact]
    public async Task Cancelling_leaves_no_error_and_a_failure_says_what_to_do()
    {
        var models = new FakeModelManager();
        var downloads = new ModelDownloads(models);
        var spec = ModelCatalog.Hinglish;

        var task = downloads.Start(spec);
        models.WaitStarted(spec);
        downloads.Cancel(spec);
        await task;
        Assert.Equal(new ModelState(ModelStatus.NotInstalled), downloads.State(spec));

        models.Failure = new System.Net.Http.HttpRequestException("503");
        await downloads.Start(spec);
        Assert.Contains("internet connection", downloads.State(spec).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_big_models_ask_first_with_size_memory_and_data()
    {
        Assert.Equal(new[] { ModelCatalog.EnglishAccurate, ModelCatalog.HinglishAccurate }, ModelCatalog.All.Where(ModelConsent.Needed));

        var low = ModelConsent.Message(ModelCatalog.EnglishAccurate, 4L << 30, metered: true);
        Assert.Contains("661 MB", low, StringComparison.Ordinal);
        Assert.Contains("This PC has 4 GB of memory. That may not be enough (6 GB or more is best)", low, StringComparison.Ordinal);
        Assert.Contains("metered connection", low, StringComparison.Ordinal);

        var plenty = ModelConsent.Message(ModelCatalog.HinglishAccurate, 32L << 30, metered: false);
        Assert.Contains("This PC has 32 GB of memory. That's enough.", plenty, StringComparison.Ordinal);
        Assert.DoesNotContain("metered", plenty, StringComparison.Ordinal);
        Assert.Equal("Download “Hindi + English, accurate”?", ModelConsent.Title(ModelCatalog.HinglishAccurate));
        Assert.Equal("79 MB", ModelConsent.Mb(ModelCatalog.EnglishLive.Bytes));
    }
}

public sealed class TextTests
{
    [Fact]
    public void The_guide_and_help_examples_are_rows_of_the_layout_vectors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "vectors", "layout.tsv")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var rows = File.ReadAllLines(Path.Combine(dir.FullName, "tests", "vectors", "layout.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .Select(c => (Flags: c[0], Input: c[1], Output: c[2].Replace("\\n", "\n", StringComparison.Ordinal)))
            .ToList();

        Assert.Contains(("mse", PracticeTexts.EmailHeard, PracticeTexts.EmailLaidOut), rows);
        Assert.Contains(("ms", PracticeTexts.ListHeard, PracticeTexts.ListLaidOut), rows);
        Assert.Equal(Spoken(PracticeTexts.EmailHeard), Spoken(PracticeTexts.EmailToSay));
        foreach (var (said, typed) in UI.HelpText.Sections(new AppSettings()).SelectMany(s => s.Examples ?? []))
        {
            Assert.Contains(rows, r => r.Output == typed && Spoken(r.Input) == Spoken(said));
        }

        static string Spoken(string text) => text.Replace("…", "...", StringComparison.Ordinal).ToLowerInvariant();
    }

    [Theory]
    [InlineData("Short one.", "Short one.")]
    [InlineData("Tom & Jerry", "Tom && Jerry")]
    [InlineData("Hey Tim,\n\nWould love to connect.\n\nThanks,\nBen", "Hey Tim, Would love to connect. Thanks, Ben")]
    [InlineData("Things to do today: order groceries, go on a walk, and call mom please", "Things to do today: order groceries, go on a walk…")]
    public void Tray_items_are_one_short_line(string text, string expected) => Assert.Equal(expected, UI.TrayMenu.MenuText(text));
}
