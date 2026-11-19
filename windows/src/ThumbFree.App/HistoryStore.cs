using System.Globalization;
using System.Text;
using ThumbFree.Core;

namespace ThumbFree.App;

/// <summary>
/// History (PLAN 3.11): the text of recent dictations, kept only on this PC in %LOCALAPPDATA%\ThumbFree\history.txt
/// (local, not roamed, not indexed by Windows Search), so one can be copied again. 30 days and at most 500, in the
/// shared format of <see cref="History"/>, so every platform reads it alike. Reads and edits happen in memory and are
/// thread-safe; the file is written on a background thread. While "Keep history" is off nothing is kept.
/// </summary>
internal sealed class HistoryStore
{
    private readonly string _path;
    private readonly Func<bool> _enabled;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();
    private List<HistoryEntry>? _entries;
    private Task _writer = Task.CompletedTask;
    private bool _writeQueued;
    private bool _unreadable;

    /// <param name="path">history.txt.</param>
    /// <param name="enabled">"Keep history", read at every <see cref="Add"/>.</param>
    /// <param name="clock">The current time (tests).</param>
    public HistoryStore(string path, Func<bool> enabled, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _enabled = enabled;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _context = SynchronizationContext.Current;
    }

    /// <summary>Raised after every change, on the thread that created the store (the UI thread) if it has a dispatcher.</summary>
    public event Action? Changed;

    private long Now => _clock().ToUnixTimeMilliseconds();

    /// <summary>Newest first, without anything older than 30 days.</summary>
    public IReadOnlyList<HistoryEntry> Entries()
    {
        lock (_gate)
        {
            var loaded = _entries ?? Read();
            var kept = History.Prune(loaded, Now).ToList();
            _entries = kept;
            if (kept.Count != loaded.Count)
            {
                QueueWrite();
            }

            return [.. kept];
        }
    }

    /// <summary>
    /// Keeps a dictation: the final text (typed, sent to the tap-to-copy notification, or cut short) and the process
    /// name of the app it was for (<see cref="WindowInfo.ProcessName"/>; may be empty). Any thread. Nothing is kept
    /// while Keep history is off, for blank text, or when <paramref name="passwordField"/> is true: the host passes
    /// true for every dictation into a password field (<see cref="FieldCheck.Password"/>), so none is ever stored.
    /// </summary>
    public void Add(string text, string? app, bool passwordField)
    {
        if (passwordField || string.IsNullOrWhiteSpace(text) || !_enabled())
        {
            return;
        }

        lock (_gate)
        {
            var now = Now;
            _entries = History.Prune((_entries ?? Read()).Prepend(new HistoryEntry(now, app ?? string.Empty, text)), now).ToList();
            QueueWrite();
        }

        RaiseChanged();
    }

    public void Delete(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries = (_entries ?? Read()).Where(e => e != entry).ToList();
            QueueWrite();
        }

        RaiseChanged();
    }

    /// <summary>Deletes every entry (Clear history, or Keep history turned off).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];
            _unreadable = false;
            QueueWrite();
        }

        RaiseChanged();
    }

    /// <summary>Waits for the file to catch up (at exit, and in tests). Never throws: a failed write was already logged.</summary>
    public bool Flush(TimeSpan timeout)
    {
        Task writer;
        lock (_gate)
        {
            writer = _writer;
        }

        try
        {
            return writer.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private List<HistoryEntry> Read()
    {
        try
        {
            return File.Exists(_path)
                ? File.ReadAllLines(_path, Encoding.UTF8).Select(History.Decode).OfType<HistoryEntry>().ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never replace a file that couldn't be read with what this session adds.
            Log.Warn($"history unreadable: {ex.GetType().Name}");
            _unreadable = true;
            return [];
        }
    }

    private void QueueWrite()
    {
        if (_writeQueued || _unreadable)
        {
            return;
        }

        _writeQueued = true;
        _writer = _writer.ContinueWith(_ => Write(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Write()
    {
        List<HistoryEntry> snapshot;
        lock (_gate)
        {
            _writeQueued = false;
            snapshot = [.. _entries ?? []];
        }

        var temp = _path + ".tmp";
        try
        {
            if (snapshot.Count == 0)
            {
                File.Delete(_path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, string.Join("\n", snapshot.Select(History.Encode)), new UTF8Encoding(false));
            File.SetAttributes(temp, FileAttributes.NotContentIndexed);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"history not saved: {ex.GetType().Name}");
        }
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handler)
        {
            return;
        }

        if (_context is null)
        {
            handler();
        }
        else
        {
            _context.Post(_ => handler(), null);
        }
    }
}

/// <summary>Readable names for the apps dictations were for, from their process names ("WINWORD" → "Word").</summary>
internal static class AppNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OUTLOOK"] = "Outlook",
        ["olk"] = "Outlook",
        ["HxOutlook"] = "Mail",
        ["WINWORD"] = "Word",
        ["EXCEL"] = "Excel",
        ["POWERPNT"] = "PowerPoint",
        ["ONENOTE"] = "OneNote",
        ["ms-teams"] = "Teams",
        ["Teams"] = "Teams",
        ["msedge"] = "Edge",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["vivaldi"] = "Vivaldi",
        ["Code"] = "VS Code",
        ["devenv"] = "Visual Studio",
        ["notepad"] = "Notepad",
        ["WhatsApp"] = "WhatsApp",
        ["WhatsApp.Root"] = "WhatsApp",
        ["slack"] = "Slack",
        ["Discord"] = "Discord",
        ["Telegram"] = "Telegram",
        ["Signal"] = "Signal",
        ["Zoom"] = "Zoom",
        ["thunderbird"] = "Thunderbird",
        ["Obsidian"] = "Obsidian",
        ["Notion"] = "Notion",
        ["WindowsTerminal"] = "Terminal",
        ["explorer"] = "File Explorer",
        ["ThumbFree"] = "ThumbFree",
    };

    public static string Friendly(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        return Known.TryGetValue(processName, out var name) ? name : char.ToUpperInvariant(processName[0]) + processName[1..];
    }
}

/// <summary>"just now", "2 minutes ago", "yesterday", then the date: how History says when.</summary>
internal static class RelativeTime
{
    public static string Describe(DateTimeOffset time, DateTimeOffset now)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            var minutes = (int)age.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        var days = (now.LocalDateTime.Date - time.LocalDateTime.Date).Days;
        if (age < TimeSpan.FromHours(12) || days == 0)
        {
            var hours = (int)age.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        return days switch
        {
            1 => "yesterday",
            < 7 => $"{days} days ago",
            _ => time.LocalDateTime.ToString("d MMM", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>"Outlook · 2 minutes ago", or "Just now" when the app isn't known.</summary>
    public static string Caption(HistoryEntry entry, DateTimeOffset now)
    {
        var when = Describe(DateTimeOffset.FromUnixTimeMilliseconds(entry.Time), now);
        var app = AppNames.Friendly(entry.App);
        return app.Length > 0 ? $"{app} · {when}" : char.ToUpperInvariant(when[0]) + when[1..];
    }
}
