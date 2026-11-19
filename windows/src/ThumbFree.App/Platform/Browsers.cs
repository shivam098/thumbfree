namespace ThumbFree.App.Platform;

/// <summary>
/// Web browsers, by process name. A browser can't reliably say whether a window is private: Chrome's and Brave's
/// private windows have no marker in their titles, and other browsers translate theirs. So whatever goes into a browser
/// is treated as possibly private (PLAN 3.8: where privacy is unknown, keep no samples).
/// </summary>
internal static class Browsers
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chromium-based. "browser" is Yandex, "comet" Perplexity, "whale" Naver. Opera GX runs as "opera" too.
        "chrome", "msedge", "brave", "opera", "vivaldi", "arc", "chromium", "thorium", "browser", "comet", "DuckDuckGo", "whale", "AvastBrowser",

        // Firefox-based (Tor Browser runs as "firefox").
        "firefox", "zen", "librewolf", "waterfox", "floorp", "mullvadbrowser", "palemoon",

        "iexplore",
    };

    /// <summary>True for a browser's process name, as Process.ProcessName gives it ("chrome"); ".exe" and case don't matter.</summary>
    public static bool IsBrowser(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return Names.Contains(name);
    }
}
