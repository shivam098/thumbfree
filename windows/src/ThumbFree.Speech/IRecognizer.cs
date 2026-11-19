using ThumbFree.Core;

namespace ThumbFree.Speech;

/// <summary>
/// A loaded speech model, the same contract as the Android app's Recognizer. <see cref="Transcribe"/> runs on the speech
/// thread, one call at a time; <see cref="Abort"/> may be called from any thread, so a cancelled dictation never waits
/// for a result nobody wants.
/// </summary>
public interface IRecognizer : IDisposable
{
    /// <summary>
    /// Raw text for 16 kHz mono audio, or null if aborted (or disposed). <paramref name="preview"/> trades accuracy for
    /// speed where the engine can; <paramref name="prompt"/> is a vocabulary hint only Whisper uses.
    /// </summary>
    string? Transcribe(float[] samples16k, bool preview, string? prompt = null);

    /// <summary>Stops the call in progress (or throws its result away); later calls return null until <see cref="Resume"/>.</summary>
    void Abort();

    void Resume();
}

/// <summary>Loads the recognizer for a model, like the Android SpeechEngine: one class per <see cref="Engine"/>.</summary>
public static class Recognizers
{
    /// <summary>
    /// Loads <paramref name="spec"/> from <paramref name="store"/> with <paramref name="threads"/> decoding threads. Throws
    /// <see cref="FileNotFoundException"/> if a file is missing (check <see cref="ModelStore.IsInstalled(ModelSpec)"/> first).
    /// </summary>
    public static IRecognizer Load(ModelSpec spec, ModelStore store, int threads)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(store);
        return spec.Engine switch
        {
            Engine.Parakeet => ParakeetRecognizer.Load(store.DirectoryOf(spec), threads),
            Engine.Streaming => StreamingRecognizer.Load(store.DirectoryOf(spec), threads),
            _ => new WhisperRecognizer(
                store.PathOf(spec.Files[0]) ?? throw new FileNotFoundException("Speech model not found.", spec.Files[0].Name),
                new RecognizerOptions { Threads = threads, AudioContextSize = Decoding.FinalAudioContext }),
        };
    }
}
