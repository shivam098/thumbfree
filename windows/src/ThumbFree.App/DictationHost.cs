using System.Threading.Channels;
using System.Windows.Threading;
using ThumbFree.App.Platform;
using ThumbFree.Core;
using ThumbFree.Speech;

namespace ThumbFree.App;

/// <summary>What happened to a dictation (for self-tests and logs).</summary>
/// <param name="Text">The whole dictation as laid out; empty when nothing was heard or it was refused.</param>
/// <param name="TypedAll">Everything went into the text box it started in (or the guide's practice box).</param>
/// <param name="Kept">It went to the tap-to-copy notification instead: the box changed or didn't take the text.</param>
/// <param name="Problem">Why nothing more was typed (refused, nothing heard, recognition failed); null otherwise.</param>
internal sealed record DictationOutcome(string Text, bool TypedAll, bool Kept, string? Problem);

/// <summary>
/// Tap-to-dictate (PLAN 3.0): Ctrl+Win opens the microphone, Ctrl+Win again finishes, Esc cancels. The text box is
/// chosen when dictation starts and text only ever goes there (3.6, 3.10 rule 1): finished phrases are typed at pauses
/// while you speak (<see cref="LiveTyping"/>, the rules every platform shares), the words not typed yet show in the
/// pill, and the rest is typed when you finish. A dictation that can't go to its box is kept for copying. Recognition
/// runs on one speech thread (<see cref="SpeechThread"/>); field checks and typing run on one serial queue, in order.
/// </summary>
internal sealed class DictationHost : IDisposable
{
    private const int SampleRate = Decoding.SampleRate;

    /// <summary>Streaming models get audio 100 ms at a time, as on Android.</summary>
    private const int ChunkSamples = SampleRate / 10;

    private const int LevelIntervalMs = 50;

    /// <summary>Models that aren't streaming re-read the newest audio this often while you speak (PLAN 3.10).</summary>
    private const int PreviewIntervalMs = 500;

    /// <summary>A pause this long ends a phrase, which is then transcribed once and can be typed (Kroko: 0.8 s).</summary>
    private const int PhrasePauseMs = 700;

    private const string PasswordRefusal = "That's a password field — I won't type there.";

    /// <summary>A model nobody has dictated with for this long is unloaded (PLAN 3.1).</summary>
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(3);

    private readonly AppServices _services;
    private readonly IDesktop _desktop;
    private readonly GuardedDesktop _guarded;
    private readonly IFieldReader _fields;
    private readonly IAudioSource _audio;
    private readonly SpeechThread _speech;
    private readonly Action<Action> _post;
    private readonly bool _soundsAllowed;
    private readonly SoundFeedback _sounds;
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _idleTimer;
    private volatile DictationFormatter _formatter;
    private DictationInserter _inserter;
    private volatile Dictation? _current;
    private int _active;
    private long _lastLevelTicks;
    private Task? _queueTask;

    public DictationHost(AppServices services, ISpeechModels models, Dispatcher ui)
        : this(
            services,
            models,
            action =>
            {
                if (!ui.HasShutdownStarted)
                {
                    ui.BeginInvoke(action);
                }
            },
            new WindowsDesktop(),
            new UiaFieldReader(),
            new MicrophoneCapture(services.Current.MicrophoneDevice),
            sounds: true)
    {
    }

    /// <param name="post">Runs an action on the UI thread (later).</param>
    /// <param name="sounds">Play the start and stop tones when Settings say so (off in tests).</param>
    internal DictationHost(AppServices services, ISpeechModels models, Action<Action> post, IDesktop desktop, IFieldReader fields, IAudioSource audio, bool sounds)
    {
        _services = services;
        _post = post;
        _desktop = desktop;
        _guarded = new GuardedDesktop(desktop, desktop is WindowsDesktop ? () => KeyboardInput.WaitForModifiersReleased() : () => { });
        _fields = fields;
        _audio = audio;
        _speech = new SpeechThread(models);
        _idleTimer = new Timer(_ => _speech.Post(UnloadIfIdle));
        var settings = services.Current;
        WindowsDesktop.InsertMethod = settings.InsertMethod;
        _soundsAllowed = sounds;
        _sounds = new SoundFeedback(sounds && settings.SoundFeedback);
        _formatter = NewFormatter(settings);
        _inserter = NewInserter(_formatter, lastText: null);
        _audio.Samples += OnSamples;
        _audio.Failed += OnAudioFailed;
        services.Settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<StatusUpdate>? Status;

    /// <summary>
    /// Raised on the UI thread with the id given to <see cref="BeginDictationCapture"/> when that recording couldn't start
    /// or stopped by itself (password field, time limit, microphone lost), so the keys stop acting as if recording.
    /// </summary>
    public event Action<int>? CaptureStopped;

    /// <summary>
    /// Raised on the UI thread with <see cref="DictationLabels.Listening"/> or <see cref="DictationLabels.Transcribing"/>;
    /// null clears it.
    /// </summary>
    public event Action<string?>? Activity;

    /// <summary>Raised on the UI thread about 20 times a second while recording with the microphone level (0 to 1).</summary>
    public event Action<float>? InputLevel;

    /// <summary>Raised on the UI thread with the words said but not typed yet (null clears them), for the pill.</summary>
    public event Action<string?>? DictationPreview;

    /// <summary>Raised on the queue when a dictation is over.</summary>
    internal event Action<DictationOutcome>? Finished;

    /// <summary>For scripted self-tests (call only from <see cref="RunOnQueueAsync"/>).</summary>
    internal DictationInserter Inserter => _inserter;

    public void Start() => _queueTask = Task.Run(ProcessQueueAsync);

    /// <summary>
    /// UI thread: starts recording for the text box that has the focus (or the guide's practice box). <paramref name="id"/>
    /// identifies this recording in <see cref="CaptureStopped"/>.
    /// </summary>
    public void BeginDictationCapture(int id)
    {
        if (_current is not null)
        {
            return;
        }

        var settings = _services.Current;
        var spec = settings.SelectedModel();
        if (!_speech.Models.IsInstalled(spec))
        {
            ModelMissing(spec, openHome: true);
            ReportCaptureStopped(id);
            return;
        }

        var practice = _services.Practice.Active;
        var window = practice is null ? _desktop.GetForegroundWindow() : null;
        if (window is not null && _fields.IsElevated(window))
        {
            Report(StatusKind.Warning, "That window runs as administrator; Windows won't let me type into it.");
            ReportCaptureStopped(id);
            return;
        }

        var live = settings.LiveTyping && (spec.Streaming || spec.Engine == Engine.Parakeet || spec.LivePreview);
        var d = new Dictation(id, spec, settings, _formatter, window, practice, live, PreviewModel(settings, spec, live));
        try
        {
            _audio.Start();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or IOException)
        {
            Log.Error($"microphone start failed: {ex.GetType().Name}");
            Report(StatusKind.Error, "Couldn't open the microphone. Check Settings › Privacy › Microphone.");
            ReportCaptureStopped(id);
            return;
        }

        _current = d;
        Interlocked.Increment(ref _active);
        _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _sounds.Play(SoundFeedback.Cue.StartTyping);
        PostActivity(DictationLabels.Listening);
        Log.Info($"dictation {id}: {spec.Id}, {(live ? "live typing" : "typed at the end")}{(d.PreviewSpec is { } p ? $", preview {p.Id}" : string.Empty)}{(practice is null ? string.Empty : ", practice box")}{(window is null && practice is null ? ", no window" : string.Empty)}");
        if (window is not null)
        {
            d.TypedAtRead = _guarded.Typed;
            d.FieldRead = Task.Run(() => _fields.Read(window));
            _ = d.FieldRead.ContinueWith(
                read =>
                {
                    if (read.Result?.Password == true)
                    {
                        _post(() => Refuse(d));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }

        _speech.Post(() => Open(d));
        if (d.PreviewSpec is not null)
        {
            d.PreviewTimer = new Timer(_ => PreviewTick(d), null, PreviewIntervalMs, PreviewIntervalMs);
        }
    }

    /// <summary>UI thread: stops recording; types the rest unless <paramref name="cancel"/>.</summary>
    public void EndDictationCapture(bool cancel)
    {
        if (_current is { } d)
        {
            Stop(d, cancel, quiet: false);
        }
    }

    /// <summary>Types the last dictation again where the cursor is (Alt+Shift+Z).</summary>
    public void PasteLastTranscript() => Enqueue(() =>
    {
        _inserter.RepeatLast();
        return Task.CompletedTask;
    });

    internal Task RunOnQueueAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>
    /// Self-tests: dictates <paramref name="phrases"/> into the focused text box without a microphone or a model, each as
    /// if followed by a pause. With <paramref name="live"/> they're typed as they come (live typing); without, at the end.
    /// </summary>
    internal Task<DictationOutcome> DictateAsync(IReadOnlyList<string> phrases, bool live)
    {
        var window = _desktop.GetForegroundWindow();
        var settings = _services.Current;
        var d = new Dictation(0, settings.SelectedModel(), settings, _formatter, window, practice: null, live, previewSpec: null);
        if (window is not null)
        {
            d.TypedAtRead = _guarded.Typed;
            d.FieldRead = Task.Run(() => _fields.Read(window));
        }

        Interlocked.Increment(ref _active);
        var done = new TaskCompletionSource<DictationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                for (var i = 1; live && i <= phrases.Count; i++)
                {
                    lock (d.Gate)
                    {
                        d.Phrases.Clear();
                        d.Phrases.AddRange(phrases.Take(i));
                    }

                    await UpdateAsync(d).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"self-test dictation failed while speaking: {ex.GetType().Name}");
            }

            try
            {
                done.SetResult(await FinishAsync(d, Task.FromResult<IReadOnlyList<string>?>(phrases), []).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    public void Dispose()
    {
        _services.Settings.Changed -= OnSettingsChanged;
        if (_current is { } d)
        {
            d.Stopped = true;
            d.Cancelled = true;
            d.PreviewTimer?.Dispose();
        }

        _audio.Stop();
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            _queueTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            Log.Warn($"queue ended with {ex.InnerException?.GetType().Name}");
        }

        _idleTimer.Dispose();
        _speech.Dispose();
        _audio.Dispose();
        _sounds.Dispose();
    }

    /// <summary>
    /// The model that re-reads the newest audio while you speak (for live typing, or only for the pill), or null. Streaming
    /// models report their text themselves; with Live typing off nothing re-reads audio (PLAN 3.10, rule 8). The accurate
    /// Hindi + English model takes seconds per pass, so the standard one previews for it when it's here.
    /// </summary>
    private ModelSpec? PreviewModel(AppSettings settings, ModelSpec spec, bool live)
    {
        if (!settings.LiveTyping || spec.Streaming)
        {
            return null;
        }

        if (live)
        {
            return spec;
        }

        return spec.Language == Language.Hinglish && spec.Id != ModelCatalog.Hinglish.Id && _speech.Models.IsInstalled(ModelCatalog.Hinglish)
            ? ModelCatalog.Hinglish
            : null;
    }

    /// <summary>UI thread: the chosen model isn't on this PC. Small models download straight away; big ones ask first (Home).</summary>
    private void ModelMissing(ModelSpec spec, bool openHome)
    {
        var models = _services.Models;
        var state = models.State(spec);
        if (state.Status == ModelStatus.Downloading)
        {
            Report(StatusKind.Info, $"The speech model is still downloading ({(int)(state.Progress * 100)}%). Try again when it's done.");
        }
        else if (!ModelConsent.Needed(spec))
        {
            _ = models.Start(spec);
            Report(StatusKind.Info, "Downloading the speech model (one time; it stays on this PC). Try again in a moment.");
        }
        else
        {
            Report(StatusKind.Warning, $"Download “{ModelConsent.ShortName(spec)}” first, on ThumbFree's Home page.");
            if (openHome)
            {
                _services.OpenPage(UI.AppPage.Home);
            }
        }
    }

    /// <summary>UI thread: the dictation's text box is a password field: stop, and keep nothing.</summary>
    private void Refuse(Dictation d)
    {
        if (Interlocked.Exchange(ref d.RefusalShown, 1) == 0)
        {
            Report(StatusKind.Warning, PasswordRefusal);
        }

        d.Refused = true;
        if (ReferenceEquals(_current, d))
        {
            ReportCaptureStopped(d.Id);
            Stop(d, cancel: true, quiet: true);
        }
    }

    /// <summary>UI thread: stops recording <paramref name="d"/>, then finishes it unless <paramref name="cancel"/>.</summary>
    private void Stop(Dictation d, bool cancel, bool quiet)
    {
        if (!ReferenceEquals(_current, d))
        {
            return;
        }

        _current = null;
        d.Stopped = true;
        d.PreviewTimer?.Dispose();
        _audio.Stop();
        if (d.Silence.Silent)
        {
            _services.Notifier?.ClearMicrophoneSilent();
        }

        _sounds.Play(cancel ? SoundFeedback.Cue.Standby : SoundFeedback.Cue.StopTyping);
        ShowPreview(null);
        d.Practice?.ShowLive(null);
        if (cancel)
        {
            d.Cancelled = true;
            PostActivity(null);
            _speech.Post(() => Close(d));
            if (!quiet)
            {
                Report(StatusKind.Info, d.TypedAny ? "Dictation cancelled. The words already typed stay." : "Dictation cancelled.");
            }

            Done(d);
            return;
        }

        PostActivity(DictationLabels.Transcribing);
        float[] audio;
        lock (d.Gate)
        {
            audio = [.. d.Audio];
        }

        var phrases = _speech.Run(() => FinishSpeech(d, audio));
        Enqueue(async () => await FinishAsync(d, phrases, audio).ConfigureAwait(false));
    }

    /// <summary>The recording reached <see cref="AppSettings.MaxDictationSeconds"/>: finish it as if Ctrl+Win was tapped.</summary>
    private void FinishAtLimit(Dictation d, int seconds)
    {
        if (!ReferenceEquals(_current, d))
        {
            return;
        }

        Log.Info($"dictation reached the {seconds}s limit");
        ReportCaptureStopped(d.Id);
        Stop(d, cancel: false, quiet: true);
        var limit = seconds % 60 == 0 ? $"{seconds / 60} minute{(seconds == 60 ? string.Empty : "s")}" : $"{seconds} seconds";
        Report(StatusKind.Info, $"Dictation stops by itself after {limit}. Tap Ctrl+Win to keep going.");
    }

    private void OnAudioFailed(Exception ex)
    {
        Log.Error($"microphone stopped: {ex.GetType().Name}");
        _post(() =>
        {
            if (_current is not { } d)
            {
                return;
            }

            // PLAN 3.6: write what was said so far, and say so.
            ReportCaptureStopped(d.Id);
            _services.Notifier?.ShowInterrupted();
            Report(StatusKind.Warning, "The microphone stopped. What you said so far is being written.");
            Stop(d, cancel: false, quiet: true);
        });
    }

    /// <summary>Capture thread.</summary>
    private void OnSamples(float[] samples)
    {
        var d = _current;
        if (d is null || d.Stopped)
        {
            return;
        }

        int total;
        lock (d.Gate)
        {
            d.Audio.AddRange(samples);
            total = d.Audio.Count;
        }

        var now = Environment.TickCount64;
        if (now - _lastLevelTicks >= LevelIntervalMs)
        {
            _lastLevelTicks = now;
            var level = AudioLevel.Of(samples);
            _post(() => InputLevel?.Invoke(level));
        }

        if (d.Silence.Add(samples))
        {
            if (d.Silence.Silent)
            {
                Log.Warn("microphone sends only silence");
                _services.Notifier?.ShowMicrophoneSilent();
                Report(StatusKind.Warning, "No sound from the microphone. Is another app or a call using it?");
            }
            else
            {
                _services.Notifier?.ClearMicrophoneSilent();
            }
        }

        if (d.Spec.Streaming && d.Ready && total - d.Fed >= ChunkSamples && Interlocked.Exchange(ref d.FeedQueued, 1) == 0)
        {
            _speech.Post(() => Feed(d));
        }

        var max = _services.Current.MaxDictationSeconds;
        if (max > 0 && !d.LimitReached && total >= (long)max * SampleRate)
        {
            d.LimitReached = true;
            _post(() => FinishAtLimit(d, max));
        }
    }

    /// <summary>Timer thread: asks the speech thread to re-read the newest audio, unless it's still busy with the last pass.</summary>
    private void PreviewTick(Dictation d)
    {
        if (d.Stopped || !d.Ready || d.Preview is not { } preview)
        {
            return;
        }

        int pending;
        lock (d.Gate)
        {
            pending = d.Audio.Count - preview.CommittedSamples;
        }

        if (pending >= SampleRate / 2 && Interlocked.Exchange(ref d.PreviewBusy, 1) == 0)
        {
            _speech.Post(() => Reread(d));
        }
    }

    // ---- speech thread ----

    /// <summary>Loads the model (audio is recorded meanwhile) and opens the streaming session or the live preview.</summary>
    private void Open(Dictation d)
    {
        if (d.Cancelled)
        {
            return;
        }

        try
        {
            var model = _speech.Model(d.Spec, Threads(d.Spec), keep: d.PreviewSpec);
            model.Resume();
            if (d.Spec.Streaming)
            {
                d.Session = _speech.Models.StartSession(model);
            }
            else if (d.PreviewSpec is { } previewSpec)
            {
                var previewModel = previewSpec.Id == d.Spec.Id ? model : _speech.Model(previewSpec, Threads(previewSpec), keep: d.Spec);
                previewModel.Resume();
                var previewPrompt = previewSpec.PromptVocabulary ? d.Prompt : null;
                d.Preview = new DictationPreview(
                    audio => Task.FromResult(Prepare(d, previewModel.Transcribe(audio, preview: true, previewPrompt), audio.Length)),
                    _speech.Models.DetectSpeech,
                    SampleRate,
                    phrasePauseMs: d.Live ? PhrasePauseMs : null,
                    commit: d.Live ? audio => Task.FromResult(Prepare(d, model.Transcribe(audio, preview: false, d.Prompt), audio.Length)) : null);
            }

            d.Ready = true;
            if (d.Session is not null)
            {
                Feed(d);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"model {d.Spec.Id} didn't load: {ex.GetType().Name} 0x{ex.HResult:X8}");
            d.LoadFailed = true;
            _post(() =>
            {
                Report(StatusKind.Error, "Couldn't load the speech model. Delete it in Settings, then download it again.");
                if (ReferenceEquals(_current, d))
                {
                    ReportCaptureStopped(d.Id);
                    Stop(d, cancel: true, quiet: true);
                }
            });
        }
    }

    /// <summary>Gives the streaming session the audio recorded since the last time, 100 ms at a time.</summary>
    private void Feed(Dictation d)
    {
        Volatile.Write(ref d.FeedQueued, 0);
        if (d.Session is not { } session || d.Cancelled)
        {
            return;
        }

        float[] fresh;
        lock (d.Gate)
        {
            fresh = d.Audio.GetRange(d.Fed, d.Audio.Count - d.Fed).ToArray();
            d.Fed = d.Audio.Count;
        }

        for (var i = 0; i < fresh.Length; i += ChunkSamples)
        {
            session.Accept(fresh[i..Math.Min(fresh.Length, i + ChunkSamples)]);
        }

        var finished = session.Finished;
        var partial = Prepare(d, session.Partial, 0);
        lock (d.Gate)
        {
            for (var i = d.Phrases.Count; i < finished.Count; i++)
            {
                d.Phrases.Add(Prepare(d, finished[i], 0));
            }

            d.Partial = partial;
        }

        ScheduleUpdate(d);
    }

    /// <summary>Re-reads the audio after the last finished phrase (models that aren't streaming).</summary>
    private void Reread(Dictation d)
    {
        try
        {
            if (d.Stopped || d.Preview is not { } preview)
            {
                return;
            }

            float[] pending;
            lock (d.Gate)
            {
                pending = d.Audio.GetRange(preview.CommittedSamples, d.Audio.Count - preview.CommittedSamples).ToArray();
            }

            preview.UpdateAsync(pending).GetAwaiter().GetResult();
            lock (d.Gate)
            {
                if (d.Live)
                {
                    d.Phrases.Clear();
                    d.Phrases.AddRange(preview.Phrases);
                    d.Partial = preview.Tentative;
                }
                else
                {
                    // Only the pill: the model that types reads everything once you finish.
                    d.Partial = LiveText.Join(preview.Phrases, preview.Tentative);
                }
            }

            ScheduleUpdate(d);
        }
        finally
        {
            Volatile.Write(ref d.PreviewBusy, 0);
        }
    }

    /// <summary>
    /// The dictation's phrases once recording stops: every phrase typed or shown before, then the rest. Null when the speech
    /// couldn't be recognized. A streaming session already heard everything; other models transcribe the audio after the
    /// last finished phrase, cut at pauses into pieces they can take (14 s for Whisper, 30 s for Parakeet).
    /// </summary>
    private IReadOnlyList<string>? FinishSpeech(Dictation d, float[] audio)
    {
        if (d.LoadFailed || d.Refused || !d.Ready)
        {
            return null;
        }

        try
        {
            if (d.Session is { } session)
            {
                Feed(d);
                var all = session.Finish();
                session.Dispose();
                d.Session = null;
                lock (d.Gate)
                {
                    for (var i = d.Phrases.Count; i < all.Count; i++)
                    {
                        d.Phrases.Add(Prepare(d, all[i], 0));
                    }

                    // The last phrase, cut at a pause, may end in a comma: the end of a dictation ends a sentence (as on Android).
                    var phrases = d.Phrases.ToList();
                    var last = phrases.FindLastIndex(p => d.Formatter.Format(p).Length > 0);
                    if (last >= 0)
                    {
                        phrases[last] = LiveText.EndSentence(phrases[last]);
                    }

                    return phrases;
                }
            }

            var model = _speech.Model(d.Spec, Threads(d.Spec), keep: d.PreviewSpec);
            model.Resume();
            var result = new List<string>();
            var from = 0;
            if (d.Live && d.Preview is { } preview)
            {
                result.AddRange(preview.Phrases);
                from = Math.Min(preview.CommittedSamples, audio.Length);
            }

            d.Preview = null;
            var tail = audio[from..];
            var maxMs = d.Spec.MaxPieceSamples * 1000 / SampleRate;
            foreach (var piece in SpeechSegments.Chunk(tail, _speech.Models.DetectSpeech(tail), SampleRate, maxMs, 200))
            {
                if (model.Transcribe(piece, preview: false, d.Prompt) is not { } text)
                {
                    return null;
                }

                result.Add(Prepare(d, text, piece.Length));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"recognition failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return null;
        }
    }

    private void Close(Dictation d)
    {
        d.Session?.Dispose();
        d.Session = null;
        d.Preview = null;
    }

    private void UnloadIfIdle()
    {
        if (Volatile.Read(ref _active) == 0)
        {
            _speech.UnloadAll();
        }
    }

    // ---- queue: the field, typing, the end of each dictation ----

    private void ScheduleUpdate(Dictation d)
    {
        if (Interlocked.Exchange(ref d.UpdateQueued, 1) == 0)
        {
            Enqueue(() => UpdateAsync(d));
        }
    }

    /// <summary>Types the phrases that are final and shows the rest in the pill (or the practice box).</summary>
    private async Task UpdateAsync(Dictation d)
    {
        Volatile.Write(ref d.UpdateQueued, 0);
        if (d.Stopped && d.Id != 0)
        {
            // The end of the dictation types everything still to type, laid out.
            return;
        }

        List<string> phrases;
        string partial;
        lock (d.Gate)
        {
            phrases = [.. d.Phrases];
            partial = d.Partial;
        }

        if (d.Practice is { } box)
        {
            var words = d.Formatter.Format(LiveText.Join(phrases, partial));
            box.ShowLive(words.Length > 0 ? words : null);
            return;
        }

        if (await TypingAsync(d).ConfigureAwait(false) is not { } typing || (d.Stopped && d.Id != 0))
        {
            return;
        }

        var step = typing.Update(phrases, partial);
        ShowPreview(step.Pending);
        if (step.Type.Length > 0)
        {
            TypeIntoField(d, step.Type);
        }
    }

    /// <summary>
    /// The live typing of <paramref name="d"/>, once its text box is known (UI Automation, read when it started). Null for a
    /// password field.
    /// </summary>
    private async Task<LiveTyping?> TypingAsync(Dictation d)
    {
        if (d.Typing is not null || d.Refused)
        {
            return d.Typing;
        }

        FocusedField? field = null;
        try
        {
            field = await d.FieldRead.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"field read failed: {ex.GetType().Name}");
        }

        d.Field = field;

        // Typing since the box was read (the end of the previous dictation, Alt+Shift+Z) changed the text before the
        // cursor: read it again, now that this dictation's turn to type has come.
        if (field is not null && d.Window is { } window && _guarded.Typed != d.TypedAtRead)
        {
            var again = await Task.Run(() => _fields.Read(window)).ConfigureAwait(false);
            if (again is not null && (field.ElementId is null || (again.ElementId is { } id && id.AsSpan().SequenceEqual(field.ElementId))))
            {
                Log.Info($"dictation {d.Id}: text before the cursor read again after typing");
                field = field with { TextBefore = again.TextBefore, CaretKnown = again.CaretKnown, Password = field.Password || again.Password };
                d.Field = field;
            }
        }

        // A box UI Automation can't read is treated as one that takes line breaks, as Voice Dictation did: "new line"
        // presses the app's new-line keys (Shift+Enter in chat apps). A browser window that says it's private stays
        // private.
        d.Info = field?.ToFieldInfo() ?? new FieldInfo(MultiLine: true, Private: d.Window is { } w && FocusInspector.IsPrivateWindow(w));
        if (d.Info.Password)
        {
            d.Refused = true;
            return null;
        }

        if (d.Window is null)
        {
            Detach(d, "There was no text box to type into.");
        }

        Log.Info($"dictation {d.Id} field: {field?.Kind ?? "unknown"}{(field?.TextBefore is { } before ? $", {before.Length} chars before" : string.Empty)}");
        d.Typing = new LiveTyping(d.Formatter, new LiveTypingContext(field?.TextBefore, d.Info, d.Live, d.Smart));
        return d.Typing;
    }

    /// <summary>
    /// Types into the dictation's own text box only. The box is checked right before each paste or key press, after any
    /// held modifier keys are let go (<see cref="GuardedDesktop"/>): a different window or box detaches the dictation
    /// (PLAN 3.6). A password field that the read at the start missed (a Chromium page not ready yet, or UI Automation
    /// too slow) refuses it, and then nothing of it is kept.
    /// </summary>
    private bool TypeIntoField(Dictation d, string text)
    {
        if (d.DetachReason is not null || d.Refused || text.Length == 0)
        {
            return false;
        }

        _guarded.Guard = () => SameField(d);
        try
        {
            if (_inserter.TypePrepared(text, d.Info))
            {
                d.TypedAny = true;
                return true;
            }

            if (_guarded.LastCheck == FieldCheck.Password)
            {
                // The inserter has said so already.
                Interlocked.Exchange(ref d.RefusalShown, 1);
                d.Refused = true;
                _post(() => Refuse(d));
                return false;
            }

            Detach(d, "The text box didn't take it.");
            return false;
        }
        catch (FieldChangedException)
        {
            Detach(d, "You moved to another window or text box, so it wasn't typed there.");
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The clipboard was busy, Windows blocked the input, or a new-line key in the settings is wrong.
            Log.Error($"typing failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            Detach(d, "The text box didn't take it.");
            return false;
        }
        finally
        {
            _guarded.Guard = null;
        }
    }

    /// <summary>The dictation's text box has the focus (or it can't be told in time, in the same window).</summary>
    private bool SameField(Dictation d)
    {
        var now = _desktop.GetForegroundWindow();
        var same = d.Field is { } field
            ? _fields.IsSameField(field, now)
            : now is not null && d.Window is not null && now.Handle == d.Window.Handle;
        if (same is null)
        {
            Log.Info("same-field check timed out in the same window; typing");
        }

        return same != false;
    }

    private static void Detach(Dictation d, string reason)
    {
        if (d.DetachReason is null)
        {
            d.DetachReason = reason;
            Log.Info($"dictation {d.Id}: nothing more is typed ({reason})");
        }
    }

    /// <summary>
    /// The end of a dictation: types the rest into its text box (or the practice box), or keeps it for copying, then
    /// History and samples.
    /// </summary>
    private async Task<DictationOutcome> FinishAsync(Dictation d, Task<IReadOnlyList<string>?> recognized, float[] audio)
    {
        var outcome = new DictationOutcome(string.Empty, false, false, "stopped");
        try
        {
            var phrases = await recognized.ConfigureAwait(false);
            if (d.Refused)
            {
                outcome = outcome with { Problem = "password field" };
                return outcome;
            }

            if (phrases is null)
            {
                Report(StatusKind.Error, d.TypedAny ? "Couldn't transcribe the rest. The words already typed stay." : "Couldn't transcribe that. Try again.");
                outcome = outcome with { Problem = "recognition failed" };
                return outcome;
            }

            var raw = LiveText.Join(phrases, string.Empty);
            if (d.Practice is { } box)
            {
                var practiced = d.Formatter.Format(raw);
                if (practiced.Length == 0)
                {
                    Report(StatusKind.Info, "I didn't catch anything.");
                    outcome = outcome with { Problem = "nothing heard" };
                    return outcome;
                }

                box.Insert(practiced);
                Keep(d, audio, raw, practiced, app: null);
                Report(StatusKind.Success, "Typed.", practiced);
                outcome = new DictationOutcome(practiced, true, false, null);
                return outcome;
            }

            if (await TypingAsync(d).ConfigureAwait(false) is not { } typing)
            {
                if (Interlocked.Exchange(ref d.RefusalShown, 1) == 0)
                {
                    Report(StatusKind.Warning, PasswordRefusal);
                }

                outcome = outcome with { Problem = "password field" };
                return outcome;
            }

            var rest = typing.Finish(phrases);
            var text = typing.FinalText;
            if (text.Length == 0)
            {
                Report(StatusKind.Info, "I didn't catch anything.");
                outcome = outcome with { Problem = "nothing heard" };
                return outcome;
            }

            if (typing.PhrasesChanged)
            {
                Detach(d, "The words changed after some were typed.");
            }
            else if (typing.FinishedWithoutLayout)
            {
                Log.Info($"dictation {d.Id}: typed without the final layout (it would have changed typed words)");
            }

            if (rest.Length > 0)
            {
                TypeIntoField(d, rest);
            }

            if (d.Refused)
            {
                // A password field after all: nothing is offered for copying or kept.
                outcome = outcome with { Problem = "password field" };
                return outcome;
            }

            if (d.DetachReason is { } reason)
            {
                _inserter.LastText = text;
                _services.Notifier?.ShowUntyped(text, reason);
                Report(StatusKind.Warning, $"{reason} Your dictation is safe: click the notification to copy it.");
                outcome = new DictationOutcome(text, false, true, null);
            }
            else
            {
                _inserter.LastText = typing.Text;
                Report(StatusKind.Success, "Typed.", typing.Text);
                outcome = new DictationOutcome(text, true, false, null);
            }

            Keep(d, audio, raw, text, d.Window?.ProcessName);
            return outcome;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return outcome;
        }
        finally
        {
            if (_current is null)
            {
                PostActivity(null);
            }

            Done(d);
            Finished?.Invoke(outcome);
        }
    }

    /// <summary>
    /// History (never from password fields, nor from windows known to be private) and, when opted in, a sample (never from
    /// password fields or web browsers: <see cref="SampleStore"/>).
    /// </summary>
    private void Keep(Dictation d, float[] audio, string raw, string text, string? app)
    {
        if (!d.Info.Private)
        {
            _services.History.Add(text, app, d.Info.Password);
        }

        _services.Samples.Save(audio, raw, text, d.Spec, d.Info.Password || d.Info.Private, app);
    }

    private void Done(Dictation d)
    {
        if (Interlocked.Decrement(ref _active) == 0 && !_shutdown.IsCancellationRequested)
        {
            _idleTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan);
        }
    }

    // ---- settings ----

    /// <summary>UI thread: Settings apply straight away; a dictation already going keeps what it started with.</summary>
    private void OnSettingsChanged(SettingsChange change)
    {
        var settings = _services.Current;
        if (change.Has(nameof(AppSettings.RemoveFillerWords), nameof(AppSettings.SpokenPunctuation), nameof(AppSettings.Replacements), nameof(AppSettings.Snippets), nameof(AppSettings.NewLineKeys)))
        {
            var formatter = NewFormatter(settings);
            _formatter = formatter;
            Enqueue(() =>
            {
                _inserter = NewInserter(formatter, _inserter.LastText);
                return Task.CompletedTask;
            });
        }

        if (change.Has(nameof(AppSettings.SoundFeedback)))
        {
            _sounds.Enabled = _soundsAllowed && settings.SoundFeedback;
        }

        if (change.Has(nameof(AppSettings.InsertMethod)))
        {
            WindowsDesktop.InsertMethod = settings.InsertMethod;
        }

        if (change.Has(nameof(AppSettings.Language)))
        {
            Report(StatusKind.Info, settings.Language == Language.Hinglish
                ? "Dictation language: Hindi + English (Hindi is typed in Roman letters)."
                : "Dictation language: English.");
        }

        if (change.Has(nameof(AppSettings.Language), nameof(AppSettings.EnglishTier), nameof(AppSettings.HinglishTier), nameof(AppSettings.Threads)))
        {
            if (!_speech.Models.IsInstalled(settings.SelectedModel()))
            {
                ModelMissing(settings.SelectedModel(), openHome: false);
            }

            // The next dictation loads the model chosen now; the old one goes if nothing is using it.
            _speech.Post(UnloadIfIdle);
        }
    }

    private static DictationFormatter NewFormatter(AppSettings settings) => new(new DictationOptions
    {
        RemoveFillerWords = settings.RemoveFillerWords,
        SpokenPunctuation = settings.SpokenPunctuation,
        Replacements = Copy(settings.Replacements),
        Snippets = Copy(settings.Snippets),
    });

    /// <summary>A copy the Settings page can't change under a dictation; keys that differ only in case keep the last value.</summary>
    private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string>? source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source ?? new Dictionary<string, string>())
        {
            copy[key] = value;
        }

        return copy;
    }

    private DictationInserter NewInserter(DictationFormatter formatter, string? lastText)
    {
        var inserter = new DictationInserter(_guarded, formatter, Copy(_services.Current.NewLineKeys)) { LastText = lastText };
        inserter.Status += update => _post(() => Status?.Invoke(update));
        return inserter;
    }

    /// <summary>Parakeet runs fastest on 4 threads; the streaming model uses 2 at most by itself.</summary>
    private int Threads(ModelSpec spec) => spec.Engine == Engine.Parakeet ? Math.Min(_services.Current.Threads, 4) : _services.Current.Threads;

    /// <summary>
    /// A phrase as the text pipeline takes it (PLAN 3.2): cleaned, Whisper's known hallucinations on short audio and echoes
    /// of the word-list prompt dropped, then near misses of listed words fixed.
    /// </summary>
    private static string Prepare(Dictation d, string? raw, int samples)
    {
        var text = TranscriptText.Clean(raw);
        if (text.Length == 0
            || (!d.Spec.Streaming && Hallucinations.IsLikely(text, samples))
            || (d.Prompt is not null && Vocabulary.IsEcho(text, d.Words)))
        {
            return string.Empty;
        }

        return Vocabulary.Correct(text, d.Words);
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"action failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                Report(StatusKind.Error, "That didn't work: " + ex.Message);
            }
        }
    }

    private void Enqueue(Func<Task> work) => _queue.Writer.TryWrite(work);

    private void ShowPreview(string? text)
    {
        var shown = string.IsNullOrEmpty(text) ? null : text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ↵ ", StringComparison.Ordinal);
        _post(() => DictationPreview?.Invoke(shown));
    }

    private void ReportCaptureStopped(int id) => _post(() => CaptureStopped?.Invoke(id));

    private void Report(StatusKind kind, string message, string? heard = null) => _post(() => Status?.Invoke(new StatusUpdate(kind, message, heard)));

    private void PostActivity(string? activity) => _post(() => Activity?.Invoke(activity));

    /// <summary>The dictation's text box lost the focus between two pieces of typing.</summary>
    private sealed class FieldChangedException() : Exception("The text box lost the focus.");

    /// <summary>
    /// The desktop the inserter types through. Right before each paste or key press it waits for held modifier keys to be
    /// let go (WindowsDesktop would, after any check) and then runs <see cref="Guard"/>: Ctrl+Tab, Ctrl+L or Ctrl+F held
    /// while a phrase is typed must not send it to the new tab or bar. It also counts what was typed and remembers the last
    /// field check, so a refusal for a password field can be told apart.
    /// </summary>
    private sealed class GuardedDesktop(IDesktop inner, Action waitForModifiers) : IDesktop
    {
        private int _typed;

        /// <summary>True while the dictation's box has the focus; null types anywhere (Alt+Shift+Z). Queue only.</summary>
        public Func<bool>? Guard { get; set; }

        public FieldCheck LastCheck { get; private set; } = FieldCheck.Unknown;

        /// <summary>How many times text or keys went into an app.</summary>
        public int Typed => Volatile.Read(ref _typed);

        public WindowInfo? GetForegroundWindow() => inner.GetForegroundWindow();

        public FieldCheck CheckFocusedField() => LastCheck = inner.CheckFocusedField();

        public bool? FocusedTextEndsWithNonWhitespace() => inner.FocusedTextEndsWithNonWhitespace();

        public void TypeText(string text)
        {
            Check();
            inner.TypeText(text);
            Interlocked.Increment(ref _typed);
        }

        public void PressKeys(string chord)
        {
            Check();
            inner.PressKeys(chord);
            Interlocked.Increment(ref _typed);
        }

        private void Check()
        {
            if (Guard is { } guard)
            {
                waitForModifiers();
                if (!guard())
                {
                    throw new FieldChangedException();
                }
            }
        }
    }

    /// <summary>One dictation, from Ctrl+Win to the end of typing. Comments say which thread uses what.</summary>
    private sealed class Dictation(int id, ModelSpec spec, AppSettings settings, DictationFormatter formatter, WindowInfo? window, IPracticeTarget? practice, bool live, ModelSpec? previewSpec)
    {
        public readonly object Gate = new();

        /// <summary>The recording (guarded by <see cref="Gate"/>).</summary>
        public readonly List<float> Audio = [];

        /// <summary>Finished phrases, prepared, in order; they never change once added (guarded by <see cref="Gate"/>).</summary>
        public readonly List<string> Phrases = [];

        public readonly SilenceWatch Silence = new(SampleRate);

        public volatile bool Stopped;
        public volatile bool Cancelled;
        public volatile bool Refused;
        public volatile bool Ready;
        public volatile bool LoadFailed;
        public volatile bool TypedAny;
        public volatile bool LimitReached;
        public int FeedQueued;
        public int PreviewBusy;
        public int UpdateQueued;
        public int RefusalShown;

        public int Id { get; } = id;

        public ModelSpec Spec { get; } = spec;

        /// <summary>
        /// Phrases are typed while speaking: Live typing is on and the model keeps up (streaming, Parakeet, or one made for
        /// it; on a PC even the accurate Parakeet does).
        /// </summary>
        public bool Live { get; } = live;

        /// <summary>The model that re-reads the newest audio while you speak; null when none does.</summary>
        public ModelSpec? PreviewSpec { get; } = previewSpec;

        public bool Smart { get; } = settings.SmartFormatting;

        public IReadOnlyList<string> Words { get; } = Vocabulary.Parse(settings.Vocabulary);

        /// <summary>The word list as a prompt, for the one model that's better with it (PLAN 3.2).</summary>
        public string? Prompt { get; } = spec.PromptVocabulary ? Vocabulary.Prompt(Vocabulary.Parse(settings.Vocabulary)) : null;

        public DictationFormatter Formatter { get; } = formatter;

        /// <summary>The window in front when it started; null for the practice box or when there was none.</summary>
        public WindowInfo? Window { get; } = window;

        public IPracticeTarget? Practice { get; } = practice;

        /// <summary>The text box, read through UI Automation when it started (null result: it couldn't tell).</summary>
        public Task<FocusedField?> FieldRead { get; set; } = Task.FromResult<FocusedField?>(null);

        /// <summary><see cref="GuardedDesktop.Typed"/> when the box was read: typing since then changed the text before the cursor.</summary>
        public int TypedAtRead { get; set; }

        public Timer? PreviewTimer { get; set; }

        /// <summary>Speech thread: the phrase being spoken (guarded by <see cref="Gate"/>).</summary>
        public string Partial { get; set; } = string.Empty;

        /// <summary>Speech thread: how much audio the streaming session has had (written under <see cref="Gate"/>).</summary>
        public int Fed { get; set; }

        public ILiveSession? Session { get; set; }

        public DictationPreview? Preview { get; set; }

        // Queue only:
        public FocusedField? Field { get; set; }

        public FieldInfo Info { get; set; } = new(MultiLine: true);

        public LiveTyping? Typing { get; set; }

        /// <summary>Why nothing more is typed into the box; the dictation is then kept for copying.</summary>
        public string? DetachReason { get; set; }
    }
}
