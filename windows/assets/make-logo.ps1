<#
.SYNOPSIS
    Regenerates ThumbFree's Windows logo and app icon from the Android launcher icon, so every platform shows the same
    mark: the white microphone of android/app/src/main/res/drawable/ic_launcher_foreground.xml on the launcher colour
    (tf_launcher_bg in android/app/src/main/res/values/colors.xml), drawn on a rounded tile.

    Writes assets\logo.png (512 px, README) and, in src\ThumbFree.App\Assets: app.ico (16 to 256 px: exe, tray and
    windows), logo-64.png (status pill, sidebar) and logo-256.png (first-run guide).

    Run with Windows PowerShell (it draws with WPF):  powershell -File assets\make-logo.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$windows = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $windows
$res = Join-Path $repo 'android\app\src\main\res'
$appAssets = Join-Path $windows 'src\ThumbFree.App\Assets'
New-Item -ItemType Directory -Force $appAssets | Out-Null

# The Android drawable: a 24-unit Material microphone, placed by its group's scale and offset in a 108-unit viewport.
$android = 'http://schemas.android.com/apk/res/android'
[xml]$vector = Get-Content -Raw (Join-Path $res 'drawable\ic_launcher_foreground.xml')
[xml]$colors = Get-Content -Raw (Join-Path $res 'values\colors.xml')
$viewport = [double]$vector.vector.GetAttribute('viewportWidth', $android)
$group = $vector.vector.group
$groupScale = [double]$group.GetAttribute('scaleX', $android)
$groupX = [double]$group.GetAttribute('translateX', $android)
$groupY = [double]$group.GetAttribute('translateY', $android)
$pathData = $group.path.GetAttribute('pathData', $android)
$glyphColour = [System.Windows.Media.ColorConverter]::ConvertFromString($group.path.GetAttribute('fillColor', $android))
$tileHex = ($colors.resources.color | Where-Object { $_.name -eq 'tf_launcher_bg' }).'#text'
$tileColour = [System.Windows.Media.ColorConverter]::ConvertFromString($tileHex)

# An adaptive icon shows the middle 72 of its 108 units (the rest is room for the launcher's mask and effects).
$visible = 72.0
$visibleStart = ($viewport - $visible) / 2

function New-Logo([int]$size) {
    $s = $size / 256.0
    $inset = if ($size -le 24) { 0.0 } else { 6 * $s }
    $tile = $size - 2 * $inset
    $radius = $tile * 0.22

    # Small icons get a bigger microphone, so its stand stays at least a pixel wide.
    $boost = if ($size -le 24) { 1.32 } elseif ($size -le 48) { 1.14 } else { 1.0 }
    $unit = $tile / $visible * $boost
    $centre = $viewport / 2

    $geometry = [System.Windows.Media.Geometry]::Parse($pathData).Clone()
    $transform = New-Object System.Windows.Media.TransformGroup
    $transform.Children.Add((New-Object System.Windows.Media.ScaleTransform $groupScale, $groupScale))
    $transform.Children.Add((New-Object System.Windows.Media.TranslateTransform ($groupX - $centre), ($groupY - $centre)))
    $transform.Children.Add((New-Object System.Windows.Media.ScaleTransform $unit, $unit))
    $transform.Children.Add((New-Object System.Windows.Media.TranslateTransform ($size / 2), ($size / 2)))
    $geometry.Transform = $transform

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $tileBrush = New-Object System.Windows.Media.SolidColorBrush $tileColour
    $dc.DrawRoundedRectangle($tileBrush, $null, (New-Object System.Windows.Rect $inset, $inset, $tile, $tile), $radius, $radius)
    $dc.DrawGeometry((New-Object System.Windows.Media.SolidColorBrush $glyphColour), $null, $geometry)
    $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function Get-PngBytes($bitmap) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    # The leading comma stops PowerShell from unrolling the byte array into the pipeline.
    return , $stream.ToArray()
}

function Save-Png([int]$size, [string]$path) {
    [System.IO.File]::WriteAllBytes($path, (Get-PngBytes (New-Logo $size)))
}

Save-Png 512 (Join-Path $PSScriptRoot 'logo.png')
Save-Png 64 (Join-Path $appAssets 'logo-64.png')
Save-Png 256 (Join-Path $appAssets 'logo-256.png')

# Multi-resolution .ico with PNG-compressed entries (Windows Vista and later).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) { $images.Add((Get-PngBytes (New-Logo $size))) }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $appAssets 'app.ico'), $ico.ToArray())
Write-Host "Logo written to assets\logo.png and $appAssets (tile $tileHex)"
