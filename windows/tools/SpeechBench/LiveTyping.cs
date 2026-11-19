using System.Diagnostics;
using ThumbFree.Core;
using ThumbFree.Speech;

namespace ThumbFree.SpeechBench;

/// <summary>
/// How soon live typing shows a word. Kroko gets each clip 100 ms at a time, as from the microphone, and the clock is
/// simulated: a chunk is decoded once it has been "recorded" and the previous one is done. A word appears when the text so
/// far first holds it in its final form. When it was spoken comes from Parakeet TDT 110M's token timestamps (the end of
/// the word's last token), matched to Kroko's words in order; the difference is the latency.
/// </summary>
internal static class LiveTyping
{
    public static void Report(ModelStore store, IReadOnlyList<Clip> clips, int threads)
    {
        using var kroko = StreamingRecognizer.Load(store.DirectoryOf(ModelCatalog.EnglishLive), threads);
        using var reference = store.IsInstalled(ModelCatalog.EnglishFast) ? ParakeetRecognizer.Load(store.DirectoryOf(ModelCatalog.EnglishFast), threads) : null;
        kroko.Transcribe(new float[Decoding.SampleRate], preview: false);

        Console.WriteLine("| Clip | Decode per 100 ms chunk: mean / max | Finish | Word shown after it was spoken: median / p90 / max | Words matched |");
        Console.WriteLine("|---|---|---|---|---|");
        var all = new List<double>();
        var phrases = new List<string>();
        foreach (var clip in clips)
        {
            var run = Run(kroko, clip.Samples);
            var latencies = reference is null ? [] : Latencies(run, SpokenWords(reference, clip.Samples));
            all.AddRange(latencies);
            phrases.Add($"- {clip.Name}: {string.Join(" ‖ ", run.Phrases)}");
            Console.WriteLine($"| {clip.Name} | {run.ChunkMs.Average():F1} / {run.ChunkMs.Max():F1} ms | {run.FinishMs:F0} ms | {Summary(latencies)} | {latencies.Count} of {run.FinalWords.Length} |");
        }

        Console.WriteLine($"| **all clips** | | | **{Summary(all)}** | {all.Count} |");
        Console.WriteLine();
        Console.WriteLine("Phrases (‖ = a pause ended the phrase):");
        Console.WriteLine();
        phrases.ForEach(Console.WriteLine);
    }

    private static string Summary(List<double> seconds)
    {
        if (seconds.Count == 0)
        {
            return "n/a";
        }

        var sorted = seconds.Order().ToList();
        return $"{sorted[sorted.Count / 2] * 1000:F0} / {sorted[(int)(sorted.Count * 0.9)] * 1000:F0} / {sorted[^1] * 1000:F0} ms";
    }

    private static LiveRun Run(StreamingRecognizer kroko, float[] samples)
    {
        using var session = kroko.StartSession();
        var updates = new List<(double At, string[] Words)>();
        var chunkMs = new List<double>();
        var clock = 0.0;
        var fed = 0;
        foreach (var chunk in samples.Chunk(StreamingRecognizer.ChunkSamples))
        {
            var decode = Stopwatch.StartNew();
            var text = session.Accept(chunk);
            var ms = decode.Elapsed.TotalMilliseconds;
            fed += chunk.Length;
            clock = Math.Max(clock, fed / (double)Decoding.SampleRate) + ms / 1000;
            chunkMs.Add(ms);
            updates.Add((clock, Words(text)));
        }

        var finish = Stopwatch.StartNew();
        var phrases = session.Finish();
        var finishMs = finish.Elapsed.TotalMilliseconds;
        var final = Words(LiveText.Join(phrases, string.Empty));
        updates.Add((Math.Max(clock, fed / (double)Decoding.SampleRate) + finishMs / 1000, final));
        return new LiveRun(updates, chunkMs, finishMs, phrases, final);
    }

    /// <summary>Words with the time their last letter was spoken, from Parakeet's token timestamps and durations.</summary>
    private static List<(string Word, double End)> SpokenWords(ParakeetRecognizer reference, float[] samples)
    {
        var result = reference.Recognize(samples) ?? throw new InvalidOperationException("Parakeet aborted");
        var words = new List<(string Word, double End)>();
        var current = string.Empty;
        var end = 0.0;
        for (var i = 0; i < result.Tokens.Length; i++)
        {
            var token = result.Tokens[i];
            if ((token.StartsWith('▁') || token.StartsWith(' ')) && current.Length > 0)
            {
                words.Add((current, end));
                current = string.Empty;
            }

            var letters = Normalize(token);
            if (letters.Length > 0)
            {
                current += letters;
                end = result.Timestamps[i] + (i < (result.Durations?.Length ?? 0) ? result.Durations![i] : 0.04);
            }
        }

        if (current.Length > 0)
        {
            words.Add((current, end));
        }

        return words;
    }

    /// <summary>For each spoken word that Kroko also wrote (in order), when Kroko showed it minus when it was said.</summary>
    private static List<double> Latencies(LiveRun run, List<(string Word, double End)> spoken)
    {
        var a = run.FinalWords;
        var lcs = new int[a.Length + 1, spoken.Count + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = spoken.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == spoken[j].Word ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var latencies = new List<double>();
        for (int i = 0, j = 0; i < a.Length && j < spoken.Count;)
        {
            if (a[i] == spoken[j].Word)
            {
                var shown = run.Updates.First(u => u.Words.Length > i && u.Words[i] == a[i]).At;
                latencies.Add(shown - spoken[j].End);
                i++;
                j++;
            }
            else if (lcs[i + 1, j] >= lcs[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return latencies;
    }

    private static string[] Words(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(w => w.Length > 0).ToArray();

    private static string Normalize(string word) => new(word.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '\'').ToArray());

    private sealed record LiveRun(List<(double At, string[] Words)> Updates, List<double> ChunkMs, double FinishMs, IReadOnlyList<string> Phrases, string[] FinalWords);
}
