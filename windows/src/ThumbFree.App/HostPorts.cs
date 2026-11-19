using ThumbFree.App.Platform;
using ThumbFree.Core;
using ThumbFree.Speech;

namespace ThumbFree.App;

/// <summary>Where dictation audio comes from: the microphone, or a recording (self-tests and tests).</summary>
internal interface IAudioSource : IDisposable
{
    /// <summary>16 kHz mono samples from -1 to 1, raised on the capture thread.</summary>
    event Action<float[]>? Samples;

    /// <summary>Raised when capture stops by itself: the microphone was unplugged, or Windows took it.</summary>
    event Action<Exception>? Failed;

    void Start();

    void Stop();
}

/// <summary>The focused text box, through UI Automation (<see cref="FocusInspector"/>); faked in tests.</summary>
internal interface IFieldReader
{
    /// <summary>The focused field of <paramref name="window"/>, or null when UI Automation can't tell in time.</summary>
    FocusedField? Read(WindowInfo window);

    /// <summary>The field with the focus now is <paramref name="start"/>; null when that can't be checked in time.</summary>
    bool? IsSameField(FocusedField start, WindowInfo? now);

    /// <summary>The window runs as administrator: Windows drops ThumbFree's typing there.</summary>
    bool IsElevated(WindowInfo window);
}

internal sealed class UiaFieldReader : IFieldReader
{
    public FocusedField? Read(WindowInfo window) => FocusInspector.Read(window);

    public bool? IsSameField(FocusedField start, WindowInfo? now) => FocusInspector.IsSameField(start, now);

    public bool IsElevated(WindowInfo window) => Win32.IsProcessElevated(window.ProcessId) == true;
}

/// <summary>
/// The desktop's typing with a last check right before the keys go out: after the clipboard holds the text and held
/// modifier keys are let go (<see cref="WindowsDesktop"/>). Faked in tests.
/// </summary>
internal interface IGatedInput
{
    /// <summary>Types <paramref name="text"/>; <paramref name="beforeKeys"/> runs right before the keys, and stops them by throwing.</summary>
    void TypeText(string text, Action beforeKeys);

    /// <summary>Presses <paramref name="chord"/>; <paramref name="beforeKeys"/> runs right before, and stops it by throwing.</summary>
    void PressKeys(string chord, Action beforeKeys);

    /// <summary>Waits (a second at most) until Ctrl, Alt, Shift and the Windows keys are let go.</summary>
    void WaitForModifiers();

    /// <summary>Ctrl, Alt, Shift or a Windows key is held now.</summary>
    bool ModifiersDown { get; }
}

/// <summary>One streaming dictation (<see cref="StreamingRecognizer.Session"/>); faked in tests.</summary>
internal interface ILiveSession : IDisposable
{
    /// <summary>The phrases that ended at a pause, punctuated, in order.</summary>
    IReadOnlyList<string> Finished { get; }

    /// <summary>The phrase still being spoken.</summary>
    string Partial { get; }

    void Accept(float[] chunk);

    /// <summary>Flushes the last words and returns every phrase of the dictation.</summary>
    IReadOnlyList<string> Finish();
}

/// <summary>
/// The speech models dictation uses: which are on this PC, and loading them. Everything but <see cref="IsInstalled"/>
/// runs on the speech thread (<see cref="SpeechThread"/>).
/// </summary>
internal interface ISpeechModels : IDisposable
{
    /// <summary>Every file the model needs is here, with the voice-activity model for the engines that use it.</summary>
    bool IsInstalled(ModelSpec spec);

    IRecognizer Load(ModelSpec spec, int threads);

    /// <summary>A streaming dictation on <paramref name="model"/>, a streaming model from <see cref="Load"/>.</summary>
    ILiveSession StartSession(IRecognizer model);

    /// <summary>Where the audio has speech (Silero). All of it when the voice-activity model is missing.</summary>
    IReadOnlyList<(TimeSpan Start, TimeSpan End)> DetectSpeech(float[] audio);
}

/// <summary>The model store's models, run with sherpa-onnx and whisper.cpp.</summary>
internal sealed class StoreSpeechModels(ModelStore store) : ISpeechModels
{
    private SpeechDetector? _detector;
    private bool _warned;

    /// <summary>
    /// Whisper and Parakeet need Silero to find pauses: it cuts long dictation and ends phrases for live typing.
    /// <see cref="StoreModelManager"/> downloads it with them.
    /// </summary>
    public static bool IsInstalled(ModelStore store, ModelSpec spec) => store.IsInstalled(spec) && (spec.Streaming || store.IsInstalled(ModelCatalog.Vad));

    public bool IsInstalled(ModelSpec spec) => IsInstalled(store, spec);

    public IRecognizer Load(ModelSpec spec, int threads) => Recognizers.Load(spec, store, threads);

    public ILiveSession StartSession(IRecognizer model) => new KrokoSession(((StreamingRecognizer)model).StartSession());

    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> DetectSpeech(float[] audio)
    {
        if (_detector is null && store.PathOf(ModelCatalog.Vad) is { } path)
        {
            try
            {
                _detector = new SpeechDetector(path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"voice activity model unusable: {ex.GetType().Name}");
            }
        }

        if (_detector is null && !_warned)
        {
            _warned = true;
            Log.Warn("no voice activity model: the whole recording counts as speech");
        }

        return _detector?.Detect(audio) ?? [(TimeSpan.Zero, TimeSpan.FromSeconds((double)audio.Length / Decoding.SampleRate))];
    }

    public void Dispose()
    {
        _detector?.Dispose();
        _detector = null;
    }

    private sealed class KrokoSession(StreamingRecognizer.Session session) : ILiveSession
    {
        public IReadOnlyList<string> Finished => session.Finished;

        public string Partial => session.Partial;

        public void Accept(float[] chunk) => session.Accept(chunk);

        public IReadOnlyList<string> Finish() => session.Finish();

        public void Dispose() => session.Dispose();
    }
}
