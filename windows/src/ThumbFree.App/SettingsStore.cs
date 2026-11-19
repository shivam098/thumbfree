using System.Reflection;
using System.Text.Json;

namespace ThumbFree.App;

/// <summary>
/// Saves the Settings page's changes to settings.json without losing anything in it. Each save re-reads the file and
/// replaces only the text of the values that changed (<see cref="SettingsJson.Splice"/>), so comments, formatting, hand
/// edits made while ThumbFree runs, values the page doesn't show (snippets, new-line keys…) and fields this version
/// doesn't know (from Voice Dictation or a newer ThumbFree) stay exactly as they are. A file that can't be read is never
/// overwritten: the change still applies until ThumbFree exits, and <see cref="LastError"/> says how to fix the file.
/// Nothing here throws because of what's in the file.
/// </summary>
internal sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _problems;
    private string? _saveError;

    /// <summary>
    /// The file couldn't be read at start, so <see cref="Current"/> holds defaults rather than what's in it: until
    /// ThumbFree restarts, saving even one value could write a default over the person's own (a dictionary box shows the
    /// default entries), so nothing is saved unless the file is deleted.
    /// </summary>
    private bool _readFailed;

    public SettingsStore(AppSettings settings, string path)
    {
        Current = settings;
        FilePath = path;
        _problems = new(settings.LoadProblems, StringComparer.Ordinal);
        _readFailed = _problems.ContainsKey(string.Empty);
    }

    /// <summary>
    /// Raised on the thread that called <see cref="Update"/> (the UI thread) with the properties that changed. The
    /// dictation host applies them without a restart; see <see cref="AppSettings.ReadAtStart"/> for the exceptions.
    /// </summary>
    public event Action<SettingsChange>? Changed;

    /// <summary>The settings the whole app uses (one shared instance).</summary>
    public AppSettings Current { get; }

    public string FilePath { get; }

    /// <summary>
    /// What's wrong, for Home and Settings to show: why the last change wasn't saved, else the values in the file that
    /// couldn't be used when it was read (those settings kept their defaults). Null when all is well.
    /// </summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                if (_saveError is not null || _problems.Count == 0)
                {
                    return _saveError;
                }

                if (_problems.TryGetValue(string.Empty, out var whole))
                {
                    return whole;
                }

                return string.Join(" ", _problems.Values) +
                    (_problems.Count == 1 ? " Fix it in the file, then restart ThumbFree." : " Fix them in the file, then restart ThumbFree.");
            }
        }
    }

    /// <summary>One short line for a notification when ThumbFree starts with a problem in the file; null when there's none.</summary>
    public string? ProblemSummary
    {
        get
        {
            lock (_gate)
            {
                if (_problems.Count == 0)
                {
                    return null;
                }

                if (_problems.TryGetValue(string.Empty, out var whole))
                {
                    return whole;
                }

                var keys = string.Join(", ", _problems.Keys.Take(3).Select(k => $"“{k}”")) + (_problems.Count > 3 ? "…" : string.Empty);
                return $"Some values in settings.json can't be used ({keys}), so ThumbFree uses its defaults for them. Settings shows what to fix.";
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to <see cref="Current"/> and saves the fields it changed. Returns false when
    /// the file couldn't be written (see <see cref="LastError"/>); the change applies anyway. Call on the UI thread.
    /// </summary>
    public bool Update(Action<AppSettings> change)
    {
        SettingsChange changed;
        bool saved;
        lock (_gate)
        {
            var before = Snapshot();
            change(Current);
            var after = Snapshot();
            var keys = after.Keys.Where(k => before[k] != after[k]).ToList();
            if (keys.Count == 0)
            {
                return true;
            }

            saved = TryWrite(keys, out _saveError);
            if (saved)
            {
                // The file now holds good values for these keys (and it parses).
                foreach (var key in keys)
                {
                    _problems.Remove(key);
                }

                _problems.Remove(string.Empty);
            }

            changed = new SettingsChange(keys.Select(k => Field(k).Property.Name).ToHashSet(StringComparer.Ordinal));
        }

        if (!saved)
        {
            Log.Warn("settings change not saved");
        }

        Changed?.Invoke(changed);
        return saved;
    }

    /// <summary>
    /// Writes every setting to a new file when there's none (or it's empty), so "Open settings file" always opens
    /// something. An existing file is left alone.
    /// </summary>
    public void CreateIfMissing()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath) || SettingsJson.IsBlank(File.ReadAllBytes(FilePath)))
                {
                    WriteWholeFile();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"couldn't create the settings file: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>Writes through a temporary file, so a crash or a full disk never leaves half a settings file.</summary>
    internal static void WriteAtomically(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A new file holds exactly what's in use, so nothing read from an older one (or its problems) applies any more.</summary>
    private void WriteWholeFile()
    {
        Current.Save(FilePath);
        _readFailed = false;
        _problems.Clear();
        _saveError = null;
    }

    private static (string Key, PropertyInfo Property) Field(string key) => AppSettings.Fields.First(f => f.Key == key);

    private Dictionary<string, string> Snapshot() =>
        AppSettings.Fields.ToDictionary(f => f.Key, f => Json(f.Property, compact: true), StringComparer.Ordinal);

    private string Json(PropertyInfo property, bool compact) =>
        JsonSerializer.Serialize(property.GetValue(Current), property.PropertyType, compact ? AppSettings.CompactOptions : AppSettings.JsonOptions);

    private bool TryWrite(List<string> keys, out string? error)
    {
        error = null;
        try
        {
            var file = File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
            if (file is null || SettingsJson.IsBlank(file))
            {
                WriteWholeFile();
                return true;
            }

            if (_readFailed)
            {
                SettingsJson.Values(file);
                error = "This change isn't saved: settings.json couldn't be read when ThumbFree started. Restart ThumbFree to use the file, then make the change again.";
                return false;
            }

            var edits = keys.Select(k => new SettingsEdit(k, Json(Field(k).Property, compact: true), Json(Field(k).Property, compact: false))).ToList();
            var spliced = SettingsJson.Splice(file, edits);
            ReadBack(spliced, edits);
            WriteAtomically(FilePath, spliced);
            return true;
        }
        catch (JsonException ex)
        {
            error = (ex.LineNumber is { } line ? $"This change isn't saved: settings.json has a mistake near line {line + 1}." : $"This change isn't saved: {ex.Message}") +
                " Fix it (Settings › Advanced › Open settings file) or delete the file to start again.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "This change isn't saved: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"settings save failed: {ex.GetType().Name}");
            error = "This change isn't saved: ThumbFree couldn't update settings.json. Its log has the details.";
            return false;
        }
    }

    /// <summary>Reads the new file back before it replaces the old one: each changed key must hold its new value.</summary>
    private static void ReadBack(byte[] spliced, List<SettingsEdit> edits)
    {
        var values = SettingsJson.Values(spliced);
        foreach (var edit in edits)
        {
            var type = Field(edit.Key).Property.PropertyType;
            var read = values[edit.Key].Deserialize(type, AppSettings.JsonOptions);
            if (JsonSerializer.Serialize(read, type, AppSettings.CompactOptions) != edit.Compact)
            {
                throw new InvalidOperationException($"{edit.Key} didn't read back");
            }
        }
    }
}

/// <summary>The <see cref="AppSettings"/> properties a change touched.</summary>
internal sealed class SettingsChange(IReadOnlySet<string> names)
{
    public IReadOnlySet<string> Names { get; } = names;

    /// <summary>True for any of <paramref name="properties"/> (use nameof(AppSettings.X)).</summary>
    public bool Has(params string[] properties) => properties.Any(Names.Contains);

    /// <summary>Something that is read only when ThumbFree starts changed.</summary>
    public bool NeedsRestart => Names.Overlaps(AppSettings.ReadAtStart);
}
