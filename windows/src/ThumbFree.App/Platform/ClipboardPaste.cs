using System.Runtime.InteropServices;

namespace ThumbFree.App.Platform;

/// <summary>
/// Inserts text by pasting, which every editor accepts atomically (synthesized Unicode keystrokes are
/// dropped or garbled by some apps, e.g. Windows 11 Notepad). The dictated text is marked so Windows
/// keeps it out of Clipboard History and cloud clipboard, and the previous clipboard is restored
/// afterwards unless the user copied something new in the meantime.
/// </summary>
internal static partial class ClipboardPaste
{
    /// <summary>How long the target app gets to read the clipboard before it is restored.</summary>
    public static int RestoreDelayMs { get; set; } = 350;

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard and presses Ctrl+V, then puts the previous clipboard back.
    /// <paramref name="beforeKeys"/> runs right before Ctrl+V; if it throws, nothing is pasted and the clipboard is put back.
    /// </summary>
    public static void Paste(string text, Action? beforeKeys = null)
    {
        object? previous = null;
        uint ourSequence = 0;
        RunSta(() =>
        {
            previous = Snapshot();
            var data = new System.Windows.DataObject();
            data.SetText(text, System.Windows.TextDataFormat.UnicodeText);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0, 0, 0, 0]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream([0, 0, 0, 0]));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream([0, 0, 0, 0]));
            SetWithRetry(data);
            ourSequence = GetClipboardSequenceNumber();
        });

        try
        {
            beforeKeys?.Invoke();
        }
        catch (Exception)
        {
            try
            {
                Restore(previous, ourSequence);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                // The reason not to paste matters more than a busy clipboard.
            }

            throw;
        }

        KeyboardInput.PressChord("Ctrl+V");
        Thread.Sleep(RestoreDelayMs);
        Restore(previous, ourSequence);
    }

    private static void Restore(object? previous, uint ourSequence) =>
        RunSta(() =>
        {
            if (GetClipboardSequenceNumber() != ourSequence)
            {
                // The user (or the app) put something new on the clipboard; leave it alone.
                return;
            }

            if (previous is System.Windows.DataObject restore)
            {
                SetWithRetry(restore);
            }
            else
            {
                System.Windows.Clipboard.Clear();
            }
        });

    /// <summary>Copies every clipboard format that can be read, so it can be put back after pasting.</summary>
    private static System.Windows.DataObject? Snapshot()
    {
        var current = System.Windows.Clipboard.GetDataObject();
        if (current is null)
        {
            return null;
        }

        var copy = new System.Windows.DataObject();
        var any = false;
        foreach (var format in current.GetFormats(autoConvert: false))
        {
            try
            {
                var value = current.GetData(format, autoConvert: false);
                if (value is not null)
                {
                    copy.SetData(format, value);
                    any = true;
                }
            }
            catch (Exception ex) when (ex is COMException or OutOfMemoryException or ExternalException or InvalidOperationException)
            {
                // Some formats (delayed rendering, private handles) can't be copied; skip them.
            }
        }

        return any ? copy : null;
    }

    private static void SetWithRetry(System.Windows.IDataObject data)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                // Another app has the clipboard open; try again shortly.
                Thread.Sleep(30);
            }
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        { IsBackground = true, Name = "Clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The clipboard is busy.");
        }

        if (error is not null)
        {
            throw new InvalidOperationException("Couldn't use the clipboard: " + error.Message, error);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();
}
