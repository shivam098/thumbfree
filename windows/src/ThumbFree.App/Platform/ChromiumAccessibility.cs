using System.Diagnostics;
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
/// on while it runs (it may use a little more memory). Nothing read here is kept or logged.
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
    private const int MaxNameReads = 6;

    private static Guid accessibleId = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    /// <summary>
    /// The page windows of the top-level window <paramref name="root"/>: its own (WebView2 apps and older Chrome), and
    /// those of the app's other top-level windows (Chrome 157 keeps them in a separate window).
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
    /// Does what screen readers do so the app builds its pages' accessibility (Chromium's
    /// AXPlatform::OnScreenReaderHoneyPotQueried and OnMinimalPropertiesUsed): the "screen reader honeypot" query to a
    /// page window, and a name read through MSAA in the same app. False when the window has no Chromium pages.
    /// </summary>
    public static bool Wake(nint root)
    {
        var pages = PageWindows(root);
        if (pages.Count == 0)
        {
            return false;
        }

        // Chromium remembers the query for the whole app, so one page window that answers is enough.
        foreach (var page in pages)
        {
            if (Win32.SendMessageTimeout(page, WmGetObject, 0, ScreenReaderHoneyPot, Win32.SMTO_ABORTIFHUNG, 250, out _) != 0)
            {
                break;
            }
        }

        // The name must be read in the app that owns the pages: the top-level window for Chrome and Electron apps, the
        // windows between it and the page for WebView2 (its pages belong to a separate browser process).
        var windows = new List<nint>();
        for (var window = pages[0]; window != 0 && windows.Count < MaxNameReads; window = Win32.GetAncestor(window, Win32.GA_PARENT))
        {
            windows.Add(window);
            if (window == root || Win32.GetAncestor(window, Win32.GA_ROOT) == window)
            {
                break;
            }
        }

        if (!windows.Contains(root))
        {
            windows.Add(root);
        }

        foreach (var window in windows)
        {
            ReadName(window);
        }

        return true;
    }

    /// <summary>
    /// The other way screen readers wake Chromium: reading the control type and name of the window's elements, some of
    /// which are the pages' own. At most <paramref name="max"/> elements, for at most <paramref name="budgetMs"/>.
    /// </summary>
    public static void ReadElements(nint root, int max = 1000, int budgetMs = 500)
    {
        var clock = Stopwatch.StartNew();
        var walker = TreeWalker.RawViewWalker;
        var queue = new Queue<AutomationElement>();
        try
        {
            queue.Enqueue(AutomationElement.FromHandle(root));
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return;
        }

        for (var read = 0; queue.Count > 0 && read < max && clock.ElapsedMilliseconds < budgetMs; read++)
        {
            var element = queue.Dequeue();
            try
            {
                _ = element.Current.ControlType;
                _ = element.Current.Name;
                for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
                {
                    queue.Enqueue(child);
                }
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
                // Elements come and go while a page loads.
            }
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
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException or InvalidCastException or InvalidComObjectException)
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
        ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException;

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint window, uint objectId, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object? accessible);
}
