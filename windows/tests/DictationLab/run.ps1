<#
.SYNOPSIS
    Runs a dictation acceptance script against a fresh Chrome window with a local test page.
.DESCRIPTION
    Opens a test page (lab.html by default) in a NEW Chrome window with a separate, temporary profile, runs its script
    through ThumbFree.exe --self-test (transcripts are scripted and recordings played; no microphone), prints the results,
    and closes only that test window. Run it while you're not using the PC: Windows won't let a background process bring
    the test window to the front while you're typing elsewhere (the script then stops at the first failed check instead
    of typing).
.EXAMPLE
    .\tests\DictationLab\run.ps1 -Exe .\src\ThumbFree.App\bin\Release\net10.0-windows\win-arm64\ThumbFree.exe
.EXAMPLE
    .\tests\DictationLab\run.ps1 -Exe ... -Page gmail-lab.html -Script gmail-lab.selftest.txt
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Page = 'lab.html',
    [string] $Script = 'lab.selftest.txt',
    [string] $Chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
)

$ErrorActionPreference = 'Stop'

# Windows only lets the process that produced the last input change the foreground window, so a script started in the
# background can't rely on the new Chrome window being in front. Tap an unassigned key (F24) to get that right, bring the
# test window forward, and refuse to run unless it really is in front (otherwise commands would act on your windows).
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class LabWindow {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static bool BringToFront(IntPtr h) {
    for (int i = 0; i < 5; i++) {
      keybd_event(0x87, 0, 0, UIntPtr.Zero); keybd_event(0x87, 0, 2, UIntPtr.Zero);
      ShowWindow(h, 9); SetForegroundWindow(h);
      System.Threading.Thread.Sleep(300);
      if (GetForegroundWindow() == h) return true;
    }
    return false;
  }
  // A window brought forward by another program can have no focus inside it: one click on the blank right-hand side of
  // the page gives Chrome the focus (the test pages then put the cursor back in their last box). Only while it's in front.
  public static bool ClickBlankPage(IntPtr h) {
    RECT r; POINT before;
    if (GetForegroundWindow() != h || !GetWindowRect(h, out r) || !GetCursorPos(out before)) return false;
    var target = new POINT { X = r.Right - 50, Y = r.Top + (r.Bottom - r.Top) / 2 };
    // Not if another window (a call's mini window, a picture-in-picture video) is on top there.
    if (GetAncestor(WindowFromPoint(target), 2) != h) return false;
    SetCursorPos(target.X, target.Y);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    SetCursorPos(before.X, before.Y);
    System.Threading.Thread.Sleep(300);
    return GetForegroundWindow() == h;
  }
}
"@
function Get-LabWindow([string] $profileMarker) {
    for ($i = 0; $i -lt 20; $i++) {
        $browser = Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*$profileMarker*" -and $_.CommandLine -notlike '*--type=*' } | Select-Object -First 1
        if ($browser) {
            $p = Get-Process -Id $browser.ProcessId -ErrorAction SilentlyContinue
            if ($p -and $p.MainWindowHandle -ne 0) { return $p.MainWindowHandle }
        }
        Start-Sleep -Milliseconds 500
    }
    return [IntPtr]::Zero
}
$here = $PSScriptRoot
$page = ([Uri](Join-Path $here $Page)).AbsoluteUri
$profileDir = Join-Path $env:TEMP 'vd-dictlab-profile'

# Start-Process joins arguments with spaces without quoting them, so quote paths (the profile may contain spaces).
Start-Process $Chrome -ArgumentList "`"--user-data-dir=$profileDir`" --no-first-run --no-default-browser-check --hide-crash-restore-bubble --window-position=60,40 --window-size=1200,900 --new-window `"$page`"" | Out-Null
try {
    Start-Sleep -Seconds 6
    $lab = Get-LabWindow 'vd-dictlab-profile'
    if ($lab -eq [IntPtr]::Zero -or -not [LabWindow]::BringToFront($lab)) { throw 'The test window could not be brought to the front; nothing was run.' }
    if (-not [LabWindow]::ClickBlankPage($lab)) { throw 'The test window left the front; nothing was run.' }
    $out = Join-Path $env:TEMP 'vd-dictlab.out.txt'
    Remove-Item $out -ErrorAction SilentlyContinue
    $p = Start-Process $Exe -ArgumentList "--self-test `"$(Join-Path $here $Script)`" `"$out`"" -PassThru
    if (-not $p.WaitForExit(300000)) { Stop-Process -Id $p.Id; throw 'self-test timed out' }
    $lines = Get-Content $out
    $lines
    $pass = @($lines | Where-Object { $_ -match '^\s+PASS' }).Count
    $fail = @($lines | Where-Object { $_ -match '^\s+FAIL' }).Count
    Write-Host "`n$pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0 -and $pass -gt 0) { 'Green' } else { 'Red' })
}
finally {
    # Close only the test profile's Chrome: ask its windows to close first so the profile isn't marked as crashed.
    $test = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like '*vd-dictlab-profile*' })
    foreach ($proc in $test) {
        $p = Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne 0) { $p.CloseMainWindow() | Out-Null }
    }

    Start-Sleep -Seconds 2
    $test | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
