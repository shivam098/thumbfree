namespace ThumbFree.Core;

/// <summary>One dictation in History: when (Unix ms), which app it was for (may be empty) and the text.</summary>
public sealed record HistoryEntry(long Time, string App, string Text);

/// <summary>
/// History is stored on the device as one line per dictation, <c>time&lt;TAB&gt;app&lt;TAB&gt;text</c>, with backslash,
/// tab and line breaks escaped. The same format and rules as the Android app, so every platform reads and prunes it alike.
/// </summary>
public static class History
{
    /// <summary>Entries older than this are removed.</summary>
    public const long MaxAgeMs = 30L * 24 * 60 * 60 * 1000;

    /// <summary>At most this many are kept (the newest).</summary>
    public const int MaxEntries = 500;

    public static string Encode(HistoryEntry entry) => $"{entry.Time}\t{Escape(entry.App)}\t{Escape(entry.Text)}";

    /// <summary>Null for a damaged line (it's skipped, never fatal).</summary>
    public static HistoryEntry? Decode(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length != 3 || !long.TryParse(parts[0], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var time))
        {
            return null;
        }

        var text = Unescape(parts[2]);
        return string.IsNullOrWhiteSpace(text) ? null : new HistoryEntry(time, Unescape(parts[1]), text);
    }

    /// <summary>Newest first, without expired entries, at most <see cref="MaxEntries"/>.</summary>
    public static IReadOnlyList<HistoryEntry> Prune(IEnumerable<HistoryEntry> entries, long now) =>
        entries.Where(e => now - e.Time <= MaxAgeMs).OrderByDescending(e => e.Time).Take(MaxEntries).ToList();

    /// <summary>Entries whose text contains every word of <paramref name="query"/> (case-insensitive).</summary>
    public static IReadOnlyList<HistoryEntry> Search(IReadOnlyList<HistoryEntry> entries, string query)
    {
        var words = query.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return entries;
        }

        return entries.Where(e =>
        {
            var text = e.Text.ToLowerInvariant();
            return words.All(w => text.Contains(w, StringComparison.Ordinal));
        }).ToList();
    }

    private static string Escape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\\' => @"\\",
                '\t' => @"\t",
                '\n' => @"\n",
                '\r' => @"\r",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    private static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                sb.Append(s[i + 1] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', var c => c });
                i++;
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }
}
