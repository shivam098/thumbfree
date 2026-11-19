using System.Windows.Automation;
using ThumbFree.Core;

namespace ThumbFree.App.Platform;

/// <summary>
/// The text box a dictation is for, read once when it starts: what kind of field it is (for the text rules and smart
/// formatting) and how to recognise it again, so text is only ever typed into that same box (PLAN 3.6).
/// </summary>
/// <param name="MultiLine">True or false when the app says so; null when it doesn't (most chat apps).</param>
/// <param name="TextBefore">Text before the cursor (at most the last 2,000 characters); null when the app doesn't say.</param>
/// <param name="CaretKnown">False when only the whole value could be read (the cursor is assumed to be at the end).</param>
internal sealed record FocusedField(
    WindowInfo Window,
    int[]? ElementId,
    bool Password,
    bool? MultiLine,
    bool Search,
    bool Email,
    bool PrivateWindow,
    string? TextBefore,
    bool CaretKnown,
    string Kind)
{
    /// <summary>
    /// For the shared text rules. A box that doesn't say whether it takes line breaks is treated as one that does, as
    /// Voice Dictation did: "new line" presses the app's new-line keys (Shift+Enter in chat apps). Only boxes known to be
    /// single-line get spaces instead.
    /// </summary>
    public FieldInfo ToFieldInfo() => new(Password, Search, MultiLine ?? true, PrivateWindow, Email);
}

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

    /// <summary>Editors where any focused text area takes line breaks.</summary>
    private static readonly HashSet<string> MultiLineEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "WINWORD", "notepad", "Notepad", "wordpad", "ONENOTE", "Code", "devenv", "notepad++", "Obsidian", "Notion",
    };

    /// <summary>
    /// A browser window whose title says it's private (Edge's InPrivate, Firefox's private browsing). Chrome's and Brave's
    /// private windows don't say so in their titles, which is why samples are never kept from browsers at all.
    /// </summary>
    public static bool IsPrivateWindow(WindowInfo window) =>
        Browsers.IsBrowser(window.ProcessName) && PrivateTitles.Any(t => window.Title.Contains(t, StringComparison.Ordinal));

    /// <summary>
    /// The focused field of <paramref name="window"/>, or null when UI Automation can't tell within the time limit (the
    /// caller then treats the field as one that takes line breaks, as Voice Dictation did). The first dictation into a
    /// Chromium app can take up to a second longer (<see cref="FocusedElement"/>).
    /// </summary>
    public static FocusedField? Read(WindowInfo window, int timeoutMs = 2000)
    {
        try
        {
            var read = Task.Run(() => Read(window, FocusedElement(window.Handle)));
            return read.Wait(timeoutMs) ? read.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    /// <summary>
    /// The element with the keyboard focus. A Chromium app whose pages' accessibility isn't on yet reports the focus as a
    /// pane: it's asked the way screen readers ask (<see cref="ChromiumAccessibility.Wake"/>), and the focus is read again
    /// for up to 1.2 s. If the page still isn't there after 0.4 s, the window's elements are read as screen readers do
    /// (<see cref="ChromiumAccessibility.ReadElements"/>). Nothing is remembered: a pane that stays a pane (the focus
    /// isn't in a text box) is asked again next time.
    /// </summary>
    /// <param name="window">The top-level window; the foreground one when 0.</param>
    public static AutomationElement? FocusedElement(nint window = 0)
    {
        var focused = Focused();
        if (focused is null || !IsPane(focused))
        {
            return focused;
        }

        var root = window != 0 ? window : Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT);
        if (root == 0 || !ChromiumAccessibility.Wake(root))
        {
            return focused;
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        var now = FocusedField(waited, untilMs: 400);
        if (now is null)
        {
            ChromiumAccessibility.ReadElements(root);
            now = FocusedField(waited, untilMs: 1200);
        }

        if (now is not null)
        {
            Log.Info($"web page accessibility on in {waited.ElapsedMilliseconds} ms");
            return now;
        }

        Log.Info("the focus stayed on a pane in a Chromium window");
        return Focused() ?? focused;
    }

    /// <summary>The focus once it's no longer a pane, read every 50 ms until <paramref name="untilMs"/>; null if it stays one.</summary>
    private static AutomationElement? FocusedField(System.Diagnostics.Stopwatch waited, int untilMs)
    {
        while (waited.ElapsedMilliseconds < untilMs)
        {
            Thread.Sleep(50);
            if (Focused() is { } now && !IsPane(now))
            {
                return now;
            }
        }

        return null;
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
            var browser = Browsers.IsBrowser(window.ProcessName);
            var addressBar = className.EndsWith("OmniboxViewViews", StringComparison.Ordinal) || (browser && controlType == ControlType.Edit && !InPage(element));
            var search = addressBar || name.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                (current.AutomationId ?? string.Empty).Contains("search", StringComparison.OrdinalIgnoreCase);
            bool? multiLine = password || search ? false : IsMultiLine(element, current, window.ProcessName, browser);
            var email = !search && (EmailProcesses.Contains(window.ProcessName) || (browser && WebmailTitles.Any(t => window.Title.Contains(t, StringComparison.Ordinal))));
            var privateWindow = IsPrivateWindow(window);
            var (before, caretKnown) = password ? (null, false) : TextBeforeCaret(element);
            var lines = multiLine switch { true => " multi-line", false => " single-line", null => string.Empty };
            var kind = $"{controlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal)}{lines}{(search ? " search" : string.Empty)}{(email ? " email" : string.Empty)}{(password ? " password" : string.Empty)}{(privateWindow ? " private" : string.Empty)}";

            // A pane in a Chromium window is a page whose accessibility tree isn't there yet: once it is, the focus is the
            // text box, a different element, so only the window is compared while typing.
            var id = controlType == ControlType.Pane && ChromiumAccessibility.PageWindows(window.Handle).Count > 0 ? null : Id(element);
            return new FocusedField(window, id, password, multiLine, search, email, privateWindow, before, caretKnown, kind);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"focused field unreadable: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Whether line breaks are text here: a document, ARIA multiline (browsers' text areas and rich editors), a Win32
    /// edit's ES_MULTILINE, or an editor app. Null when the app doesn't say, which most chat apps don't.
    /// </summary>
    private static bool? IsMultiLine(AutomationElement element, AutomationElement.AutomationElementInformation current, string process, bool browser)
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

        if (aria.Contains("multiline=false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (current.NativeWindowHandle != 0 && current.FrameworkId == "Win32" &&
            ((current.ClassName ?? string.Empty).StartsWith("Edit", StringComparison.OrdinalIgnoreCase) || (current.ClassName ?? string.Empty).StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)))
        {
            return (Win32.GetWindowLongPtr(current.NativeWindowHandle, GwlStyle) & EsMultiline) != 0;
        }

        return !browser && MultiLineEditors.Contains(process) && (current.ControlType == ControlType.Edit || current.ControlType == ControlType.Document) ? true : null;
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

    private static bool IsPane(AutomationElement element) => TryGet(() => element.Current.ControlType) == ControlType.Pane;

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
