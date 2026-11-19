using ThumbFree.Core;

namespace ThumbFree.Core.Tests;

/// <summary>
/// The shared vectors every ThumbFree platform must pass (tests/vectors at the repository root). formatter.tsv was
/// generated from this C# formatter; Android runs the same rows, so a difference here means the platforms drifted.
/// </summary>
public class SharedVectorsTests
{
    [Fact]
    public void Formatter_matches_the_shared_vectors()
    {
        var formatter = new DictationFormatter(new DictationOptions
        {
            Replacements = new Dictionary<string, string> { ["git hub"] = "GitHub", ["kube"] = "Kubernetes" },
            Snippets = new Dictionary<string, string> { ["my signature"] = "Thanks,\nShivam" },
        });
        var rows = Vectors.Rows("formatter.tsv");
        Assert.True(rows.Count > 100, $"expected the vector file, found {rows.Count} rows");

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var cells = row.Split('\t');
            Assert.True(cells.Length == 4, $"bad row: {row}");
            var actual = formatter.Format(Vectors.Unescape(cells[2]), cells[0] == "1", cells[1] == "1");
            if (actual != Vectors.Unescape(cells[3]))
            {
                failures.Add($"  [{cells[0]}{cells[1]}] {cells[2]}\n     expected: {cells[3]}\n     actual:   {Vectors.Escape(actual)}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {rows.Count} vectors differ:\n" + string.Join("\n", failures.Take(25)));
    }
}

/// <summary>Reads tests/vectors/*.tsv (one row per line, # comments, backslash escapes).</summary>
internal static class Vectors
{
    public static IReadOnlyList<string> Rows(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "vectors", file)))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, $"tests/vectors/{file} not found above {AppContext.BaseDirectory}");
        return File.ReadAllLines(Path.Combine(dir!.FullName, "tests", "vectors", file), System.Text.Encoding.UTF8)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
    }

    public static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                sb.Append(s[i + 1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var c => c });
                i++;
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }

    public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
