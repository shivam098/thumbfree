using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // People read and edit this file: "Alt+Shift+H" rather than "Alt\u002BShift\u002BH".
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new NamedEnumConverter<Language>(), new NamedEnumConverter<Tier>() },
    };

    /// <summary>The same as <see cref="JsonOptions"/>, on one line.</summary>
    internal static readonly JsonSerializerOptions CompactOptions = new(JsonOptions) { WriteIndented = false };

    /// <summary>Every setting the file can hold: its JSON key (camelCase) and its property.</summary>
    internal static IReadOnlyList<(string Key, PropertyInfo Property)> Fields { get; } = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p is { CanRead: true, CanWrite: true } && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .Select(p => (JsonNamingPolicy.CamelCase.ConvertName(p.Name), p))
        .ToList();

    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);

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
    /// Reads <paramref name="path"/>, first copying Voice Dictation's settings there if it doesn't exist yet. Never
    /// throws. Fields this version doesn't know are ignored (and kept in the file), missing ones take their defaults, a
    /// key written twice takes its last value, and a value of the wrong kind keeps that one setting's default (see
    /// <see cref="LoadProblems"/>). A file with a mistake gives the defaults and is never overwritten. Only a new (or
    /// empty) file is written.
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
        catch (Exception ex)
        {
            Log.Warn($"couldn't copy Voice Dictation's settings: {ex.GetType().Name}");
        }

        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path) is var file && !SettingsJson.IsBlank(file))
            {
                return FromFile(file, dataDir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings unreadable, using defaults: {ex.GetType().Name}");
            var unreadable = new AppSettings();
            unreadable._problems[string.Empty] = $"ThumbFree couldn't read settings.json ({ex.Message}), so it's using its default settings and won't change the file until it restarts.";
            return unreadable;
        }

        var settings = new AppSettings();
        settings.Migrate(new Dictionary<string, JsonElement>(), dataDir);
        try
        {
            settings.Save(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"couldn't write default settings: {ex.GetType().Name}");
        }

        return settings;
    }

    /// <summary>Writes every setting to a new file. Changes to an existing file go through <see cref="SettingsStore"/>, which keeps the rest of it.</summary>
    public void Save() => Save(AppPaths.SettingsFile);

    internal void Save(string path) => SettingsStore.WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));

    /// <summary>
    /// What was wrong in the file when it was read: a message per JSON key whose value couldn't be used (that setting
    /// kept its default), or under "" when the whole file couldn't be read.
    /// </summary>
    internal IReadOnlyDictionary<string, string> LoadProblems => _problems;

    /// <summary>"In settings.json, “threads” should be a whole number…": the message for a value of the wrong kind.</summary>
    internal static string WrongValue(string key, string expected) =>
        $"In settings.json, “{key}” should be {expected}, so ThumbFree uses its default for it.";

    /// <summary>"settings.json has a mistake near line 3…" for a file that can't be parsed.</summary>
    internal static string Mistake(JsonException ex) => ex.LineNumber is { } line
        ? $"settings.json has a mistake near line {line + 1}, so ThumbFree is using its default settings and won't change the file. Fix it (Settings › Advanced › Open settings file) and restart ThumbFree, or delete it to start again."
        : $"{ex.Message} ThumbFree is using its default settings and won't change the file. Fix it (Settings › Advanced › Open settings file) and restart ThumbFree, or delete it to start again.";

    /// <summary>Each known key on its own: one value of the wrong kind costs only that setting, never the rest.</summary>
    private static AppSettings FromFile(byte[] file, string? dataDir)
    {
        var settings = new AppSettings();
        Dictionary<string, JsonElement> values;
        try
        {
            values = SettingsJson.Values(file);
        }
        catch (JsonException ex)
        {
            Log.Warn("settings.json has a syntax error, using defaults");
            settings._problems[string.Empty] = Mistake(ex);
            return settings;
        }

        foreach (var (key, property) in Fields)
        {
            if (values.TryGetValue(key, out var value))
            {
                settings.Read(key, property, value);
            }
        }

        settings.Normalize();
        settings.Migrate(values, dataDir);
        return settings;
    }

    private void Read(string key, PropertyInfo property, JsonElement value)
    {
        if (property.PropertyType == typeof(Dictionary<string, string>))
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                _problems[key] = WrongValue(key, "a { } list of “what”: “text” pairs");
                return;
            }

            // Keys are matched ignoring case, so "Git Hub" and "git hub" are one entry: the last one wins.
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in value.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    entries[entry.Name] = entry.Value.GetString()!;
                }
                else
                {
                    _problems[key] = $"In settings.json, “{key}” should have text in quotes for “{entry.Name}”, so that entry is left out.";
                }
            }

            property.SetValue(this, entries);
            return;
        }

        try
        {
            var read = value.Deserialize(property.PropertyType, JsonOptions);
            if (read is null && new NullabilityInfoContext().Create(property).WriteState != NullabilityState.Nullable)
            {
                throw new JsonException("null");
            }

            property.SetValue(this, read);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            _problems[key] = WrongValue(key, Expected(property.PropertyType));
        }
    }

    private static string Expected(Type type) =>
        type == typeof(bool) ? "true or false"
        : type == typeof(int) ? "a whole number"
        : type == typeof(string) ? "text in quotes"
        : type.IsEnum ? string.Join(", ", Enum.GetNames(type)[..^1]) + " or " + Enum.GetNames(type)[^1]
        : "a different kind of value";

    /// <summary>Repairs what a hand-edited or older file can leave behind: dictionaries that ignore case, a live Hindi tier.</summary>
    private void Normalize()
    {
        Replacements = Merged(Replacements);
        Snippets = Merged(Snippets);
        NewLineKeys = Merged(NewLineKeys);
        Vocabulary ??= string.Empty;
        if (HinglishTier == Tier.Live)
        {
            HinglishTier = Tier.Standard;
        }
    }

    /// <summary>The entries with keys that ignore case; of two that differ only in case, the last one wins.</summary>
    private static Dictionary<string, string> Merged(Dictionary<string, string>? source)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source ?? [])
        {
            if (key is not null && value is not null)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    /// <summary>Carries older choices over when the file doesn't have the newer field yet.</summary>
    private void Migrate(IReadOnlyDictionary<string, JsonElement> file, string? dataDir)
    {
        if (!file.ContainsKey("liveTyping") && file.TryGetValue("livePreview", out var preview) && preview.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            LiveTyping = preview.GetBoolean();
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

    /// <summary>Reads an enum's name in any case; anything else is a mistake reported for that one setting.</summary>
    private sealed class NamedEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name && !name.Any(char.IsDigit) &&
                Enum.TryParse<T>(name, ignoreCase: true, out var value) && Enum.IsDefined(value))
            {
                return value;
            }

            throw new JsonException($"Not one of {string.Join(", ", Enum.GetNames<T>())}.");
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
