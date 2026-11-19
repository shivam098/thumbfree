using System.Text;
using ThumbFree.App.Platform;
using ThumbFree.Core;
using ThumbFree.Speech;

namespace ThumbFree.App.Tests;

/// <summary>
/// The dictation host with Windows, UI Automation, the microphone and the models faked: what gets typed where, and when.
/// </summary>
public sealed class DictationHostTests : IDisposable
{
    private static readonly WindowInfo Chat = new(42, 7, "WhatsApp", "Chat");

    private readonly TempFolder _folder = new();
    private readonly FakeDesktop _desktop = new();
    private readonly FakeFields _fields = new();
    private readonly FakeAudio _audio = new();
    private readonly FakeModels _models = new();
    private readonly FakeModelManager _downloads = new();
    private readonly FakeSurface _surface = new();
    private readonly List<StatusUpdate> _status = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _stopped = new();
    private readonly TaskCompletionSource<DictationOutcome> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AppServices _services;
    private readonly DictationHost _host;

    public DictationHostTests()
    {
        _services = TestServices.Create(_folder, models: _downloads);

        // Made off the test framework's synchronization context, so notifications happen inline, on any thread.
        _services.Notifier = Task.Run(() => new Notifier(_surface, _services.State, _ => { })).Result;
        _desktop.Window = Chat;
        _fields.Field = new FocusedField(Chat, [1, 2], Password: false, MultiLine: true, Search: false, Email: false, PrivateWindow: false, TextBefore: string.Empty, CaretKnown: true, Kind: "Edit multi-line");
        _host = new DictationHost(_services, _models, action => action(), _desktop, _fields, _audio, sounds: false);
        _host.Status += update =>
        {
            lock (_status)
            {
                _status.Add(update);
            }
        };
        _host.CaptureStopped += _stopped.Enqueue;
        _host.Finished += outcome => _finished.TrySetResult(outcome);
        _host.Start();
    }

    [Fact]
    public async Task Streaming_types_each_phrase_once_the_next_one_has_started_then_the_rest_at_the_end()
    {
        _models.Session.Script = accepted => accepted switch
        {
            < 5 => ([], "hello there"),
            < 10 => (["Hello there."], "how are"),
            _ => (["Hello there.", "How are you?"], "I'm fine"),
        };
        _models.Session.Final = ["Hello there.", "How are you?", "I'm fine"];

        _host.BeginDictationCapture(1);
        await _audio.SpeakAsync(seconds: 1.2);
        await WaitForAsync(() => _desktop.Typed.Length > 0);
        var typedWhileSpeaking = _desktop.Typed;
        _host.EndDictationCapture(cancel: false);
        var outcome = await FinishedAsync();

        Assert.Equal("Hello there.", typedWhileSpeaking);
        Assert.Equal("Hello there. How are you? I'm fine.", _desktop.Typed);
        Assert.True(outcome.TypedAll);
        Assert.Equal("Hello there. How are you? I'm fine.", Assert.Single(_services.History.Entries()).Text);
    }

    [Fact]
    public async Task Moving_to_another_text_box_types_nothing_more_and_keeps_the_dictation_to_copy()
    {
        _models.Session.Script = accepted => accepted < 5 ? (["Meet me at noon."], "then") : (["Meet me at noon.", "Bring the slides."], string.Empty);
        _models.Session.Final = ["Meet me at noon.", "Bring the slides.", "Thanks"];

        _host.BeginDictationCapture(1);
        await _audio.SpeakAsync(seconds: 1.2);
        await WaitForAsync(() => _desktop.Typed.Length > 0);
        _fields.Same = false;
        _host.EndDictationCapture(cancel: false);
        var outcome = await FinishedAsync();

        Assert.Equal("Meet me at noon.", _desktop.Typed);
        Assert.True(outcome.Kept);
        Assert.Equal("Meet me at noon. Bring the slides. Thanks.", _services.Notifier!.UntypedText);
        Assert.Contains(_surface.Shown, n => n.Title == "Your dictation is safe");
    }

    [Fact]
    public async Task A_password_field_stops_the_recording_and_keeps_nothing()
    {
        await _services.Settings.UpdateAsync(s => s.SaveSamples = true);
        _fields.Field = _fields.Field! with { Password = true, MultiLine = false, TextBefore = null };
        _models.Session.Script = _ => (["my secret"], string.Empty);
        _models.Session.Final = ["my secret", "again"];

        _host.BeginDictationCapture(7);
        await WaitForAsync(() => _stopped.Contains(7));
        await _audio.SpeakAsync(seconds: 0.5);

        Assert.Empty(_desktop.Typed);
        Assert.Empty(_services.History.Entries());
        Assert.Equal(0, _services.Samples.Summary().Count);
        Assert.Single(_status, s => s.Message.Contains("password", StringComparison.Ordinal));
        Assert.False(_audio.Running);
    }

    [Fact]
    public async Task A_password_field_the_first_read_missed_is_refused_when_typing_and_nothing_is_kept()
    {
        // A Chromium page whose accessibility wasn't ready, or UI Automation too slow: the read gives nothing, but the
        // check right before typing sees the password field.
        await _services.Settings.UpdateAsync(s => s.SaveSamples = true);
        _fields.Field = null;
        _desktop.Check = FieldCheck.Password;
        _models.Session.Script = accepted => accepted < 5 ? (["My secret words."], "and") : (["My secret words.", "And more."], string.Empty);
        _models.Session.Final = ["My secret words.", "And more.", "Done"];

        _host.BeginDictationCapture(5);
        await _audio.SpeakAsync(seconds: 1.2);
        await WaitForAsync(() => _stopped.Contains(5));

        Assert.Empty(_desktop.Typed);
        Assert.False(_audio.Running);
        Assert.Empty(_services.History.Entries());
        Assert.Equal(0, _services.Samples.Summary().Count);
        Assert.Null(_services.Notifier!.UntypedText);
        Assert.Null(_host.Inserter.LastText);
        Assert.Single(_status, s => s.Message.Contains("password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_password_field_first_seen_when_the_end_is_typed_keeps_nothing()
    {
        // Nothing was typed while speaking, so the check right before typing the end is the first to see it.
        await _services.Settings.UpdateAsync(s => s.SaveSamples = true);
        _fields.Field = null;
        _desktop.Check = FieldCheck.Password;

        var outcome = await _host.DictateAsync(["My secret words."], live: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("password field", outcome.Problem);
        Assert.Empty(_desktop.Typed);
        Assert.Empty(_services.History.Entries());
        Assert.Equal(0, _services.Samples.Summary().Count);
        Assert.Null(_services.Notifier!.UntypedText);
        Assert.Null(_host.Inserter.LastText);
    }

    [Fact]
    public async Task A_private_browser_window_keeps_no_history_even_when_its_box_cant_be_read()
    {
        _desktop.Window = new WindowInfo(43, 8, "msedge", "New tab - [InPrivate] - Microsoft Edge");
        _fields.Field = null;
        _models.Session.Script = _ => ([], "private words");
        _models.Session.Final = ["Private words."];

        _host.BeginDictationCapture(1);
        await _audio.SpeakAsync(seconds: 0.5);
        _host.EndDictationCapture(cancel: false);
        var outcome = await FinishedAsync();

        Assert.True(outcome.TypedAll);
        Assert.Equal("Private words.", _desktop.Typed);
        Assert.Empty(_services.History.Entries());
    }

    [Fact]
    public async Task Typing_that_fails_types_nothing_more_and_keeps_the_whole_dictation_to_copy()
    {
        _desktop.FailTyping = true;
        _models.Session.Script = accepted => accepted < 5 ? (["Hello there."], "how are") : (["Hello there.", "How are you?"], string.Empty);
        _models.Session.Final = ["Hello there.", "How are you?", "I'm fine"];

        _host.BeginDictationCapture(1);
        await _audio.SpeakAsync(seconds: 1.2);
        _host.EndDictationCapture(cancel: false);
        var outcome = await FinishedAsync();

        Assert.Empty(_desktop.Typed);
        Assert.True(outcome.Kept);
        Assert.Equal("Hello there. How are you? I'm fine.", _services.Notifier!.UntypedText);
        Assert.Equal("Hello there. How are you? I'm fine.", Assert.Single(_services.History.Entries()).Text);
    }

    [Fact]
    public async Task A_scripted_dictation_ends_even_when_typing_fails()
    {
        _desktop.FailTyping = true;

        var dictation = _host.DictateAsync(["Hello there."], live: false);
        var outcome = await dictation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(outcome.Kept);
    }

    [Fact]
    public async Task The_text_box_is_checked_right_before_typing_after_everything_else()
    {
        // The focus moves while the inserter checks the field (as when Ctrl+Tab is held while a phrase is typed).
        _desktop.OnCheck = () => _fields.Same = false;

        var outcome = await _host.DictateAsync(["Hello there."], live: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(_desktop.Typed);
        Assert.True(outcome.Kept);
    }

    [Fact]
    public async Task A_dictation_started_while_the_last_one_types_reads_its_box_again()
    {
        // The box's text as it is when read; typing lands late, as it does after Ctrl+Win while the keys are let go.
        _fields.Reader = () => _fields.Field! with { TextBefore = _desktop.Typed };
        _desktop.TypeDelayMs = 300;

        var first = _host.DictateAsync(["Hello there."], live: false);
        var second = _host.DictateAsync(["how are you?"], live: false);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Hello there. How are you?", _desktop.Typed);
    }

    [Fact]
    public async Task The_guides_practice_box_gets_the_dictation_and_the_app_in_front_gets_nothing()
    {
        var box = new FakePracticeBox();
        _services.Practice.SetActive(box);
        _models.Session.Script = accepted => accepted < 5 ? ([], "um things to do today") : (["Um, things to do today:"], "order groceries");
        _models.Session.Final = ["Um, things to do today:", "order groceries, go on a walk, and call mom"];

        _host.BeginDictationCapture(1);
        await _audio.SpeakAsync(seconds: 1.2);
        await WaitForAsync(() => box.Live.Count > 0);
        _host.EndDictationCapture(cancel: false);
        var outcome = await FinishedAsync();

        Assert.True(outcome.TypedAll);
        Assert.Equal("Things to do today: order groceries, go on a walk, and call mom.", box.Inserted);
        Assert.Empty(_desktop.Typed);
    }

    [Fact]
    public async Task A_model_that_re_reads_audio_types_a_phrase_after_the_pause_that_ends_the_next_one()
    {
        await _services.Settings.UpdateAsync(s => s.EnglishTier = Tier.Standard);

        _host.BeginDictationCapture(1);
        await _audio.SpeakAsync(seconds: 0.8, word: 0.3f);
        await _audio.SpeakAsync(seconds: 1.0, word: 0f);
        await _audio.SpeakAsync(seconds: 0.8, word: 0.5f);
        await _audio.SpeakAsync(seconds: 1.2, word: 0f);
        await WaitForAsync(() => _desktop.Typed.Length > 0);
        var typedWhileSpeaking = _desktop.Typed;
        await _audio.SpeakAsync(seconds: 0.8, word: 0.7f);
        _host.EndDictationCapture(cancel: false);
        var outcome = await FinishedAsync();

        Assert.Equal("Hello there.", typedWhileSpeaking);
        Assert.Equal("Hello there. How are you? I'm fine.", _desktop.Typed);
        Assert.True(outcome.TypedAll);
    }

    [Fact]
    public void Switching_language_applies_at_once_and_fetches_a_small_model_that_is_missing()
    {
        _models.Installed.Remove(ModelCatalog.Hinglish.Id);

        LanguageSwitch.Toggle(_services.Settings);

        _downloads.WaitStarted(ModelCatalog.Hinglish);
        Assert.Contains(_status, s => s.Message.Contains("Hindi + English", StringComparison.Ordinal));
        _host.BeginDictationCapture(3);
        Assert.Contains(3, _stopped);
        Assert.False(_audio.Running);
    }

    [Fact]
    public async Task Alt_Shift_H_keeps_the_model_chosen_for_each_language()
    {
        await _services.Settings.UpdateAsync(s =>
        {
            s.EnglishTier = Tier.Standard;
            s.HinglishTier = Tier.Accurate;
        });

        LanguageSwitch.Toggle(_services.Settings);
        Assert.Equal(ModelCatalog.HinglishAccurate, _services.Current.SelectedModel());
        LanguageSwitch.Toggle(_services.Settings);

        Assert.Equal(ModelCatalog.EnglishFast, _services.Current.SelectedModel());
        Assert.Equal(Tier.Accurate, _services.Current.HinglishTier);
    }

    [Fact]
    public void Exact_silence_means_the_microphone_was_cut_off_until_sound_comes_back()
    {
        var watch = new SilenceWatch(16_000);
        Assert.False(watch.Add(new float[16_000]));
        Assert.True(watch.Add(new float[800]));
        Assert.True(watch.Silent);
        Assert.True(watch.Add([0.01f]));
        Assert.False(watch.Silent);
        Assert.False(watch.Add(new float[48_000]));
        Assert.True(watch.Add(new float[100]));
        Assert.True(watch.Silent);
    }

    public void Dispose()
    {
        _host.Dispose();
        _folder.Dispose();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "timed out");
    }

    private async Task<DictationOutcome> FinishedAsync()
    {
        var done = await Task.WhenAny(_finished.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(_finished.Task, done);
        return await _finished.Task;
    }

    private sealed class FakeDesktop : IDesktop
    {
        private readonly StringBuilder _typed = new();

        public volatile bool FailTyping;

        public WindowInfo? Window { get; set; }

        /// <summary>What the inserter's own field check finds.</summary>
        public FieldCheck Check { get; set; } = FieldCheck.Safe;

        /// <summary>Runs during the inserter's field check.</summary>
        public Action? OnCheck { get; set; }

        /// <summary>How long a paste takes to land (as when Ctrl+Win is still being let go).</summary>
        public int TypeDelayMs { get; set; }

        public string Typed
        {
            get
            {
                lock (_typed)
                {
                    return _typed.ToString();
                }
            }
        }

        public WindowInfo? GetForegroundWindow() => Window;

        public FieldCheck CheckFocusedField()
        {
            OnCheck?.Invoke();
            return Check;
        }

        public bool? FocusedTextEndsWithNonWhitespace() => null;

        public void TypeText(string text)
        {
            if (FailTyping)
            {
                throw new InvalidOperationException("The clipboard is in use by another app.");
            }

            if (TypeDelayMs > 0)
            {
                Thread.Sleep(TypeDelayMs);
            }

            lock (_typed)
            {
                _typed.Append(text);
            }
        }

        public void PressKeys(string chord)
        {
            lock (_typed)
            {
                _typed.Append('\n');
            }
        }
    }

    private sealed class FakeFields : IFieldReader
    {
        public FocusedField? Field { get; set; }

        /// <summary>Reads the box as it is at the time (instead of <see cref="Field"/>).</summary>
        public Func<FocusedField?>? Reader { get; set; }

        public volatile bool Same = true;

        public FocusedField? Read(WindowInfo window) => Reader is { } read ? read() : Field;

        public bool? IsSameField(FocusedField start, WindowInfo? now) => Same;

        public bool IsElevated(WindowInfo window) => false;
    }

    /// <summary>A microphone the test speaks into: 100 ms buffers in real time.</summary>
    private sealed class FakeAudio : IAudioSource
    {
        public event Action<float[]>? Samples;

        public event Action<Exception>? Failed
        {
            add { }
            remove { }
        }

        public volatile bool Running;

        public void Start() => Running = true;

        public void Stop() => Running = false;

        public void Dispose() => Stop();

        /// <summary>Sound at <paramref name="word"/>'s level (a fake word the fake recognizer reads back); 0 is a pause.</summary>
        public async Task SpeakAsync(double seconds, float word = 0.3f)
        {
            for (var sent = 0.0; sent < seconds; sent += 0.1)
            {
                var buffer = new float[1600];
                Array.Fill(buffer, word == 0 ? 0.0002f : word);
                if (Running)
                {
                    Samples?.Invoke(buffer);
                }

                await Task.Delay(100);
            }
        }
    }

    /// <summary>
    /// Models that are all installed. The streaming session follows <see cref="FakeSession.Script"/>; the others hear a
    /// word for each level of sound (0.3 "Hello there.", 0.5 "How are you?", 0.7 "I'm fine."), and speech is anything
    /// louder than 0.1.
    /// </summary>
    private sealed class FakeModels : ISpeechModels
    {
        private static readonly Dictionary<int, string> Words = new() { [3] = "Hello there.", [5] = "How are you?", [7] = "I'm fine." };

        public HashSet<string> Installed { get; } = [.. ModelCatalog.All.Select(m => m.Id)];

        public FakeSession Session { get; } = new();

        public bool IsInstalled(ModelSpec spec) => Installed.Contains(spec.Id);

        public IRecognizer Load(ModelSpec spec, int threads) => new FakeRecognizer();

        public ILiveSession StartSession(IRecognizer model) => Session;

        public IReadOnlyList<(TimeSpan Start, TimeSpan End)> DetectSpeech(float[] audio)
        {
            var regions = new List<(TimeSpan, TimeSpan)>();
            int? start = null;
            for (var i = 0; i <= audio.Length; i++)
            {
                var speech = i < audio.Length && audio[i] > 0.1f;
                if (speech && start is null)
                {
                    start = i;
                }
                else if (!speech && start is { } s)
                {
                    regions.Add((TimeSpan.FromSeconds(s / 16_000.0), TimeSpan.FromSeconds(i / 16_000.0)));
                    start = null;
                }
            }

            return regions;
        }

        public void Dispose()
        {
        }

        private sealed class FakeRecognizer : IRecognizer
        {
            public string? Transcribe(float[] samples16k, bool preview, string? prompt = null) =>
                string.Join(' ', samples16k.Where(s => s > 0.1f).Select(s => (int)MathF.Round(s * 10)).Distinct().Select(level => Words.GetValueOrDefault(level, "?")));

            public void Abort()
            {
            }

            public void Resume()
            {
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeSession : ILiveSession
    {
        private int _accepted;

        public Func<int, (string[] Finished, string Partial)> Script { get; set; } = _ => ([], string.Empty);

        public string[] Final { get; set; } = [];

        public IReadOnlyList<string> Finished => Script(_accepted).Finished;

        public string Partial => Script(_accepted).Partial;

        public void Accept(float[] chunk) => _accepted++;

        public IReadOnlyList<string> Finish() => Final;

        public void Dispose()
        {
        }
    }

    private sealed class FakePracticeBox : IPracticeTarget
    {
        public PracticeField Field { get; } = new(MultiLine: true, AtStart: true, Email: false);

        public List<string?> Live { get; } = [];

        public string? Inserted { get; private set; }

        public void ShowLive(string? text)
        {
            lock (Live)
            {
                Live.Add(text);
            }
        }

        public void Insert(string text) => Inserted = text;
    }
}

internal static class SettingsStoreTestExtensions
{
    /// <summary>A change as the Settings page makes it (on the test's thread, which stands in for the UI thread).</summary>
    public static Task UpdateAsync(this SettingsStore store, Action<AppSettings> change)
    {
        store.Update(change);
        return Task.CompletedTask;
    }
}
