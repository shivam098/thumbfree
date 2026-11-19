<#
.SYNOPSIS
    Builds and installs ThumbFree for the current user. No administrator rights are needed.

.DESCRIPTION
    1. Uses the .NET 10 SDK (installs it into %LOCALAPPDATA%\Microsoft\dotnet if missing).
    2. Publishes a self-contained build for this machine's architecture.
    3. Installs to %LOCALAPPDATA%\Programs\ThumbFree and adds a Start menu shortcut.
    4. Starts the app. The first-run guide downloads the speech model you pick (models Voice Dictation or Voice Assistant
       already downloaded are reused).

.EXAMPLE
    .\scripts\install.ps1
.EXAMPLE
    .\scripts\install.ps1 -StartAtSignIn
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ThumbFree'),

    [switch] $StartAtSignIn,

    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'src\ThumbFree.App\ThumbFree.App.csproj'
$Exe = Join-Path $InstallDir 'ThumbFree.exe'
$Shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ThumbFree.lnk'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# Only ever replace a folder that is empty or is an existing ThumbFree install.
function Assert-SafeInstallDir {
    if (-not (Test-Path $InstallDir)) { return }
    $isInstall = Test-Path (Join-Path $InstallDir 'ThumbFree.exe')
    $isEmpty = -not (Get-ChildItem $InstallDir -Force | Select-Object -First 1)
    if (-not ($isInstall -or $isEmpty)) {
        throw "Refusing to use '$InstallDir': it is not empty and is not a ThumbFree install. Choose an empty or new folder."
    }
}

function Get-Rid {
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    switch ($arch) {
        'ARM64' { 'win-arm64' }
        'AMD64' { 'win-x64' }
        default { throw "Unsupported processor architecture '$arch'." }
    }
}

function Get-DotNet {
    $candidates = @(
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks -match '^10\.') { return $candidate }
    }

    Write-Step 'Installing the .NET 10 SDK for the current user (no admin needed)'
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    $arch = if ((Get-Rid) -eq 'win-arm64') { 'arm64' } else { 'x64' }
    & $installer -Channel 10.0 -Architecture $arch -InstallDir (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet') | Out-Null
    return (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
}

$rid = Get-Rid
Assert-SafeInstallDir
$dotnet = Get-DotNet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Write-Step "Building ThumbFree ($rid, self-contained)"
$staging = Join-Path $env:TEMP "ThumbFree-publish-$([guid]::NewGuid().ToString('N'))"
& $dotnet publish $Project -c Release -r $rid --self-contained true -o $staging -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$running = Get-Process ThumbFree -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Step 'Stopping the running copy'
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Write-Step "Installing to $InstallDir"
# The stopped process can hold its native DLLs for a moment; retry instead of failing half-way.
for ($attempt = 1; (Test-Path $InstallDir); $attempt++) {
    try { Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop }
    catch {
        if ($attempt -ge 10) { throw }
        Start-Sleep -Milliseconds 500
    }
}
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $staging '*') $InstallDir -Recurse -Force
Remove-Item $staging -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($Shortcut)
$link.TargetPath = $Exe
$link.WorkingDirectory = $InstallDir
$link.IconLocation = "$Exe,0"
$link.Description = 'Tap Ctrl+Win, speak, tap again to type into any app.'
$link.Save()

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($StartAtSignIn) {
    # --background: start quietly in the tray, without opening the window.
    Set-ItemProperty $runKey -Name ThumbFree -Value "`"$Exe`" --background"
    Write-Step 'ThumbFree will start when you sign in'
}

if (-not $NoLaunch) {
    Write-Step 'Starting ThumbFree'
    Start-Process $Exe
}

Write-Host ''
Write-Host 'Installed. The first-run guide sets up the microphone and the speech model.' -ForegroundColor Green
Write-Host '  Tap Ctrl+Win, speak, tap Ctrl+Win again -> typed where your cursor is (Esc = cancel)'
Write-Host '  Alt+Shift+Z types the last dictation again; Alt+Shift+H switches to Hindi + English'
foreach ($other in @(@('VoiceAssistant', 'Voice Assistant'), @('VoiceDictation', 'Voice Dictation'))) {
    if (Get-Process $other[0] -ErrorAction SilentlyContinue) {
        Write-Warning "$($other[1]) is running and also uses Ctrl+Win, so text would be typed twice. Exit one of them."
    }
}
