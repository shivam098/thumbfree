using System.Diagnostics;
using SherpaOnnx;
using ThumbFree.Core;
using Xunit.Abstractions;

namespace ThumbFree.Speech.Tests;

/// <summary>
/// The engines on recorded speech (tests/audio), checked the way the Android on-device tests check them. Each test runs
/// only when its model is on this PC; timings are printed so regressions show.
/// </summary>
public sealed class RecognizerTests(ITestOutputHelper output)
{
    private static readonly string[] Words = ["GitHub Copilot", "Kubernetes"];

    [Fact]
    public void Sherpa_runs_with_its_own_onnxruntime_not_the_one_in_System32()
    {
        Assert.False(string.IsNullOrEmpty(VersionInfo.Version));

        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().ToList();
        var sherpa = modules.Single(m => m.ModuleName.Equals("sherpa-onnx-c-api.dll", StringComparison.OrdinalIgnoreCase));
        var onnxruntime = modules.Single(m => m.ModuleName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"sherpa-onnx {VersionInfo.Version}, onnxruntime {VersionInfo.OnnxruntimeVersion}: {onnxruntime.FileName}");
        Assert.Equal(Path.GetDirectoryName(sherpa.FileName), Path.GetDirectoryName(onnxruntime.FileName));
    }

    [Fact]
    public void A_model_sherpa_rejects_throws_instead_of_crashing_later()
    {
        var error = Assert.Throws<InvalidDataException>(() => Sherpa.Created(new OfflineRecognizer(new OfflineRecognizerConfig()), "test model"));

        Assert.Equal("sherpa-onnx couldn't load the test model.", error.Message);
    }

    [ModelFact("en-parakeet-110m")]
    public void Loads_a_model_from_a_folder_named_outside_the_system_code_page()
    {
        // A Hindi Windows user name puts one in every path; the wrapper would pass it to sherpa-onnx as "?".
        using var scratch = new Scratch();
        var folder = scratch.File("मॉडल");
        Directory.CreateDirectory(folder);
        foreach (var file in ModelCatalog.EnglishFast.Files)
        {
            File.Copy(TestModels.Store.PathOf(file)!, Path.Combine(folder, file.Name));
        }

        if (!Sherpa.NativePath(folder).All(char.IsAscii))
        {
            // 8.3 names are off on this volume, so there is nothing to work around with.
            return;
        }

        using var model = ParakeetRecognizer.Load(folder, 2);
        Assert.Contains("build logs", model.Transcribe(Audio.Read("send-logs.wav"), preview: false), StringComparison.OrdinalIgnoreCase);
    }

    [ModelFact("en-parakeet-110m")]
    public void Parakeet_transcribes_with_punctuation_and_fixes_listed_words()
    {
        using var model = Timed("parakeet 110m load", () => Recognizers.Load(ModelCatalog.EnglishFast, TestModels.Store, TestModels.Threads));
        Assert.IsType<ParakeetRecognizer>(model);

        var logs = Timed("parakeet 110m send-logs", () => model.Transcribe(Audio.Read("send-logs.wav"), preview: false))!;
        Assert.Contains("send me the latest build logs", logs, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".", logs.TrimEnd(), StringComparison.Ordinal);

        var genres = model.Transcribe(Audio.Read("genres.wav"), preview: false)!;
        Assert.Contains("videos of different genres", genres, StringComparison.OrdinalIgnoreCase);

        var text = Format(Timed("parakeet 110m dictation", () => model.Transcribe(Audio.Read("dictation.wav"), preview: false))!);
        output.WriteLine($"formatted: {text}");
        Assert.Contains("GitHub Copilot", text, StringComparison.Ordinal);
        Assert.Contains("pull request,", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" um ", text, StringComparison.OrdinalIgnoreCase);

        // Clips under a second are padded, not refused.
        Assert.NotNull(model.Transcribe(new float[Decoding.SampleRate / 10], preview: true));

        model.Abort();
        Assert.Null(model.Transcribe(Audio.Read("send-logs.wav"), preview: false));
        model.Resume();
        Assert.Contains("build", model.Transcribe(Audio.Read("send-logs.wav"), preview: true), StringComparison.OrdinalIgnoreCase);

        // 31 s of speech takes a few hundred ms: aborted while it runs, the result is thrown away.
        AbortDuringACall(model, [.. Audio.Read("dictation.wav"), .. Audio.Read("dictation.wav"), .. Audio.Read("dictation.wav")]);
        Assert.Contains("build", model.Transcribe(Audio.Read("send-logs.wav"), preview: false), StringComparison.OrdinalIgnoreCase);

        model.Dispose();
        Assert.Null(model.Transcribe(Audio.Read("send-logs.wav"), preview: false));
    }

    [ModelFact("en-parakeet-0.6b")]
    public void Parakeet_accurate_transcribes_a_dictation()
    {
        using var model = Timed("parakeet 0.6b load", () => Recognizers.Load(ModelCatalog.EnglishAccurate, TestModels.Store, TestModels.Threads));

        var logs = Timed("parakeet 0.6b send-logs", () => model.Transcribe(Audio.Read("send-logs.wav"), preview: false))!;
        Assert.Contains("send me the latest build logs", logs, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".", logs.TrimEnd(), StringComparison.Ordinal);

        var text = Format(Timed("parakeet 0.6b dictation", () => model.Transcribe(Audio.Read("dictation.wav"), preview: false))!);
        Assert.Contains("GitHub Copilot", text, StringComparison.Ordinal);
        Assert.Contains("pull request", text, StringComparison.OrdinalIgnoreCase);
    }

    [ModelFact("en-kroko-live")]
    public void Kroko_types_while_you_speak_and_finishes_with_punctuation()
    {
        using var model = Timed("kroko load", () => StreamingRecognizer.Load(TestModels.Store.DirectoryOf(ModelCatalog.EnglishLive), TestModels.Threads));
        Assert.True(model.Punctuates);

        // As the app does: 100 ms of audio at a time, text back after each.
        var audio = Audio.Read("dictation.wav");
        var seen = new List<string>();
        var started = Stopwatch.StartNew();
        IReadOnlyList<string> phrases;
        using (var session = model.StartSession())
        {
            foreach (var chunk in Audio.Chunks(audio))
            {
                seen.Add(session.Accept(chunk));
            }

            phrases = session.Finish();
            Assert.Equal(phrases, session.Finish());
            Assert.Equal(LiveText.EndSentence(LiveText.Join(phrases, string.Empty)), session.FinishText());
        }

        var ms = started.ElapsedMilliseconds;
        var text = LiveText.EndSentence(LiveText.Join(phrases, string.Empty));
        output.WriteLine($"kroko live: {ms} ms for {audio.Length / 16} ms of audio, phrases: {string.Join(" | ", phrases)}");
        Assert.True(ms < audio.Length / 16, $"keeps up with speech: {ms} ms");
        Assert.True(seen.Take(seen.Count / 2).Any(s => s.Length > 0), $"text arrives while speaking: {string.Join(" / ", seen.Take(40))}");
        Assert.All(phrases, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        var fixedText = Vocabulary.Correct(text, Words);
        Assert.Contains("GitHub Copilot", fixedText, StringComparison.Ordinal);
        Assert.Contains("pull request", fixedText, StringComparison.OrdinalIgnoreCase);
        Assert.True(text.Any(c => c is '.' or ',' or '?'), $"punctuated: {text}");
        Assert.DoesNotMatch("[.,?!;:]{2}", text);

        var logs = Timed("kroko whole recording", () => model.Transcribe(Audio.Read("send-logs.wav"), preview: false))!;
        Assert.EndsWith(".", logs, StringComparison.Ordinal);
        Assert.Contains("send me the latest build logs", logs, StringComparison.OrdinalIgnoreCase);

        model.Abort();
        Assert.Null(model.Transcribe(audio, preview: false));
        model.Resume();
        Assert.Equal(string.Empty, model.Transcribe(new float[Decoding.SampleRate], preview: false));

        // Aborted while it runs, the transcription stops at the next 100 ms.
        AbortDuringACall(model, [.. audio, .. audio, .. audio]);
    }

    [ModelFact("en-kroko-live")]
    public void Kroko_keeps_the_last_words_when_you_finish_right_after_speaking()
    {
        using var model = StreamingRecognizer.Load(TestModels.Store.DirectoryOf(ModelCatalog.EnglishLive), TestModels.Threads);
        foreach (var (clip, lastWords) in new[] { ("send-logs.wav", "latest build logs"), ("genres.wav", "different genres"), ("dictation.wav", "push the build") })
        {
            // The clips end with about 0.75 s of silence: cutting 0.7 s is a tap to finish right after the last word. Silence
            // in front moves that end to other places in Kroko's 1.28 s windows, where different amounts of tail are needed.
            var speech = Audio.Read(clip)[..^(Decoding.SampleRate * 7 / 10)];
            foreach (var lead in new[] { 0, 400, 800 })
            {
                float[] audio = [.. new float[Decoding.SampleRate * lead / 1000], .. speech];
                using var session = model.StartSession();
                foreach (var chunk in Audio.Chunks(audio))
                {
                    session.Accept(chunk);
                }

                var live = session.FinishText();
                var whole = model.Transcribe(audio, preview: false);
                output.WriteLine($"{clip} after {lead} ms: {live}");
                Assert.True(live.Contains(lastWords, StringComparison.OrdinalIgnoreCase), $"{clip} after {lead} ms, session: {live}");
                Assert.True(whole?.Contains(lastWords, StringComparison.OrdinalIgnoreCase), $"{clip} after {lead} ms, Transcribe: {whole}");
            }
        }
    }

    [ModelFact("en-kroko-live")]
    public void Kroko_keeps_its_own_marks_without_doubling_them()
    {
        using var model = StreamingRecognizer.Load(TestModels.Store.DirectoryOf(ModelCatalog.EnglishLive), TestModels.Threads);

        // Phrases as Kroko writes them (its vocabulary has . , ? ! : ; and ...). The punctuation model hardly ever ends a
        // phrase with a mark itself; given Kroko's marks, it repeats them, sometimes twice ("build..", "logs.,"), and adds
        // stray commas ("build, logs.").
        var question = model.Punctuate("is it ready?");
        var dictation = model.Punctuate("so um I wanted to check the pull request comma and then I'm merge it into main full stop new line after that we can ask GitHub copilot to review the Kubernet's config and push the build.");
        var logs = model.Punctuate("Please send me the latest build logs.");
        output.WriteLine($"{question} | {dictation} | {logs}");
        Assert.Equal("Is it ready?", question);
        Assert.EndsWith("push the build.", dictation, StringComparison.Ordinal);
        Assert.Contains("send me the latest build logs.", logs, StringComparison.Ordinal);

        // On speech: the three clips back to back, where Kroko writes "logs." and "genres." in the middle of a phrase.
        float[] audio = [.. Audio.Read("send-logs.wav"), .. Audio.Read("genres.wav"), .. Audio.Read("dictation.wav")];
        using var session = model.StartSession();
        foreach (var chunk in Audio.Chunks(audio))
        {
            session.Accept(chunk);
        }

        var phrases = session.Finish();
        output.WriteLine(string.Join(" | ", phrases));
        Assert.All(phrases.Append(question).Append(dictation).Append(logs), text => Assert.DoesNotMatch("[.,?!;:]{2}", text));
    }

    [Theory]
    [InlineData("push the Build..", "push the Build.")]
    [InlineData("I saw it yesterday., then", "I saw it yesterday. then")]
    [InlineData("Is it Ready?.", "Is it Ready?")]
    [InlineData("wait... what?!", "wait. what?")]
    [InlineData("e.g. the U.S., then", "e.g. the U.S. then")]
    [InlineData("Hi, all. Done?", "Hi, all. Done?")]
    public void Each_word_keeps_its_first_mark(string text, string expected) =>
        Assert.Equal(expected, StreamingRecognizer.FirstMarks(text));

    [ModelFact("en-kroko-live")]
    public void Kroko_finishes_a_phrase_at_each_pause_while_you_speak()
    {
        using var model = StreamingRecognizer.Load(TestModels.Store.DirectoryOf(ModelCatalog.EnglishLive), TestModels.Threads);
        float[] audio = [.. Audio.Read("send-logs.wav"), .. new float[Decoding.SampleRate * 3 / 2], .. Audio.Read("genres.wav")];
        using var session = model.StartSession();
        var partialSeen = false;
        foreach (var chunk in Audio.Chunks(audio))
        {
            session.Accept(chunk);
            partialSeen |= session.Partial.Length > 0;
        }

        var whileSpeaking = session.Finished;
        var phrases = session.Finish();

        output.WriteLine($"phrases: {string.Join(" | ", phrases)}");
        Assert.True(partialSeen);
        Assert.NotEmpty(whileSpeaking);
        Assert.Contains("send me the latest build logs", whileSpeaking[0], StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[A-Z]", whileSpeaking[0]);
        Assert.Equal(whileSpeaking, phrases.Take(whileSpeaking.Count));
        Assert.True(phrases.Count > whileSpeaking.Count, "the last phrase comes from Finish");
        Assert.Contains("videos of different genres", string.Join(' ', phrases.Skip(1)), StringComparison.OrdinalIgnoreCase);
    }

    [ModelFact("en-kroko-live")]
    public void Disposing_the_streaming_model_ends_its_sessions()
    {
        var model = StreamingRecognizer.Load(TestModels.Store.DirectoryOf(ModelCatalog.EnglishLive), TestModels.Threads);
        var session = model.StartSession();
        session.Accept(Audio.Read("send-logs.wav"));

        model.Dispose();
        model.Dispose();

        Assert.Equal(session.Text, session.Accept(new float[Decoding.SampleRate]));
        Assert.Equal(session.Finished, session.Finish());
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => model.StartSession());
        Assert.Null(model.Transcribe(Audio.Read("send-logs.wav"), preview: false));
    }

    [ModelFact("hinglish-swift")]
    public void Whisper_works_through_the_same_interface()
    {
        using var model = Timed("whisper hinglish load", () => Recognizers.Load(ModelCatalog.Hinglish, TestModels.Store, TestModels.Threads));
        Assert.IsType<WhisperRecognizer>(model);

        var logs = Timed("whisper hinglish send-logs", () => model.Transcribe(Audio.Read("send-logs.wav"), preview: false));
        Assert.Contains("build logs", logs, StringComparison.OrdinalIgnoreCase);

        var genres = Timed("whisper hinglish with a prompt", () => model.Transcribe(Audio.Read("genres.wav"), preview: false, prompt: "GitHub Copilot, genres"));
        Assert.Contains("genres", genres, StringComparison.OrdinalIgnoreCase);

        model.Abort();
        Assert.Null(model.Transcribe(Audio.Read("send-logs.wav"), preview: false));
        model.Resume();
        Assert.Contains("logs", model.Transcribe(Audio.Read("send-logs.wav"), preview: true), StringComparison.OrdinalIgnoreCase);

        // Whisper.net can't stop mid-run: the result is thrown away.
        AbortDuringACall(model, Audio.Read("dictation.wav"));
        Assert.Contains("logs", model.Transcribe(Audio.Read("send-logs.wav"), preview: false), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts a transcription on another thread and aborts it 50 ms in (it takes several times that): the call returns
    /// null, and the recognizer works again after <see cref="IRecognizer.Resume"/>.
    /// </summary>
    private static void AbortDuringACall(IRecognizer model, float[] audio)
    {
        string? result = "not run";
        using var started = new ManualResetEventSlim();
        var call = new Thread(() =>
        {
            started.Set();
            result = model.Transcribe(audio, preview: false);
        });
        call.Start();
        started.Wait();
        Thread.Sleep(50);
        model.Abort();
        call.Join();
        model.Resume();
        Assert.Null(result);
    }

    private static string Format(string raw) => new DictationFormatter().Format(Vocabulary.Correct(TranscriptText.Clean(raw), Words));

    private T Timed<T>(string label, Func<T> work)
    {
        var started = Stopwatch.StartNew();
        var result = work();
        output.WriteLine($"{label}: {started.ElapsedMilliseconds} ms{(result is string text ? $": {text}" : string.Empty)}");
        return result;
    }
}
