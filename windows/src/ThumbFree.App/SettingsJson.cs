using System.Text;
using System.Text.Json;

namespace ThumbFree.App;

/// <summary>
/// settings.json the way people write it: comments, trailing commas, a key written twice. Reading takes each top-level
/// value (the last one when a key is repeated, like Voice Dictation did). Writing replaces only the text of the values
/// that changed, so comments, formatting and every other key stay exactly as they were.
/// </summary>
internal static class SettingsJson
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Nothing but white space (an empty file is treated like a missing one).</summary>
    public static bool IsBlank(ReadOnlySpan<byte> file) => WithoutBom(file).Trim(" \t\r\n"u8).IsEmpty;

    /// <summary>
    /// The top-level values by key, the last one for a key written twice. Throws <see cref="JsonException"/> for a
    /// mistake in the file (its line number is in the exception) or when the file isn't one { … } object.
    /// </summary>
    public static Dictionary<string, JsonElement> Values(byte[] file)
    {
        using var document = JsonDocument.Parse(file.AsMemory(Bom(file)), DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("settings.json isn't one { … } object.");
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return values;
    }

    /// <summary>
    /// The file with each changed key's value replaced (every occurrence of a key written twice), and keys it didn't
    /// have added before the closing brace with the file's own indentation. Nothing else in the file changes.
    /// </summary>
    public static byte[] Splice(byte[] file, IReadOnlyList<SettingsEdit> changes)
    {
        var bom = Bom(file);
        var json = file.AsSpan(bom);
        var (properties, open, close) = Scan(json);
        var newline = json.IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
        var edits = new List<(int Start, int End, string Text)>();
        var missing = new List<SettingsEdit>();
        foreach (var change in changes)
        {
            var found = properties.Where(p => p.Key == change.Key).ToList();
            if (found.Count == 0)
            {
                missing.Add(change);
                continue;
            }

            foreach (var property in found)
            {
                var indent = LineIndent(json, property.NameStart);
                edits.Add((property.ValueStart, property.ValueEnd, indent is null ? change.Compact : Reindent(change.Indented, indent, newline)));
            }
        }

        if (missing.Count > 0)
        {
            AddMissing(json, properties, open, close, newline, missing, edits);
        }

        using var output = new MemoryStream(file.Length + 256);
        output.Write(file.AsSpan(0, bom));
        var at = 0;
        foreach (var (start, end, text) in edits.OrderBy(e => e.Start))
        {
            output.Write(json[at..start]);
            output.Write(Encoding.UTF8.GetBytes(text));
            at = end;
        }

        output.Write(json[at..]);
        return output.ToArray();
    }

    /// <summary>Where each top-level property's name and value are, and where the braces are.</summary>
    private static (List<(string Key, int NameStart, int ValueStart, int ValueEnd)> Properties, int Open, int Close) Scan(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("settings.json isn't one { … } object.");
        }

        var open = (int)reader.TokenStartIndex;
        var properties = new List<(string, int, int, int)>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString()!;
            var nameStart = (int)reader.TokenStartIndex;
            reader.Read();
            var valueStart = (int)reader.TokenStartIndex;
            reader.Skip();
            properties.Add((key, nameStart, valueStart, (int)reader.BytesConsumed));
        }

        var close = (int)reader.TokenStartIndex;
        if (reader.Read())
        {
            throw new JsonException("settings.json has more after its closing brace.");
        }

        return (properties, open, close);
    }

    /// <summary>New keys go just before the closing brace, after a comma if the last value has none, indented like the others.</summary>
    private static void AddMissing(
        ReadOnlySpan<byte> json,
        List<(string Key, int NameStart, int ValueStart, int ValueEnd)> properties,
        int open,
        int close,
        string newline,
        List<SettingsEdit> missing,
        List<(int Start, int End, string Text)> edits)
    {
        var comma = properties.Count > 0 && !HasComma(json[properties[^1].ValueEnd..close]);
        if (json[open..close].IndexOf((byte)'\n') < 0)
        {
            // An object on one line stays on one line: right after the last value, or before the brace after a trailing comma.
            var inline = string.Join(",", missing.Select(m => $"{Name(m.Key)}:{m.Compact}"));
            edits.Add(comma ? (properties[^1].ValueEnd, properties[^1].ValueEnd, "," + inline) : (close, close, inline));
            return;
        }

        if (comma)
        {
            edits.Add((properties[^1].ValueEnd, properties[^1].ValueEnd, ","));
        }

        string? indent = null;
        for (var i = properties.Count - 1; i >= 0 && indent is null; i--)
        {
            indent = LineIndent(json, properties[i].NameStart);
        }

        indent ??= "  ";
        var block = string.Join("," + newline, missing.Select(m => $"{indent}{Name(m.Key)}: {Reindent(m.Indented, indent, newline)}"));
        var lineStart = close;
        while (lineStart > 0 && json[lineStart - 1] is (byte)' ' or (byte)'\t')
        {
            lineStart--;
        }

        if (lineStart == 0 || json[lineStart - 1] == (byte)'\n')
        {
            edits.Add((lineStart, lineStart, block + newline));
        }
        else
        {
            edits.Add((close, close, newline + block + newline));
        }
    }

    /// <summary>The white space before a key when the key starts its line; null when something else comes first on the line.</summary>
    private static string? LineIndent(ReadOnlySpan<byte> json, int at)
    {
        var start = at;
        while (start > 0 && json[start - 1] is (byte)' ' or (byte)'\t')
        {
            start--;
        }

        return start == 0 || json[start - 1] == (byte)'\n' ? Encoding.UTF8.GetString(json[start..at]) : null;
    }

    /// <summary>A value written on several lines, its later lines indented under its key.</summary>
    private static string Reindent(string indented, string indent, string newline) =>
        indented.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline + indent, StringComparison.Ordinal);

    /// <summary>A comma among white space and comments (a trailing comma after the last value).</summary>
    private static bool HasComma(ReadOnlySpan<byte> between)
    {
        for (var i = 0; i < between.Length; i++)
        {
            if (between[i] == (byte)',')
            {
                return true;
            }

            if (between[i] == (byte)'/' && i + 1 < between.Length && between[i + 1] == (byte)'/')
            {
                var end = between[i..].IndexOf((byte)'\n');
                i = end < 0 ? between.Length : i + end;
            }
            else if (between[i] == (byte)'/' && i + 1 < between.Length && between[i + 1] == (byte)'*')
            {
                var end = between[(i + 2)..].IndexOf("*/"u8);
                i = end < 0 ? between.Length : i + 2 + end + 1;
            }
        }

        return false;
    }

    private static string Name(string key) => JsonSerializer.Serialize(key);

    private static int Bom(ReadOnlySpan<byte> file) => file.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;

    private static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> file) => file[Bom(file)..];
}

/// <summary>A new value for one key: on one line, and indented for files written over several lines.</summary>
internal sealed record SettingsEdit(string Key, string Compact, string Indented);
