using System.Windows.Automation;
using ThumbFree.Core;

namespace ThumbFree.App.Platform;

/// <summary>
/// The text box a dictation is for, read once when it starts: what kind of field it is (for the text rules and smart
/// formatting) and how to recognise it again, so text is only ever typed into that same box (PLAN 3.6).
/// </summary>
/// <param name="TextBefore">Text before the cursor (at most the last 2,000 characters); null when the app doesn't say.</param>
/// <param name="CaretKnown">False when only the whole value could be read (the cursor is assumed to be at the end).</param>
internal sealed record FocusedField(
    WindowInfo Window,
    int[]? ElementId,
    bool Password,
    bool MultiLine,
    bool Search,
    bool Email,
    bool PrivateWindow,
    string? TextBefore,
    bool CaretKnown,
    string Kind);

/// <summary>Reads the focused field through UI Automation. Never logs or keeps text or titles.</summary>
internal static class FocusInspector
{
    private const int MaxTextBefore = 2000;
    private const int EsMultiline = 0x0004;
    private const int GwlStyle = -16;

    private static readonly HashSet<string> EmailProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "OUTLOOK", "olk", "HxOutlook", "thunderbird", "Mailbird", "eM Client", "MailClient", "Spark",
    };

    private static readonly string[] WebmailTitles =
        ["Gmail", "Outlook", "Proton Mail", "Yahoo Mail", "Zoho Mail", "Fastmail", "Superhuman", "Thunderbird", "Spark"];

    private static readonly string[] PrivateTitles = ["InPrivate", "Incognito", "Private Browsing", "Private browsing"];

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "zen", "librewolf", "waterfox", "chromium", "thorium",
    };

    /// <summary>Editors where any focused text area takes line breaks.</summary>
    private static readonly HashSet<string> MultiLineEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "WINWORD", "notepad", "Notepad", "wordpad", "ONENOTE", "Code", "devenv", "notepad++", "Obsidian", "Notion",
    };

    /// <summary>
    /// The focused field of <paramref name="window"/>, or null when UI Automation can't tell within the time limit (the
    /// caller then treats the field as an ordinary single-line box: no layout, newlines as spaces).
    /// </summary>
    public static FocusedField? Read(WindowInfo window, int timeoutMs = 900)
    {
        try
        {
            var read = Task.Run(() => Read(window, Focused()));
            return read.Wait(timeoutMs) ? read.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the field that has focus now is <paramref name="start"/>'s (same top-level window and, when UI
    /// Automation identified it, the same element). Null when it can't be checked in time.
    /// </summary>
    public static bool? IsSameField(FocusedField start, WindowInfo? now, int timeoutMs = 600)
    {
        if (now is null || now.Handle != start.Window.Handle)
        {
            return false;
        }

        if (start.ElementId is null)
        {
            return true;
        }

        try
        {
            var check = Task.Run(() => Focused() is { } focused && Id(focused) is { } id ? id.AsSpan().SequenceEqual(start.ElementId) : (bool?)null);
            return check.Wait(timeoutMs) ? check.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    private static FocusedField? Read(WindowInfo window, AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            var current = element.Current;
            var className = current.ClassName ?? string.Empty;
            var name = current.Name ?? string.Empty;
            var controlType = current.ControlType;
            var password = current.IsPassword;
            var browser = Browsers.Contains(window.ProcessName);
            var addressBar = className.EndsWith("OmniboxViewViews", StringComparison.Ordinal) || (browser && controlType == ControlType.Edit && !InPage(element));
            var search = addressBar || name.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                (current.AutomationId ?? string.Empty).Contains("search", StringComparison.OrdinalIgnoreCase);
            var multiLine = !password && !search && IsMultiLine(element, current, window.ProcessName, browser);
            var email = !search && (EmailProcesses.Contains(window.ProcessName) || (browser && WebmailTitles.Any(t => window.Title.Contains(t, StringComparison.Ordinal))));
            var privateWindow = browser && PrivateTitles.Any(t => window.Title.Contains(t, StringComparison.Ordinal));
            var (before, caretKnown) = password ? (null, false) : TextBeforeCaret(element);
            var kind = $"{controlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal)}{(multiLine ? " multi-line" : string.Empty)}{(search ? " search" : string.Empty)}{(email ? " email" : string.Empty)}";
            return new FocusedField(window, Id(element), password, multiLine, search, email, privateWindow, before, caretKnown, kind);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"focused field unreadable: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Whether line breaks are text here. Wrong "yes" could send a chat message or submit a form with Enter, so it needs
    /// evidence: a document, a Win32 edit with ES_MULTILINE, ARIA multiline (browsers' text areas and rich editors), or an
    /// editor app.
    /// </summary>
    private static bool IsMultiLine(AutomationElement element, AutomationElement.AutomationElementInformation current, string process, bool browser)
    {
        if (current.ControlType == ControlType.Document && !browser)
        {
            return true;
        }

        var aria = UiaAria.FocusedAriaProperties() ?? string.Empty;
        if (aria.Contains("multiline=true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (current.NativeWindowHandle != 0 && current.FrameworkId == "Win32" &&
            ((current.ClassName ?? string.Empty).StartsWith("Edit", StringComparison.OrdinalIgnoreCase) || (current.ClassName ?? string.Empty).StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)))
        {
            return (Win32.GetWindowLongPtr(current.NativeWindowHandle, GwlStyle) & EsMultiline) != 0;
        }

        return !browser && MultiLineEditors.Contains(process) && (current.ControlType == ControlType.Edit || current.ControlType == ControlType.Document);
    }

    /// <summary>
    /// TextPattern: from the start of the field to the cursor (exact). ValuePattern only: the whole value, cursor assumed
    /// at the end. Only the last <see cref="MaxTextBefore"/> characters are kept; nothing is logged.
    /// </summary>
    private static (string? Text, bool CaretKnown) TextBeforeCaret(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            {
                var text = (TextPattern)textPattern;
                var selection = text.GetSelection();
                if (selection.Length > 0)
                {
                    var range = text.DocumentRange.Clone();
                    range.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, selection[0], System.Windows.Automation.Text.TextPatternRangeEndpoint.Start);
                    var value = range.GetText(-1);
                    return (value.Length > MaxTextBefore ? value[^MaxTextBefore..] : value, true);
                }
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                var value = ((ValuePattern)valuePattern).Current.Value ?? string.Empty;
                return (value.Length > MaxTextBefore ? value[^MaxTextBefore..] : value, false);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"text before the cursor unreadable: {ex.GetType().Name}");
        }

        return (null, false);
    }

    /// <summary>Part of a web page: the element or a parent is a document. A browser's address, search and find bars are not.</summary>
    private static bool InPage(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var depth = 0; current is not null && depth < 60; depth++)
        {
            if (current.Current.ControlType == ControlType.Document)
            {
                return true;
            }

            current = walker.GetParent(current);
        }

        return false;
    }

    private static int[]? Id(AutomationElement element) => TryGet(element.GetRuntimeId);

    private static T? TryGet<T>(Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static AutomationElement? Focused()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
