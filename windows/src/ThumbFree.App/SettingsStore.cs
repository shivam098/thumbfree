using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ThumbFree.App;

/// <summary>
/// Saves the Settings page's changes to settings.json without losing anything in it. Each save re-reads the file and
/// writes only the fields that changed, so hand edits made while ThumbFree runs, values the page doesn't show
/// (snippets, new-line keys…) and fields this version doesn't know (from Voice Dictation or a newer ThumbFree) stay
/// as they are. A file that can't be read is never overwritten: the change still applies until ThumbFree exits, and
/// <see cref="LastError"/> says how to fix the file.
/// </summary>
internal sealed class SettingsStore
{
    /// <summary>JSON key → <see cref="AppSettings"/> property name.</summary>
    private static readonly Dictionary<string, string> PropertyNames = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p is { CanRead: true, CanWrite: true } && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p.Name, StringComparer.Ordinal);

    private readonly object _gate = new();

    public SettingsStore(AppSettings settings, string path)
    {
        Current = settings;
        FilePath = path;
    }

    /// <summary>
    /// Raised on the thread that called <see cref="Update"/> (the UI thread) with the properties that changed. The
    /// dictation host applies them without a restart; see <see cref="AppSettings.ReadAtStart"/> for the exceptions.
    /// </summary>
    public event Action<SettingsChange>? Changed;

    /// <summary>The settings the whole app uses (one shared instance).</summary>
    public AppSettings Current { get; }

    public string FilePath { get; }

    /// <summary>Why the last change wasn't saved, or null when it was.</summary>
    public string? LastError { get; private set; }

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
            var keys = before.Select(p => p.Key).Union(after.Select(p => p.Key))
                .Where(k => !JsonNode.DeepEquals(before[k], after[k]))
                .ToList();
            if (keys.Count == 0)
            {
                return true;
            }

            saved = TryWrite(keys, after, out var error);
            LastError = error;
            changed = new SettingsChange(keys.Select(k => PropertyNames.GetValueOrDefault(k, k)).ToHashSet(StringComparer.Ordinal));
        }

        if (!saved)
        {
            Log.Warn("settings change not saved");
        }

        Changed?.Invoke(changed);
        return saved;
    }

    /// <summary>Writes through a temporary file, so a crash or a full disk never leaves half a settings file.</summary>
    internal static void WriteAtomically(string path, JsonObject json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(AppSettings.JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private JsonObject Snapshot() => JsonSerializer.SerializeToNode(Current, AppSettings.JsonOptions)!.AsObject();

    private bool TryWrite(List<string> keys, JsonObject values, out string? error)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                WriteAtomically(FilePath, Snapshot());
                error = null;
                return true;
            }

            var file = JsonNode.Parse(File.ReadAllText(FilePath), null, AppSettings.DocumentOptions) as JsonObject
                ?? throw new JsonException("settings.json isn't a JSON object");
            foreach (var key in keys)
            {
                // A hand-written "KeepHistory" is never read (keys are camelCase); don't leave it beside "keepHistory".
                foreach (var stale in file.Select(p => p.Key).Where(k => k != key && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    file.Remove(stale);
                }

                if (values[key] is { } value)
                {
                    file[key] = value.DeepClone();
                }
                else
                {
                    file.Remove(key);
                }
            }

            WriteAtomically(FilePath, file);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"This change isn't saved: settings.json has a mistake near line {(ex.LineNumber ?? 0) + 1}. " +
                "Fix it (Settings › Open settings file) or delete the file to start again.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "This change isn't saved: " + ex.Message;
            return false;
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
