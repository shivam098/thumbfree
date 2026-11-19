using System.Diagnostics;
using System.Runtime.InteropServices;
using SherpaOnnx;
using ThumbFree.Core;
using ThumbFree.Speech;

namespace ThumbFree.SpeechBench;

/// <summary>Runs every installed model on the test clips and prints Markdown for README.md.</summary>
internal static class Bench
{
    private const int Repeats = 5;

    private static readonly ModelSpec[] Models = [ModelCatalog.EnglishLive, ModelCatalog.EnglishFast, ModelCatalog.EnglishAccurate, ModelCatalog.Hinglish, ModelCatalog.HinglishAccurate];

    public static int Run(ModelStore store, Options options)
    {
        var clips = Clip.Load(options.Audio);
        if (clips is null)
        {
            Console.Error.WriteLine("tests/audio not found; pass --audio <folder>.");
            return 2;
        }

        var installed = Models.Where(s => store.IsInstalled(s) && (options.Ids.Count == 0 || options.Ids.Contains(s.Id))).ToList();
        if (installed.Count == 0)
        {
            Console.Error.WriteLine($"No models in {store.PrimaryFolder}. Run: SpeechBench download");
            return 1;
        }

        Console.WriteLine($"""
            ## {Machine()}

            sherpa-onnx {VersionInfo.Version} (onnxruntime {VersionInfo.OnnxruntimeVersion}), Whisper.net {typeof(Whisper.net.WhisperFactory).Assembly.GetName().Version?.ToString(3)}, .NET {Environment.Version},
            {RuntimeInformation.ProcessArchitecture} process. Median of {Repeats} runs after a warm-up; real-time factor (RTF) =
            processing time / audio length.

            | Model | Threads | Load | 1st call | {string.Join(" | ", clips.Select(c => $"{c.Name} ({c.Seconds:F1} s)"))} |
            |---|---|---|---|{string.Concat(clips.Select(_ => "---|"))}
            """);

        var texts = new List<(string Model, string Clip, string Text)>();
        foreach (var spec in installed)
        {
            foreach (var threads in spec.Engine == Engine.Streaming ? options.Threads.Take(1) : options.Threads)
            {
                var row = Measure(spec, store, threads, clips, out var results);
                Console.WriteLine(row);
                if (threads == options.Threads[0] || spec.Engine == Engine.Streaming)
                {
                    texts.AddRange(results.Select(r => (Name(spec), r.Clip, r.Text)));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("| Model | Clip | Text |");
        Console.WriteLine("|---|---|---|");
        foreach (var (model, clip, text) in texts)
        {
            Console.WriteLine($"| {model} | {clip} | {text.Replace("|", "\\|", StringComparison.Ordinal)} |");
        }

        if (installed.Contains(ModelCatalog.EnglishLive))
        {
            Console.WriteLine();
            LiveTyping.Report(store, clips, options.Threads[0]);
        }

        return 0;
    }

    private static string Measure(ModelSpec spec, ModelStore store, int threads, IReadOnlyList<Clip> clips, out List<(string Clip, string Text)> texts)
    {
        texts = [];
        var started = Stopwatch.StartNew();
        using var model = Recognizers.Load(spec, store, threads);
        var load = started.Elapsed;
        started.Restart();
        model.Transcribe(new float[Decoding.SampleRate], preview: false);
        var first = started.Elapsed;

        var cells = new List<string>();
        foreach (var clip in clips)
        {
            // Whisper pieces are at most 14 s in the app (a 768-frame window); longer audio would be cut.
            if (clip.Samples.Length > spec.MaxPieceSamples)
            {
                cells.Add("(split in the app)");
                continue;
            }

            var times = new List<double>();
            string? text = null;
            for (var i = 0; i < Repeats && (i == 0 || times[0] < 3_000); i++)
            {
                var run = Stopwatch.StartNew();
                text = model.Transcribe(clip.Samples, preview: false);
                times.Add(run.Elapsed.TotalMilliseconds);
            }

            var ms = times.Order().ElementAt(times.Count / 2);
            cells.Add($"{ms:F0} ms ({ms / 1000 / clip.Seconds:F3})");
            texts.Add((clip.Name, text ?? "(null)"));
        }

        var shownThreads = spec.Engine == Engine.Streaming ? Math.Min(threads, 2) : threads;
        return $"| {Name(spec)} | {shownThreads} | {load.TotalMilliseconds:F0} ms | {first.TotalMilliseconds:F0} ms | {string.Join(" | ", cells)} |";
    }

    private static string Name(ModelSpec spec) => spec.Id switch
    {
        "en-kroko-live" => "Kroko live (streaming)",
        "en-parakeet-110m" => "Parakeet TDT 110M",
        "en-parakeet-0.6b" => "Parakeet TDT 0.6B v2",
        "hinglish-swift" => "Whisper Hinglish Swift",
        "hinglish-apex" => "Whisper Hinglish Apex",
        _ => spec.Id,
    };

    private static string Machine()
    {
        var cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown CPU";
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? cpu;
        }

        return $"{cpu}, {Environment.ProcessorCount} cores, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}";
    }
}

/// <summary>A test clip from tests/audio (and all three joined: the "17 s of speech" in PLAN.md).</summary>
internal sealed record Clip(string Name, float[] Samples)
{
    public double Seconds => Samples.Length / (double)Decoding.SampleRate;

    public static IReadOnlyList<Clip>? Load(string? folder)
    {
        folder ??= Find();
        if (folder is null)
        {
            return null;
        }

        var clips = new[] { "send-logs", "genres", "dictation" }
            .Select(name => new Clip(name, WavFile.ReadPcm16Mono16k(Path.Combine(folder, name + ".wav"))))
            .ToList();
        clips.Add(new Clip("all three", clips.SelectMany(c => c.Samples).ToArray()));
        return clips;
    }

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var audio = Path.Combine(dir.FullName, "tests", "audio");
            if (File.Exists(Path.Combine(audio, "dictation.wav")))
            {
                return audio;
            }
        }

        return null;
    }
}
