using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ThumbFree.Core;

namespace ThumbFree.App;

internal static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThumbFree");

    /// <summary>Where downloaded models go: a "models" folder next to the exe (portable zip with models) or per-user data.</summary>
    public static string ModelsDir { get; } = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "models"))
        ? Path.Combine(AppContext.BaseDirectory, "models")
        : Path.Combine(DataDir, "models");

    /// <summary>
    /// Models already downloaded by Voice Assistant or Voice Dictation (ThumbFree's predecessor on Windows) are reused,
    /// read only, instead of downloading them again.
    /// </summary>
    public static IReadOnlyList<string> SharedModelsDirs { get; } =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceDictation", "models"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant", "models"),
    ];

    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Present while the status pill is shown all the time (by default it appears only while it has something to say).</summary>
    public static string OverlayPinnedFlag => Path.Combine(DataDir, "overlay-pinned");

    /// <summary>Present while dictation uses the Hindi + English model.</summary>
    public static string HinglishDictationFlag => Path.Combine(DataDir, "dictation-hinglish");

    /// <summary>Present while Hindi + English dictation uses the larger, more accurate (slower) model.</summary>
    public static string HinglishAccurateFlag => Path.Combine(DataDir, "dictation-hinglish-accurate");

    /// <summary>Present while the hotkeys are paused from the tray.</summary>
    public static string PausedFlag => Path.Combine(DataDir, "paused");

    /// <summary>History: the text of recent dictations (30 days, at most 500), on this PC only (Local, not roaming).</summary>
    public static string HistoryFile => Path.Combine(DataDir, "history.txt");

    /// <summary>"Help improve ThumbFree": dictations kept to share by hand, only while that switch is on.</summary>
    public static string SamplesDir => Path.Combine(DataDir, "samples");

    public static string SettingsFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThumbFree", "settings.json");

    /// <summary>Voice Dictation's settings: copied once on first run, so the dictionary and options carry over.</summary>
    public static string VoiceDictationSettingsFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceDictation", "settings.json");

    /// <summary>Full path of a model file, looking in <see cref="ModelsDir"/> then the shared folders; null if absent.</summary>
    public static string? FindModel(string file)
    {
        foreach (var dir in SharedModelsDirs.Prepend(ModelsDir))
        {
            var path = Path.Combine(dir, file);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

/// <summary>Bits of app state (paused, the pill pinned) remembered as empty flag files, apart from settings.json.</summary>
internal static class StateFlags
{
    public static void Set(string path, bool on)
    {
        try
        {
            if (on)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"couldn't save {Path.GetFileName(path)}: {ex.GetType().Name}");
        }
    }
}

/// <summary>
/// %APPDATA%\ThumbFree\settings.json. Settings that the app window shows are saved through <see cref="SettingsStore"/>,
/// which writes only what changed; everything else in the file is edited by hand (Settings › Advanced › Open settings file).
/// </summary>
internal sealed class AppSettings
{
    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // People read and edit this file: "Alt+Shift+H" rather than "Alt\u002BShift\u002BH".
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new LenientEnumConverter<Language>(), new LenientEnumConverter<Tier>() },
    };

    /// <summary>
    /// Settings read only when ThumbFree starts: a change to them (by hand in the file) needs a restart. Everything the
    /// Settings page changes applies without one, through <see cref="SettingsStore.Changed"/>.
    /// </summary>
    public static IReadOnlySet<string> ReadAtStart { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ModelFile), nameof(VadModelFile), nameof(HinglishModelFile), nameof(HinglishAccurateModelFile),
        nameof(DictationLanguageHotkey), nameof(PasteLastHotkey), nameof(Threads), nameof(AudioContextSize),
        nameof(VocabularyPrompt), nameof(MicrophoneDevice), nameof(InsertMethod), nameof(Snippets), nameof(NewLineKeys),
    };

    public string ModelFile { get; set; } = "ggml-small.en-q8_0.bin";

    /// <summary>Silero voice-activity model: skips silence and splits long recordings at pauses. Optional but recommended.</summary>
    public string VadModelFile { get; set; } = "ggml-silero-v6.2.0.bin";

    /// <summary>Used when "Hindi + English" is switched on (Alt+Shift+H or the tray); types Hindi in Roman script.</summary>
    public string HinglishModelFile { get; set; } = "ggml-hinglish-swift-q8_0.bin";

    /// <summary>
    /// "Hindi + English (accurate)": a larger model (834 MB, about 10x slower, fewer mistakes). Typed text comes from it;
    /// the live preview keeps using the fast model.
    /// </summary>
    public string HinglishAccurateModelFile { get; set; } = "ggml-hinglish-apex-q8_0.bin";

    public string DictationLanguageHotkey { get; set; } = "Alt+Shift+H";

    public string PasteLastHotkey { get; set; } = "Alt+Shift+Z";

    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount * 2 / 3, 2, 8);

    /// <summary>768 tokens covers 15.36 s of audio and is ~3x faster than the default 30 s window.</summary>
    public int AudioContextSize { get; set; } = 768;

    /// <summary>Optional vocabulary hint. Off by default: Whisper can echo prompt text on near-silent input.</summary>
    public string? VocabularyPrompt { get; set; }

    /// <summary>-1 uses the Windows default recording device.</summary>
    public int MicrophoneDevice { get; set; } = -1;

    /// <summary>
    /// "Paste": insert via the clipboard (reliable everywhere; kept out of Clipboard History and your previous
    /// clipboard is restored). "Type": synthesized keystrokes (some apps drop characters).
    /// </summary>
    public string InsertMethod { get; set; } = "Paste";

    /// <summary>Short tones when recording starts and ends ("Sounds" in Settings).</summary>
    public bool SoundFeedback { get; set; } = true;

    /// <summary>
    /// Live typing: words appear as you speak (typed at each pause, the phrase still being spoken in the pill). Off,
    /// they show only in the pill, and the models that re-read the audio stop doing so. Replaces Voice Dictation's
    /// "livePreview", which old settings files carry over.
    /// </summary>
    public bool LiveTyping { get; set; } = true;

    /// <summary>Voice Dictation's name for <see cref="LiveTyping"/>, for code that still reads it.</summary>
    [JsonIgnore]
    public bool LivePreview
    {
        get => LiveTyping;
        set => LiveTyping = value;
    }

    /// <summary>
    /// A recording that is still going after this many seconds (Ctrl+Win was never tapped again) is finished and
    /// typed, so the microphone can't stay on by mistake. Default 600 (10 minutes); 0 turns the limit off.
    /// </summary>
    public int MaxDictationSeconds { get; set; } = 600;

    public bool RemoveFillerWords { get; set; } = true;

    public bool SpokenPunctuation { get; set; } = true;

    /// <summary>Whole-word fixes applied to dictated text (personal dictionary), e.g. { "git hub": "GitHub" }.</summary>
    public Dictionary<string, string> Replacements { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git hub"] = "GitHub",
        ["co-pilot"] = "Copilot",
        ["co pilot"] = "Copilot",
    };

    /// <summary>Say the trigger (or “insert &lt;trigger&gt;”) to type the text, e.g. { "my signature": "Thanks,\nShivam" }.</summary>
    public Dictionary<string, string> Snippets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keys pressed for “new line” per process name. Apps not listed get Shift+Enter, which chat apps treat as a new
    /// line rather than “send”.
    /// </summary>
    public Dictionary<string, string> NewLineKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Notepad"] = "Enter",
        ["WINWORD"] = "Enter",
        ["Code"] = "Enter",
        ["devenv"] = "Enter",
    };

    /// <summary>Dictation language; Alt+Shift+H, the tray and Settings switch it.</summary>
    public Language Language { get; set; } = Language.English;

    /// <summary>English model: Live (types as you speak; the default), Standard or Accurate.</summary>
    public Tier EnglishTier { get; set; } = Tier.Live;

    /// <summary>Hindi + English model: Standard or Accurate. There's no live model for it yet.</summary>
    public Tier HinglishTier { get; set; } = Tier.Standard;

    /// <summary>Smart formatting: emails laid out, spoken lists made into lists, “3 pm… actually 4 pm” → “4 pm”.</summary>
    public bool SmartFormatting { get; set; } = true;

    /// <summary>
    /// "Words I use often": names and jargon, one per line, written the way they're listed. Only the first
    /// <see cref="Core.Vocabulary.MaxEntries"/> are used.
    /// </summary>
    public string Vocabulary { get; set; } = string.Empty;

    /// <summary>"What do you do?": the field picked last (its words were added to <see cref="Vocabulary"/>). Stays on this PC.</summary>
    public string? Profession { get; set; }

    /// <summary>History: keep the text of each dictation on this PC for 30 days. Turning it off deletes History.</summary>
    public bool KeepHistory { get; set; } = true;

    /// <summary>"Help improve ThumbFree": keep each dictation's audio and text on this PC, to share only by hand.</summary>
    public bool SaveSamples { get; set; }

    /// <summary>The first-run guide was finished, skipped or closed; it's offered again from Home, Settings and the tray.</summary>
    public bool GuideDone { get; set; }

    public Tier TierFor(Language language) => language == Language.Hinglish ? HinglishTier : EnglishTier;

    /// <summary>The model chosen for <paramref name="language"/>.</summary>
    public ModelSpec ModelFor(Language language) => ModelCatalog.ForLanguage(language, TierFor(language));

    /// <summary>The model dictation uses now: the chosen tier of the current language.</summary>
    public ModelSpec SelectedModel() => ModelFor(Language);

    public static AppSettings LoadOrCreate() => Load(AppPaths.SettingsFile, AppPaths.VoiceDictationSettingsFile, AppPaths.DataDir);

    /// <summary>
    /// Reads <paramref name="path"/>, first copying Voice Dictation's settings there if it doesn't exist yet. Fields
    /// this version doesn't know are ignored (and kept in the file), missing ones take their defaults, and a file that
    /// can't be read gives the defaults without being overwritten. Only a new file is written.
    /// </summary>
    /// <param name="dataDir">Where the older on/off flag files are (they become <see cref="Language"/> and <see cref="HinglishTier"/>).</param>
    internal static AppSettings Load(string path, string? voiceDictationPath = null, string? dataDir = null)
    {
        try
        {
            if (!File.Exists(path) && voiceDictationPath is not null && File.Exists(voiceDictationPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(voiceDictationPath, path);
                Log.Info("settings copied from Voice Dictation");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"couldn't copy Voice Dictation's settings: {ex.GetType().Name}");
        }

        try
        {
            if (File.Exists(path))
            {
                var file = JsonNode.Parse(File.ReadAllText(path), null, DocumentOptions) as JsonObject
                    ?? throw new JsonException("settings.json isn't a JSON object");
                var loaded = file.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
                loaded.Normalize();
                loaded.Migrate(file, dataDir);
                return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"settings unreadable, using defaults: {ex.GetType().Name}");
            return new AppSettings();
        }

        var settings = new AppSettings();
        settings.Migrate(new JsonObject(), dataDir);
        try
        {
            settings.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"couldn't write default settings: {ex.GetType().Name}");
        }

        return settings;
    }

    /// <summary>Writes every setting to a new file. Changes to an existing file go through <see cref="SettingsStore"/>, which keeps the rest of it.</summary>
    public void Save() => Save(AppPaths.SettingsFile);

    internal void Save(string path) => SettingsStore.WriteAtomically(path, JsonSerializer.SerializeToNode(this, JsonOptions)!.AsObject());

    /// <summary>Repairs what a hand-edited or older file can leave behind: nulls, case-sensitive dictionaries, a live Hindi tier.</summary>
    private void Normalize()
    {
        Replacements = new(Replacements ?? [], StringComparer.OrdinalIgnoreCase);
        Snippets = new(Snippets ?? [], StringComparer.OrdinalIgnoreCase);
        NewLineKeys = new(NewLineKeys ?? [], StringComparer.OrdinalIgnoreCase);
        Vocabulary ??= string.Empty;
        DictationLanguageHotkey ??= "Alt+Shift+H";
        PasteLastHotkey ??= "Alt+Shift+Z";
        if (HinglishTier == Tier.Live)
        {
            HinglishTier = Tier.Standard;
        }
    }

    /// <summary>Carries older choices over when the file doesn't have the newer field yet.</summary>
    private void Migrate(JsonObject file, string? dataDir)
    {
        if (!file.ContainsKey("liveTyping") && file["livePreview"] is JsonValue preview && preview.TryGetValue(out bool live))
        {
            LiveTyping = live;
        }

        if (dataDir is null)
        {
            return;
        }

        if (!file.ContainsKey("language") && File.Exists(Path.Combine(dataDir, Path.GetFileName(AppPaths.HinglishDictationFlag))))
        {
            Language = Language.Hinglish;
        }

        if (!file.ContainsKey("hinglishTier") && File.Exists(Path.Combine(dataDir, Path.GetFileName(AppPaths.HinglishAccurateFlag))))
        {
            HinglishTier = Tier.Accurate;
        }
    }

    /// <summary>Reads enum names in any case and turns unknown values into the default, so one typo can't reset every setting.</summary>
    private sealed class LenientEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named))
            {
                return named;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number))
            {
                return (T)Enum.ToObject(typeof(T), number);
            }

            reader.Skip();
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// The personal dictionary as Settings shows it: one "what it hears = what to type" per line. settings.json keeps it
/// as <see cref="AppSettings.Replacements"/>; a line break inside what to type is written \n, and a backslash \\.
/// </summary>
internal static class PersonalDictionary
{
    public static string Format(IReadOnlyDictionary<string, string> entries) =>
        string.Join(Environment.NewLine, entries.Select(e => $"{e.Key} = {Escape(e.Value ?? string.Empty)}"));

    /// <summary>False, with a message naming the line, when a line isn't "heard = typed".</summary>
    public static bool TryParse(string text, out Dictionary<string, string> entries, out string? error)
    {
        entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                error = $"Line {i + 1} needs an “=” between what it hears and what to type (journals = genres).";
                return false;
            }

            var heard = line[..eq].Trim();
            if (heard.Length == 0)
            {
                error = $"Line {i + 1} needs what it hears before the “=”.";
                return false;
            }

            entries[heard] = Unescape(line[(eq + 1)..].Trim());
        }

        return true;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is '\\' or 'n')
            {
                sb.Append(value[i + 1] == 'n' ? '\n' : '\\');
                i++;
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }
}
