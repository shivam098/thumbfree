using System.Text.RegularExpressions;

namespace ThumbFree.Core;

/// <summary>Where dictated text is going, for <see cref="Layout"/>.</summary>
/// <param name="MultiLine">The field accepts line breaks (lists and emails need them).</param>
/// <param name="AtStart">Only whitespace before the cursor: the dictation starts the text (needed for email layout).</param>
/// <param name="Email">An email app or an email body field: a greeting or a sign-off alone is enough for email layout.</param>
public sealed record LayoutContext(bool MultiLine, bool AtStart, bool Email = false);

/// <summary>
/// Shapes formatted dictation the way it would be typed: an email gets its greeting and sign-off on their own lines, a
/// spoken list becomes a bulleted or numbered list, and a corrected time or day keeps only the correction ("Friday at 3
/// pm… actually 4 pm" → "Friday at 4 pm"). Runs after <see cref="DictationFormatter"/>; conservative by design, so
/// ordinary sentences come back unchanged. Same rules as the Android app, held together by tests/vectors/layout.tsv.
/// </summary>
public static partial class Layout
{
    private const string Greetings = "hi|hello|hey|dear|good morning|good afternoon|good evening|greetings";

    // A name may carry an apostrophe, a full stop or a hyphen ("O'Neil", "St.", "Mary-Ann").
    private const string NameWord = @"[\p{L}][\p{L}'’.-]*";
    private const string CapitalNameWord = @"\p{Lu}[\p{L}'’.-]*";

    private static readonly string[] OrdinalOrder = ["first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth"];

    public static string Apply(string text, LayoutContext context)
    {
        var corrected = SelfCorrection.Apply(text);
        if (!context.MultiLine || corrected.Contains('\n'))
        {
            return corrected;
        }

        return (context.AtStart ? Email(corrected, context.Email) : null) ?? List(corrected) ?? corrected;
    }

    /// <summary>
    /// Whether a dictation that begins with <paramref name="start"/> (formatted) may be laid out once it's finished: a
    /// bullet command, a "first, …" list, a list heading ("Things to do today", "My shopping list"), or, in an email field,
    /// a greeting at the start of the text. Elsewhere an email also needs a signed name, which only comes at the end, so a
    /// greeting alone doesn't count (chat messages keep live typing). Live typing holds such a dictation back, because
    /// text typed into another app can't be laid out afterwards. Built from the same patterns as <see cref="Apply"/>, so
    /// it only predicts what can happen; a spoken line break means no layout at all.
    /// </summary>
    public static bool MayLayOut(string start, LayoutContext context)
    {
        if (!context.MultiLine || start.Contains('\n'))
        {
            return false;
        }

        var ordinal = OrdinalRegex().Match(start);
        return (context.AtStart && context.Email && GreetingStartRegex().IsMatch(start))
            || BulletRegex().IsMatch(start)
            || (ordinal.Success && OrdinalWord(ordinal) == "first")
            || IsListHeading(SplitHead(start)?.Head ?? start);
    }

    // ---- email ----

    /// <summary>Null unless the text looks like an email (see <see cref="LayoutContext.Email"/>).</summary>
    private static string? Email(string text, bool emailApp)
    {
        var greeting = GreetingRegex().Match(text);
        var body = greeting.Success ? greeting.Groups[2].Value : text;
        var signOff = SignOffRegex().Match(body);
        var signName = signOff.Success ? signOff.Groups[2].Value.TrimEnd('.') : string.Empty;
        var looksLikeEmail = emailApp ? greeting.Success || signOff.Success : greeting.Success && signName.Length > 0;
        if (!looksLikeEmail)
        {
            return null;
        }

        var parts = new List<string>();
        if (greeting.Success)
        {
            parts.Add(greeting.Groups[1].Value.Trim() + ",");
        }

        if (signOff.Success)
        {
            body = body[..signOff.Index];
        }

        body = body.Trim();
        if (body.Length > 0)
        {
            parts.Add(Capitalize(body));
        }

        if (signOff.Success)
        {
            var phrase = Capitalize(signOff.Groups[1].Value.Trim());
            var spoken = signOff.Value.Trim();
            var end = spoken.Length > 0 && spoken[^1] is '!' or '.' ? spoken[^1] : '.';
            parts.Add(signName.Length > 0 ? $"{phrase},\n{signName}" : phrase + end);
        }

        return parts.Count < 2 ? null : string.Join("\n\n", parts);
    }

    // ---- lists ----

    /// <summary>Null unless the text is a spoken list.</summary>
    private static string? List(string text) => Bullets(text) ?? Ordinals(text) ?? CommaList(text);

    /// <summary>"Shopping list bullet point milk bullet point eggs" → "Shopping list:\n- Milk.\n- Eggs."</summary>
    private static string? Bullets(string text)
    {
        var parts = BulletRegex().Split(text);
        if (parts.Length < 2)
        {
            return null;
        }

        var items = parts.Skip(1).Select(Item).Where(i => i.Length > 0).ToList();
        return items.Count < 2 ? null : Render(Header(parts[0]), items.Select(i => "- " + i));
    }

    /// <summary>"My plan: first, book flights, second, find a hotel, and third, pack" → a numbered list.</summary>
    private static string? Ordinals(string text)
    {
        var marks = OrdinalRegex().Matches(text);
        if (marks.Count < 3)
        {
            return null;
        }

        var words = marks.Select(OrdinalWord).ToList();
        if (words[0] != "first")
        {
            return null;
        }

        for (var k = 1; k < words.Count; k++)
        {
            var inOrder = (k < OrdinalOrder.Length && words[k] == OrdinalOrder[k]) || (k == words.Count - 1 && words[k] is "final" or "last");
            if (!inOrder)
            {
                return null;
            }
        }

        var items = new List<string>();
        for (var k = 0; k < marks.Count; k++)
        {
            var end = k + 1 < marks.Count ? marks[k + 1].Index : text.Length;
            items.Add(Item(text[(marks[k].Index + marks[k].Length)..end]));
        }

        if (items.Any(i => i.Length == 0 || Words(i) > 20))
        {
            return null;
        }

        return Render(Header(text[..marks[0].Index]), items.Select((item, k) => $"{k + 1}. {item}"));
    }

    /// <summary>"Things to do today: order groceries, go on a walk and call mom" → a bulleted list.</summary>
    private static string? CommaList(string text)
    {
        if (SplitHead(text) is not { } split || !IsListHeading(split.Head))
        {
            return null;
        }

        var pieces = ItemSplitRegex().Split(split.Items.Trim()).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (pieces.Count == 0)
        {
            return null;
        }

        // "go on a walk and call mom" at the end of the list: two items when both sides are real phrases.
        var last = pieces[^1];
        var ands = AndRegex().Matches(last);
        if (ands.Count > 0 && !LeadingJoinerRegex().IsMatch(last))
        {
            var and = ands[^1];
            var left = last[..and.Index];
            var right = last[(and.Index + and.Length)..];
            if (Words(left) >= 2 && Words(right) >= 2)
            {
                pieces[^1] = left;
                pieces.Add(right);
            }
        }

        var items = pieces.Select(Item).Where(i => i.Length > 0).ToList();

        // With a colon two items make a list; without one, three (so "Tasks, then lunch" stays a sentence).
        if (items.Count < (split.Colon ? 2 : 3) || items.Any(i => Words(i) > 12))
        {
            return null;
        }

        return Render(Header(split.Head), items.Select(i => "- " + i));
    }

    /// <summary>A list heading before a colon (within 100 characters), else before the first item separator; null if neither.</summary>
    private static (string Head, string Items, bool Colon)? SplitHead(string text)
    {
        var colon = text.IndexOf(':');
        if (colon is >= 1 and <= 100)
        {
            return (text[..colon], text[(colon + 1)..], true);
        }

        var cut = ItemSplitRegex().Match(text);
        return cut.Success ? (text[..cut.Index], text[(cut.Index + cut.Length)..], false) : null;
    }

    /// <summary>Short, starts with a cue ("things to", "shopping", "agenda"…) and isn't a statement ("The agenda was long").</summary>
    private static bool IsListHeading(string head) => Words(head) <= 10 && CueRegex().IsMatch(head.Trim()) && !CopulaRegex().IsMatch(head);

    /// <summary>"Firstly" → "first", "finally" → "final".</summary>
    private static string OrdinalWord(Match mark)
    {
        var word = mark.Groups[1].Value.ToLowerInvariant();
        return word.EndsWith("ly", StringComparison.Ordinal) ? word[..^2] : word;
    }

    private static string Render(string header, IEnumerable<string> lines) =>
        (header.Length == 0 ? string.Empty : header + "\n") + string.Join("\n", lines);

    private static string Header(string raw)
    {
        var h = raw.Trim().TrimEnd(',', '.', ';', ':', ' ').Trim();
        return h.Length == 0 ? string.Empty : Capitalize(h) + ":";
    }

    private static string Item(string raw)
    {
        var s = raw.Trim().TrimEnd(',', ';', ':', ' ');
        s = LeadingJoinerRegex().Replace(s, string.Empty);
        s = s.TrimEnd('.', ',', ';', ' ');
        if (s.Length == 0)
        {
            return string.Empty;
        }

        s = Capitalize(s);
        return s[^1] is '!' or '?' ? s : s + ".";
    }

    private static int Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Capitalize(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    [GeneratedRegex(@"^((?i:" + Greetings + @")(?:\s+" + NameWord + @"){0,3}?)\s*[,!:—–-]\s*(\S.*)$", RegexOptions.Singleline)]
    private static partial Regex GreetingRegex();

    // The greeting at the start of a dictation still being spoken: what follows it may not have been said yet.
    [GeneratedRegex(@"^(?i:" + Greetings + @")(?:\s+" + NameWord + @"){0,3}?\s*(?:[,!:—–-]|$)")]
    private static partial Regex GreetingStartRegex();

    [GeneratedRegex(@"(?:^|(?<=[.!?])\s+)((?i:thanks(?: again| so much| a lot)?|thank you(?: so much| again)?|many thanks|regards|best regards|kind regards|warm regards|warmly|best|best wishes|all the best|cheers|sincerely|yours sincerely|yours truly|take care|talk soon))(?:\s*[,.!])?(?:\s+(" + CapitalNameWord + @"(?:\s+" + CapitalNameWord + @"){0,2}))?\s*[.!]?\s*$")]
    private static partial Regex SignOffRegex();

    // Only unambiguous commands: "bullet point …", "new/next bullet …", or "bullet …" opening the text. Never "bite the
    // bullet", "bullet train" or "bullet-proof".
    [GeneratedRegex(@"(?:[,.;:]?\s*(?<![\p{L}])(?i:(?:new |next |another )?bullet point|(?:new|next|another) bullet)|^(?i:bullet))(?![\p{L}-])[,.;:]?\s*")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"(?:^|(?<=[.,;:!?])\s*(?:(?i:and)\s+)?|\s+(?i:and)\s+)(?<![\p{L}])((?i:first(?:ly)?|second(?:ly)?|third(?:ly)?|fourth|fifth|sixth|seventh|eighth|ninth|tenth|finally|lastly))(?![\p{L}])(?:\s*[,:])?\s+")]
    private static partial Regex OrdinalRegex();

    [GeneratedRegex(@"^(?i:(?:my|our|the|a|your|today's|tomorrow's|this week's|here's (?:my|the|a|our)|here is (?:my|the|a|our))\s+)?(?i:to[- ]?dos?|things (?:to|I need to|we need to|I have to|we have to)|shopping|grocer(?:y|ies)|checklist|agenda|action items?|tasks?|reminders?|list|need to buy|steps|ingredients|packing)(?![\p{L}])")]
    private static partial Regex CueRegex();

    [GeneratedRegex(@"(?<![\p{L}])(?i:is|was|are|were|seems|seemed|looks|looked|felt)(?![\p{L}])")]
    private static partial Regex CopulaRegex();

    [GeneratedRegex(@"\s*(?:[,;]|\.(?=\s|$))\s*")]
    private static partial Regex ItemSplitRegex();

    [GeneratedRegex(@"^(?i:and|then|also|and then|plus)\s+")]
    private static partial Regex LeadingJoinerRegex();

    [GeneratedRegex(@"\s+(?i:and)\s+")]
    private static partial Regex AndRegex();
}

/// <summary>
/// "at 3 pm… actually 4 pm" → "at 4 pm", "on Monday, sorry, Tuesday" → "on Tuesday". Only times and days are corrected
/// (and a bare number into a time), where a spoken correction is unambiguous.
/// </summary>
public static partial class SelfCorrection
{
    private const string Weekdays = "monday|tuesday|wednesday|thursday|friday|saturday|sunday";
    private const string Day = Weekdays + "|today|tomorrow|tonight";
    private const string Time = @"\d{1,2}(?::\d{2})?(?:\s?(?:[aApP]\.?\s?[mM](?![\p{L}])\.?|o'clock))?";
    private const string Value = "(?i:" + Day + ")|" + Time;

    private static readonly HashSet<string> Days = [.. Day.Split('|')];
    private static readonly HashSet<string> WeekdayNames = [.. Weekdays.Split('|')];

    private enum Kind
    {
        Day,
        Time,
        Number,
    }

    public static string Apply(string text) => CorrectionRegex().Replace(text, m =>
    {
        var first = m.Groups[1].Value;
        var separator = m.Groups[2].Value;
        var marker = m.Groups[3].Value;
        var second = m.Groups[4].Value;

        // "Friday. Actually, tomorrow I'm busy" starts a new thought; "3 PM. Actually 4 PM?" only corrects the time.
        var newSentence = (separator.IndexOfAny(['.', '!', '?']) >= 0 && !separator.Contains("...", StringComparison.Ordinal) && !separator.Contains('…'))
            || (first.EndsWith('.') && char.IsUpper(marker[0]));
        if (newSentence && !ClauseEndRegex().IsMatch(text.AsSpan(m.Index + m.Length)))
        {
            return m.Value;
        }

        return Replacement(first, second, newSentence) ?? m.Value;
    });

    private static string? Replacement(string first, string second, bool newSentence) => (KindOf(first), KindOf(second)) switch
    {
        (Kind.Day, Kind.Day) => WeekdayNames.Contains(second.ToLowerInvariant()) ? char.ToUpperInvariant(second[0]) + second[1..] : second.ToLowerInvariant(),
        (Kind.Time, Kind.Time) or (Kind.Number, Kind.Time) => second,

        // "3 pm, actually 4" → "4 pm"
        (Kind.Time, Kind.Number) when MeridiemRegex().Match(first) is { Success: true } meridiem => second + meridiem.Value,

        // "2, actually 3" only within a sentence ("Chapter 2. Actually 3." is not a correction).
        (Kind.Number, Kind.Number) when !newSentence => second,
        _ => null,
    };

    private static Kind KindOf(string value) =>
        Days.Contains(value.ToLowerInvariant()) ? Kind.Day
        : value.Contains(':') || MeridiemRegex().IsMatch(value) ? Kind.Time
        : Kind.Number;

    // Never inside a decimal ("2.5, actually 3"): no digit-and-point just before or after a value.
    [GeneratedRegex(@"(?<![\p{L}\p{Nd}:])(?<!\p{Nd}[.,])(" + Value + @")(\s*(?:\.\.\.|…|[,.;!?—–-])?\s*)((?i:actually|sorry|I mean|no wait|or rather|make (?:it|that)))(?:\s*[,.]|\s*\.\.\.|\s*…)?\s+(?:(?i:at|on|by)\s+)?(" + Value + @")(?![\p{L}\p{Nd}]|[.,]\p{Nd})")]
    private static partial Regex CorrectionRegex();

    [GeneratedRegex(@"\s?(?:[ap]\.?\s?m\.?|o'clock)$", RegexOptions.IgnoreCase)]
    private static partial Regex MeridiemRegex();

    [GeneratedRegex(@"^\s*(?:[.,!?;:]|$)")]
    private static partial Regex ClauseEndRegex();
}
