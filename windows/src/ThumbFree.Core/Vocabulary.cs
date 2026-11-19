using System.Text.RegularExpressions;

namespace ThumbFree.Core;

/// <summary>
/// "Words I use often" (names, jargon): written the way they're listed, and given to the accurate Hindi + English model
/// as a prompt. Whisper can repeat its prompt on near-silent audio, so a transcript made only of list entries is dropped
/// (but a single entry on its own is kept: that's indistinguishable from saying the word). Same rules as the Android app.
/// </summary>
public static partial class Vocabulary
{
    public const int MaxEntries = 40;

    private static readonly string[] Endings = ["s", "es", "ed", "d", "ing"];

    /// <summary>One entry per line or comma.</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        (text ?? string.Empty).Split('\n', ',')
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .DistinctBy(e => e.ToLowerInvariant())
            .Take(MaxEntries)
            .ToList();

    public static string? Prompt(IReadOnlyList<string> entries) => entries.Count == 0 ? null : string.Join(", ", entries);

    public static bool IsEcho(string text, IReadOnlyList<string> entries)
    {
        var heard = TranscriptText.Normalize(text);
        if (heard.Length == 0)
        {
            return false;
        }

        var normalized = entries.Select(TranscriptText.Normalize).Where(e => e.Length > 0).ToList();
        var padded = $" {heard} ";
        if (!$" {string.Join(' ', normalized)} ".Contains(padded, StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Count(e => padded.Contains($" {e} ", StringComparison.Ordinal)) >= 2;
    }

    /// <summary>
    /// Writes listed words the way they're listed when the recognizer got them nearly right: spacing and case ("git hub
    /// co-pilot" → "GitHub Copilot") for any entry, and small misspellings ("Kubirnets" → "Kubernetes") for entries of 8+
    /// letters, at most one edit per 5 letters, same first letter. Never across punctuation, never changing a number
    /// ("Windows 10" stays with "Windows 11" listed) and never dropping or adding an ending ("Priyanka's", "Chromebooks").
    /// Short entries are only matched exactly, so "genre" never turns into "genres".
    /// </summary>
    public static string Correct(string text, IReadOnlyList<string> entries)
    {
        if (entries.Count == 0 || text.Length == 0)
        {
            return text;
        }

        var targets = entries
            .Select(e => (Entry: e, Target: TranscriptText.Compact(e)))
            .Where(t => t.Target.Length >= 3)
            .OrderByDescending(t => t.Target.Length)
            .ToList();
        if (targets.Count == 0)
        {
            return text;
        }

        var words = WordRegex().Matches(text);
        var sb = new System.Text.StringBuilder(text.Length);
        var copied = 0;
        var i = 0;
        while (i < words.Count)
        {
            // Words from i that are separated only by spaces (a match never swallows punctuation).
            var joinable = 1;
            while (i + joinable < words.Count && string.IsNullOrWhiteSpace(Between(text, words[i + joinable - 1], words[i + joinable])))
            {
                joinable++;
            }

            (int Words, string Replacement, int Edits)? best = null;
            foreach (var (entry, target) in targets)
            {
                var allowed = target.Length >= 8 ? target.Length / 5 : 0;
                var entryWords = entry.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                for (var n = Math.Max(1, entryWords - 1); n <= Math.Min(entryWords + 2, joinable); n++)
                {
                    var heard = TranscriptText.Compact(string.Join(' ', Enumerable.Range(i, n).Select(k => words[k].Value)));
                    if (Math.Abs(heard.Length - target.Length) > allowed || heard.Length == 0 || heard[0] != target[0])
                    {
                        continue;
                    }

                    var edits = Distance(heard, target, allowed);
                    if (edits > allowed || (edits > 0 && !PlausibleTypo(heard, target)))
                    {
                        continue;
                    }

                    if (best is null || edits < best.Value.Edits || (edits == best.Value.Edits && n > best.Value.Words))
                    {
                        best = (n, entry.Trim(), edits);
                    }
                }
            }

            if (best is not { } match)
            {
                i++;
                continue;
            }

            var start = words[i].Index;
            var last = words[i + match.Words - 1];
            sb.Append(text, copied, start - copied).Append(match.Replacement);
            copied = last.Index + last.Length;
            i += match.Words;
        }

        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static string Between(string text, Match a, Match b) => text[(a.Index + a.Length)..b.Index];

    /// <summary>A misspelling, not a different number or the same word with another ending.</summary>
    private static bool PlausibleTypo(string heard, string target)
    {
        if (!new string(heard.Where(char.IsDigit).ToArray()).Equals(new string(target.Where(char.IsDigit).ToArray()), StringComparison.Ordinal))
        {
            return false;
        }

        var (longer, shorter) = heard.Length >= target.Length ? (heard, target) : (target, heard);
        return !(longer.StartsWith(shorter, StringComparison.Ordinal) && Endings.Contains(longer[shorter.Length..]));
    }

    /// <summary>Levenshtein distance, or limit + 1 once it's certainly above <paramref name="limit"/>.</summary>
    private static int Distance(string a, string b, int limit)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var y = 0; y <= b.Length; y++)
        {
            prev[y] = y;
        }

        for (var x = 1; x <= a.Length; x++)
        {
            cur[0] = x;
            var rowMin = cur[0];
            for (var y = 1; y <= b.Length; y++)
            {
                cur[y] = Math.Min(Math.Min(prev[y] + 1, cur[y - 1] + 1), prev[y - 1] + (a[x - 1] == b[y - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, cur[y]);
            }

            if (rowMin > limit)
            {
                return limit + 1;
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    [GeneratedRegex(@"[\p{L}\p{Mn}\p{Nd}][\p{L}\p{Mn}\p{Nd}'’-]*")]
    private static partial Regex WordRegex();
}

/// <summary>
/// "What do you do?" from the first-run guide. Used only on this PC: picking a role adds that field's names and jargon
/// to "Words I use often", so they're spelled right from the first dictation. Entries are proper nouns, brands and
/// acronyms. Never ordinary words that are also names (Notion, Excel, Medium): listed words are written exactly as
/// listed, so "excel at" would become "Excel at". And no ordinary words of 8+ letters, which the near-miss correction
/// could reach from other words ("escalating" → "escalation"). Same lists as the Android app.
/// </summary>
public static class Professions
{
    public static IReadOnlyList<(string Role, IReadOnlyList<string> Words)> All { get; } =
    [
        ("Developer", ["GitHub", "GitHub Copilot", "pull request", "Kubernetes", "Docker", "API", "JSON", "TypeScript", "JavaScript", "Python", "PostgreSQL", "VS Code"]),
        ("Product", ["roadmap", "OKR", "KPI", "PRD", "Jira", "Figma", "go-to-market"]),
        ("Founder / CEO", ["OKR", "KPI", "ARR", "go-to-market", "term sheet", "LinkedIn"]),
        ("Sales", ["CRM", "Salesforce", "HubSpot", "LinkedIn"]),
        ("Marketing", ["SEO", "LinkedIn", "Instagram", "YouTube", "HubSpot", "Google Analytics", "CTA"]),
        ("Customer support", ["Zendesk", "Freshdesk", "SLA"]),
        ("Healthcare", ["MRI", "CT scan", "ECG", "ibuprofen", "paracetamol", "amoxicillin", "BP", "OPD"]),
        ("Educator", ["Google Classroom", "syllabus", "rubric", "PTM", "CBSE"]),
        ("Student", ["Google Classroom", "syllabus", "GPA", "CGPA"]),
        ("Writer / creator", ["Substack", "YouTube", "Instagram"]),
        ("Recruiting", ["LinkedIn", "Naukri", "CTC"]),
        ("Legal", ["NDA", "affidavit", "indemnity", "plaintiff", "defendant"]),
        ("Data analysis", ["SQL", "Power BI", "Tableau", "pandas", "Jupyter"]),
        ("Operations", ["SOP", "KPI", "SAP Ariba"]),
        ("Real estate", ["RERA", "EMI", "escrow"]),
        ("Other", []),
    ];

    /// <summary><paramref name="current"/> word list with <paramref name="role"/>'s words added (case-insensitive, nothing removed, order kept).</summary>
    public static string AddTo(string current, string role)
    {
        var existing = Vocabulary.Parse(current).Select(e => e.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var words = All.FirstOrDefault(p => p.Role == role).Words ?? [];
        var added = words.Where(w => existing.Add(w.ToLowerInvariant())).ToList();
        if (added.Count == 0)
        {
            return current;
        }

        var baseText = current.TrimEnd();
        return (baseText.Length == 0 ? string.Empty : baseText + "\n") + string.Join("\n", added);
    }
}
