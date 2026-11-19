using Microsoft.Win32;

namespace ThumbFree.App.Platform;

/// <summary>
/// Web browsers, by process name. A browser can't reliably say whether a window is private: Chrome's and Brave's
/// private windows have no marker in their titles, and other browsers translate theirs. So whatever goes into a browser
/// is treated as possibly private (PLAN 3.8: where privacy is unknown, keep no samples). Besides the browsers listed
/// here, every program registered as a browser on this PC counts (<see cref="BrowserList"/>).
/// </summary>
internal static class Browsers
{
    private const string Registered = @"SOFTWARE\Clients\StartMenuInternet";

    private static BrowserList _list = new(RegisteredOnThisPc);

    /// <summary>True for a browser's process name, as Process.ProcessName gives it ("chrome"); ".exe" and case don't matter.</summary>
    public static bool IsBrowser(string? processName) => _list.IsBrowser(processName);

    /// <summary>Reads the browsers registered on this PC in the background (at start), so no dictation waits for it.</summary>
    public static void Prepare() => _list.Prepare();

    /// <summary>Tests: the registered browsers come from <paramref name="read"/> instead of this PC's registry.</summary>
    internal static void ReadRegisteredFrom(Func<IEnumerable<string>> read) => _list = new BrowserList(read);

    /// <summary>
    /// The program a registered command runs, without ".exe": "chrome" for "C:\…\chrome.exe" (quoted or not, with or
    /// without arguments). Null when the command doesn't run an .exe.
    /// </summary>
    internal static string? ProgramName(string? command)
    {
        var text = command?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string path;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            path = end < 0 ? text[1..] : text[1..end];
        }
        else
        {
            // Unquoted paths can have spaces ("C:\Program Files\Internet Explorer\iexplore.exe"): up to the first ".exe" that ends a word.
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            while (exe >= 0 && exe + 4 < text.Length && !char.IsWhiteSpace(text[exe + 4]))
            {
                exe = text.IndexOf(".exe", exe + 4, StringComparison.OrdinalIgnoreCase);
            }

            path = exe < 0 ? text : text[..(exe + 4)];
        }

        path = path.Trim();
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// Every browser that can be the default registers its command under StartMenuInternet, for all users (HKLM, one
    /// copy for 32- and 64-bit programs) or for one (HKCU). A key that can't be read is skipped.
    /// </summary>
    private static List<string> RegisteredOnThisPc()
    {
        var names = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var clients = hive.OpenSubKey(Registered);
                foreach (var browser in clients?.GetSubKeyNames() ?? [])
                {
                    try
                    {
                        using var command = clients!.OpenSubKey(browser + @"\shell\open\command");
                        if (ProgramName(command?.GetValue(null) as string) is { } name)
                        {
                            names.Add(name);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        Log.Warn($"couldn't read a registered browser: {ex.GetType().Name}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Warn($"couldn't read the registered browsers: {ex.GetType().Name}");
            }
        }

        return names;
    }
}

/// <summary>
/// The known browsers plus the ones registered on this PC, read once (in the background) and kept. Reading the
/// registry never throws and never holds a dictation up for more than <c>wait</c>: until it's read, only the known
/// names count.
/// </summary>
internal sealed class BrowserList(Func<IEnumerable<string>> readRegistered, TimeSpan? wait = null)
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chromium-based. "browser" is Yandex, "comet" Perplexity, "whale" Naver, "360se" and "360chrome" Qihoo 360's
        // two browsers. Opera GX runs as "opera" too.
        "chrome", "msedge", "brave", "opera", "vivaldi", "arc", "chromium", "thorium", "browser", "comet", "DuckDuckGo", "whale",
        "AvastBrowser", "AVGBrowser", "CCleanerBrowser", "360se", "360chrome", "QQBrowser", "Maxthon", "UCBrowser",

        // Firefox and other Gecko-based browsers (Tor Browser runs as "firefox").
        "firefox", "zen", "librewolf", "waterfox", "floorp", "mullvadbrowser", "palemoon", "seamonkey",

        "iexplore",
    };

    private readonly TimeSpan _wait = wait ?? TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private Task<HashSet<string>>? _registered;

    /// <summary>True for a browser's process name; ".exe" and case don't matter. Empty is no name, not a browser.</summary>
    public bool IsBrowser(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (Known.Contains(name))
        {
            return true;
        }

        var registered = Registered();
        return registered.Wait(_wait) && registered.Result.Contains(name);
    }

    /// <summary>Starts reading the registered browsers, if that hasn't started yet.</summary>
    public void Prepare() => _ = Registered();

    private Task<HashSet<string>> Registered()
    {
        lock (_gate)
        {
            return _registered ??= Task.Run(Read);
        }
    }

    private HashSet<string> Read()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in readRegistered())
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            // Without the registered ones, the known browsers still count.
            Log.Warn($"couldn't read the registered browsers: {ex.GetType().Name}");
        }

        return names;
    }
}