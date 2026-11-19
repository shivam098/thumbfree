namespace ThumbFree.App;

/// <summary>
/// Something failed while ThumbFree was starting, before its tray icon existed. With no icon there's no way to see it
/// or exit it, and a hidden copy would keep the next launch from starting, so it says what went wrong and exits.
/// </summary>
internal sealed class StartupGuard(Action<string> showError, Action exit)
{
    public static string Message(Exception ex) =>
        $"ThumbFree couldn't start: {ex.Message}\n\nTry starting it again. If this keeps happening, its log has the details: {AppPaths.LogsDir}";

    /// <summary>True when ThumbFree exits because of <paramref name="ex"/>; false once the tray icon exists (the usual handling applies).</summary>
    public bool Handle(Exception ex, bool trayExists)
    {
        if (trayExists)
        {
            return false;
        }

        try
        {
            showError(Message(ex));
        }
        catch (Exception dialog)
        {
            Log.Error($"couldn't show the startup error: {dialog.GetType().Name}");
        }

        exit();
        return true;
    }
}
