using SherpaOnnx;
using ThumbFree.Core;

namespace ThumbFree.Speech;

/// <summary>
/// NVIDIA Parakeet TDT through sherpa-onnx (the Android app's ParakeetRecognizer). Greedy decoding; the output already has
/// punctuation and capitals. A decode can't be interrupted, but it takes a fraction of the audio's length, and an aborted
/// result is thrown away.
/// </summary>
public sealed class ParakeetRecognizer : IRecognizer
{
    private readonly OfflineRecognizer _recognizer;
    private readonly object _gate = new();
    private volatile bool _aborted;
    private bool _closed;

    private ParakeetRecognizer(OfflineRecognizer recognizer)
    {
        _recognizer = recognizer;
    }

    /// <summary>Loads encoder/decoder/joiner/tokens from <paramref name="directory"/>; throws if they can't be read.</summary>
    public static ParakeetRecognizer Load(string directory, int threads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        var paths = Sherpa.RequireFiles(directory, "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt");
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = Decoding.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = paths[0];
        config.ModelConfig.Transducer.Decoder = paths[1];
        config.ModelConfig.Transducer.Joiner = paths[2];
        config.ModelConfig.Tokens = paths[3];
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        return new ParakeetRecognizer(Sherpa.Created(new OfflineRecognizer(config), "Parakeet model"));
    }

    /// <inheritdoc/>
    /// <remarks><paramref name="preview"/> and <paramref name="prompt"/> are ignored: Parakeet is fast enough for both, and its hotwords made results worse.</remarks>
    public string? Transcribe(float[] samples16k, bool preview, string? prompt = null) => Recognize(samples16k)?.Text;

    /// <summary>The full sherpa-onnx result (tokens with start times and durations), or null if aborted. For the benchmark.</summary>
    internal OfflineRecognizerResult? Recognize(float[] samples16k)
    {
        ArgumentNullException.ThrowIfNull(samples16k);
        lock (_gate)
        {
            if (_aborted || _closed)
            {
                return null;
            }

            // Very short clips confuse the encoder; pad to a second.
            var input = samples16k;
            if (input.Length < Decoding.SampleRate)
            {
                input = new float[Decoding.SampleRate];
                samples16k.CopyTo(input, 0);
            }

            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(Decoding.SampleRate, input);
            _recognizer.Decode(stream);
            return _aborted ? null : stream.Result;
        }
    }

    public void Abort() => _aborted = true;

    public void Resume() => _aborted = false;

    /// <summary>Waits for a decode in progress (a fraction of a second), then frees the model. Safe to call twice.</summary>
    public void Dispose()
    {
        _aborted = true;
        lock (_gate)
        {
            if (!_closed)
            {
                _closed = true;
                _recognizer.Dispose();
            }
        }
    }
}
