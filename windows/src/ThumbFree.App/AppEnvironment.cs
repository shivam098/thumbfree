using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace ThumbFree.App;

/// <summary>Whether Windows lets ThumbFree (a desktop app) use the microphone.</summary>
internal enum MicrophoneAccess
{
    Allowed,
    NoMicrophone,

    /// <summary>"Let desktop apps access your microphone" is off.</summary>
    BlockedForDesktopApps,

    /// <summary>"Microphone access" is off for everything.</summary>
    BlockedForAllApps,
}

/// <summary>A yes/no question: <paramref name="Confirm"/> is the button that does it ("Delete", "Download").</summary>
internal sealed record Confirmation(string Title, string Message, string Confirm, string Cancel = "Cancel", bool Danger = false);

/// <summary>
/// What the app window, the guide and the tray ask of Windows. Tests and offscreen renders use a fake, so they never
/// touch the person's clipboard, registry or screen.
/// </summary>
internal interface IAppEnvironment
{
    /// <summary>"Start ThumbFree when I sign in" (a Run entry that starts it in the tray).</summary>
    bool StartAtSignIn { get; set; }

    /// <summary>This PC's physical memory.</summary>
    long TotalMemoryBytes { get; }

    void CopyText(string text);

    /// <summary>Opens a web page, an ms-settings: page, a folder or a program.</summary>
    void Open(string target, string? arguments = null);

    /// <summary>Opens File Explorer with <paramref name="path"/> selected.</summary>
    void ShowInFolder(string path);

    /// <summary>Windows says the connection is metered (mobile data, a capped plan).</summary>
    bool IsMeteredConnection();

    MicrophoneAccess CheckMicrophone();

    /// <summary>Windows notifications are on for this person.</summary>
    bool NotificationsEnabled();

    bool Confirm(Window? owner, Confirmation question);

    /// <summary>Asks where to save a file; null when cancelled.</summary>
    string? AskSavePath(Window? owner, string title, string fileName, string filter);
}

/// <summary>The real <see cref="IAppEnvironment"/>.</summary>
internal sealed partial class WindowsEnvironment : IAppEnvironment
{
    public const string BackgroundArgument = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ThumbFree";
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public bool StartAtSignIn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }

        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
            {
                key.SetValue(RunValue, RunCommand);
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
    }

    public long TotalMemoryBytes
    {
        get
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : 0;
        }
    }

    /// <summary>Starts in the tray with the window closed.</summary>
    private static string RunCommand => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";

    /// <summary>Entries written before ThumbFree had a window (or by install.ps1) open it at sign-in; make them start in the tray.</summary>
    public static void UpgradeStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValue) is string value && !value.Contains(BackgroundArgument, StringComparison.Ordinal))
            {
                key.SetValue(RunValue, RunCommand);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"couldn't update the sign-in entry: {ex.GetType().Name}");
        }
    }

    public void CopyText(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);

        // Dictations stay on this PC: never synced to other devices by the cloud clipboard.
        data.SetData("CanUploadToCloudClipboard", new MemoryStream([0, 0, 0, 0]));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(30);
            }
        }
    }

    public void Open(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, arguments ?? string.Empty) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"couldn't open {Path.GetFileName(target)}: {ex.GetType().Name}");
        }
    }

    public void ShowInFolder(string path) => Open("explorer.exe", $"/select,\"{path}\"");

    /// <summary>Lets the running copy of ThumbFree take the foreground (called by a second launch).</summary>
    public static void AllowAnyForeground() => AllowSetForegroundWindow(-1);

    public bool IsMeteredConnection()
    {
        const uint Fixed = 0x2, Variable = 0x4, OverDataLimit = 0x10000, Roaming = 0x40000;
        try
        {
            var manager = (INetworkCostManager)new NetworkListManager();
            try
            {
                manager.GetCost(out var cost, 0);
                return (cost & (Fixed | Variable | OverDataLimit | Roaming)) != 0;
            }
            finally
            {
                Marshal.ReleaseComObject(manager);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public MicrophoneAccess CheckMicrophone()
    {
        if (Denied(Registry.LocalMachine, ConsentStore) || Denied(Registry.CurrentUser, ConsentStore))
        {
            return MicrophoneAccess.BlockedForAllApps;
        }

        if (Denied(Registry.CurrentUser, ConsentStore + @"\NonPackaged"))
        {
            return MicrophoneAccess.BlockedForDesktopApps;
        }

        try
        {
            return NAudio.Wave.WaveInEvent.DeviceCount == 0 ? MicrophoneAccess.NoMicrophone : MicrophoneAccess.Allowed;
        }
        catch (NAudio.MmException)
        {
            return MicrophoneAccess.Allowed;
        }
    }

    public bool NotificationsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            return key?.GetValue("ToastEnabled") is not int enabled || enabled != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public bool Confirm(Window? owner, Confirmation question) => UI.ConfirmDialog.Ask(owner, question);

    public string? AskSavePath(Window? owner, string title, string fileName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = fileName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private static bool Denied(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue("Value") is string value && value.Equals("Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    /// <summary>The Network List Manager, which knows whether the connection costs money (netlistmgr.h).</summary>
    [ComImport]
    [Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager
    {
    }

    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, nint destination);

        void GetDataPlanStatus(nint status, nint destination);

        void SetDestinationAddresses(uint length, nint addresses, [MarshalAs(UnmanagedType.VariantBool)] bool append);
    }
}
