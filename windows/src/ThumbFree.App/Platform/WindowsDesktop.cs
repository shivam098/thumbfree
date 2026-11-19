using System.Diagnostics;
using System.Windows.Automation;
using ThumbFree.Core;

namespace ThumbFree.App.Platform;

/// <summary>Inserts text where the cursor is, using Win32 input and UI Automation for the field checks.</summary>
internal sealed class WindowsDesktop : IDesktop, IGatedInput
{
    private readonly int _ownProcessId = Environment.ProcessId;

    /// <summary>"Paste" (reliable in every editor) or "Type" (synthesized keystrokes).</summary>
    public static string InsertMethod { get; set; } = "Paste";

    public WindowInfo? GetForegroundWindow()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == 0)
        {
            return null;
        }

        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root != 0)
        {
            hwnd = root;
        }

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _ownProcessId)
        {
            return null;
        }

        return new WindowInfo(hwnd, (int)pid, ProcessName((int)pid), Win32.GetWindowTitle(hwnd));
    }

    public FieldCheck CheckFocusedField()
    {
        if (GetForegroundWindow() is { } foreground && Win32.IsProcessElevated(foreground.ProcessId) == true)
        {
            return FieldCheck.Elevated;
        }

        // The focus as it is: Chromium pages are woken only when a dictation starts (FocusInspector.Read).
        try
        {
            var check = Task.Run(() => Check(FocusInspector.Focused()));
            return check.Wait(2000) ? check.Result : FieldCheck.Unknown;
        }
        catch (AggregateException)
        {
            return FieldCheck.Unknown;
        }
    }

    public bool? FocusedTextEndsWithNonWhitespace()
    {
        try
        {
            var check = Task.Run(() => HasTrailingText(FocusInspector.Focused()));
            return check.Wait(2000) ? check.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    public void TypeText(string text) => TypeText(text, beforeKeys: () => { });

    /// <summary>
    /// Pastes (or types) <paramref name="text"/>. <paramref name="beforeKeys"/> runs last, right before Ctrl+V or the
    /// keystrokes: after the clipboard holds the text, which can take seconds when it held Office content, and after held
    /// modifier keys are let go. If it throws, nothing is typed and the clipboard is put back.
    /// </summary>
    public void TypeText(string text, Action beforeKeys)
    {
        text = text.Replace("\r\n", " ").Replace('\n', ' ');
        if (string.Equals(InsertMethod, "Paste", StringComparison.OrdinalIgnoreCase))
        {
            ClipboardPaste.Paste(text, () => BeforeKeys(beforeKeys));
        }
        else
        {
            BeforeKeys(beforeKeys);
            KeyboardInput.TypeUnicode(text);
        }
    }

    public void PressKeys(string chord) => PressKeys(chord, beforeKeys: () => { });

    public void PressKeys(string chord, Action beforeKeys)
    {
        BeforeKeys(beforeKeys);
        KeyboardInput.PressChord(chord);
    }

    public void WaitForModifiers() => KeyboardInput.WaitForModifiersReleased();

    public bool ModifiersDown => KeyboardInput.ModifiersDown();

    private static void BeforeKeys(Action check)
    {
        KeyboardInput.WaitForModifiersReleased();
        check();
    }

    private static FieldCheck Check(AutomationElement? element)
    {
        if (element is null)
        {
            return FieldCheck.Unknown;
        }

        try
        {
            return element.Current.IsPassword ? FieldCheck.Password : FieldCheck.Safe;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return FieldCheck.Unknown;
        }
    }

    private static bool? HasTrailingText(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                var value = ((ValuePattern)pattern).Current.Value;
                return value.Length > 0 && !char.IsWhiteSpace(value[^1]);
            }

            return null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
