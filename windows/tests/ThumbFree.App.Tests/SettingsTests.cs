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

        var settings = AppSettings.Load(path);

        Assert.Equal(Tier.Live, settings.EnglishTier);
        Assert.Equal(Tier.Standard, settings.HinglishTier);
        Assert.Equal(Language.English, settings.Language);
        Assert.Equal("ThumbFree", settings.Replacements["thumb free"]);
    }

    [Fact]
    public void A_file_with_a_mistake_is_never_overwritten()
    {
        var path = _folder.File("settings.json");
        const string broken = "{\n  \"insertMethod\": \"Type\",\n  \"oops\" \"no colon\"\n}";
        File.WriteAllText(path, broken);
        var store = new SettingsStore(AppSettings.Load(path), path);
        var changed = false;
        store.Changed += _ => changed = true;

        Assert.False(store.Update(s => s.SoundFeedback = false));

        Assert.Equal(broken, File.ReadAllText(path));
        Assert.Contains("line 3", store.LastError, StringComparison.Ordinal);
        Assert.True(changed);
        Assert.False(store.Current.SoundFeedback);
    }

    [Fact]
    public void Enums_are_written_as_names_and_hand_written_case_variants_are_replaced()
    {
        var path = _folder.File("settings.json");
        File.WriteAllText(path, """{ "KeepHistory": true, "pasteLastHotkey": "Alt+Shift+Z" }""");
        var store = new SettingsStore(AppSettings.Load(path), path);

        store.Update(s =>
        {
            s.KeepHistory = false;
            s.EnglishTier = Tier.Accurate;
        });

        var text = File.ReadAllText(path);
        var file = Read(path);
        Assert.False(file.ContainsKey("KeepHistory"));
        Assert.False(file["keepHistory"]!.GetValue<bool>());
        Assert.Equal("Accurate", file["englishTier"]!.GetValue<string>());
        Assert.Contains("\"Alt+Shift+Z\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_file_gets_every_setting_and_cleared_values_lose_their_key()
    {
        var path = _folder.File("settings.json");
        var store = new SettingsStore(new AppSettings(), path);

        store.Update(s => s.Profession = "Developer");
        Assert.Equal("Developer", Read(path)["profession"]!.GetValue<string>());
        Assert.True(Read(path).ContainsKey("replacements"));

        store.Update(s => s.Profession = null);
        Assert.False(Read(path).ContainsKey("profession"));
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

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
}
