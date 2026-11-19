using System.Diagnostics;
using Whisper.net;

namespace ThumbFree.Speech;

public sealed record RecognizerOptions
{
    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>Whisper encoder context (0 = model default, 1500 = 30 s). Smaller is faster but caps utterance length.</summary>
    public int AudioContextSize { get; init; }

    /// <summary>Optional vocabulary hint. Keep short: Whisper can echo prompts on near-silent input.</summary>
    public string? Prompt { get; init; }
}

public sealed record TranscriptionResult(string Text, float Probability, TimeSpan Elapsed);

/// <summary>
/// OpenAI Whisper through whisper.cpp (Whisper.net), the engine for Hindi + English. Usable directly
/// (<see cref="TranscribeAsync"/>) or as an <see cref="IRecognizer"/> like the sherpa-onnx engines.
/// </summary>
public sealed class WhisperRecognizer : IRecognizer
{
    public const int SampleRate = 16000;

    // whisper.cpp refuses input shorter than 1000 ms, which would drop one-word commands like "send".
    private const int MinSamples = SampleRate * 11 / 10;

    private readonly RecognizerOptions _options;
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperProcessor? _promptProcessor;
    private string? _promptProcessorPrompt;
    private volatile bool _aborted;
    private bool _disposed;

    public WhisperRecognizer(string modelPath, RecognizerOptions? options = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Speech model not found.", modelPath);
        }

        _options = options ?? new RecognizerOptions();
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = BuildProcessor(_options.Prompt);
    }

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await RunAsync(_processor, samples, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Runs to the end once started: Whisper.net keeps running natively after a cancel, so <see cref="Abort"/> throws the
    /// result away instead. A <paramref name="prompt"/> other than <see cref="RecognizerOptions.Prompt"/> gets its own
    /// processor, kept until the prompt changes. <paramref name="preview"/> is ignored: Whisper.net fixes the audio
    /// context when a processor is built.
    /// </remarks>
    public string? Transcribe(float[] samples16k, bool preview, string? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(samples16k);
        if (_aborted)
        {
            return null;
        }

        _gate.Wait();
        try
        {
            if (_aborted || _disposed)
            {
                return null;
            }

            // On the thread pool, so a caller with a synchronization context can't deadlock Whisper.net's awaits.
            var processor = ProcessorFor(prompt);
            var result = Task.Run(() => RunAsync(processor, samples16k, CancellationToken.None)).GetAwaiter().GetResult();
            return _aborted ? null : result.Text;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Abort() => _aborted = true;

    public void Resume() => _aborted = false;

    public void Dispose()
    {
        _aborted = true;

        // Whisper.net returns from a cancelled ProcessAsync while native work is still running, and its
        // synchronous Dispose throws in that state. DisposeAsync waits for the native work; run it off the
        // caller's thread (it may be the UI thread) and give up after a timeout rather than hang or crash.
        if (!_gate.Wait(TimeSpan.FromSeconds(5)))
        {
            return;
        }

        if (_disposed)
        {
            _gate.Release();
            return;
        }

        _disposed = true;
        try
        {
            var disposed = Task.Run(async () =>
            {
                if (_promptProcessor is not null)
                {
                    await _promptProcessor.DisposeAsync().ConfigureAwait(false);
                }

                await _processor.DisposeAsync().ConfigureAwait(false);
            }).Wait(TimeSpan.FromSeconds(5));
            if (disposed)
            {
                _factory.Dispose();
            }
        }
        catch (AggregateException)
        {
            // Native resources are reclaimed at process exit.
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<TranscriptionResult> RunAsync(WhisperProcessor processor, float[] samples, CancellationToken cancellationToken)
    {
        var input = samples;
        if (input.Length < MinSamples)
        {
            input = new float[MinSamples];
            Array.Copy(samples, input, samples.Length);
        }

        var sw = Stopwatch.StartNew();
        var parts = new List<string>();
        float probability = 0;
        var count = 0;
        await foreach (var segment in processor.ProcessAsync(input, cancellationToken).ConfigureAwait(false))
        {
            parts.Add(segment.Text);
            probability += segment.Probability;
            count++;
        }

        return new TranscriptionResult(string.Concat(parts).Trim(), count == 0 ? 0 : probability / count, sw.Elapsed);
    }

    private WhisperProcessor ProcessorFor(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt == _options.Prompt)
        {
            return _processor;
        }

        if (_promptProcessor is null || _promptProcessorPrompt != prompt)
        {
            _promptProcessor?.Dispose();
            _promptProcessor = BuildProcessor(prompt);
            _promptProcessorPrompt = prompt;
        }

        return _promptProcessor;
    }

    private WhisperProcessor BuildProcessor(string? prompt)
    {
        var builder = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(_options.Threads)
            .WithNoContext()
            .WithSingleSegment()
            .WithProbabilities();

        if (_options.AudioContextSize > 0)
        {
            builder = builder.WithAudioContextSize(_options.AudioContextSize);
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            builder = builder.WithPrompt(prompt);
        }

        return builder.Build();
    }
}
