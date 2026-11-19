using SherpaOnnx;
using ThumbFree.Core;

namespace ThumbFree.Speech;

/// <summary>
/// A streaming model (Kroko Zipformer) through sherpa-onnx's online recognizer, for live typing (the Android app's
/// StreamingRecognizer): audio goes in as it's recorded and the text so far comes straight back. A phrase ends at a short
/// pause; each finished phrase gets punctuation from a small model, keeping the recognizer's own words and capitals.
/// </summary>
public sealed class StreamingRecognizer : IRecognizer
{
    /// <summary>100 ms: how much audio <see cref="Transcribe"/> feeds at a time, as the app does while recording.</summary>
    public const int ChunkSamples = Decoding.SampleRate / 10;

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
    /// Best effort: the phrase is kept as recognized if anything goes wrong.
    /// </summary>
    private string Punctuate(string text)
    {
        if (_punctuation is null)
        {
            return text;
        }

        try
        {
            // Kroko writes a few marks itself ("push the build."); the model would add its own after them ("build..").
            // Only the model's marks are kept anyway, so it gets the bare words.
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.TrimEnd(Marks)).Where(w => w.Length > 0);
            return LiveText.MergePunctuation(text, _punctuation.AddPunct(string.Join(' ', words)));
        }
        catch (Exception)
        {
            return text;
        }
    }

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
        public IReadOnlyList<string> Finish()
        {
            lock (_owner._gate)
            {
                if (!_owner._closed && !_done)
                {
                    // A little silence lets the model emit the last word before the stream ends.
                    var recognizer = _owner._recognizer;
                    _stream.AcceptWaveform(Decoding.SampleRate, new float[Decoding.SampleRate * 3 / 10]);
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
