using System.Text.RegularExpressions;
using SherpaOnnx;
using ThumbFree.Core;

namespace ThumbFree.Speech;

/// <summary>
/// A streaming model (Kroko Zipformer) through sherpa-onnx's online recognizer, for live typing (the Android app's
/// StreamingRecognizer): audio goes in as it's recorded and the text so far comes straight back. A phrase ends at a short
/// pause; each finished phrase gets punctuation from a small model, keeping the recognizer's own words and capitals.
/// </summary>
public sealed partial class StreamingRecognizer : IRecognizer
{
    /// <summary>100 ms: how much audio <see cref="Transcribe"/> feeds at a time, as the app does while recording.</summary>
    public const int ChunkSamples = Decoding.SampleRate / 10;

    /// <summary>Silence <see cref="Session.Finish"/> adds after the last words: at least 1 s, at most 3 s (see there).</summary>
    private const int MinTailSamples = Decoding.SampleRate;

    private const int MaxTailSamples = Decoding.SampleRate * 3;

    /// <summary>The marks <see cref="LiveText.MergePunctuation"/> takes from the punctuation model.</summary>
    private static readonly char[] Marks = ['.', ',', '?', '!', ';', ':'];

    private readonly OnlineRecognizer _recognizer;
    private readonly OnlinePunctuation? _punctuation;
    private readonly object _gate = new();
    private readonly List<Session> _sessions = [];
    private volatile bool _aborted;
    private bool _closed;

    private StreamingRecognizer(OnlineRecognizer recognizer, OnlinePunctuation? punctuation)
    {
        _recognizer = recognizer;
        _punctuation = punctuation;
    }

    /// <summary>Whether finished phrases get punctuation (the punctuation model is present and loaded).</summary>
    public bool Punctuates => _punctuation is not null;

    /// <summary>Loads the Kroko streaming model (and the punctuation model when present) from <paramref name="directory"/>.</summary>
    public static StreamingRecognizer Load(string directory, int threads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        var paths = Sherpa.RequireFiles(directory, "encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt");
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = Decoding.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = paths[0];
        config.ModelConfig.Transducer.Decoder = paths[1];
        config.ModelConfig.Transducer.Joiner = paths[2];
        config.ModelConfig.Tokens = paths[3];
        config.ModelConfig.NumThreads = Math.Min(threads, 2); // two threads already run ~10x faster than you speak
        config.ModelConfig.Provider = "cpu";

        // A phrase ends after 0.8 s of silence (punctuation is added then). As on Android: rule 1 (2.4 s of silence before
        // any word) and rule 3 (a 20 s phrase) also end it; sherpa-onnx requires a word only for rule 2.
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.4f;
        config.Rule2MinTrailingSilence = 0.8f;
        config.Rule3MinUtteranceLength = 20f;
        config.DecodingMethod = "greedy_search";
        var recognizer = Sherpa.Created(new OnlineRecognizer(config), "streaming model");
        return new StreamingRecognizer(recognizer, LoadPunctuation(directory));
    }

    /// <summary>Speech thread: a new dictation. Dispose it (or call <see cref="Session.Finish"/>) when done.</summary>
    public Session StartSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var session = new Session(this);
            _sessions.Add(session);
            return session;
        }
    }

    /// <summary>The whole recording at once, fed 100 ms at a time (tests, and the fallback when no live session ran).</summary>
    /// <remarks><paramref name="preview"/> and <paramref name="prompt"/> are ignored: a streaming model gives the same text either way.</remarks>
    public string? Transcribe(float[] samples16k, bool preview, string? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(samples16k);
        if (_aborted)
        {
            return null;
        }

        Session session;
        try
        {
            session = StartSession();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        using (session)
        {
            for (var i = 0; i < samples16k.Length; i += ChunkSamples)
            {
                session.Accept(samples16k[i..Math.Min(samples16k.Length, i + ChunkSamples)]);
                if (_aborted)
                {
                    return null;
                }
            }

            return session.FinishText();
        }
    }

    public void Abort() => _aborted = true;

    public void Resume() => _aborted = false;

    /// <summary>Ends open sessions and frees the models. Safe to call twice.</summary>
    public void Dispose()
    {
        _aborted = true;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            foreach (var session in _sessions.ToArray())
            {
                session.CloseLocked();
            }

            _recognizer.Dispose();
            _punctuation?.Dispose();
        }
    }

    private static OnlinePunctuation? LoadPunctuation(string directory)
    {
        var model = Path.Combine(directory, "punct.int8.onnx");
        var vocab = Path.Combine(directory, "punct-bpe.vocab");
        if (!File.Exists(model) || !File.Exists(vocab))
        {
            return null;
        }

        var config = new OnlinePunctuationConfig();
        config.Model.CnnBiLstm = Sherpa.NativePath(model);
        config.Model.BpeVocab = Sherpa.NativePath(vocab);
        config.Model.NumThreads = 1;
        config.Model.Provider = "cpu";
        try
        {
            return Sherpa.Created(new OnlinePunctuation(config), "punctuation model");
        }
        catch (InvalidDataException)
        {
            // Phrases are typed without punctuation rather than not at all.
            return null;
        }
    }

    /// <summary>
    /// Punctuation from the small model, merged into the recognizer's words and capitals (<see cref="LiveText.MergePunctuation"/>).
    /// Kroko writes a few marks itself ("is it ready?", "push the build."). The model gets the bare words: given Kroko's
    /// marks it repeats them, sometimes twice ("build..", "logs.,"), and adds stray commas ("build, logs."). Kroko's
    /// marks are then put back where it wrote them, since the model hardly ever ends a phrase with one (a question would
    /// lose its "?"), and each word keeps one mark. Best effort: the phrase is kept as recognized if anything goes wrong.
    /// </summary>
    internal string Punctuate(string text)
    {
        if (_punctuation is null)
        {
            return text;
        }

        try
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bare = words.Select(w => w.TrimEnd(Marks)).ToArray();
            var model = bare.All(w => w.Length > 0)
                ? _punctuation.AddPunct(string.Join(' ', bare)).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [];
            if (model.Length != words.Length)
            {
                return text;
            }

            var marked = model.Select((w, i) => words[i].Length > bare[i].Length ? w.TrimEnd(Marks) + words[i][bare[i].Length] : w);
            return LiveText.MergePunctuation(text, FirstMarks(string.Join(' ', marked)));
        }
        catch (Exception)
        {
            return text;
        }
    }

    /// <summary>Reduces each run of marks at the end of a word to its first mark: "build.." → "build.", "Ready?." → "Ready?".</summary>
    internal static string FirstMarks(string text) => MarkRunRegex().Replace(text, "$1");

    [GeneratedRegex(@"([.,?!;:])[.,?!;:]+(?=\s|$)")]
    private static partial Regex MarkRunRegex();

    /// <summary>
    /// One dictation. <see cref="Accept"/> and <see cref="Finish"/> run on the speech thread; the text properties can be
    /// read from any thread.
    /// </summary>
    public sealed class Session : IDisposable
    {
        private readonly StreamingRecognizer _owner;
        private readonly OnlineStream _stream;
        private readonly List<string> _finished = [];
        private string _partial = string.Empty;
        private bool _done;

        internal Session(StreamingRecognizer owner)
        {
            _owner = owner;
            _stream = owner._recognizer.CreateStream();
        }

        /// <summary>Phrases that ended at a pause, punctuated, in order (a copy). The app types each one as it arrives.</summary>
        public IReadOnlyList<string> Finished
        {
            get
            {
                lock (_owner._gate)
                {
                    return _finished.ToArray();
                }
            }
        }

        /// <summary>The words of the phrase still being spoken (no punctuation yet): shown in the pill, not typed.</summary>
        public string Partial
        {
            get
            {
                lock (_owner._gate)
                {
                    return _partial;
                }
            }
        }

        /// <summary>The text so far: finished phrases, then the phrase in progress.</summary>
        public string Text
        {
            get
            {
                lock (_owner._gate)
                {
                    return LiveText.Join(_finished, _partial);
                }
            }
        }

        /// <summary>Adds 16 kHz audio (the app sends 100 ms at a time) and returns the text so far.</summary>
        public string Accept(float[] chunk)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            lock (_owner._gate)
            {
                if (_owner._closed || _done)
                {
                    return LiveText.Join(_finished, _partial);
                }

                var recognizer = _owner._recognizer;
                _stream.AcceptWaveform(Decoding.SampleRate, chunk);
                while (recognizer.IsReady(_stream))
                {
                    recognizer.Decode(_stream);
                }

                _partial = recognizer.GetResult(_stream).Text.Trim();
                if (recognizer.IsEndpoint(_stream))
                {
                    if (_partial.Length > 0)
                    {
                        _finished.Add(_owner.Punctuate(_partial));
                    }

                    _partial = string.Empty;
                    recognizer.Reset(_stream);
                }

                return LiveText.Join(_finished, _partial);
            }
        }

        /// <summary>
        /// Flushes the last words and returns every phrase of the dictation, punctuated and in order: the app types the ones
        /// it hasn't typed yet. The session can't be used afterwards; calling it again returns the same phrases.
        /// </summary>
        /// <remarks>
        /// sherpa-onnx only decodes whole windows (Kroko: 1.41 s of audio, moving on 1.28 s at a time), so the last words of
        /// a dictation finished right after speaking wait for audio after them. Silence goes in 100 ms at a time until two
        /// windows past the end are decoded, which covers every recorded frame, and at least 1 s of it, for models with
        /// shorter windows. For Kroko that's 1.3–2.7 s of silence and two decodes (about 0.1 s); a fixed 1 s lost the last
        /// words at a third of the alignments tested.
        /// </remarks>
        public IReadOnlyList<string> Finish()
        {
            lock (_owner._gate)
            {
                if (!_owner._closed && !_done)
                {
                    var recognizer = _owner._recognizer;
                    var silence = new float[ChunkSamples];
                    var decodes = 0;
                    for (var tail = 0; (decodes < 2 || tail < MinTailSamples) && tail < MaxTailSamples; tail += silence.Length)
                    {
                        _stream.AcceptWaveform(Decoding.SampleRate, silence);
                        while (recognizer.IsReady(_stream))
                        {
                            recognizer.Decode(_stream);
                            decodes++;
                        }
                    }

                    _stream.InputFinished();
                    while (recognizer.IsReady(_stream))
                    {
                        recognizer.Decode(_stream);
                    }

                    var last = recognizer.GetResult(_stream).Text.Trim();
                    if (last.Length > 0)
                    {
                        _finished.Add(_owner.Punctuate(last));
                    }

                    _partial = string.Empty;
                }

                CloseLocked();
                return _finished.ToArray();
            }
        }

        /// <summary>
        /// <see cref="Finish"/> as one text that ends like a sentence (the Android app's finish()): the last phrase, cut at
        /// a pause, may end in a comma, which becomes a full stop.
        /// </summary>
        public string FinishText() => LiveText.EndSentence(LiveText.Join(Finish(), string.Empty));

        /// <summary>Stops without flushing (cancel). Safe to call twice.</summary>
        public void Close()
        {
            lock (_owner._gate)
            {
                CloseLocked();
            }
        }

        public void Dispose() => Close();

        internal void CloseLocked()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _stream.Dispose();
            _owner._sessions.Remove(this);
        }
    }
}
