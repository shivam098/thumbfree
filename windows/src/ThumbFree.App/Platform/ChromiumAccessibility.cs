using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ThumbFree.App.Platform;

/// <summary>
/// Chromium apps (Chrome, Edge and the other Chromium browsers, Electron apps such as VS Code, Slack and Discord, and
/// WebView2 apps such as the new Outlook) build the accessibility tree of their pages only once an assistive tool asks
/// for it. Until then UI Automation reports the focus as a pane, with nothing about the text box: password, lines, the
/// text before the cursor. <see cref="Wake"/> asks the way screen readers do; the app then keeps basic page accessibility
/// on while it runs (it may use a little more memory). Even then, Chrome 157 reports the focus as the browser's pane,
/// because its page windows aren't the browser window's children: <see cref="FocusedInPage"/> finds the page element that
/// has the keyboard focus. Nothing read here is kept or logged.
/// </summary>
internal static class ChromiumAccessibility
{
    /// <summary>The window Chromium keeps for each page's accessibility.</summary>
    public const string PageWindowClass = "Chrome_RenderWidgetHostHWND";

    private const string TopWindowClassPrefix = "Chrome_WidgetWin_";
    private const uint WmGetObject = 0x003D;

    /// <summary>Chromium's kIdScreenReaderHoneyPot: the object id screen readers ask a page window for.</summary>
    private const nint ScreenReaderHoneyPot = 1;

    private const uint ObjIdClient = 0xFFFFFFFC;
    private const int MaxPageNameReads = 8;

    private static readonly Condition IsDocument = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
    private static readonly Condition HasFocus = new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true);

    private static Guid accessibleId = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    /// <summary>
    /// The page windows of the top-level window <paramref name="root"/>: its own (WebView2 apps and older Chrome), and
    /// those under the app's other top-level windows (Chrome 157 keeps them in a separate window, or parks them in its
    /// hidden window).
    /// </summary>
    public static List<nint> PageWindows(nint root)
    {
        var pages = Win32.FindChildWindows(root, PageWindowClass);
        if (!ClassName(root).StartsWith(TopWindowClassPrefix, StringComparison.Ordinal) ||
            Win32.GetWindowThreadProcessId(root, out var processId) == 0)
        {
            return pages;
        }

        Win32.EnumWindows(
            (top, _) =>
            {
                if (top != root && Win32.GetWindowThreadProcessId(top, out var owner) != 0 && owner == processId)
                {
                    pages.AddRange(Win32.FindChildWindows(top, PageWindowClass));
                }

                return true;
            },
            0);
        return pages;
    }

    /// <summary>
    /// Does what screen readers do so the app builds its pages' accessibility. Chromium turns it on for the whole app
    /// once a client reads what kind of element a part of a page is, or its name, through UI Automation
    /// (AXPlatformNodeWin::OnPropertiesUsed): so the window's documents are looked up by control type and their names
    /// read. Also the "screen reader honeypot" query to the page windows plus an MSAA name read in the same app
    /// (AXPlatform::OnScreenReaderHoneyPotQueried and OnMinimalPropertiesUsed), which a page window parked in
    /// Chromium's hidden window ignores. False when the window has no Chromium pages.
    /// </summary>
    public static bool Wake(nint root)
    {
        var pages = PageWindows(root);
        if (pages.Count == 0)
        {
            return false;
        }

        foreach (var page in pages)
        {
            Win32.SendMessageTimeout(page, WmGetObject, 0, ScreenReaderHoneyPot, Win32.SMTO_ABORTIFHUNG, 250, out _);
        }

        // The name is read in the app that owns the pages: the page windows themselves (WebView2's belong to its own
        // browser process), and the window (Chrome and Electron apps).
        foreach (var page in pages.Take(MaxPageNameReads))
        {
            ReadName(page);
        }

        ReadName(root);
        ReadDocuments(root);
        return true;
    }

    /// <summary>
    /// The page element with the keyboard focus, when UI Automation reports a pane in a Chromium window: the deepest
    /// focused element under the pane, else in the whole window (an element that isn't a document wins over the pages'
    /// own documents). Null when only panes have the focus.
    /// </summary>
    public static AutomationElement? FocusedInPage(AutomationElement pane, nint root)
    {
        if (Deepest(pane) is { } underPane)
        {
            return underPane;
        }

        try
        {
            return Deepest(AutomationElement.FromHandle(root));
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return null;
        }
    }

    private static AutomationElement? Deepest(AutomationElement scope)
    {
        AutomationElement? document = null;
        try
        {
            var focused = scope.FindAll(TreeScope.Descendants, HasFocus);
            for (var i = focused.Count - 1; i >= 0; i--)
            {
                var type = focused[i].Current.ControlType;
                if (type == ControlType.Document)
                {
                    document ??= focused[i];
                }
                else if (type != ControlType.Pane)
                {
                    return focused[i];
                }
            }
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            // Elements come and go while a page loads.
        }

        return document;
    }

    /// <summary>What screen readers ask first: the kind of each element (UI Automation evaluates it for the search), and the documents' names.</summary>
    private static void ReadDocuments(nint root)
    {
        try
        {
            foreach (AutomationElement document in AutomationElement.FromHandle(root).FindAll(TreeScope.Descendants, IsDocument))
            {
                try
                {
                    _ = document.Current.Name;
                }
                catch (Exception ex) when (IsUiaFailure(ex))
                {
                    // A page that went away meanwhile.
                }
            }
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            // The window went away, or the app didn't answer in time.
        }
    }

    private static void ReadName(nint window)
    {
        object? accessible = null;
        try
        {
            if (AccessibleObjectFromWindow(window, ObjIdClient, ref accessibleId, out accessible) == 0 && accessible is not null)
            {
                _ = accessible.GetType().InvokeMember("accName", BindingFlags.GetProperty, null, accessible, [0], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or TimeoutException or TargetInvocationException or MissingMethodException or InvalidCastException or InvalidComObjectException)
        {
            // An app that doesn't answer MSAA simply isn't woken this way.
        }
        finally
        {
            if (accessible is not null && Marshal.IsComObject(accessible))
            {
                Marshal.ReleaseComObject(accessible);
            }
        }
    }

    private static string ClassName(nint window)
    {
        var name = new StringBuilder(256);
        return Win32.GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    private static bool IsUiaFailure(Exception ex) =>
        ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or TimeoutException or UnauthorizedAccessException or COMException;

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint window, uint objectId, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object? accessible);
}
