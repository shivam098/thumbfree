using System.Text.Json.Nodes;
using ThumbFree.Core;

namespace ThumbFree.App.Tests;

public sealed class SettingsTests : IDisposable
{
    private const string VoiceDictationFile = """
        {
          // Voice Dictation 0.4, edited by hand
          "modelFile": "ggml-small.en-q8_0.bin",
          "insertMethod": "Type",
          "livePreview": false,
          "replacements": { "git hub": "GitHub", "journals": "genres" },
          "snippets": { "my signature": "Thanks,\nShivam" },
          "newLineKeys": { "Notepad": "Enter", "obsidian": "Enter" },
          "someFutureSetting": { "nested": [1, 2, 3] },
          "voiceDictationOnly": true,
        }
        """;

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Saving_a_change_keeps_everything_else_in_the_file()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, VoiceDictationFile);
        var store = new SettingsStore(AppSettings.Load(path), path);
        SettingsChange? changed = null;
        store.Changed += c => changed = c;

        Assert.True(store.Update(s => s.KeepHistory = false));

        var file = Read(path);
        Assert.False(file["keepHistory"]!.GetValue<bool>());
        Assert.Equal("Type", file["insertMethod"]!.GetValue<string>());
        Assert.Equal("Thanks,\nShivam", file["snippets"]!["my signature"]!.GetValue<string>());
        Assert.Equal("Enter", file["newLineKeys"]!["obsidian"]!.GetValue<string>());
        Assert.Equal("genres", file["replacements"]!["journals"]!.GetValue<string>());
        Assert.Equal(3, file["someFutureSetting"]!["nested"]!.AsArray().Count);
        Assert.True(file["voiceDictationOnly"]!.GetValue<bool>());
        Assert.Equal(new[] { nameof(AppSettings.KeepHistory) }, changed!.Names);
        Assert.False(changed.NeedsRestart);
    }

    [Fact]
    public void A_save_writes_only_what_changed_so_hand_edits_since_loading_survive()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, VoiceDictationFile);
        var store = new SettingsStore(AppSettings.Load(path), path);
        File.WriteAllText(path, VoiceDictationFile.Replace("\"Type\"", "\"Paste\"", StringComparison.Ordinal));

        store.Update(s => s.SmartFormatting = false);

        var file = Read(path);
        Assert.Equal("Paste", file["insertMethod"]!.GetValue<string>());
        Assert.False(file["smartFormatting"]!.GetValue<bool>());
        Assert.Equal("Type", store.Current.InsertMethod);
    }

    [Fact]
    public void Voice_Dictation_settings_are_copied_and_older_choices_carry_over()
    {
        var path = _folder.File(Path.Combine("ThumbFree", "settings.json"));
        var voiceDictation = _folder.File("voice-dictation.json");
        File.WriteAllText(voiceDictation, VoiceDictationFile);
        File.WriteAllText(_folder.File("dictation-hinglish"), string.Empty);
        File.WriteAllText(_folder.File("dictation-hinglish-accurate"), string.Empty);

        var settings = AppSettings.Load(path, voiceDictation, _folder.Path);

        Assert.True(File.Exists(path));
        Assert.False(settings.LiveTyping);
        Assert.False(settings.LivePreview);
        Assert.Equal(Language.Hinglish, settings.Language);
        Assert.Equal(Tier.Accurate, settings.HinglishTier);
        Assert.Equal(ModelCatalog.HinglishAccurate, settings.SelectedModel());
        Assert.Equal(Tier.Live, settings.EnglishTier);
        Assert.True(settings.KeepHistory);
        Assert.False(settings.SaveSamples);
        Assert.False(settings.GuideDone);
        Assert.Equal("Type", settings.InsertMethod);
        Assert.Equal("GitHub", settings.Replacements["GIT HUB"]);
    }

    [Fact]
    public void Newer_fields_win_over_the_older_flags()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, """{ "language": "English", "liveTyping": true, "livePreview": false }""");
        File.WriteAllText(_folder.File("dictation-hinglish"), string.Empty);

        var settings = AppSettings.Load(path, null, _folder.Path);

        Assert.Equal(Language.English, settings.Language);
        Assert.True(settings.LiveTyping);
    }

    [Fact]
    public void Unknown_choices_fall_back_to_defaults_without_losing_the_rest()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, """{ "englishTier": "Turbo", "hinglishTier": "live", "language": 7, "replacements": { "thumb free": "ThumbFree" } }""");

        var store = new SettingsStore(AppSettings.Load(path), path);

        Assert.Equal(Tier.Live, store.Current.EnglishTier);
        Assert.Equal(Tier.Standard, store.Current.HinglishTier);
        Assert.Equal(Language.English, store.Current.Language);
        Assert.Equal("ThumbFree", store.Current.Replacements["thumb free"]);
        Assert.Contains("“englishTier” should be Live, Standard or Accurate", store.LastError, StringComparison.Ordinal);
        Assert.Contains("“language” should be English or Hinglish", store.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("hinglishTier", store.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_a_mistake_is_never_overwritten()
    {
        var path = _folder.File("settings.json");
        const string broken = "{\n  \"insertMethod\": \"Type\",\n  \"oops\" \"no colon\"\n}";
        File.WriteAllText(path, broken);
        var store = new SettingsStore(AppSettings.Load(path), path);
        Assert.StartsWith("settings.json has a mistake near line 3,", store.LastError, StringComparison.Ordinal);
        Assert.Equal(store.LastError, store.ProblemSummary);
        var changed = false;
        store.Changed += _ => changed = true;

        Assert.False(store.Update(s => s.SoundFeedback = false));

        Assert.Equal(broken, File.ReadAllText(path));
        Assert.StartsWith("This change isn't saved: settings.json has a mistake near line 3.", store.LastError, StringComparison.Ordinal);
        Assert.True(changed);
        Assert.False(store.Current.SoundFeedback);
    }

    [Fact]
    public void A_file_fixed_while_ThumbFree_runs_waits_for_a_restart_and_a_deleted_one_starts_again()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, "{ \"replacements\": { \"journals\": \"genres\" }, }}");
        var store = new SettingsStore(AppSettings.Load(path), path);
        const string fixedByHand = "{ \"replacements\": { \"journals\": \"genres\" } }";
        File.WriteAllText(path, fixedByHand);

        Assert.False(store.Update(s => s.Replacements = new(s.Replacements) { ["thumb free"] = "ThumbFree" }));
        Assert.Equal(fixedByHand, File.ReadAllText(path));
        Assert.Contains("Restart ThumbFree", store.LastError, StringComparison.Ordinal);

        File.Delete(path);
        Assert.True(store.Update(s => s.SoundFeedback = false));
        Assert.Null(store.LastError);
        Assert.False(AppSettings.Load(path).SoundFeedback);
        Assert.Equal("ThumbFree", AppSettings.Load(path).Replacements["thumb free"]);
    }

    [Fact]
    public void Enums_are_written_as_names_and_keys_this_version_does_not_read_are_left_alone()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, """{ "KeepHistory": true, "pasteLastHotkey": "Alt+Shift+Z" }""");
        var store = new SettingsStore(AppSettings.Load(path), path);

        store.Update(s =>
        {
            s.KeepHistory = false;
            s.EnglishTier = Tier.Accurate;
        });

        Assert.Equal("""{ "KeepHistory": true, "pasteLastHotkey": "Alt+Shift+Z","englishTier":"Accurate","keepHistory":false }""", File.ReadAllText(path));
        Assert.False(AppSettings.Load(path).KeepHistory);
    }

    [Fact]
    public void A_new_file_gets_every_setting_and_a_cleared_value_becomes_null()
    {
        var path = _folder.File("settings.json");
        var store = new SettingsStore(new AppSettings(), path);

        store.Update(s => s.Profession = "Developer");
        Assert.Equal("Developer", Read(path)["profession"]!.GetValue<string>());
        Assert.True(Read(path).ContainsKey("replacements"));

        store.Update(s => s.Profession = null);
        Assert.Null(Read(path)["profession"]);
        Assert.Null(AppSettings.Load(path).Profession);
    }

    [Fact]
    public void No_change_writes_nothing()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, "{ }");
        var store = new SettingsStore(AppSettings.Load(path), path);
        var raised = false;
        store.Changed += _ => raised = true;

        store.Update(s => s.KeepHistory = true);

        Assert.Equal("{ }", File.ReadAllText(path));
        Assert.False(raised);
    }

    [Fact]
    public void Settings_read_only_at_start_say_so()
    {
        var store = new SettingsStore(new AppSettings(), _folder.File("settings.json"));
        SettingsChange? changed = null;
        store.Changed += c => changed = c;

        store.Update(s => s.InsertMethod = "Type");

        Assert.True(changed!.NeedsRestart);
    }

    [Fact]
    public void The_personal_dictionary_round_trips_through_text()
    {
        var entries = new Dictionary<string, string>
        {
            ["journals"] = "genres",
            ["my address"] = "1 Main Street\nBengaluru",
            ["back slash"] = @"C:\temp",
        };

        Assert.True(PersonalDictionary.TryParse(PersonalDictionary.Format(entries), out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(entries, parsed);
        Assert.Equal("genres", parsed["JOURNALS"]);
    }

    [Theory]
    [InlineData("journals = genres\r\n\r\nthumb free = ThumbFree\r\n", true, null)]
    [InlineData("journals genres", false, "Line 1")]
    [InlineData("ok = fine\n = nothing heard", false, "Line 2")]
    public void The_personal_dictionary_names_the_line_with_a_mistake(string text, bool valid, string? where)
    {
        Assert.Equal(valid, PersonalDictionary.TryParse(text, out var entries, out var error));
        if (where is null)
        {
            Assert.Equal(2, entries.Count);
        }
        else
        {
            Assert.StartsWith(where, error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_key_written_twice_loads_and_the_last_one_wins()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, """
            {
              "insertMethod": "Paste",
              "soundFeedback": false,
              "insertMethod": "Type"
            }
            """);

        var settings = AppSettings.Load(path);

        Assert.Equal("Type", settings.InsertMethod);
        Assert.False(settings.SoundFeedback);
    }

    [Fact]
    public void Dictionary_entries_that_differ_only_in_case_are_merged_and_the_last_one_wins()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, """{ "replacements": { "Git Hub": "Github", "journals": "genres", "git hub": "GitHub" } }""");

        var settings = AppSettings.Load(path);

        Assert.Equal(2, settings.Replacements.Count);
        Assert.Equal("GitHub", settings.Replacements["GIT HUB"]);
    }

    [Theory]
    [InlineData("{ \"insertMethod\": \"Paste\", \"insertMethod\": \"Type\" }")]
    [InlineData("{ \"snippets\": { \"Sig\": \"a\", \"sig\": \"b\" }, \"newLineKeys\": { \"Code\": \"Enter\", \"code\": \"Shift+Enter\" } }")]
    [InlineData("{ \"replacements\": [\"git hub\", \"GitHub\"] }")]
    [InlineData("{ \"replacements\": { \"git hub\": 5, \"journals\": null } }")]
    [InlineData("{ \"threads\": 1e40, \"maxDictationSeconds\": -1.5, \"language\": {} }")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("\"just text\"")]
    [InlineData("{ } { }")]
    [InlineData("{ \"a\": [[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]] }")]
    [InlineData("")]
    public void Loading_never_throws(string text)
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, text);

        var settings = AppSettings.Load(path);

        Assert.NotNull(settings.Replacements);
        Assert.NotNull(new SettingsStore(settings, path).Current);
    }

    [Fact]
    public void A_wrong_value_keeps_its_default_and_every_other_value()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, WrongTypes);

        var store = new SettingsStore(AppSettings.Load(path), path);

        Assert.False(store.Current.KeepHistory);
        Assert.Equal("Type", store.Current.InsertMethod);
        Assert.Equal("genres", store.Current.Replacements["journals"]);
        Assert.Equal(new AppSettings().Threads, store.Current.Threads);
        Assert.Equal(-1, store.Current.MicrophoneDevice);
        Assert.Contains("“threads” should be a whole number", store.LastError, StringComparison.Ordinal);
        Assert.Contains("“microphoneDevice” should be a whole number", store.LastError, StringComparison.Ordinal);
        Assert.Equal(
            "Some values in settings.json can't be used (“threads”, “microphoneDevice”), so ThumbFree uses its defaults for them. Settings shows what to fix.",
            store.ProblemSummary);
    }

    [Fact]
    public void An_unrelated_save_keeps_the_real_entries_and_the_wrong_value_as_written()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, WrongTypes);
        var store = new SettingsStore(AppSettings.Load(path), path);

        store.Update(s => s.Replacements = new(s.Replacements, StringComparer.OrdinalIgnoreCase) { ["co pilot"] = "Copilot" });

        var text = File.ReadAllText(path);
        Assert.Contains("“threads” should be a whole number", store.LastError, StringComparison.Ordinal);
        Assert.Contains("\"threads\": \"four\",", text, StringComparison.Ordinal);
        Assert.Contains("\"microphoneDevice\": null,", text, StringComparison.Ordinal);
        Assert.Contains("\"keepHistory\": false,", text, StringComparison.Ordinal);
        Assert.Equal(
            new Dictionary<string, string> { ["journals"] = "genres", ["thumb free"] = "ThumbFree", ["co pilot"] = "Copilot" },
            AppSettings.Load(path).Replacements);
    }

    [Theory]
    [InlineData("\"replacements\": { \"caf@\": \"Caf@\", \"journals\": \"genres\" }", "replacements", "an entry in “replacements”")]
    [InlineData("\"snippets\": { \"sig\": \"Thanks, Ren@\" }", "snippets", "an entry in “snippets”")]
    [InlineData("\"vocabulary\": \"Caf@\\nGitHub\"", "vocabulary", "“vocabulary”")]
    [InlineData("\"caf@\": true", null, null)]
    [InlineData("/* caf@ */ \"x\": 1", null, null)]
    public void Text_saved_in_another_encoding_costs_only_its_own_setting(string line, string? key, string? named)
    {
        var path = _folder.File("settings.json");
        var original = Ansi("{\n  \"keepHistory\": false,\n  " + line + ",\n  \"insertMethod\": \"Type\"\n}");
        File.WriteAllBytes(path, original);

        var store = new SettingsStore(AppSettings.Load(path), path);

        Assert.False(store.Current.KeepHistory);
        Assert.Equal("Type", store.Current.InsertMethod);
        Assert.Equal(string.Empty, store.Current.Vocabulary);
        Assert.Empty(store.Current.Snippets);
        Assert.Equal(key == "replacements" ? new Dictionary<string, string> { ["journals"] = "genres" } : new AppSettings().Replacements, store.Current.Replacements);
        if (named is null)
        {
            Assert.Null(store.LastError);
        }
        else
        {
            Assert.StartsWith($"In settings.json, {named} has text that isn't UTF-8 (the file may have been saved in another encoding)", store.LastError, StringComparison.Ordinal);
        }

        Assert.True(store.Update(s => s.SoundFeedback = false));
        var saved = File.ReadAllBytes(path);
        Assert.Equal(original.Count(b => b == 0xE9), saved.Count(b => b == 0xE9));
        Assert.False(AppSettings.Load(path).SoundFeedback);
    }

    [Fact]
    public void Comments_and_formatting_survive_a_save()
    {
        var path = _folder.File("settings.json");
        const string original = """
            {
              // Voice Dictation 0.4, edited by hand
              "insertMethod": "Type", /* keystrokes for my old editor */
              "soundFeedback": true,
              "voiceDictationOnly": true,
            }
            """;
        File.WriteAllText(path, original);
        var store = new SettingsStore(AppSettings.Load(path), path);

        Assert.True(store.Update(s => s.SoundFeedback = false));

        Assert.Equal(original.Replace("\"soundFeedback\": true", "\"soundFeedback\": false", StringComparison.Ordinal), File.ReadAllText(path));
    }

    [Fact]
    public void New_keys_go_before_the_closing_brace_with_the_file_s_indentation_and_line_endings()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, "{\r\n    \"insertMethod\": \"Type\" // mine\r\n}\r\n");
        var store = new SettingsStore(AppSettings.Load(path), path);

        store.Update(s =>
        {
            s.KeepHistory = false;
            s.Replacements = new() { ["journals"] = "genres" };
        });

        Assert.Equal(
            "{\r\n    \"insertMethod\": \"Type\", // mine\r\n    \"replacements\": {\r\n      \"journals\": \"genres\"\r\n    },\r\n    \"keepHistory\": false\r\n}\r\n",
            File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{\n  \"insertMethod\": \"Type\",\n}\n", "{\n  \"insertMethod\": \"Type\",\n  \"keepHistory\": false\n}\n")]
    [InlineData("{\"insertMethod\":\"Type\",}", "{\"insertMethod\":\"Type\",\"keepHistory\":false}")]
    [InlineData("{\n}", "{\n  \"keepHistory\": false\n}")]
    [InlineData("{}", "{\"keepHistory\":false}")]
    [InlineData("{\n  \"insertMethod\": \"Type\" /* a, b */ }", "{\n  \"insertMethod\": \"Type\", /* a, b */ \n  \"keepHistory\": false\n}")]
    [InlineData("{ \"insertMethod\": \"Type\",\n\t\"x\": 2\n}", "{ \"insertMethod\": \"Type\",\n\t\"x\": 2,\n\t\"keepHistory\": false\n}")]
    public void New_keys_respect_trailing_commas_and_files_on_one_line(string original, string expected)
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, original);
        var store = new SettingsStore(AppSettings.Load(path), path);

        Assert.True(store.Update(s => s.KeepHistory = false));

        Assert.Equal(expected, File.ReadAllText(path));
        Assert.False(AppSettings.Load(path).KeepHistory);
    }

    [Fact]
    public void Every_copy_of_a_key_written_twice_is_updated()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, "{\n  \"soundFeedback\": true,\n  // again, further down\n  \"soundFeedback\": true,\n}\n");
        var store = new SettingsStore(AppSettings.Load(path), path);

        store.Update(s => s.SoundFeedback = false);

        Assert.Equal("{\n  \"soundFeedback\": false,\n  // again, further down\n  \"soundFeedback\": false,\n}\n", File.ReadAllText(path));
    }

    [Fact]
    public void A_dictionary_is_rewritten_in_place_under_its_key()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, "{\n  // the defaults\n  \"replacements\": {\n    \"git hub\": \"GitHub\"\n  },\n  \"x\": 1\n}");
        var store = new SettingsStore(AppSettings.Load(path), path);

        store.Update(s => s.Replacements = new(s.Replacements) { ["journals"] = "genres" });

        Assert.Equal("{\n  // the defaults\n  \"replacements\": {\n    \"git hub\": \"GitHub\",\n    \"journals\": \"genres\"\n  },\n  \"x\": 1\n}", File.ReadAllText(path));
    }

    [Fact]
    public void A_byte_order_mark_and_unusual_spacing_are_kept()
    {
        var path = _folder.File("settings.json");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var original = "{ \"soundFeedback\"  :   true ,\n\t\"modelFile\": \"ggml-base.en-q8_0.bin\" }";
        File.WriteAllBytes(path, bom.Concat(System.Text.Encoding.UTF8.GetBytes(original)).ToArray());
        var store = new SettingsStore(AppSettings.Load(path), path);
        Assert.Equal("ggml-base.en-q8_0.bin", store.Current.ModelFile);

        store.Update(s => s.SoundFeedback = false);

        var expected = bom.Concat(System.Text.Encoding.UTF8.GetBytes(original.Replace("true", "false", StringComparison.Ordinal))).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(nameof(AppSettings.SoundFeedback))]
    [InlineData(nameof(AppSettings.RemoveFillerWords))]
    [InlineData(nameof(AppSettings.SpokenPunctuation))]
    [InlineData(nameof(AppSettings.Replacements))]
    public void Settings_the_host_applies_at_once_are_each_reported_on_their_own(string name)
    {
        var store = new SettingsStore(new AppSettings(), _folder.File("settings.json"));
        SettingsChange? changed = null;
        store.Changed += c => changed = c;

        store.Update(s =>
        {
            switch (name)
            {
                case nameof(AppSettings.SoundFeedback):
                    s.SoundFeedback = false;
                    break;
                case nameof(AppSettings.RemoveFillerWords):
                    s.RemoveFillerWords = false;
                    break;
                case nameof(AppSettings.SpokenPunctuation):
                    s.SpokenPunctuation = false;
                    break;
                default:
                    s.Replacements = new(s.Replacements) { ["journals"] = "genres" };
                    break;
            }
        });

        Assert.Equal(new[] { name }, changed!.Names);
        Assert.False(changed.NeedsRestart);
    }

    private const string WrongTypes = """
        {
          "threads": "four",
          "microphoneDevice": null,
          "keepHistory": false,
          "insertMethod": "Type",
          "replacements": { "journals": "genres", "thumb free": "ThumbFree" }
        }
        """;

    private static JsonObject Read(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();

    /// <summary>The file as Notepad's "ANSI" would save it: each "@" stands for "é", the single byte E9, which isn't UTF-8.</summary>
    private static byte[] Ansi(string json) =>
        json.Split('@').SelectMany((part, i) => (i == 0 ? Array.Empty<byte>() : [0xE9]).Concat(System.Text.Encoding.UTF8.GetBytes(part))).ToArray();
}
