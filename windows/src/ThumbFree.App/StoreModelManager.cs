using ThumbFree.Core;
using ThumbFree.Speech;

namespace ThumbFree.App;

/// <summary>
/// The app window's view of the speech models (<see cref="IModelManager"/>), backed by the model store: resumable
/// downloads pinned by SHA-256 into %LOCALAPPDATA%\ThumbFree\models, and models Voice Dictation or Voice Assistant
/// already downloaded, read where they are.
/// </summary>
internal sealed class StoreModelManager : IModelManager
{
    public StoreModelManager(ModelStore store) => Store = store;

    public event Action<ModelSpec>? Changed;

    public ModelStore Store { get; }

    /// <summary>The model's files, and Silero for the engines that find pauses with it (Whisper and Parakeet).</summary>
    public bool IsInstalled(ModelSpec spec) => StoreSpeechModels.IsInstalled(Store, spec);

    /// <summary>
    /// Whisper and Parakeet find speech with Silero, which is small (885 KB) and comes with the first model that needs it.
    /// </summary>
    public async Task DownloadAsync(ModelSpec spec, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (spec.Engine != Engine.Streaming && !Store.IsInstalled(ModelCatalog.Vad))
        {
            await Store.DownloadAsync(ModelCatalog.Vad, null, cancellationToken).ConfigureAwait(false);
        }

        await Store.DownloadAsync(spec, progress is null ? null : new Relay(progress), cancellationToken).ConfigureAwait(false);
        Log.Info($"model ready: {spec.Id}");
        Changed?.Invoke(spec);
    }

    public void Delete(ModelSpec spec)
    {
        Store.Delete(spec);
        Log.Info($"model deleted: {spec.Id}");
        Changed?.Invoke(spec);
    }

    /// <summary>Passes progress straight through, on the download's thread (Progress&lt;T&gt; would reorder it).</summary>
    private sealed class Relay(IProgress<double> target) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => target.Report(value.Fraction);
    }
}
