using ThumbFree.Core;

namespace ThumbFree.Core.Tests;

/// <summary>The parts of the core every platform shares (ported from the Android core tests, same cases).</summary>
public class SharedCoreTests
{
    [Fact]
    public void Model_catalog_matches_the_android_app()
    {
        Assert.Equal(ModelCatalog.EnglishLive, ModelCatalog.ForLanguage(Language.English, Tier.Live));
        Assert.Equal(ModelCatalog.EnglishFast, ModelCatalog.ForLanguage(Language.English, Tier.Standard));
        Assert.Equal(ModelCatalog.EnglishAccurate, ModelCatalog.ForLanguage(Language.English, Tier.Accurate));
        Assert.Equal(ModelCatalog.Hinglish, ModelCatalog.ForLanguage(Language.Hinglish, Tier.Live));
        Assert.Equal(ModelCatalog.Hinglish, ModelCatalog.ForLanguage(Language.Hinglish, Tier.Standard));
        Assert.Equal(ModelCatalog.HinglishAccurate, ModelCatalog.ForLanguage(Language.Hinglish, Tier.Accurate));
        Assert.True(ModelCatalog.EnglishLive.Streaming);
        Assert.Equal(78_693_144L, ModelCatalog.EnglishLive.Bytes);
        Assert.False(ModelCatalog.Hinglish.PromptVocabulary);
        Assert.True(ModelCatalog.HinglishAccurate.PromptVocabulary);
        Assert.False(ModelCatalog.HinglishAccurate.LivePreview);
        Assert.Equal(Engine.Parakeet, ModelCatalog.EnglishFast.Engine);
        Assert.Equal(136_490_421L, ModelCatalog.EnglishFast.Bytes);
        Assert.Equal(Decoding.SampleRate * 30, ModelCatalog.EnglishFast.MaxPieceSamples);
        Assert.Equal(Decoding.MaxPieceSamples, ModelCatalog.Hinglish.MaxPieceSamples);
        Assert.Equal("parakeet-tdt-110m/tokens.txt", ModelCatalog.EnglishFast.File("tokens.txt").Path);
        Assert.Equal(ModelCatalog.All.Sum(s => s.Files.Count) + 1, ModelCatalog.KnownPaths.Count);
        Assert.Same(ModelCatalog.EnglishLive, ModelCatalog.ById("en-kroko-live"));
        Assert.Null(ModelCatalog.ById("whisper-small"));
    }

    [Fact]
    public void Live_text_keeps_the_recognizers_capitals_and_takes_the_punctuation()
    {
        Assert.Equal(
            "I tested it on YouTube videos of different genres.",
            LiveText.MergePunctuation("I tested it on YouTube videos of different genres", "I tested it on YOUTUBE videos of different genres."));
        Assert.Equal(
            "Hey, Tim would love to connect. Are you available on Friday? Thanks, Ben",
            LiveText.MergePunctuation("hey Tim would love to connect are you available on Friday thanks Ben", "Hey, Tim would love to connect. Are you available on Friday? Thanks, Ben"));

        // Different words: keep the recognizer's text.
        Assert.Equal("send the logs", LiveText.MergePunctuation("send the logs", "Send the log."));
        Assert.Equal("send the logs", LiveText.MergePunctuation(" send the logs ", ""));
        Assert.Equal("One. two three", LiveText.Join(["One."], " two three "));
        Assert.Equal("One.", LiveText.Join(["One."], ""));
        Assert.Equal("Please send me the latest build logs.", LiveText.EndSentence("Please send me the latest build logs, "));
        Assert.Equal("Are you free?", LiveText.EndSentence("Are you free?"));
        Assert.Equal("Meet at 4 pm.", LiveText.EndSentence("Meet at 4 pm"));
        Assert.Equal("", LiveText.EndSentence(""));
    }

    [Fact]
    public void History_round_trips_prunes_and_searches()
    {
        var email = new HistoryEntry(1_000, "OUTLOOK", "Hey Tim,\n\nAre you free?\tThanks, C:\\Ben");
        Assert.Equal(email, History.Decode(History.Encode(email)));
        Assert.DoesNotContain('\n', History.Encode(email));
        Assert.Null(History.Decode("garbage"));
        Assert.Null(History.Decode("12\tapp\t   "));

        var now = History.MaxAgeMs + 10_000;
        var old = new HistoryEntry(5_000, "", "too old");
        var recent = Enumerable.Range(1, History.MaxEntries + 5).Select(i => new HistoryEntry(now - i, "", $"entry {i}"));
        var kept = History.Prune(recent.Append(old), now);
        Assert.Equal(History.MaxEntries, kept.Count);
        Assert.Equal("entry 1", kept[0].Text);
        Assert.DoesNotContain(kept, e => e.Text == "too old");

        HistoryEntry[] two = [new(2, "", "Send me the build logs"), new(1, "", "Call mom")];
        Assert.Equal([two[0]], History.Search(two, " logs  SEND "));
        Assert.Equal(two, History.Search(two, ""));
    }

    [Fact]
    public void Word_list_fixes_near_misses_but_not_ordinary_words()
    {
        string[] words = ["Kubernetes", "GitHub Copilot", "genres", "vibe coding"];
        Assert.Equal("Review the Kubernetes config.", Vocabulary.Correct("Review the Kubirnets config.", words));
        Assert.Equal("Ask GitHub Copilot to review it", Vocabulary.Correct("Ask GitHub co-pilot to review it", words));
        Assert.Equal("ask GitHub Copilot now", Vocabulary.Correct("ask git hub copilot now", words));
        Assert.Equal("I love vibe coding.", Vocabulary.Correct("I love Vibe Coding.", words));
        Assert.Equal("Pick a genre or two genes", Vocabulary.Correct("Pick a genre or two genes", words));
        Assert.Equal("Review the Cabernets config", Vocabulary.Correct("Review the Cabernets config", words));
        Assert.Equal("nothing to do here", Vocabulary.Correct("nothing to do here", words));
        Assert.Equal("Kubernetes, Kubernetes!", Vocabulary.Correct("Kubernets, kubernetes!", words));

        // Numbers, endings and punctuation are never changed.
        Assert.Equal("I upgraded to Windows 10 yesterday.", Vocabulary.Correct("I upgraded to Windows 10 yesterday.", ["Windows 11"]));
        Assert.Equal("Pixel 8 Pro is fine", Vocabulary.Correct("Pixel 8 Pro is fine", ["Pixel 9 Pro"]));
        Assert.Equal("Ask Priyanka's team.", Vocabulary.Correct("Ask Priyanka's team.", ["Priyanka"]));
        Assert.Equal("We sold two Chromebooks.", Vocabulary.Correct("We sold two Chromebooks.", ["Chromebook"]));
        Assert.Equal("Let me check it out. Look at this.", Vocabulary.Correct("Let me check it out. Look at this.", ["Outlook"]));
        Assert.Equal("Open Outlook now", Vocabulary.Correct("Open out look now", ["Outlook"]));
        Assert.Equal("Genres matter.", new DictationFormatter().Format(Vocabulary.Correct("Genres matter.", words)));
    }

    [Fact]
    public void Vocabulary_becomes_a_prompt_and_echoes_are_dropped()
    {
        var words = Vocabulary.Parse("GitHub Copilot\n vibe coding, genres\n\ngenres\nGENRES\n");
        Assert.Equal(["GitHub Copilot", "vibe coding", "genres"], words);
        Assert.Equal("GitHub Copilot, vibe coding, genres", Vocabulary.Prompt(words));
        Assert.Null(Vocabulary.Prompt([]));
        Assert.True(Vocabulary.IsEcho("GitHub Copilot, vibe coding, genres.", words));
        Assert.True(Vocabulary.IsEcho("Vibe coding, genres", words));
        Assert.False(Vocabulary.IsEcho("Genres.", words)); // saying one listed word is real dictation
        Assert.False(Vocabulary.IsEcho("I tested videos of different genres", words));
        Assert.False(Vocabulary.IsEcho("", words));
    }

    [Fact]
    public void Profession_words_are_added_once_and_never_rewrite_ordinary_words()
    {
        Assert.Equal(
            "genres\ngithub\nGitHub Copilot\npull request\nKubernetes\nDocker\nAPI\nJSON\nTypeScript\nJavaScript\nPython\nPostgreSQL\nVS Code",
            Professions.AddTo("genres\ngithub", "Developer"));
        Assert.Equal("genres", Professions.AddTo("genres", "Other"));
        Assert.Equal(Professions.AddTo("", "Sales"), Professions.AddTo(Professions.AddTo("", "Sales"), "Sales"));

        // Long all-lowercase entries are reachable from ordinary words by the near-miss correction.
        string[] jargon = ["ibuprofen", "paracetamol", "amoxicillin", "affidavit", "indemnity", "plaintiff", "defendant", "syllabus", "go-to-market", "pull request", "term sheet"];
        foreach (var (role, list) in Professions.All)
        {
            foreach (var word in list)
            {
                if (word.Count(char.IsLetterOrDigit) >= 8 && !word.Any(char.IsUpper))
                {
                    Assert.True(jargon.Contains(word), $"{role}: \"{word}\" could rewrite ordinary words");
                }
            }
        }
    }
}
