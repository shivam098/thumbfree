using ThumbFree.Core;

namespace ThumbFree.Speech.Tests;

/// <summary>
/// A test that needs a downloaded model: skipped, not failed, when the model isn't on this PC, so CI stays green without
/// downloads. Models are looked up like the app does (%LOCALAPPDATA%\ThumbFree\models, then Voice Dictation's and Voice
/// Assistant's folders); THUMBFREE_MODELS points at one folder instead. "SpeechBench download" fetches them.
/// </summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute(string modelId)
    {
        var spec = ModelCatalog.ById(modelId) ?? throw new ArgumentException($"Unknown model {modelId}", nameof(modelId));
        if (!TestModels.Store.IsInstalled(spec))
        {
            Skip = $"{spec.Id} isn't in {TestModels.Store.PrimaryFolder} (run: dotnet run --project windows/tools/SpeechBench -- download)";
        }
    }
}

internal static class TestModels
{
    public static ModelStore Store { get; } = Environment.GetEnvironmentVariable("THUMBFREE_MODELS") is { Length: > 0 } folder
        ? new ModelStore(folder)
        : new ModelStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThumbFree", "models"),
            ModelStore.DefaultSharedFolders);

    public static int Threads { get; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
}

/// <summary>The speech clips in tests/audio at the repository root (shared with the Android tests).</summary>
internal static class Audio
{
    public static float[] Read(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "audio", name)))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, $"tests/audio/{name} not found above {AppContext.BaseDirectory}");
        return WavFile.ReadPcm16Mono16k(Path.Combine(dir!.FullName, "tests", "audio", name));
    }

    /// <summary>16 kHz chunks of <paramref name="ms"/> milliseconds, as the microphone delivers them.</summary>
    public static IEnumerable<float[]> Chunks(float[] samples, int ms = 100)
    {
        var size = Decoding.SampleRate * ms / 1000;
        for (var i = 0; i < samples.Length; i += size)
        {
            yield return samples[i..Math.Min(samples.Length, i + size)];
        }
    }
}

/// <summary>An empty folder under the test output (bin), deleted afterwards.</summary>
internal sealed class Scratch : IDisposable
{
    public Scratch()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file still open on a slow machine; the next build's clean removes it.
        }
    }
}
