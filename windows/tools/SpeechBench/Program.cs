using System.Diagnostics;
using ThumbFree.Core;
using ThumbFree.Speech;

namespace ThumbFree.SpeechBench;

/// <summary>
/// Measures the speech engines on this PC: load time, real-time factor per clip and the text, plus how soon live typing
/// (Kroko) shows a word after it was spoken. "download" fetches models through <see cref="ModelStore"/>. See README.md.
/// </summary>
public static class Program
{
    private static readonly string[] DefaultDownloads = [ModelCatalog.EnglishLive.Id, ModelCatalog.EnglishFast.Id, ModelCatalog.EnglishAccurate.Id];

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("""
                Usage: SpeechBench [download] [model-id ...] [--models <folder>] [--threads <n>[,<n>...]] [--audio <folder>]
                  download   fetch models (default: en-kroko-live en-parakeet-110m en-parakeet-0.6b) with ModelStore
                  (none)     benchmark the installed models (or only those listed) on tests/audio
                """);
            return 2;
        }

        var store = new ModelStore(options.Models, ModelStore.DefaultSharedFolders);
        return options.Command == "download"
            ? await DownloadAsync(store, options.Ids.Count > 0 ? options.Ids : DefaultDownloads).ConfigureAwait(false)
            : Bench.Run(store, options);
    }

    private static async Task<int> DownloadAsync(ModelStore store, IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            var spec = ModelCatalog.ById(id);
            if (spec is null)
            {
                Console.Error.WriteLine($"Unknown model {id}. Known: {string.Join(", ", ModelCatalog.All.Select(s => s.Id))}");
                return 2;
            }

            var started = Stopwatch.StartNew();
            var shown = -1;
            var progress = new Reporter<DownloadProgress>(p =>
            {
                var percent = (int)(p.Fraction * 100);
                if (percent / 10 > shown / 10)
                {
                    shown = percent;
                    Console.WriteLine($"  {spec.Id}: {percent,3}%  {p.Done / 1e6,6:F0} of {p.Total / 1e6:F0} MB  {started.Elapsed.TotalSeconds,5:F0} s");
                }
            });
            try
            {
                await store.DownloadAsync(spec, progress).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                Console.Error.WriteLine($"{spec.Id}: {e.Message}");
                return 1;
            }

            Console.WriteLine($"{spec.Id}: ready in {store.DirectoryOf(spec)} ({spec.Bytes / 1e6:F0} MB, {started.Elapsed.TotalSeconds:F1} s)");
        }

        return 0;
    }

    /// <summary>Reports synchronously, in order (<see cref="Progress{T}"/> would post to the thread pool).</summary>
    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>Command line: a command, model ids, and options.</summary>
internal sealed record Options(string Command, IReadOnlyList<string> Ids, string Models, IReadOnlyList<int> Threads, string? Audio)
{
    public static string DefaultModels { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThumbFree", "models");

    public static Options? Parse(string[] args)
    {
        var command = "bench";
        var ids = new List<string>();
        var models = DefaultModels;
        IReadOnlyList<int> threads = [Math.Clamp(Environment.ProcessorCount / 2, 2, 8)];
        string? audio = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--models" when i + 1 < args.Length:
                    models = args[++i];
                    break;
                case "--audio" when i + 1 < args.Length:
                    audio = args[++i];
                    break;
                case "--threads" when i + 1 < args.Length:
                    var parsed = args[++i].Split(',').Select(t => int.TryParse(t, out var n) && n > 0 ? n : 0).ToList();
                    if (parsed.Contains(0))
                    {
                        return null;
                    }

                    threads = parsed;
                    break;
                case "download" when i == 0:
                    command = "download";
                    break;
                case var a when a.StartsWith('-'):
                    return null;
                default:
                    ids.Add(args[i]);
                    break;
            }
        }

        return new Options(command, ids, models, threads, audio);
    }
}
